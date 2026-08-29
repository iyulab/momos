using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Momos.Host.Tests;

/// <summary>
/// Boots the real <c>Momos.Host</c> pipeline (migrations included) against a
/// throwaway SQLite file per factory instance.
/// </summary>
public sealed class MomosHostFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"momos-host-test-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Overridable so a reclaim test can shrink it far below the production default and
    /// observe a claim going stale within a normal test's lifetime, instead of waiting out
    /// the real timeout.
    /// </summary>
    public TimeSpan InspectionClaimReclaimTimeout { get; set; } = TimeSpan.FromMinutes(30);

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:MomosDb", $"Data Source={_dbPath}")
            .UseSetting("Momos:Host:InspectionClaim:ReclaimTimeout", InspectionClaimReclaimTimeout.ToString());

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_dbPath))
        {
            // SQLite connection pooling can keep a native handle on the file open even
            // after every connection is disposed; clear pools before deleting so the
            // file isn't still locked (observed on Windows).
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }
    }
}
