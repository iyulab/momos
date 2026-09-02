using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Momos.Worker.Tests.Execution;

/// <summary>
/// Boots the real <c>Momos.Host</c> pipeline in-process against a throwaway SQLite
/// file, so <see cref="Momos.Worker.Execution.HostApiClient"/> can be exercised
/// against the actual wire contract without a second OS process.
/// </summary>
public sealed class TestMomosHostFactory : WebApplicationFactory<global::Program>
{
    /// <summary>The Host's configured worker key — see <see cref="CreateAuthorizedClient()"/>.</summary>
    public const string WorkerApiKey = "test-worker-key";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"momos-worker-test-{Guid.NewGuid():N}.db");
    private readonly string _knowledgeDbPath = Path.Combine(Path.GetTempPath(), $"momos-worker-test-knowledge-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:MomosDb", $"Data Source={_dbPath}")
            .UseSetting("Momos:Host:Knowledge:SqlitePath", _knowledgeDbPath)
            .UseSetting("Momos:Host:WorkerAuth:ApiKey", WorkerApiKey);

    /// <summary>
    /// A client carrying the configured worker key, so a real <see cref="HostApiClient"/>
    /// built from it authenticates against the booted-up Host without callers setting the
    /// header themselves.
    /// </summary>
    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkerApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteIfExists(_dbPath);
            DeleteIfExists(_knowledgeDbPath);
            // FluxIndex derives a companion entity-graph database from the configured path
            // (see Momos.Host.Tests.Knowledge.KnowledgeBootstrapTests) — clean it up too.
            var knowledgeDir = Path.GetDirectoryName(_knowledgeDbPath)!;
            var knowledgeNameNoExt = Path.GetFileNameWithoutExtension(_knowledgeDbPath);
            DeleteIfExists(Path.Combine(knowledgeDir, $"{knowledgeNameNoExt}-entitygraph.db"));
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
