using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

/// <summary>Records UpdateAsync calls without exiting the test process.</summary>
internal sealed class FakeWorkerSelfUpdater : IWorkerSelfUpdater
{
    public List<string?> RequestedVersions { get; } = [];

    public async Task UpdateAsync(string? targetVersion, CancellationToken cancellationToken)
    {
        RequestedVersions.Add(targetVersion);

        // The real IWorkerSelfUpdater.UpdateAsync "never returns normally on success" (it exits
        // the process instead) — this fake honors that contract by blocking here until the test
        // stops the service, rather than returning immediately. Returning immediately let the
        // pull loop race ahead into its next iteration and issue a second self-update request
        // for the same still-outstanding recommendation before the test could observe and stop
        // it after the first.
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
