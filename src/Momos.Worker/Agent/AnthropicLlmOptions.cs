namespace Momos.Worker.Agent;

public sealed class AnthropicLlmOptions
{
    public const string SectionName = "Momos:Llm:Anthropic";

    public required string ApiKey { get; set; }

    public required string Model { get; set; }
}
