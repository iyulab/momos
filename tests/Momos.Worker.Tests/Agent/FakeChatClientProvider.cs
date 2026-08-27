using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

public sealed class FakeChatClientProvider(string reply) : IChatClientProvider
{
    public string ProviderName => "fake";

    public bool IsAvailable => true;

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IChatClient>(new FakeChatClient(reply));

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvailableModelInfo>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
