using System.ComponentModel.DataAnnotations;

namespace Momos.Host.Endpoints;

public sealed class WorkerAuthOptions
{
    public const string SectionName = "Momos:Host:WorkerAuth";

    /// <summary>
    /// Shared secret every Momos.Worker sends as <c>Authorization: Bearer {ApiKey}</c> on the
    /// claim-next/report/fail endpoints. One value for all workers rather than a key per
    /// worker — simplest option for a small worker fleet; per-worker identity can be layered
    /// on later without changing this header-based wire shape.
    /// </summary>
    [Required]
    public required string ApiKey { get; set; }
}
