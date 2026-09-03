using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Momos.Host.Tests;

/// <summary>
/// Boots the real <c>Momos.Host</c> pipeline (migrations included) against a throwaway
/// PostgreSQL container per factory instance — the same engine production runs, so EF
/// translation differences (see MomosDbContextTests) can't hide behind a different test-only
/// provider.
/// </summary>
public sealed class MomosHostFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Matches the worker-auth key this factory configures the Host with.</summary>
    public const string WorkerApiKey = "test-worker-key";

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("momos")
        .Build();

    private readonly PostgreSqlContainer _knowledgeContainer = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("momos_knowledge")
        .Build();

    /// <summary>
    /// Overridable so a reclaim test can shrink it far below the production default and
    /// observe a claim going stale within a normal test's lifetime, instead of waiting out
    /// the real timeout.
    /// </summary>
    public TimeSpan InspectionClaimReclaimTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Overridable so a reclaim test can advance a fake time provider (e.g.
    /// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) instead of waiting out a
    /// real timeout — the claim-next endpoint reads
    /// <see cref="TimeProvider.GetUtcNow"/> rather than <see cref="DateTimeOffset.UtcNow"/>
    /// for exactly this reason.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

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
            .UseSetting("Momos:Host:InspectionClaim:ReclaimTimeout", InspectionClaimReclaimTimeout.ToString())
            .UseSetting("Momos:Host:WorkerAuth:ApiKey", WorkerApiKey)
            .ConfigureServices(services => services.AddSingleton(TimeProvider));

    /// <summary>
    /// A client carrying the configured worker key — what every test in this project wants
    /// except <c>WorkerAuthTests</c>, which uses <c>CreateDefaultClient()</c> deliberately to
    /// prove the *unauthenticated* path is rejected.
    /// </summary>
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
