using System.ComponentModel.DataAnnotations;

namespace Momos.Host.Knowledge;

public sealed class KnowledgeOptions
{
    public const string SectionName = "Momos:Host:Knowledge";

    /// <summary>
    /// Connection-string-style SQLite path for the knowledge index — deliberately a
    /// separate file from <c>ConnectionStrings:MomosDb</c>, not a shared one, so the two
    /// databases never contend for a single SQLite file lock. Defaults to a file alongside the working
    /// directory for local development; production sets this to <c>/data/knowledge.db</c>
    /// the same way <c>ConnectionStrings__MomosDb</c> is set in the Dockerfile.
    /// </summary>
    [Required]
    public string SqlitePath { get; set; } = "knowledge.db";

    /// <summary>
    /// GPUStack's OpenAI-compatible base URL. Optional: when unset, no embedding service is
    /// registered and FluxIndex falls back to its own <c>InMemoryEmbeddingService</c> (real
    /// vectors, not semantically meaningful) — acceptable for local development and tests,
    /// which is exactly why this is optional rather than <c>[Required]</c> the way
    /// <see cref="Momos.Worker.Agent.GpuStackLlmOptions"/> is. Production must set it.
    /// </summary>
    public string? EmbeddingEndpoint { get; set; }

    public string? EmbeddingApiKey { get; set; }

    public string EmbeddingModel { get; set; } = "qwen3-embedding-0.6b";

    /// <summary>
    /// Confirmed empirically for qwen3-embedding-0.6b on GPUStack (2026-09-02): a live
    /// <c>POST /v1/embeddings</c> call returned a 1024-float vector.
    /// </summary>
    public int EmbeddingDimension { get; set; } = 1024;
}
