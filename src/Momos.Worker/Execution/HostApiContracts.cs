namespace Momos.Worker.Execution;

/// <summary>
/// Mirrors <c>Momos.Host.Domain.InspectionRequestStatus</c>'s wire shape (named
/// values, via <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// on both sides — see <c>HostApiClient.JsonOptions</c>). Kept as a local copy
/// rather than a shared reference — Worker and Host are separate deployables by
/// design.
/// </summary>
public enum InspectionRequestStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

public sealed record ClaimedInspectionRequest(
    Guid Id,
    Guid ProjectId,
    string? Focus,
    string? CommitRef,
    DateTimeOffset SubmittedAt,
    InspectionRequestStatus Status,
    string? FailureReason);

public sealed record ProjectInfo(
    Guid Id,
    string Name,
    string? RepositoryUrl,
    string? DeploymentUrl,
    string Purpose,
    string Vision,
    string Scope,
    string? AppInstallerUri = null,
    string? AppInstallPlatform = null,
    string? AppInstallArgs = null,
    string? AppInstallLaunchCommand = null);

/// <summary>
/// Mirrors <c>Momos.Host.Domain.FindingCategory</c>'s wire shape — see
/// <see cref="InspectionRequestStatus"/> for why this is a local copy rather than a
/// shared reference.
/// </summary>
public enum FindingCategory
{
    FunctionalDefect,
    UxConsistency,
}

public sealed record FindingPayload(FindingCategory Category, string Description, string Evidence);

public sealed record SubmitReportRequest(IReadOnlyList<FindingPayload> Findings);

public sealed record SubmitFailureRequest(string Reason);

public sealed record ClaimNextRequest(int ProtocolVersion, string WorkerVersion);

/// <summary>Mirrors Host's ClaimNextResponse wire shape (Momos.Host.Contracts.ClaimNextResponse) —
/// see ClaimedInspectionRequest's doc comment for why this is a local copy.</summary>
public sealed record ClaimNextResult(ClaimedInspectionRequest? Request, bool UpdateRequired, string? RecommendedWorkerVersion);
