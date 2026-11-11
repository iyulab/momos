using Microsoft.Extensions.DependencyInjection;
using Momos.Host.Knowledge;

namespace Momos.Host.Tests.Knowledge;

public sealed class FluxIndexKnowledgeIndexTests(MomosHostFactory factory) : IClassFixture<MomosHostFactory>
{
    [Fact]
    public async Task ConcurrentCalls_AllSucceed()
    {
        // The Host indexes from requests and from a background service at the same time, so the
        // one index instance they share must take overlapping calls.
        var index = factory.Services.GetRequiredService<IKnowledgeIndex>();
        var projectId = Guid.NewGuid().ToString();
        var filter = new Dictionary<string, object> { ["ProjectId"] = projectId };

        // TODO(upstream: FluxIndex concurrent identity table creation): the first write on a new
        // database creates its embedding-identity table with an unguarded CREATE TABLE IF NOT
        // EXISTS, which fails for the losers when several first writes overlap (23505 on
        // pg_type_typname_nsp_index). One sequential write first; drop it once a release makes the
        // creation safe.
        await index.IndexAsync("Warm-up document", $"concurrency-warmup-{projectId}",
            new Dictionary<string, object> { ["ProjectId"] = projectId }, CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            var documentId = $"concurrency-{projectId}-{i}";
            await index.DeleteAsync(documentId, CancellationToken.None);
            await index.IndexAsync($"Concurrent document {i} about Vermilionwidget", documentId,
                new Dictionary<string, object> { ["ProjectId"] = projectId }, CancellationToken.None);
            await index.SearchAsync("Vermilionwidget", filter, 5, CancellationToken.None);
        })));

        Assert.NotEmpty(await index.SearchAsync("Vermilionwidget", filter, 20, CancellationToken.None));
    }
}
