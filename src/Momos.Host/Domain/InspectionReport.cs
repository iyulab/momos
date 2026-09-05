namespace Momos.Host.Domain;

/// <summary>
/// The 1:1 outcome of one <see cref="InspectionRequest"/>. Tool-call traces are not a report
/// property — they belong to the request itself (<see cref="InspectionRequest.ToolCalls"/>),
/// since the agent loop makes the same calls whether the run ends in a report or a failure.
/// </summary>
public sealed class InspectionReport
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid InspectionRequestId { get; set; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    public ICollection<Finding> Findings { get; init; } = new List<Finding>();
}
