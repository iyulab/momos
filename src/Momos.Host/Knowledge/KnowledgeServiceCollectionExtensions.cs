using FluxIndex.Providers.OpenAI.Services;
using FluxIndex.SDK;
using FluxIndex.Storage.PostgreSQL;
using Microsoft.Extensions.Logging;
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
                    // FluxIndex's own warnings (a vector size mismatch, a resolved embedder that is
                    // not the one configured) belong in the Host's log, not a null provider.
                    s.AddSingleton(sp.GetRequiredService<ILoggerFactory>());
                    s.AddLogging();
                    // AddPostgreSQLStorage() below registers its own Configure<PostgreSQLOptions>
                    // after this, which would overwrite a Configure() call here — PostConfigure
                    // runs after all Configure calls and wins.
                    s.PostConfigure<PostgreSQLOptions>(o => o.EmbeddingDimensions = options.EmbeddingDimension);
                });
            if (!string.IsNullOrEmpty(options.EmbeddingEndpoint))
            {
                // The builder's explicit selection: it states that this, not the in-memory
                // default, is the embedding service. (A registration through ConfigureServices
                // is also honored since FluxIndex 0.52.1.)
                builder.UseEmbeddingService(fluxServices => new OpenAICompatibleEmbeddingService(
                    options.EmbeddingEndpoint, options.EmbeddingApiKey,
                    options.EmbeddingModel, options.EmbeddingDimension,
                    fluxServices.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAICompatibleEmbeddingService>()));
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

            builder.Options.GraphStore.AutoMigrate = false;
            builder.Options.SemanticCache.AutoMigrate = false;

            return builder.AddPostgreSQLStorage().Build();
        });
        services.AddSingleton<IKnowledgeIndex, FluxIndexKnowledgeIndex>();
        services.AddSingleton<IValidateOptions<KnowledgeOptions>, KnowledgeOptionsValidator>();
        services.AddHostedService<KnowledgeIndexInitializer>();
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

internal sealed class FluxIndexKnowledgeIndex(IFluxIndexContext context) : IKnowledgeIndex, IDisposable
{
    // TODO(upstream): FluxIndexContext resolves one scoped vector store (and so one EF Core
    // DbContext) when it is built and hands it to every Indexer and Retriever call, so two calls
    // that overlap fail with "A second operation was started on this context instance". Requests
    // and the model projection service call in parallel, so every call is serialized here until
    // the context is safe for concurrent use; remove the gate then.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task IndexAsync(string content, string documentId, Dictionary<string, object> metadata, CancellationToken cancellationToken) =>
        ExclusiveAsync(() => context.Indexer.IndexDocumentAsync(content, documentId, metadata, cancellationToken), cancellationToken);

    // DeleteByDocumentIdAsync removes the vector chunks and the keyword postings together; its
    // bool result only says whether anything existed, which callers don't need.
    public Task DeleteAsync(string documentId, CancellationToken cancellationToken) =>
        ExclusiveAsync(() => context.Indexer.DeleteByDocumentIdAsync(documentId, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, Dictionary<string, object> filter, int maxResults, CancellationToken cancellationToken)
    {
        // Retriever.SearchAsync is vector-only (its own doc comment: "벡터 유사도 검색") — with
        // no production embedding endpoint configured (e.g. in tests, see KnowledgeOptions),
        // FluxIndex falls back to InMemoryEmbeddingService, whose similarity scores are not
        // semantically meaningful and can miss an exact-term match entirely. HybridSearchAsync's
        // keyword leg is unaffected by embedding quality, so it catches what pure vector search
        // would drop while still counting toward relevance when a real embedding IS configured.
        var results = await ExclusiveAsync(
            () => context.Retriever.HybridSearchAsync(query, query, maxResults, vectorWeight: 0.5, filter, cancellationToken),
            cancellationToken);
        return results
            .OrderByDescending(r => r.Score)
            .Select(r => new KnowledgeSearchHit(r.DocumentChunk.Content, r.Score))
            .ToList();
    }

    public void Dispose() => _gate.Dispose();

    private async Task ExclusiveAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
