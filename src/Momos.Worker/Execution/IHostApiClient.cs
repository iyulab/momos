namespace Momos.Worker.Execution;

/// <summary>Worker-side client for the subset of the Host API the pull loop needs (ADR-0008).</summary>
public interface IHostApiClient
{
    /// <summary>Atomically claims the oldest Pending inspection request, or null if none is pending.</summary>
    Task<ClaimedInspectionRequest?> ClaimNextAsync(CancellationToken cancellationToken);

    Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken);

    Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, CancellationToken cancellationToken);

    Task SubmitFailureAsync(Guid inspectionRequestId, string reason, CancellationToken cancellationToken);
}
