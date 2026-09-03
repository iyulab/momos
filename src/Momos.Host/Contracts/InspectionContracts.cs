using Momos.Host.Domain;

namespace Momos.Host.Contracts;

public sealed record CreateInspectionRequestRequest(string? Focus, string? CommitRef);

public sealed record InspectionRequestResponse(
    Guid Id,
    Guid ProjectId,
    string? Focus,
    string? CommitRef,
    DateTimeOffset SubmittedAt,
    InspectionRequestStatus Status,
    string? FailureReason,
    DateTimeOffset? ClaimedAt)
{
    public static InspectionRequestResponse FromEntity(InspectionRequest request) => new(
        request.Id,
        request.ProjectId,
        request.Focus,
        request.CommitRef,
        request.SubmittedAt,
        request.Status,
        request.FailureReason,
        request.ClaimedAt);
}

public sealed record FindingResponse(Guid Id, FindingCategory Category, string Description, string Evidence)
{
    public static FindingResponse FromEntity(Finding finding) => new(
        finding.Id,
        finding.Category,
        finding.Description,
        finding.Evidence);
}

public sealed record ToolCallResponse(Guid Id, string Tool, string Summary, bool Success, int DurationMs)
{
    public static ToolCallResponse FromEntity(ToolCall call) => new(
        call.Id,
        call.Tool,
        call.Summary,
        call.Success,
        call.DurationMs);
}

public sealed record InspectionReportResponse(
    Guid Id,
    Guid InspectionRequestId,
    DateTimeOffset CompletedAt,
    IReadOnlyList<FindingResponse> Findings,
    IReadOnlyList<ToolCallResponse> ToolCalls)
{
    public static InspectionReportResponse FromEntity(InspectionReport report) => new(
        report.Id,
        report.InspectionRequestId,
        report.CompletedAt,
        report.Findings.Select(FindingResponse.FromEntity).ToList(),
        report.ToolCalls.Select(ToolCallResponse.FromEntity).ToList());
}

/// <summary>One finding as submitted by a Worker completing an inspection run.</summary>
public sealed record SubmitFindingRequest(FindingCategory Category, string Description, string Evidence);

/// <summary>One tool invocation as submitted by a Worker completing an inspection run — no
/// <c>Order</c> field, the Host assigns it from submission order (mirrors <see cref="SubmitFindingRequest"/>).</summary>
public sealed record SubmitToolCallRequest(string Tool, string Summary, bool Success, int DurationMs);

/// <summary>A Worker's completed-run submission for one <see cref="InspectionRequest"/>.</summary>
public sealed record SubmitInspectionReportRequest(
    IReadOnlyList<SubmitFindingRequest> Findings,
    IReadOnlyList<SubmitToolCallRequest> ToolCalls);

/// <summary>A Worker's failed-run report for one <see cref="InspectionRequest"/>.</summary>
public sealed record FailInspectionRequestRequest(string Reason);

/// <summary>A Worker's claim-next call, carrying the protocol/build versions it speaks so the
/// Host can refuse work to a too-old Worker before handing out an <see cref="InspectionRequest"/>.</summary>
public sealed record ClaimNextRequest(int ProtocolVersion, string WorkerVersion);

/// <summary>Always-200 envelope for claim-next, replacing the old 200-or-204 status-code
/// signaling: <see cref="Request"/> is null when there's nothing to claim OR when the calling
/// Worker's protocol is unsupported (<see cref="UpdateRequired"/> distinguishes the two).</summary>
public sealed record ClaimNextResponse(InspectionRequestResponse? Request, bool UpdateRequired, string? RecommendedWorkerVersion);
