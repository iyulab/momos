using FluxIndex.SDK;
using FluxIndex.Storage.SQLite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Momos.Host.Tests.Knowledge;

public sealed class KnowledgeBootstrapTests
{
    [Fact]
    public async Task Build_WithGraphAndCacheAutoMigrateDisabled_CreatesOnlyTheMainFile()
    {
        var tempDir = Directory.CreateTempSubdirectory("momos-knowledge-bootstrap-test");
        try
        {
            var dbPath = Path.Combine(tempDir.FullName, "knowledge.db");
            var builder = FluxIndexContext.CreateBuilder()
                .UseSQLite(dbPath)
                .ConfigureServices(s => s.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance)));
            builder.Options.GraphStore.AutoMigrate = false;
            builder.Options.SemanticCache.AutoMigrate = false;
            // Build() returns IFluxIndexContext, not the concrete FluxIndexContext, so it
            // can't be the target of a `using` statement here — the concrete instance still
            // implements IDisposable underneath (confirmed by the disposal below not
            // throwing), and the DI container in production registers/disposes it the same
            // way (see KnowledgeServiceCollectionExtensions).
            var context = builder.AddSQLiteStorage().Build();
            try
            {
                await context.Indexer.IndexDocumentAsync("probe content", "probe-doc");

                // WAL-mode sidecars (-shm/-wal) come and go depending on connection/checkpoint
                // state, so asserting on them would be flaky — filter to the logical database
                // files only.
                var databaseFilesCreated = Directory.GetFiles(tempDir.FullName)
                    .Select(f => Path.GetFileName(f)!)
                    .Where(f => !f.EndsWith("-shm", StringComparison.Ordinal) && !f.EndsWith("-wal", StringComparison.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray();

                // Empirically confirmed (2026-09-02): Options.GraphStore.AutoMigrate = false
                // suppresses *schema provisioning* into the entity-graph companion file, but
                // not the file's creation itself — FluxIndex still derives and creates
                // "<name>-entitygraph.db" beside the main database. Both files live on the
                // same volume/mount as the main knowledge.db (no separate infra), so this is
                // an accepted, harmless artifact rather than something to work around — see
                // the implementation plan's Global Constraints note, updated to match.
                Assert.Equal(["knowledge-entitygraph.db", "knowledge.db"], databaseFilesCreated);
            }
            finally
            {
                (context as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            tempDir.Delete(recursive: true);
        }
    }
}
