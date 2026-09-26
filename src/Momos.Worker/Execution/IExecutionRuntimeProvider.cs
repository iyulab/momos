namespace Momos.Worker.Execution;

/// <summary>
/// Momos-owned port over an isolated code-execution sandbox — the same pattern as
/// <c>IChatClientProvider</c>: Momos owns the abstraction,
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
/// no <c>Project</c> schema field backs this yet, so the caller is expected to detect
/// it from the checked-out repository rather than wait on a schema change.
/// </summary>
public sealed record ExecutionSessionRequest(string Language);

public sealed record ExecutionSessionHandle(string SessionId);

/// <summary>
/// One process to run in a session. <paramref name="Environment"/> is set for this process
/// only — it may carry a secret, so <see cref="ToString"/> names its variables and never
/// their values.
/// </summary>
public sealed record ExecutionCommand(
    string Name,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Args = [{string.Join(", ", Args)}], WorkingDirectory = {WorkingDirectory}");
        if (Environment is { Count: > 0 })
        {
            builder.Append($", Environment = [{string.Join(", ", Environment.Keys)}]");
        }

        return true;
    }
}

public sealed record ExecutionCommandResult(
    bool Success,
    string? Output,
    string? Error,
    int DurationMs);
