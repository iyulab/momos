using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

/// <summary>Mirrors the FakeSessionManager pattern — no code-beaker runtime required to test agent-loop wiring.</summary>
public sealed class FakeExecutionRuntimeProvider : IExecutionRuntimeProvider
{
    public List<ExecutionSessionRequest> CreatedSessions { get; } = [];
    public List<ExecutionSessionHandle> ClosedSessions { get; } = [];
    public List<(ExecutionSessionHandle Session, ExecutionCommand Command)> ExecutedCommands { get; } = [];
    public (ExecutionSessionHandle Session, ExecutionCommand Command)? LastExecuted { get; private set; }
    public ExecutionCommandResult NextResult { get; set; } = new(true, "ok", null, 1);

    public Task<ExecutionSessionHandle> CreateSessionAsync(
        ExecutionSessionRequest request, CancellationToken cancellationToken = default)
    {
        CreatedSessions.Add(request);
        return Task.FromResult(new ExecutionSessionHandle($"fake-session-{CreatedSessions.Count}"));
    }

    public Task<ExecutionCommandResult> ExecuteAsync(
        ExecutionSessionHandle session, ExecutionCommand command, CancellationToken cancellationToken = default)
    {
        LastExecuted = (session, command);
        ExecutedCommands.Add((session, command));
        return Task.FromResult(NextResult);
    }

    public Task CloseSessionAsync(ExecutionSessionHandle session, CancellationToken cancellationToken = default)
    {
        ClosedSessions.Add(session);
        return Task.CompletedTask;
    }
}
