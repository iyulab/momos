using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public sealed class SessionScopedAgentLoopTests
{
    [Fact]
    public async Task Continue_IsForwardedToTheInnerLoop()
    {
        var inner = new RecordingLoop();
        var loop = new SessionScopedAgentLoop(inner, new FakeExecutionRuntimeProvider(), new ExecutionSessionHandle("s"));

        await loop.ContinueAsync();
        await loop.ContinueAsync(new ChatOptions());
        await foreach (var _ in loop.ContinueStreamingAsync()) { }
        await foreach (var _ in loop.ContinueStreamingAsync(new ChatOptions())) { }

        Assert.Equal(["Continue", "Continue(options)", "ContinueStreaming", "ContinueStreaming(options)"], inner.Calls);
    }

    [Fact]
    public async Task Disposing_ClosesTheSession()
    {
        var runtime = new FakeExecutionRuntimeProvider();
        var session = new ExecutionSessionHandle("s");
        await using (new SessionScopedAgentLoop(new RecordingLoop(), runtime, session)) { }

        Assert.Contains(session, runtime.ClosedSessions);
    }

    private sealed class RecordingLoop : IAgentLoop
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<ChatMessage> History => [];
        public Task<AgentResponse> RunAsync(string input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentResponse> RunAsync(string input, ChatOptions? chatOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentResponseChunk> RunStreamingAsync(string input, ChatOptions? chatOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentResponse> ContinueAsync(CancellationToken cancellationToken = default) { Calls.Add("Continue"); return Task.FromResult(Response()); }
        public Task<AgentResponse> ContinueAsync(ChatOptions? chatOptions, CancellationToken cancellationToken = default) { Calls.Add("Continue(options)"); return Task.FromResult(Response()); }
        public IAsyncEnumerable<AgentResponseChunk> ContinueStreamingAsync(CancellationToken cancellationToken = default) { Calls.Add("ContinueStreaming"); return Empty(); }
        public IAsyncEnumerable<AgentResponseChunk> ContinueStreamingAsync(ChatOptions? chatOptions, CancellationToken cancellationToken = default) { Calls.Add("ContinueStreaming(options)"); return Empty(); }
        public void InitializeHistory(IEnumerable<ChatMessage> messages) { }
        public void ClearHistory() { }
        public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatMessage>>([]);
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InitializeHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static AgentResponse Response() => new() { Content = "" };
        private static async IAsyncEnumerable<AgentResponseChunk> Empty() { await Task.CompletedTask; yield break; }
    }
}
