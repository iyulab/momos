namespace Momos.Host.Domain;

/// <summary>
/// One tool invocation an agent loop made while producing an <see cref="InspectionReport"/> —
/// a shell command run or a knowledge query, recorded regardless of outcome. Never carries raw
/// command output/error text (that can contain secrets from the inspected target); only a short
/// summary of what was invoked travels here.
/// </summary>
public sealed class ToolCall
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid InspectionReportId { get; set; }
    public required string Tool { get; set; }
    public required string Summary { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }

    /// <summary>Position within the report as submitted by the Worker (0-based) — same
    /// ordering contract as <see cref="Finding.Order"/>.</summary>
    public int Order { get; set; }
}
