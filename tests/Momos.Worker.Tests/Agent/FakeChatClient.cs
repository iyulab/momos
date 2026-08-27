using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Minimal <see cref="IChatClient"/> stub that echoes a fixed reply — lets the
/// <c>IronHive.Agent</c> loop itself (ADR-0006) be exercised without a live
/// LLM provider or network access.
/// </summary>
public sealed class FakeChatClient(string reply) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
