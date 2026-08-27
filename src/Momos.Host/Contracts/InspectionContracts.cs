using Momos.Host.Domain;

namespace Momos.Host.Contracts;

public sealed record CreateInspectionRequestRequest(string? Focus);

public sealed record InspectionRequestResponse(
    Guid Id,
    Guid ProjectId,
    string? Focus,
    DateTimeOffset SubmittedAt,
    InspectionRequestStatus Status)
{
    public static InspectionRequestResponse FromEntity(InspectionRequest request) => new(
        request.Id,
        request.ProjectId,
        request.Focus,
        request.SubmittedAt,
        request.Status);
}

public sealed record FindingResponse(Guid Id, FindingCategory Category, string Description, string Evidence)
{
    public static FindingResponse FromEntity(Finding finding) => new(
        finding.Id,
        finding.Category,
        finding.Description,
        finding.Evidence);
}

public sealed record InspectionReportResponse(
    Guid Id,
    Guid InspectionRequestId,
    DateTimeOffset CompletedAt,
    IReadOnlyList<FindingResponse> Findings)
{
    public static InspectionReportResponse FromEntity(InspectionReport report) => new(
        report.Id,
        report.InspectionRequestId,
        report.CompletedAt,
        report.Findings.Select(FindingResponse.FromEntity).ToList());
}
