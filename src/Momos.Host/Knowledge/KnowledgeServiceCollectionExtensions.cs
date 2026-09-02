using FluxIndex.Providers.OpenAI.Extensions;
using FluxIndex.SDK;
using FluxIndex.Storage.SQLite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Momos.Host.Knowledge;

public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeIndex(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value;

            var builder = FluxIndexContext.CreateBuilder()
                .UseSQLite(options.SqlitePath)
                .ConfigureServices(s =>
                {
                    // FluxIndex builds its own internal service provider, separate from
                    // ASP.NET Core's — it needs its own logging registration rather than
                    // forwarding the host's ILoggerFactory.
                    s.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
                    if (!string.IsNullOrEmpty(options.EmbeddingEndpoint))
                    {
                        s.AddOpenAICompatibleEmbedding(
                            options.EmbeddingEndpoint, options.EmbeddingApiKey,
                            options.EmbeddingModel, options.EmbeddingDimension);
                    }
                    // No EmbeddingEndpoint configured: FluxIndex falls back to its own
                    // InMemoryEmbeddingService (see KnowledgeOptions.EmbeddingEndpoint doc).
                });
            // Confirmed empirically (KnowledgeBootstrapTests): these suppress schema
            // provisioning but not the companion entity-graph file's creation — see the
            // implementation plan's Global Constraints.
            builder.Options.GraphStore.AutoMigrate = false;
            builder.Options.SemanticCache.AutoMigrate = false;

            return builder.AddSQLiteStorage().Build();
        });
        services.AddSingleton<IKnowledgeIndex, FluxIndexKnowledgeIndex>();
        return services;
    }
}

public interface IKnowledgeIndex
{
    Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken);
}

public sealed record KnowledgeSearchHit(string Content, double Score);

internal sealed class FluxIndexKnowledgeIndex(IFluxIndexContext context) : IKnowledgeIndex
{
    public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
        context.Indexer.IndexDocumentAsync(content, documentId, metadata, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken)
    {
        // Retriever.SearchAsync is vector-only (its own doc comment: "벡터 유사도 검색") — with
        // no production embedding endpoint configured (e.g. in tests, see KnowledgeOptions),
        // FluxIndex falls back to InMemoryEmbeddingService, whose similarity scores are not
        // semantically meaningful and can miss an exact-term match entirely. HybridSearchAsync's
        // keyword leg is unaffected by embedding quality, so it catches what pure vector search
        // would drop while still counting toward relevance when a real embedding IS configured.
        var results = await context.Retriever.HybridSearchAsync(query, query, maxResults, vectorWeight: 0.5, filter, cancellationToken);
        return results.Select(r => new KnowledgeSearchHit(r.DocumentChunk.Content, r.Score)).ToList();
    }
}
