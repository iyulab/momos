namespace Momos.Host.Domain;

public enum FindingCategory
{
    FunctionalDefect,
    UxConsistency,
}

/// <summary>
/// One reported observation within an <see cref="InspectionReport"/>. Never
/// carries raw evidence artifacts (screenshots/logs) — those stay on the Worker;
/// only the written-up evidence text travels here.
/// </summary>
public sealed class Finding
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid InspectionReportId { get; set; }
    public FindingCategory Category { get; set; }
    public required string Description { get; set; }
    public required string Evidence { get; set; }

    /// <summary>
    /// Position within the report as submitted by the Worker (0-based). Findings are
    /// returned ordered by this field — SQLite/EF give no ordering guarantee for an
    /// unordered <c>Include</c>, and a report can now legitimately carry more than one.
    /// </summary>
    public int Order { get; set; }
}
