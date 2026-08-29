using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Execution;

/// <summary>
/// Decorates an <see cref="IAgentLoop"/> so the code-execution session backing its
/// native tool closes when the caller is done with the loop.
/// <see cref="IAgentLoop"/> has no disposal contract of its own — IronHive.Agent expects
/// the loop to outlive a single turn — so <see cref="Momos.Worker.Execution.PullExecutionBackgroundService"/>
/// closes the session via the <see cref="IAsyncDisposable"/> this wrapper also implements,
/// matching the one-session-per-inspection-request lifetime the rest of the pipeline uses.
/// </summary>
public sealed class SessionScopedAgentLoop(
    IAgentLoop inner,
    IExecutionRuntimeProvider runtimeProvider,
    ExecutionSessionHandle session) : IAgentLoop, IAsyncDisposable
{
    public IReadOnlyList<ChatMessage> History => inner.History;

    public Task<AgentResponse> RunAsync(string input, CancellationToken cancellationToken = default) =>
        inner.RunAsync(input, cancellationToken);

    public Task<AgentResponse> RunAsync(string input, ChatOptions? chatOptions, CancellationToken cancellationToken = default) =>
        inner.RunAsync(input, chatOptions, cancellationToken);

    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string input, CancellationToken cancellationToken = default) =>
        inner.RunStreamingAsync(input, cancellationToken);

    public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string input, ChatOptions? chatOptions, CancellationToken cancellationToken = default) =>
        inner.RunStreamingAsync(input, chatOptions, cancellationToken);

    public void InitializeHistory(IEnumerable<ChatMessage> messages) => inner.InitializeHistory(messages);

    public void ClearHistory() => inner.ClearHistory();

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        inner.GetHistoryAsync(cancellationToken);

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        inner.ClearHistoryAsync(cancellationToken);

    public Task InitializeHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        inner.InitializeHistoryAsync(messages, cancellationToken);

    public ValueTask DisposeAsync() => new(runtimeProvider.CloseSessionAsync(session));
}
