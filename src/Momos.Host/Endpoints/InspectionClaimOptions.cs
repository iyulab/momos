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
    /// a bad finding. Sized before long-run measurements exist; recalibrate after the next one.
    /// </summary>
    public TimeSpan ReclaimTimeout { get; set; } = TimeSpan.FromMinutes(60);
}
