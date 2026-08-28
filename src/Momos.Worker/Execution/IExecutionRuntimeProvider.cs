namespace Momos.Worker.Execution;

/// <summary>
/// Momos-owned port over an isolated code-execution sandbox (ADR-0009 decision 1) —
/// the same pattern as <c>IChatClientProvider</c>: Momos owns the abstraction,
/// code-beaker (or any future sandbox) adapts to it, so the domain never depends
/// on an upstream provider's types directly.
/// </summary>
public interface IExecutionRuntimeProvider
{
    Task<ExecutionSessionHandle> CreateSessionAsync(
        ExecutionSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionCommandResult> ExecuteAsync(
        ExecutionSessionHandle session,
        ExecutionCommand command,
        CancellationToken cancellationToken = default);

    Task CloseSessionAsync(
        ExecutionSessionHandle session,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <paramref name="Language"/> selects the sandbox environment (e.g. "python", "node") —
/// no <c>Project</c> schema field backs this yet (ADR-0009 "잠금 효과"); the caller
/// is expected to detect it from the checked-out repository rather than wait on a
/// schema change.
/// </summary>
public sealed record ExecutionSessionRequest(string Language);

public sealed record ExecutionSessionHandle(string SessionId);

public sealed record ExecutionCommand(
    string Name,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null);

public sealed record ExecutionCommandResult(
    bool Success,
    string? Output,
    string? Error,
    int DurationMs);
