namespace Momos.Worker.Execution;

public sealed class PullExecutionOptions
{
    public const string SectionName = "Momos:Worker:Pull";

    /// <summary>How often to poll Host for a Pending inspection request when the queue is empty. Fixed interval — event-based polling is YAGNI until latency is a real complaint.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often a Worker tells Host it is still running the request it claimed, which keeps Host
    /// from reclaiming a slow but live run after <c>Momos:Host:InspectionClaim:ReclaimTimeout</c>.
    /// Zero turns it off. Keep it well below that timeout (five minutes against an hour by default).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Consecutive pull-loop-iteration failures (e.g. Host unreachable) after which the
    /// per-iteration error log is downgraded to a single critical log and then suppressed
    /// until the loop next succeeds. The loop itself never stops retrying — this only caps
    /// log volume once a prolonged outage has already been made loud once. Default of 12
    /// is a provisional number (no production incident data yet): at the default
    /// <see cref="PollInterval"/> of 5 seconds that's about a minute of visible retries
    /// before going quiet.
    /// </summary>
    public int ConsecutiveFailureLogThreshold { get; set; } = 12;
}
