using System.Net.Http;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

/// <summary>Records what <see cref="Momos.Worker.Execution.PullExecutionBackgroundService"/> submits, and signals when an outcome (report or failure) lands.</summary>
internal sealed class FakeHostApiClient(
    IReadOnlyList<ClaimedInspectionRequest> claims,
    bool getProjectThrows = false,
    int claimNextThrowsForFirstNCalls = 0,
    int claimNextThrowsCanceledForFirstNCalls = 0,
    string? repositoryUrl = null,
    int updateRequiredForFirstNCalls = 0,
    string? recommendedWorkerVersion = null) : IHostApiClient
{
    private int _claimIndex;
    private int _claimCallCount;
    private readonly TaskCompletionSource _outcomeReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<SubmitReportRequest> SubmittedReports { get; } = [];
    public List<(Guid Id, string Reason)> SubmittedFailures { get; } = [];

    /// <summary>Total <see cref="ClaimNextAsync"/> calls so far, successful or throwing — lets a test condition-wait on the pull loop having reached a given iteration instead of sleeping an arbitrary duration.</summary>
    public int ClaimCallCount => _claimCallCount;

    public Task<ClaimNextResult> ClaimNextAsync(CancellationToken cancellationToken)
    {
        _claimCallCount++;
        if (_claimCallCount <= claimNextThrowsForFirstNCalls)
        {
            throw new HttpRequestException("connection refused");
        }

        if (_claimCallCount <= claimNextThrowsCanceledForFirstNCalls)
        {
            // Simulates an HttpClient.Timeout expiring mid-request: a TaskCanceledException
            // (an OperationCanceledException) unrelated to the loop's own stoppingToken —
            // it must be treated as a transient failure, not as a shutdown signal.
            throw new TaskCanceledException("simulated HttpClient timeout");
        }

        if (_claimCallCount <= updateRequiredForFirstNCalls)
        {
            return Task.FromResult(new ClaimNextResult(Request: null, UpdateRequired: true, recommendedWorkerVersion));
        }

        var request = _claimIndex < claims.Count ? claims[_claimIndex++] : null;
        return Task.FromResult(new ClaimNextResult(request, UpdateRequired: false, recommendedWorkerVersion));
    }

    public Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        getProjectThrows
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(new ProjectInfo(projectId, "acme", repositoryUrl, null, "purpose", "vision", "scope"));

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
