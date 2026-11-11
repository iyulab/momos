namespace Momos.Host.Endpoints;

public sealed class InspectionClaimOptions
{
    public const string SectionName = "Momos:Host:InspectionClaim";

    /// <summary>
    /// How long a claimed (<see cref="Momos.Host.Domain.InspectionRequestStatus.Running"/>)
    /// inspection request is left alone before claim-next treats its worker as gone and
    /// reclaims it. The longest request is an analysis, which the Worker bounds at 45 minutes
    /// (<c>Momos:Worker:Analysis:MaxDuration</c>), so the default is that plus a quarter hour of
    /// slack: a legitimately slow run isn't reclaimed out from under its worker. The Host cannot
    /// read the Worker's setting, so keep the two apart by configuration. A reclaimed-too-early
    /// request can still run twice, but the report/fail endpoints' Running-only guard stops the
    /// loser from corrupting the result, so the cost of guessing wrong here is wasted work, not
    /// a bad finding. A Worker that renews its claim with the heartbeat route (every five minutes
    /// by default) keeps a long run from being reclaimed at all; this timeout then only has to
    /// outlast a few missed heartbeats, not the longest run, and can be lowered. Sized before
    /// long-run measurements exist; recalibrate after the next one.
    /// </summary>
    public TimeSpan ReclaimTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// How many times one request may be handed to a Worker. A request whose Worker stops each
    /// time (an out-of-memory kill, a crash on that repository) is reclaimed after every
    /// <see cref="ReclaimTimeout"/>; without a limit it is reclaimed for ever and a checkup waiting
    /// on it never completes. Once a request has been claimed this many times and its latest
    /// claim has also gone stale, claim-next fails it with a reason, so the exam behind it ends
    /// as not run and the checkup still produces its report.
    /// </summary>
    public int MaxClaims { get; set; } = 3;
}
