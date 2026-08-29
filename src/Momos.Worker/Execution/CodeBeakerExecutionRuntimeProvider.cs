using CodeBeaker.Commands.Models;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Models;
using CodeBeaker.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Momos.Worker.Execution;

/// <summary>
/// Adapts code-beaker's <see cref="ISessionManager"/> to <see cref="IExecutionRuntimeProvider"/> —
/// a thin wrapper, not a reimplementation: sandboxing, resource limits, and command
/// execution all stay code-beaker's responsibility.
///
/// Wired directly into <c>Momos.Worker</c>'s own DI container (<c>AddMomosWorker</c>) as a
/// native in-process tool rather than a separate MCP server process, so code-beaker's
/// process-local <c>InMemorySessionStore</c> living in the same process as the sessions it
/// tracks is exactly what this needs.
/// </summary>
public sealed class CodeBeakerExecutionRuntimeProvider(
    ISessionManager sessionManager,
    ILogger<CodeBeakerExecutionRuntimeProvider> logger) : IExecutionRuntimeProvider
{
    /// <summary>
    /// Default posture: sandbox on, filesystem restricted to the session workspace,
    /// network left enabled (checking out the target repo and installing its
    /// dependencies both need it).
    /// </summary>
    public static SecurityConfig DefaultSecurityConfig => new()
    {
        EnableSandbox = true,
        SandboxRestrictFilesystem = true,
        SandboxDisableNetwork = false,
    };

    /// <summary>Suggested default (2GB) — no pilot data yet to tune it.</summary>
    public const long DefaultMemoryLimitMB = 2048;

    public async Task<ExecutionSessionHandle> CreateSessionAsync(
        ExecutionSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = new SessionConfig
        {
            Language = request.Language,
            MemoryLimitMB = DefaultMemoryLimitMB,
            Security = DefaultSecurityConfig,
            // Left unset, code-beaker's RuntimeSelector defaults to Balanced — a scoring
            // formula that weighs startup time and memory overhead so heavily that a
            // near-zero-overhead runtime (NativeProcessRuntime) wins almost regardless of
            // isolation. Security picks the most isolated *available* runtime instead (e.g.
            // Docker, when its daemon is reachable) and still falls back to whatever is
            // available — including native — when nothing more isolated is, so this never
            // makes a pilot unrunnable on a machine without Docker.
            RuntimePreference = RuntimePreference.Security,
        };

        var session = await sessionManager.CreateSessionAsync(config, cancellationToken);

        if (session.RuntimeType == RuntimeType.NativeProcess)
        {
            // NativeProcessRuntime has no sandboxing of its own — a command run in this
            // session is exactly as isolated as any other process on this machine, so an
            // agent-directed command can affect unrelated processes and data on a shared
            // host. Warning and continuing anyway isn't enough to prevent that, so the
            // fallback is refused rather than merely logged. The session is closed first
            // so an unsandboxed process isn't left running for a caller that never
            // receives its handle.
            logger.LogWarning(
                "Refusing session {SessionId} — no isolated runtime (e.g. Docker) was available for {Language}, only the unsandboxed native runtime",
                session.SessionId, request.Language);
            await sessionManager.CloseSessionAsync(session.SessionId, cancellationToken);
            throw new UnisolatedExecutionRuntimeException(
                $"No isolated runtime (e.g. Docker) was available for {request.Language} — refusing to run unsandboxed on the native runtime.");
        }

        return new ExecutionSessionHandle(session.SessionId);
    }

    public async Task<ExecutionCommandResult> ExecuteAsync(
        ExecutionSessionHandle session,
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        var shellCommand = new ExecuteShellCommand
        {
            CommandName = command.Name,
            Args = [.. command.Args],
            WorkingDirectory = command.WorkingDirectory,
        };

        var result = await sessionManager.ExecuteInSessionAsync(session.SessionId, shellCommand, cancellationToken);
        return new ExecutionCommandResult(result.Success, result.Result as string, result.Error, result.DurationMs);
    }

    public Task CloseSessionAsync(ExecutionSessionHandle session, CancellationToken cancellationToken = default) =>
        sessionManager.CloseSessionAsync(session.SessionId, cancellationToken);
}
