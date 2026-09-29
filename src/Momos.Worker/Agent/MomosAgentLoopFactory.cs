using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Momos.Worker.Execution;

namespace Momos.Worker.Agent;

/// <summary>
/// Momos's implementation of <c>IronHive.Agent</c>'s <see cref="IAgentLoopFactory"/> —
/// the library registers the surrounding collaborators
/// (<see cref="IUsageTracker"/>, <see cref="ContextManager"/>,
/// <see cref="IErrorRecoveryService"/>) via <c>AddIronHiveAgent()</c> but leaves
/// the top-level factory and chat-client resolution to the consumer.
///
/// Also composes the code-execution and finding-reporting tools — every loop this
/// factory builds gets two native
/// <see cref="AIFunctionFactory.Create(System.Delegate)"/>-wrapped tools: one bound to a
/// code-beaker session (one per inspection request), one accumulating
/// into a <see cref="FindingSink"/> the caller reads back after the run. Actual tool
/// invocation depends on the chat client resolved by <c>chatClients</c> being wrapped
/// with <c>UseFunctionInvocation()</c> (see
/// <see cref="Momos.Worker.ServiceCollectionExtensions.AddIronHiveAgentEngine"/>) —
/// <c>IAgentLoop.RunAsync</c> itself never invokes a requested tool call.
/// <see cref="PullExecutionBackgroundService"/> needs the session to exist before the
/// loop does (to check out the target repo into its workspace first), so it creates and
/// owns that session itself and calls the <see cref="ISessionAwareAgentLoopFactory"/>
/// overload; <see cref="CreateAsync(AgentLoopFactoryOptions,CancellationToken)"/> stays
/// available for callers with no repo to check out, opening and owning its own session.
/// Analysis passes get their loop from <see cref="CreateAnalysisLoopAsync"/>: the same command
/// and knowledge tools, the caller's proposal tools, and no finding tool.
/// </summary>
public sealed class MomosAgentLoopFactory(
    MomosChatClientFactory chatClients,
    IUsageTracker usageTracker,
    UsageLimiter usageLimiter,
    ContextManager contextManager,
    IErrorRecoveryService errorRecovery,
    IToolRetriever toolRetriever,
    IExecutionRuntimeProvider executionRuntimeProvider,
    IHostApiClient hostApiClient,
    ILoggerFactory loggerFactory) : ISessionAwareAgentLoopFactory, IAnalysisAgentLoopFactory
{
    public Task<IAgentLoop> CreateAsync(CancellationToken cancellationToken = default) =>
        CreateAsync(new AgentLoopFactoryOptions(), cancellationToken);

    public async Task<IAgentLoop> CreateAsync(AgentLoopFactoryOptions options, CancellationToken cancellationToken = default)
    {
        // "native" is a placeholder until repo language detection exists —
        // NativeProcessRuntime's catch-all environment covers it without inventing a
        // result we can't observe.
        // Only reached by a caller with no repo to check out first (see
        // ISessionAwareAgentLoopFactory) — this factory owns this session's lifetime.
        var session = await executionRuntimeProvider.CreateSessionAsync(
            new ExecutionSessionRequest("native"), cancellationToken);
        var (agentLoop, _, _) = await BuildAgentLoopAsync(options, session, projectId: null, cancellationToken);
        return new SessionScopedAgentLoop(agentLoop, executionRuntimeProvider, session);
    }

    public Task<(IAgentLoop Loop, FindingSink Findings, ToolCallTraceSink ToolCalls)> CreateAsync(
        AgentLoopFactoryOptions options, ExecutionSessionHandle session, Guid? projectId = null, CancellationToken cancellationToken = default) =>
        BuildAgentLoopAsync(options, session, projectId, cancellationToken);

    public async Task<IAgentLoop> CreateAnalysisLoopAsync(
        ExecutionSessionHandle session, Guid projectId, IReadOnlyList<AIFunction> proposalTools, string? model, string systemPrompt,
        AnalysisContextOptions context, AnalysisToolMonitor monitor, CancellationToken cancellationToken)
    {
        var toolCalls = new ToolCallTraceSink();
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                new CodeExecutionTools(executionRuntimeProvider, session, toolCalls, loggerFactory.CreateLogger<CodeExecutionTools>(), context.MaxCommandOutputChars).RunCommand),
            AIFunctionFactory.Create(
                new KnowledgeQueryTools(hostApiClient, projectId, toolCalls, loggerFactory.CreateLogger<KnowledgeQueryTools>()).QueryProjectKnowledge),
        };
        tools.AddRange(proposalTools);
        return await BuildLoopAsync(new AgentLoopFactoryOptions { Model = model, SystemPrompt = systemPrompt }, monitor.Watch(tools), cancellationToken, context);
    }

    private async Task<(IAgentLoop Loop, FindingSink Findings, ToolCallTraceSink ToolCalls)> BuildAgentLoopAsync(
        AgentLoopFactoryOptions options, ExecutionSessionHandle session, Guid? projectId, CancellationToken cancellationToken)
    {
        var toolCalls = new ToolCallTraceSink();
        var findings = new FindingSink();
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                new CodeExecutionTools(executionRuntimeProvider, session, toolCalls, loggerFactory.CreateLogger<CodeExecutionTools>()).RunCommand),
            AIFunctionFactory.Create(new FindingReportingTools(findings).ReportFinding),
        };
        if (projectId is { } id)
        {
            tools.Add(AIFunctionFactory.Create(
                new KnowledgeQueryTools(hostApiClient, id, toolCalls, loggerFactory.CreateLogger<KnowledgeQueryTools>()).QueryProjectKnowledge));
        }

        return (await BuildLoopAsync(options, tools, cancellationToken), findings, toolCalls);
    }

    private async Task<IAgentLoop> BuildLoopAsync(
        AgentLoopFactoryOptions options, IReadOnlyList<AIFunction> tools, CancellationToken cancellationToken,
        AnalysisContextOptions? analysisContext = null)
    {
        // usageLimiter is a single process-wide instance (see
        // ServiceCollectionExtensions.AddIronHiveAgentEngine) tracked against by
        // UsageLimitingChatClient on every model call — reset it here so one loop's usage never
        // counts against the next. Safe because PullExecutionBackgroundService processes requests
        // strictly sequentially; this factory is not built concurrently for two overlapping
        // sessions. An analysis's passes are separate loops too; the allowance that spans them
        // is a TokenBudget, which this reset does not touch.
        usageLimiter.Reset();

        // An inspection keeps the shared manager and a pipeline without a tool-round reducer. An
        // analysis pass gets its own manager, per loop rather than the shared one: a manager tracks one
        // conversation's goal and window, and each pass is its own conversation. The same manager
        // goes to the loop (once per turn) and to the reducer inside function invocation (every
        // tool round), since a pass is one turn made of many rounds.
        var loopContext = contextManager;
        if (analysisContext is { } ac)
        {
            loopContext = ContextManager.ForModel(options.Model ?? "analysis", new CompactionConfig
            {
                MaxContextTokens = ac.MaxContextTokens,
                EnableObservationMasking = true,
                ObservationMaskingProtectedRounds = ac.ProtectedToolRounds,
                EnableToolResultCompaction = true,
                MaxToolResultChars = ac.MaxCommandOutputChars,
            }, summarizer: null);
        }

        var clients = chatClients.For(analysisContext is null ? null : loopContext);
        var chatClient = string.IsNullOrEmpty(options.Provider)
            ? await clients.CreateAsync(options.Model, cancellationToken)
            : await clients.CreateAsync(options.Provider, options.Model, cancellationToken);

        var agentOptions = new AgentOptions
        {
            SystemPrompt = options.SystemPrompt,
            ModelId = options.Model,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            // AgentOptions.Tools is IList<AITool> — List<AIFunction> isn't assignment-compatible
            // with it (IList<T> isn't covariant), so this is copied into the base AITool.
            Tools = [.. tools.Cast<AITool>()],
            // Momos's tool retriever (KeywordToolRetriever) scores tools against the
            // prompt's keywords and drops anything under its relevance threshold —
            // a filter meant for large, discoverable tool sets. None of these tools are
            // optional or query-dependent from the retriever's point of view (even the
            // knowledge-query tool, which is only situationally *useful*, must still be
            // visible to the agent every time it's present) — they must always survive
            // retrieval regardless of what the prompt happens to say.
            ToolRetrievalOptions = new ToolRetrievalOptions { AlwaysInclude = [.. tools.Select(t => t.Name)] },
        };

        return new AgentLoop(chatClient, agentOptions, usageTracker, loopContext, errorRecovery, toolRetriever);
    }
}
