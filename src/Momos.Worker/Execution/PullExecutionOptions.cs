namespace Momos.Worker.Execution;

public sealed class PullExecutionOptions
{
    public const string SectionName = "Momos:Worker:Pull";

    /// <summary>How often to poll Host for a Pending inspection request when the queue is empty. Fixed interval (ADR-0008 decision 5) — event-based polling is YAGNI until latency is a real complaint.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
