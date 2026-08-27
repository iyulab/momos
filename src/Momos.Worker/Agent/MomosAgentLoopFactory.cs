using IronHive.Agent.Context;
using IronHive.Agent.ErrorRecovery;
using IronHive.Agent.Loop;
using IronHive.Agent.Providers;
using IronHive.Agent.Tracking;

namespace Momos.Worker.Agent;

/// <summary>
/// Momos's implementation of <c>IronHive.Agent</c>'s <see cref="IAgentLoopFactory"/> —
/// the library registers the surrounding collaborators
/// (<see cref="IUsageTracker"/>, <see cref="ContextManager"/>,
/// <see cref="IErrorRecoveryService"/>) via <c>AddIronHiveAgent()</c> but leaves
/// the top-level factory and chat-client resolution to the consumer.
/// </summary>
public sealed class MomosAgentLoopFactory(
    IChatClientFactory chatClientFactory,
    IUsageTracker usageTracker,
    ContextManager contextManager,
    IErrorRecoveryService errorRecovery,
    IToolRetriever toolRetriever) : IAgentLoopFactory
{
    public Task<IAgentLoop> CreateAsync(CancellationToken cancellationToken = default) =>
        CreateAsync(new AgentLoopFactoryOptions(), cancellationToken);

    public async Task<IAgentLoop> CreateAsync(AgentLoopFactoryOptions options, CancellationToken cancellationToken = default)
    {
        var chatClient = string.IsNullOrEmpty(options.Provider)
            ? await chatClientFactory.CreateAsync(options.Model, cancellationToken)
            : await chatClientFactory.CreateAsync(options.Provider, options.Model, cancellationToken);

        var agentOptions = new AgentOptions
        {
            SystemPrompt = options.SystemPrompt,
            ModelId = options.Model,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
        };

        return new AgentLoop(chatClient, agentOptions, usageTracker, contextManager, errorRecovery, toolRetriever);
    }
}
