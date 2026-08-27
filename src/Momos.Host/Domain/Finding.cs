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
}
