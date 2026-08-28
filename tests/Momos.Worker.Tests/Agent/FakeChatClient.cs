using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

/// <summary>
/// Minimal <see cref="IChatClient"/> stub that lets the <c>IronHive.Agent</c> loop itself
/// (and, when wrapped with <c>UseFunctionInvocation()</c>, the function-invocation
/// middleware sitting in front of it) be exercised without a live LLM provider or network
/// access. Scripted with a fixed final reply plus, optionally, a sequence of responses to
/// return before it — a scripted <see cref="FunctionCallContent"/> response followed by a
/// final text response is what proves a tool call actually gets invoked end-to-end.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _scriptedResponses;
    private readonly ChatResponse _finalResponse;

    public FakeChatClient(string reply)
        : this([], new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)))
    {
    }

    public FakeChatClient(IEnumerable<ChatResponse> responsesBeforeFinal, ChatResponse finalResponse)
    {
        _scriptedResponses = new Queue<ChatResponse>(responsesBeforeFinal);
        _finalResponse = finalResponse;
    }

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
        var response = _scriptedResponses.Count > 0 ? _scriptedResponses.Dequeue() : _finalResponse;
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        var response = _scriptedResponses.Count > 0 ? _scriptedResponses.Dequeue() : _finalResponse;
        await Task.Yield();
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
