using FluxIndex.Providers.OpenAI.Extensions;
using FluxIndex.SDK;
using FluxIndex.Storage.PostgreSQL;
using Microsoft.Extensions.Logging;
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
                .UsePostgreSQL(options.ConnectionString)
                .ConfigureServices(s =>
                {
                    s.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
                    // AddPostgreSQLStorage() below registers its own Configure<PostgreSQLOptions>
                    // after this, which would overwrite a Configure() call here — PostConfigure
                    // runs after all Configure calls and wins.
                    s.PostConfigure<PostgreSQLOptions>(o => o.EmbeddingDimensions = options.EmbeddingDimension);
                    if (!string.IsNullOrEmpty(options.EmbeddingEndpoint))
                    {
                        s.AddOpenAICompatibleEmbedding(
                            options.EmbeddingEndpoint, options.EmbeddingApiKey,
                            options.EmbeddingModel, options.EmbeddingDimension);
                    }
                    else
                    {
                        sp.GetRequiredService<ILogger<FluxIndexKnowledgeIndex>>().LogWarning(
                            "Momos:Host:Knowledge:EmbeddingEndpoint is not set — the project " +
                            "knowledge index will use FluxIndex's in-memory embedding fallback, " +
                            "whose vectors are not semantically meaningful. Set it (and " +
                            "EmbeddingApiKey) to a real embedding endpoint for knowledge search " +
                            "to work.");
                    }
                });
            builder.Options.GraphStore.AutoMigrate = false;
            builder.Options.SemanticCache.AutoMigrate = false;

            // Documents here are replaced and deleted (model claims are re-projected on every
            // analysis and correction), but the retriever's search-result cache is not
            // invalidated when the indexer deletes or replaces a document, so a repeated query
            // would keep returning removed or outdated content until the entry expires. This
            // reaches HybridSearchAsync too: its vector leg is a call to the cached vector
            // SearchAsync. Any cache provider other than the default "Memory" keeps Build() from
            // registering the in-memory search cache, so the retriever runs without one. The
            // EnableSearchCache flag is not consulted by the retriever, so it cannot be used for
            // this.
            // TODO(upstream): re-enable once FluxIndex invalidates cached search results on
            // document delete/re-index. Re-verified on FluxIndex 0.51.4: still not invalidated,
            // and a claim dropped by re-analysis stays searchable with the cache on.
            builder.Options.Cache.CacheProvider = "None";

            return builder.AddPostgreSQLStorage().Build();
        });
        services.AddSingleton<IKnowledgeIndex, FluxIndexKnowledgeIndex>();
        return services;
    }
}

public interface IKnowledgeIndex
{
    Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken);

    /// <summary>Removes a document and all of its chunks from the index. Deleting a document
    /// that was never indexed is not an error.</summary>
    Task DeleteAsync(string documentId, CancellationToken cancellationToken);
}

public sealed record KnowledgeSearchHit(string Content, double Score);

internal sealed class FluxIndexKnowledgeIndex(IFluxIndexContext context) : IKnowledgeIndex
{
    public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
        context.Indexer.IndexDocumentAsync(content, documentId, metadata, cancellationToken);

    // DeleteByDocumentIdAsync removes the vector chunks and the keyword postings together; its
    // bool result only says whether anything existed, which callers don't need.
    public Task DeleteAsync(string documentId, CancellationToken cancellationToken) =>
        context.Indexer.DeleteByDocumentIdAsync(documentId, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken)
    {
        // Retriever.SearchAsync is vector-only (its own doc comment: "벡터 유사도 검색") — with
        // no production embedding endpoint configured (e.g. in tests, see KnowledgeOptions),
        // FluxIndex falls back to InMemoryEmbeddingService, whose similarity scores are not
        // semantically meaningful and can miss an exact-term match entirely. HybridSearchAsync's
        // keyword leg is unaffected by embedding quality, so it catches what pure vector search
        // would drop while still counting toward relevance when a real embedding IS configured.
        var results = await context.Retriever.HybridSearchAsync(query, query, maxResults, vectorWeight: 0.5, filter, cancellationToken);
        return results
            .OrderByDescending(r => r.Score)
            .Select(r => new KnowledgeSearchHit(r.DocumentChunk.Content, r.Score))
            .ToList();
    }
}
