namespace Momos.Host.Endpoints;

/// <summary>
/// Compatibility contract between Host and Worker for claim-next. There is no version-skew
/// tolerance policy: a Worker below <see cref="MinSupportedProtocolVersion"/> is refused work
/// outright, with no grace window.
/// </summary>
public sealed class WorkerCompatibilityOptions
{
    public const string SectionName = "Momos:Host:WorkerCompatibility";

    /// <summary>
    /// The value is maintained by hand to match the protocol contract the Host ships with —
    /// nothing enforces the pairing, so bumping the wire contract requires bumping this too.
    /// </summary>
    public int MinSupportedProtocolVersion { get; set; } = 1;

    /// <summary>Soft update hint returned only when it differs from the Worker's reported
    /// WorkerVersion. Null (the default) means "unset" — an operator sets this when cutting a
    /// new worker release.</summary>
    public string? RecommendedWorkerVersion { get; set; }
}
