using System.ComponentModel.DataAnnotations;

namespace Momos.Worker.Execution;

public sealed class HostClientOptions
{
    public const string SectionName = "Momos:Worker:Host";

    [Required]
    public required string BaseUrl { get; set; }

    /// <summary>
    /// Sent as <c>Authorization: Bearer {ApiKey}</c> on every Host request — must match the
    /// Host's own configured worker key or claim-next/report/fail reject with 401.
    /// </summary>
    [Required]
    public required string ApiKey { get; set; }
}
