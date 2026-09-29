using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Momos.Host.Data;

namespace Momos.Host.Knowledge;

/// <summary>
/// Keeps the knowledge index caught up with each project's latest model, outside any request:
/// a submit or a correction marks the model unindexed and signals; this service projects it and
/// records <c>KnowledgeIndexedAt</c> only when every document operation succeeded. Unindexed
/// models found at startup are projected too, so a restart mid-projection loses nothing.
/// </summary>
/// <remarks>The projector (and through it the knowledge store) is resolved per pass, not injected:
/// hosted services are all constructed before any of them starts, so injecting it would build the
/// store ahead of <see cref="KnowledgeIndexInitializer"/>, whose failure names the misconfigured
/// setting.</remarks>
public sealed class ModelProjectionService(
    IServiceScopeFactory scopes, ModelProjectionSignal signal,
    IOptions<KnowledgeOptions> options, TimeProvider time, ILogger<ModelProjectionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            signal.Consume();
            if (await TryProjectPendingAsync(stoppingToken))
            {
                await signal.WaitAsync(stoppingToken);
            }
            else
            {
                await Task.Delay(options.Value.ProjectionRetryDelay, time, stoppingToken);
            }
        }
    }

    // An unhandled exception would stop the whole Host (BackgroundService's default), so a pass
    // that cannot even read the database is treated like one the index refused: retried later.
    private async Task<bool> TryProjectPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ProjectPendingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not project unindexed project models into the knowledge base; retrying.");
            return false;
        }
    }

    /// <summary>Projects every project's latest unindexed model; true when none is left pending.</summary>
    private async Task<bool> ProjectPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MomosDbContext>();
        var projector = scope.ServiceProvider.GetRequiredService<ModelKnowledgeProjector>();
        var projectIds = await db.ProjectModels.Where(m => m.KnowledgeIndexedAt == null)
            .Select(m => m.ProjectId).Distinct().ToListAsync(cancellationToken);

        var allDone = true;
        foreach (var projectId in projectIds)
        {
            var latest = await db.ProjectModels.AsNoTracking().Include(m => m.Claims)
                .Where(m => m.ProjectId == projectId).OrderByDescending(m => m.ModelVersion)
                .FirstAsync(cancellationToken);
            var currentKeys = latest.Claims.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            var staleKeys = (await db.ProjectModels
                    .Where(m => m.ProjectId == projectId && m.Id != latest.Id)
                    .SelectMany(m => m.Claims.Select(c => c.Key)).Distinct().ToListAsync(cancellationToken))
                .Where(k => !currentKeys.Contains(k)).ToList();

            if (!await projector.ProjectModelAsync(latest, staleKeys, cancellationToken))
            {
                logger.LogWarning("The knowledge index did not take every claim of project {ProjectId} model {Version}; retrying.", projectId, latest.ModelVersion);
                allDone = false;
                continue;
            }

            // The latest model now stands for the project in the index; older versions are
            // superseded, so they count as indexed too and are never projected over it. A newer
            // version submitted meanwhile is left alone by the version bound, and a correction saved
            // meanwhile (a claim corrected later than any this pass read) keeps the latest version
            // unindexed: both signal, and the next pass projects what this one did not see.
            var lastCorrectionRead = latest.Claims.Max(c => c.CorrectedAt);
            var now = time.GetUtcNow();
            await db.ProjectModels
                .Where(m => m.ProjectId == projectId && m.KnowledgeIndexedAt == null && m.ModelVersion <= latest.ModelVersion)
                .Where(m => m.ModelVersion < latest.ModelVersion
                    || !m.Claims.Any(c => c.CorrectedAt != null && (lastCorrectionRead == null || c.CorrectedAt > lastCorrectionRead)))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.KnowledgeIndexedAt, now), cancellationToken);
        }

        return allDone;
    }
}
