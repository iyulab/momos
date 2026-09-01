namespace Momos.Worker.Execution;

/// <summary>
/// Downloads, verifies, and stages a new Worker build, then exits the process so the residency
/// mechanism (systemd/Windows Service "always restart") relaunches into it. See
/// PLAN-momos-20260901-worker-host-update-strategy.md 설계 섹션 2 — 실제 재시작 로직은 여기 없다.
/// </summary>
public interface IWorkerSelfUpdater
{
    /// <summary>Fetches and stages <paramref name="targetVersion"/> (or "latest" release if null),
    /// then exits the process via Environment.Exit. Never returns normally on success.</summary>
    Task UpdateAsync(string? targetVersion, CancellationToken cancellationToken);
}
