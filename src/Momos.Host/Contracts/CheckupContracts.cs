using Momos.Host.Domain;

namespace Momos.Host.Contracts;

/// <summary>Both fields are optional: without a language the project's, then the Host's default, applies.</summary>
public sealed record CreateCheckupRequest(string? CommitRef = null, string? Language = null);

public sealed record ExamRunResponse(string Program, Guid RequestId, ExamRunStatus Status, string? Reason, int? ModelVersion);

public sealed record CheckupResponse(
    Guid Id,
    Guid ProjectId,
    string Language,
    string? CommitRef,
    string? BaseCommit,
    int? ModelVersion,
    CheckupStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ExamRunResponse> Exams)
{
    /// <param name="checkup">The checkup, with its exams loaded.</param>
    /// <param name="modelVersions">Project model id to its version, for the exams that produced a model.</param>
    public static CheckupResponse FromEntity(Checkup checkup, IReadOnlyDictionary<Guid, int> modelVersions) => new(
        checkup.Id,
        checkup.ProjectId,
        checkup.Language,
        checkup.CommitRef,
        checkup.BaseCommit,
        checkup.ModelVersion,
        checkup.Status,
        checkup.CreatedAt,
        checkup.CompletedAt,
        checkup.Exams
            .Select(e => new ExamRunResponse(
                e.Program,
                e.RequestId,
                e.Status,
                e.Reason,
                e.ProjectModelId is { } modelId && modelVersions.TryGetValue(modelId, out var version) ? version : null))
            .ToList());
}

/// <summary>A finished checkup's report: markdown documents with relative paths, <c>index.md</c> first.</summary>
public sealed record CheckupReportResponse(Guid CheckupId, string Language, IReadOnlyList<ReportDocumentDto> Documents);
