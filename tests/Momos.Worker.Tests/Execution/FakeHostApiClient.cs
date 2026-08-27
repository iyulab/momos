using System.Net.Http;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

/// <summary>Records what <see cref="Momos.Worker.Execution.PullExecutionBackgroundService"/> submits, and signals when an outcome (report or failure) lands.</summary>
internal sealed class FakeHostApiClient(IReadOnlyList<ClaimedInspectionRequest> claims, bool getProjectThrows = false, int claimNextThrowsForFirstNCalls = 0) : IHostApiClient
{
    private int _claimIndex;
    private int _claimCallCount;
    private readonly TaskCompletionSource _outcomeReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<SubmitReportRequest> SubmittedReports { get; } = [];
    public List<(Guid Id, string Reason)> SubmittedFailures { get; } = [];

    public Task<ClaimedInspectionRequest?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        _claimCallCount++;
        if (_claimCallCount <= claimNextThrowsForFirstNCalls)
        {
            throw new HttpRequestException("connection refused");
        }

        return Task.FromResult(_claimIndex < claims.Count ? claims[_claimIndex++] : null);
    }

    public Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        getProjectThrows
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(new ProjectInfo(projectId, "acme", null, null, "purpose", "vision", "scope"));

    public Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, CancellationToken cancellationToken)
    {
        SubmittedReports.Add(new SubmitReportRequest(findings));
        _outcomeReceived.TrySetResult();
        return Task.CompletedTask;
    }

    public Task SubmitFailureAsync(Guid inspectionRequestId, string reason, CancellationToken cancellationToken)
    {
        SubmittedFailures.Add((inspectionRequestId, reason));
        _outcomeReceived.TrySetResult();
        return Task.CompletedTask;
    }

    public Task WaitForOutcomeAsync(TimeSpan timeout) => _outcomeReceived.Task.WaitAsync(timeout);
}
