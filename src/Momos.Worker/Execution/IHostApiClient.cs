namespace Momos.Worker.Execution;

/// <summary>Worker-side client for the subset of the Host API the pull loop needs.</summary>
public interface IHostApiClient
{
    /// <summary>Atomically claims the oldest Pending inspection request, or a result with a null
    /// Request if none is pending or this Worker's protocol version is no longer supported.</summary>
    Task<ClaimNextResult> ClaimNextAsync(CancellationToken cancellationToken);

    Task<ProjectInfo> GetProjectAsync(Guid projectId, CancellationToken cancellationToken);

    Task SubmitReportAsync(Guid inspectionRequestId, IReadOnlyList<FindingPayload> findings, IReadOnlyList<ToolCallPayload> toolCalls, CancellationToken cancellationToken);

    /// <summary>Completes a claimed analysis request with the project model extracted from it —
    /// the analysis counterpart of <see cref="SubmitReportAsync"/>.</summary>
    Task SubmitModelAsync(Guid analysisRequestId, ProjectModelPayload model, CancellationToken cancellationToken);

    Task SubmitFailureAsync(Guid inspectionRequestId, string reason, IReadOnlyList<ToolCallPayload> toolCalls, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> QueryKnowledgeAsync(Guid projectId, string query, CancellationToken cancellationToken);
}
