using System.ComponentModel.DataAnnotations;

namespace Momos.Host.Knowledge;

public sealed class KnowledgeOptions
{
    public const string SectionName = "Momos:Host:Knowledge";

    /// <summary>
    /// PostgreSQL 연결 문자열 — momos.db(EF Core)와 별도 데이터베이스(momos_knowledge)를
    /// 가리킨다. 같은 서버라도 데이터베이스를 분리해 두 컴포넌트가 스키마를 공유하지 않게
    /// 한다(momos.db와 분리해 두던 기존 SQLite 시절의 의도를 그대로 유지).
    /// </summary>
    [Required]
    public string ConnectionString { get; set; } = "";

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
