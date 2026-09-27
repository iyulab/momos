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
    /// The value is maintained by hand to match the protocol contract the Host ships with; a test
    /// pins the Worker's own protocol constant to this default, so bumping the wire contract
    /// means bumping both. Protocol 3 added the request <c>Kind</c> to claim-next: a protocol-2
    /// Worker would run an analysis request as an inspection. Protocol 4 made the model
    /// submission carry flows, invariants, an outline and the analysis coverage: a protocol-3
    /// Worker would be refused only at submission, after the whole analysis.
    /// </summary>
    public int MinSupportedProtocolVersion { get; set; } = 4;

    /// <summary>Soft update hint returned only when it differs from the Worker's reported
    /// WorkerVersion. Null (the default) means "unset" — an operator sets this when cutting a
    /// new worker release.</summary>
    public string? RecommendedWorkerVersion { get; set; }
}
