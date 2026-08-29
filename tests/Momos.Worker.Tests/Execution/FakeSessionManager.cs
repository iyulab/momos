using CodeBeaker.Commands.Models;
using CodeBeaker.Core.Interfaces;
using CodeBeaker.Core.Models;

namespace Momos.Worker.Tests.Execution;

/// <summary>Mirrors the FakeChatClientProvider pattern (Agent/FakeChatClientProvider.cs) — no code-beaker runtime required to test the adapter's own logic.</summary>
public sealed class FakeSessionManager : ISessionManager
{
    public SessionConfig? LastCreatedConfig { get; private set; }
    public (string SessionId, Command Command)? LastExecuted { get; private set; }
    public string? LastClosedSessionId { get; private set; }
    public CommandResult NextResult { get; set; } = CommandResult.Ok("ok");
    public RuntimeType NextRuntimeType { get; set; } = RuntimeType.Docker;

    public Task<Session> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken = default)
    {
        LastCreatedConfig = config;
        return Task.FromResult(new Session { SessionId = "fake-session-id", Language = config.Language, RuntimeType = NextRuntimeType });
    }

    public Task<Session?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Session?>(null);

    public Task<CommandResult> ExecuteInSessionAsync(
        string sessionId, Command command, CancellationToken cancellationToken = default)
    {
        LastExecuted = (sessionId, command);
        return Task.FromResult(NextResult);
    }

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        LastClosedSessionId = sessionId;
        return Task.CompletedTask;
    }

    public Task<List<Session>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<Session>());

    public Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ResourceUsage?> GetSessionResourceUsageAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ResourceUsage?>(null);
}
