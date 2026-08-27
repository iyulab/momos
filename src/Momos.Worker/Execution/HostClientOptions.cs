using System.ComponentModel.DataAnnotations;

namespace Momos.Worker.Execution;

public sealed class HostClientOptions
{
    public const string SectionName = "Momos:Worker:Host";

    [Required]
    public required string BaseUrl { get; set; }
}
