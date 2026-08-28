using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using Momos.Worker.Execution;

namespace Momos.Worker.Agent;

/// <summary>
/// Momos's implementation of <c>IronHive.Agent</c>'s <see cref="IAgentLoopFactory"/> —
/// the library registers the surrounding collaborators
/// (<see cref="IUsageTracker"/>, <see cref="ContextManager"/>,
/// <see cref="IErrorRecoveryService"/>) via <c>AddIronHiveAgent()</c> but leaves
/// the top-level factory and chat-client resolution to the consumer.
///
/// Also composes the code-execution tool (ADR-0009 decision 3 = B) — every loop this
/// factory builds gets a native <see cref="AIFunctionFactory.Create(System.Delegate)"/>-wrapped
/// tool bound to a code-beaker session (ADR-0009 decision 2: one per inspection request).
/// <see cref="PullExecutionBackgroundService"/> needs the session to exist before the
/// loop does (to check out the target repo into its workspace first), so it creates and
/// owns that session itself and calls the <see cref="ISessionAwareAgentLoopFactory"/>
/// overload; <see cref="CreateAsync(AgentLoopFactoryOptions,CancellationToken)"/> stays
/// available for callers with no repo to check out, opening and owning its own session.
/// </summary>
public sealed class MomosAgentLoopFactory(
    IChatClientFactory chatClientFactory,
    IUsageTracker usageTracker,
    ContextManager contextManager,
    IErrorRecoveryService errorRecovery,
    IToolRetriever toolRetriever,
    IExecutionRuntimeProvider executionRuntimeProvider) : ISessionAwareAgentLoopFactory
{
    public Task<IAgentLoop> CreateAsync(CancellationToken cancellationToken = default) =>
        CreateAsync(new AgentLoopFactoryOptions(), cancellationToken);

    public async Task<IAgentLoop> CreateAsync(AgentLoopFactoryOptions options, CancellationToken cancellationToken = default)
    {
        // "native" is a placeholder until repo language detection exists —
        // ADR-0009's "잠금 효과" note anticipated this gap; NativeProcessRuntime's
        // catch-all environment covers it without inventing a result we can't observe.
        // Only reached by a caller with no repo to check out first (see
        // ISessionAwareAgentLoopFactory) — this factory owns this session's lifetime.
        var session = await executionRuntimeProvider.CreateSessionAsync(
            new ExecutionSessionRequest("native"), cancellationToken);
        var agentLoop = await BuildAgentLoopAsync(options, session, cancellationToken);
        return new SessionScopedAgentLoop(agentLoop, executionRuntimeProvider, session);
    }

    public Task<IAgentLoop> CreateAsync(
        AgentLoopFactoryOptions options, ExecutionSessionHandle session, CancellationToken cancellationToken = default) =>
        BuildAgentLoopAsync(options, session, cancellationToken);

    private async Task<IAgentLoop> BuildAgentLoopAsync(
        AgentLoopFactoryOptions options, ExecutionSessionHandle session, CancellationToken cancellationToken)
    {
        var chatClient = string.IsNullOrEmpty(options.Provider)
            ? await chatClientFactory.CreateAsync(options.Model, cancellationToken)
            : await chatClientFactory.CreateAsync(options.Provider, options.Model, cancellationToken);

        var codeExecutionTool = AIFunctionFactory.Create(
            new CodeExecutionTools(executionRuntimeProvider, session).RunCommand);

        var agentOptions = new AgentOptions
        {
            SystemPrompt = options.SystemPrompt,
            ModelId = options.Model,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            Tools = [codeExecutionTool],
            // Momos's tool retriever (KeywordToolRetriever) scores tools against the
            // prompt's keywords and drops anything under its relevance threshold —
            // a filter meant for large, discoverable tool sets. The code-execution tool
            // isn't optional or query-dependent: every inspection needs it, so it must
            // always survive retrieval regardless of what the prompt happens to say.
            ToolRetrievalOptions = new ToolRetrievalOptions { AlwaysInclude = [codeExecutionTool.Name] },
        };

        return new AgentLoop(chatClient, agentOptions, usageTracker, contextManager, errorRecovery, toolRetriever);
    }
}
