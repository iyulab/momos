using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Momos.Worker.IntegrationTests.Execution;

/// <summary>
/// Boots the real <c>Momos.Host</c> pipeline in-process against real PostgreSQL containers, so
/// <see cref="Momos.Worker.Execution.HostApiClient"/> can be exercised against the actual wire
/// contract without a second OS process.
/// </summary>
public sealed class TestMomosHostFactory : WebApplicationFactory<global::Program>, IAsyncLifetime
{
    public const string WorkerApiKey = "test-worker-key";

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private readonly PostgreSqlContainer _knowledgeContainer = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .Build();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        await _knowledgeContainer.StartAsync();

        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Momos.Host.Data.MomosDbContext>()
            .Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder
            .UseSetting("ConnectionStrings:MomosDb", _dbContainer.GetConnectionString())
            .UseSetting("Momos:Host:Knowledge:ConnectionString", _knowledgeContainer.GetConnectionString())
            .UseSetting("Momos:Host:Knowledge:EmbeddingDimension", "384")
            .UseSetting("Momos:Host:WorkerAuth:ApiKey", WorkerApiKey);

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkerApiKey);
        return client;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        await _knowledgeContainer.DisposeAsync();
    }
}
