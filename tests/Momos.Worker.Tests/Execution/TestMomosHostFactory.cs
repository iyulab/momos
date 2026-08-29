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

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:MomosDb", $"Data Source={_dbPath}")
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
        if (disposing && File.Exists(_dbPath))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }
    }
}
