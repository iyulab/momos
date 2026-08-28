using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

public sealed class FakeChatClientProvider : IChatClientProvider
{
    private readonly Func<FakeChatClient> _clientFactory;

    public FakeChatClientProvider(string reply) : this(() => new FakeChatClient(reply))
    {
    }

    public FakeChatClientProvider(IEnumerable<ChatResponse> responsesBeforeFinal, ChatResponse finalResponse)
        : this(() => new FakeChatClient(responsesBeforeFinal, finalResponse))
    {
    }

    private FakeChatClientProvider(Func<FakeChatClient> clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>The most recently created client — lets a test inspect what the agent
    /// loop actually sent it (e.g. <see cref="FakeChatClient.LastOptions"/>) after a run.</summary>
    public FakeChatClient? LastClient { get; private set; }

    public string ProviderName => "fake";

    public bool IsAvailable => true;

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default)
    {
        LastClient = _clientFactory();
        return Task.FromResult<IChatClient>(LastClient);
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvailableModelInfo>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
