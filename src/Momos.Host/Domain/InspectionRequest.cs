namespace Momos.Host.Domain;

public enum InspectionRequestStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// A single, lightweight trigger for one inspection run against a <see cref="Project"/>.
/// </summary>
public sealed class InspectionRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; set; }
    public string? Focus { get; set; }
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
    public InspectionRequestStatus Status { get; set; } = InspectionRequestStatus.Pending;
}
