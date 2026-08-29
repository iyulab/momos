namespace Momos.Host.Endpoints;

public sealed class InspectionClaimOptions
{
    public const string SectionName = "Momos:Host:InspectionClaim";

    /// <summary>
    /// How long a claimed (<see cref="Momos.Host.Domain.InspectionRequestStatus.Running"/>)
    /// inspection request is left alone before claim-next treats its worker as gone and
    /// reclaims it. No inspection-duration data exists yet (walking-skeleton stage, the agent
    /// loop has no turn/time cap of its own) — generous default so a legitimately slow run
    /// isn't reclaimed out from under its worker. A reclaimed-too-early request can still run
    /// twice, but the report/fail endpoints' Running-only guard stops the loser from
    /// corrupting the result, so the cost of guessing wrong here is wasted work, not a bad
    /// finding. Revisit once golden-set runs give real duration data.
    /// </summary>
    public TimeSpan ReclaimTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
