using IronHive.Agent.Providers;
using Microsoft.Extensions.AI;

namespace Momos.Worker.IntegrationTests.Analysis;

/// <summary>One scripted conversation per agent loop, in order: each is the list of replies the
/// model "gives", the last being its final answer.</summary>
internal sealed class ScriptedChatClientProvider(params IReadOnlyList<ChatResponse>[] passes) : IChatClientProvider
{
    private int _next;

    public string ProviderName => "scripted";

    public bool IsAvailable => true;

    public Task<IChatClient> GetChatClientAsync(string? modelOverride = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IChatClient>(new Scripted(new Queue<ChatResponse>(passes[Math.Min(_next++, passes.Length - 1)])));

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvailableModelInfo>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Scripted(Queue<ChatResponse> replies) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(replies.Count > 1 ? replies.Dequeue() : replies.Peek());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
