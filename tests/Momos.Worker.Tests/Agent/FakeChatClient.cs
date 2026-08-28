using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Minimal <see cref="IChatClient"/> stub that echoes a fixed reply — lets the
/// <c>IronHive.Agent</c> loop itself be exercised without a live LLM provider
/// or network access.
/// </summary>
public sealed class FakeChatClient(string reply) : IChatClient
{
    /// <summary>The <see cref="ChatOptions"/> passed on the most recent call — lets tests
    /// verify a tool survived whatever the agent loop's <c>IToolRetriever</c> does to it
    /// before it would reach a real LLM.</summary>
    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
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
