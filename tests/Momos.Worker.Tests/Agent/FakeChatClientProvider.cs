using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

public sealed class FakeChatClientProvider(string reply) : IChatClientProvider
{
    /// <summary>The most recently created client — lets a test inspect what the agent
    /// loop actually sent it (e.g. <see cref="FakeChatClient.LastOptions"/>) after a run.</summary>
    public FakeChatClient? LastClient { get; private set; }

    public string ProviderName => "fake";

    public bool IsAvailable => true;

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        LastClient = new FakeChatClient(reply);
        return Task.FromResult<IChatClient>(LastClient);
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvailableModelInfo>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
