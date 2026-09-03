namespace Momos.Host.Domain;

/// <summary>
/// The 1:1 outcome of one <see cref="InspectionRequest"/>.
/// </summary>
public sealed class InspectionReport
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid InspectionRequestId { get; set; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    public ICollection<Finding> Findings { get; init; } = new List<Finding>();
    public ICollection<ToolCall> ToolCalls { get; init; } = new List<ToolCall>();
}
