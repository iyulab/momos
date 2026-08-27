using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Momos.Host.Tests;

/// <summary>
/// Boots the real <c>Momos.Host</c> pipeline (migrations included) against a
/// throwaway SQLite file per factory instance, and against fake-but-valid
/// GPUStack config so <c>ValidateOnStart()</c> doesn't block boot — no real
/// LLM call happens in any endpoint test.
/// </summary>
public sealed class MomosHostFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"momos-host-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:MomosDb", $"Data Source={_dbPath}");
        builder.UseSetting("Momos:Llm:GpuStack:Endpoint", "http://gpustack.example.internal:9443");
        builder.UseSetting("Momos:Llm:GpuStack:ApiKey", "test-key");
        builder.UseSetting("Momos:Llm:GpuStack:Model", "test-model");
    }

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
