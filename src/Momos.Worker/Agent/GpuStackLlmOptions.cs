using System.ComponentModel.DataAnnotations;

namespace Momos.Worker.Agent;

public sealed class GpuStackLlmOptions
{
    public const string SectionName = "Momos:Llm:GpuStack";

    [Required]
    public required string Endpoint { get; set; }

    [Required]
    public required string ApiKey { get; set; }

    [Required]
    public required string Model { get; set; }
}
