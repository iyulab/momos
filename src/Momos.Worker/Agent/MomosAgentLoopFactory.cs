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
/// invocation depends on the chat client resolved by <c>chatClientFactory</c> being wrapped
/// with <c>UseFunctionInvocation()</c> (see
/// <see cref="Momos.Worker.ServiceCollectionExtensions.AddIronHiveAgentEngine"/>) —
/// <c>IAgentLoop.RunAsync</c> itself never invokes a requested tool call.
/// <see cref="PullExecutionBackgroundService"/> needs the session to exist before the
/// loop does (to check out the target repo into its workspace first), so it creates and
/// owns that session itself and calls the <see cref="ISessionAwareAgentLoopFactory"/>
/// overload; <see cref="CreateAsync(AgentLoopFactoryOptions,CancellationToken)"/> stays
/// available for callers with no repo to check out, opening and owning its own session.
/// </summary>
public sealed class MomosAgentLoopFactory(
    IChatClientFactory chatClientFactory,
    IUsageTracker usageTracker,
    UsageLimiter usageLimiter,
    ContextManager contextManager,
    IErrorRecoveryService errorRecovery,
    IToolRetriever toolRetriever,
    IExecutionRuntimeProvider executionRuntimeProvider,
    IHostApiClient hostApiClient,
    ILoggerFactory loggerFactory) : ISessionAwareAgentLoopFactory
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
        var (agentLoop, _) = await BuildAgentLoopAsync(options, session, projectId: null, cancellationToken);
        return new SessionScopedAgentLoop(agentLoop, executionRuntimeProvider, session);
    }

    public Task<(IAgentLoop Loop, FindingSink Findings)> CreateAsync(
        AgentLoopFactoryOptions options, ExecutionSessionHandle session, Guid? projectId = null, CancellationToken cancellationToken = default) =>
        BuildAgentLoopAsync(options, session, projectId, cancellationToken);

    private async Task<(IAgentLoop Loop, FindingSink Findings)> BuildAgentLoopAsync(
        AgentLoopFactoryOptions options, ExecutionSessionHandle session, Guid? projectId, CancellationToken cancellationToken)
    {
        // usageLimiter is a single process-wide instance (see
        // ServiceCollectionExtensions.AddIronHiveAgentEngine) tracked against by
        // UsageLimitingChatClient on every model call — reset it here so one inspection's
        // usage never counts against the next. Safe because PullExecutionBackgroundService
        // processes requests strictly sequentially; this factory is not built concurrently
        // for two overlapping sessions.
        usageLimiter.Reset();

        var chatClient = string.IsNullOrEmpty(options.Provider)
            ? await chatClientFactory.CreateAsync(options.Model, cancellationToken)
            : await chatClientFactory.CreateAsync(options.Provider, options.Model, cancellationToken);

        var codeExecutionTool = AIFunctionFactory.Create(
            new CodeExecutionTools(executionRuntimeProvider, session, new ToolCallTraceSink(), loggerFactory.CreateLogger<CodeExecutionTools>()).RunCommand);
        var findings = new FindingSink();
        var reportFindingTool = AIFunctionFactory.Create(
            new FindingReportingTools(findings).ReportFinding);

        // AgentOptions.Tools is IList<AITool> — List<AIFunction> isn't assignment-compatible
        // with it (IList<T> isn't covariant), so this is typed to the base AITool.
        var tools = new List<AITool> { codeExecutionTool, reportFindingTool };
        var alwaysIncludeToolNames = new List<string> { codeExecutionTool.Name, reportFindingTool.Name };
        if (projectId is { } id)
        {
            var knowledgeQueryTool = AIFunctionFactory.Create(
                new KnowledgeQueryTools(hostApiClient, id, loggerFactory.CreateLogger<KnowledgeQueryTools>()).QueryProjectKnowledge);
            tools.Add(knowledgeQueryTool);
            alwaysIncludeToolNames.Add(knowledgeQueryTool.Name);
        }

        var agentOptions = new AgentOptions
        {
            SystemPrompt = options.SystemPrompt,
            ModelId = options.Model,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            Tools = tools,
            // Momos's tool retriever (KeywordToolRetriever) scores tools against the
            // prompt's keywords and drops anything under its relevance threshold —
            // a filter meant for large, discoverable tool sets. None of these tools are
            // optional or query-dependent from the retriever's point of view (even the
            // knowledge-query tool, which is only situationally *useful*, must still be
            // visible to the agent every time it's present) — they must always survive
            // retrieval regardless of what the prompt happens to say.
            ToolRetrievalOptions = new ToolRetrievalOptions { AlwaysInclude = alwaysIncludeToolNames },
        };

        var agentLoop = new AgentLoop(chatClient, agentOptions, usageTracker, contextManager, errorRecovery, toolRetriever);
        return (agentLoop, findings);
    }
}
