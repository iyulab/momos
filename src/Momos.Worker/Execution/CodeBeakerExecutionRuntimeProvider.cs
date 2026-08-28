using CodeBeaker.Commands.Models;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Models;

namespace Momos.Worker.Execution;

/// <summary>
/// Adapts code-beaker's <see cref="ISessionManager"/> to <see cref="IExecutionRuntimeProvider"/>
/// (ADR-0009 decision 1) — a thin wrapper, not a reimplementation: sandboxing, resource
/// limits, and command execution all stay code-beaker's responsibility.
///
/// Not wired into <c>Momos.Worker</c>'s own DI container. Decision 3 (ADR-0009) puts tool
/// invocation in a separate MCP server process per inspection request — code-beaker's
/// <c>InMemorySessionStore</c> is process-local, so a session this class creates must live
/// in that MCP server process, not here. This class is library code the MCP server
/// (once it exists) composes <see cref="ISessionManager"/> around.
/// </summary>
public sealed class CodeBeakerExecutionRuntimeProvider(ISessionManager sessionManager) : IExecutionRuntimeProvider
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
        };

        var session = await sessionManager.CreateSessionAsync(config, cancellationToken);
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
