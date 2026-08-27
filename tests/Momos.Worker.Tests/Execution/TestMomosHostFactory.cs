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
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"momos-worker-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:MomosDb", $"Data Source={_dbPath}");

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
