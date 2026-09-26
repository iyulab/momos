using Microsoft.Extensions.Options;

namespace Momos.Host.Knowledge;

/// <summary>
/// Rejects embedding settings that would otherwise fall back silently to FluxIndex's in-memory
/// embedder, whose vectors carry no meaning: an API key with no endpoint (the endpoint was
/// forgotten), and no endpoint at all outside development.
/// </summary>
public sealed class KnowledgeOptionsValidator(IHostEnvironment environment) : IValidateOptions<KnowledgeOptions>
{
    public ValidateOptionsResult Validate(string? name, KnowledgeOptions options)
    {
        var hasEndpoint = !string.IsNullOrWhiteSpace(options.EmbeddingEndpoint);

        if (!hasEndpoint && !string.IsNullOrWhiteSpace(options.EmbeddingApiKey))
        {
            return ValidateOptionsResult.Fail(
                $"{KnowledgeOptions.SectionName}:EmbeddingApiKey is set but {KnowledgeOptions.SectionName}:EmbeddingEndpoint is not. " +
                "Set the endpoint the key belongs to.");
        }

        if (!hasEndpoint && !environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail(
                $"{KnowledgeOptions.SectionName}:EmbeddingEndpoint is required outside Development. " +
                "Without it the knowledge index uses an in-memory embedder whose vectors are not semantically meaningful.");
        }

        return ValidateOptionsResult.Success;
    }
}
