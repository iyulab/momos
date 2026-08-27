using System.ComponentModel.DataAnnotations;

namespace Momos.Worker.Agent;

public sealed class AnthropicLlmOptions
{
    public const string SectionName = "Momos:Llm:Anthropic";

    [Required]
    public required string ApiKey { get; set; }

    [Required]
    public required string Model { get; set; }
}
