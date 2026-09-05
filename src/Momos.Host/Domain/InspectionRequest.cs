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

    /// <summary>
    /// Git ref (commit SHA, tag, branch) to check out after cloning <see cref="Project.RepositoryUrl"/>,
    /// instead of whatever the clone's default branch resolves to. Lives on the request, not the
    /// project — pinning is a per-run concern (e.g. re-running an inspection against the exact
    /// commit a known defect was reproduced at), while <see cref="Project"/> is a stable declaration
    /// that changes rarely.
    /// </summary>
    public string? CommitRef { get; set; }

    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;
    public InspectionRequestStatus Status { get; set; } = InspectionRequestStatus.Pending;
    public string? FailureReason { get; set; }

    /// <summary>
    /// When claim-next last transitioned this request to <see cref="InspectionRequestStatus.Running"/>.
    /// Null until first claimed. Used to reclaim a request whose worker went away mid-run (no
    /// heartbeat exists to signal "still alive but slow" vs. "dead" — see claim-next's
    /// ReclaimTimeout) — not a general last-activity timestamp.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Every tool call the agent loop made while running this request, regardless of
    /// whether the run ended in an <see cref="InspectionReport"/> or a failure.</summary>
    public ICollection<ToolCall> ToolCalls { get; init; } = new List<ToolCall>();
}
