using CodeBeaker.Commands.Models;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Models;
using CodeBeaker.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Momos.Worker.Execution;

/// <summary>
/// Adapts code-beaker's <see cref="ISessionManager"/> to <see cref="IExecutionRuntimeProvider"/>
/// (ADR-0009 decision 1) — a thin wrapper, not a reimplementation: sandboxing, resource
/// limits, and command execution all stay code-beaker's responsibility.
///
/// Wired directly into <c>Momos.Worker</c>'s own DI container (<c>AddMomosWorker</c>) —
/// decision 3 (ADR-0009) resolved to B (native in-process tool registration, not a
/// separate MCP server process), so code-beaker's process-local <c>InMemorySessionStore</c>
/// living in the same process as the sessions it tracks is exactly what this needs.
/// </summary>
public sealed class CodeBeakerExecutionRuntimeProvider(
    ISessionManager sessionManager,
    ILogger<CodeBeakerExecutionRuntimeProvider> logger) : IExecutionRuntimeProvider
{
    /// <summary>
    /// ADR-0009 decision 2's default posture: sandbox on, filesystem restricted to the
    /// session workspace, network left enabled (checking out the target repo and
    /// installing its dependencies both need it).
    /// </summary>
    public static SecurityConfig DefaultSecurityConfig => new()
    {
        EnableSandbox = true,
        SandboxRestrictFilesystem = true,
        SandboxDisableNetwork = false,
    };

    /// <summary>ADR-0009 decision 2's suggested default (2GB) — no pilot data yet to tune it.</summary>
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
            // session is exactly as isolated as any other process on this machine, which
            // is what let a real inspection run kill every dotnet.exe process on a shared
            // host (this session's own Worker included) instead of just its own sandbox.
            logger.LogWarning(
                "Session {SessionId} is running on the unsandboxed native runtime — no more isolated runtime (e.g. Docker) was available for {Language}",
                session.SessionId, request.Language);
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
