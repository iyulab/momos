namespace Momos.Host.Domain;

public enum CheckupStatus
{
    Running,
    Completed,
}

/// <summary>How one exam program of a checkup ended. <see cref="Partial"/>: it produced results but
/// some of its work was cut (a budget ran out); <see cref="NotRun"/>: it produced nothing, with a reason.</summary>
public enum ExamRunStatus
{
    Pending,
    Completed,
    Partial,
    NotRun,
}

public static class ExamProgram
{
    /// <summary>The design analysis: the project model and its engineering manual.</summary>
    public const string DesignAnalysis = "design-analysis";

    /// <summary>Static quality: findings from linters and analyzers. No checkup runs it yet.</summary>
    public const string StaticQuality = "static-quality";

    /// <summary>Convention conformance: the code checked against the project's own conventions. No checkup runs it yet.</summary>
    public const string ConventionConformance = "convention-conformance";

    /// <summary>Every program a checkup can list, in report order. A program a checkup did not
    /// include is still shown in its report — as not included, never as passed.</summary>
    public static IReadOnlyList<string> All { get; } = [DesignAnalysis, StaticQuality, ConventionConformance];
}

/// <summary>
/// A health checkup of a project: one request per exam program on the shared queue, and a report
/// assembled from what the programs produced once every one of them has ended.
/// </summary>
public sealed class Checkup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string Language { get; init; }
    public string? CommitRef { get; init; }

    /// <summary>The commit the design analysis read; known once it has submitted its model.</summary>
    public string? BaseCommit { get; set; }

    /// <summary>The project model version the design analysis produced.</summary>
    public int? ModelVersion { get; set; }

    public CheckupStatus Status { get; set; } = CheckupStatus.Running;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<ExamRun> Exams { get; init; } = [];
}

public sealed class ExamRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CheckupId { get; init; }
    public required string Program { get; init; }

    /// <summary>The queued request that runs this program.</summary>
    public required Guid RequestId { get; init; }

    public ExamRunStatus Status { get; set; } = ExamRunStatus.Pending;
    public string? Reason { get; set; }
    public Guid? ProjectModelId { get; set; }
}
