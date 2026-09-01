namespace Momos.Worker.Execution;

/// <summary>
/// Downloads, verifies, and stages a new Worker build, then exits the process. Relaunching is
/// deliberately not done here: the process supervisor (systemd, a Windows Service) is configured to
/// restart the Worker whenever it exits, so an update needs no restart logic of its own and no way
/// to tell a supervisor "this exit was intentional".
/// </summary>
public interface IWorkerSelfUpdater
{
    /// <summary>
    /// Fetches and stages <paramref name="targetVersion"/> (or the latest release when null), then
    /// exits the process via <c>Environment.Exit</c> — so on success this never returns normally.
    /// Returns without doing anything when self-update is switched off, and throws when staging
    /// fails, leaving the caller to decide how loudly to report a run of failures.
    /// </summary>
    Task UpdateAsync(string? targetVersion, CancellationToken cancellationToken);
}
