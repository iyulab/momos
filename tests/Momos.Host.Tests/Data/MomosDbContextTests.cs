using Microsoft.EntityFrameworkCore;
using Momos.Host.Data;
using Momos.Host.Domain;
using Testcontainers.PostgreSql;

namespace Momos.Host.Tests.Data;

/// <summary>
/// Exercises the real PostgreSQL provider (not EF Core's InMemory provider) so the
/// unique-index and cascade-delete constraints configured in
/// <see cref="MomosDbContext.OnModelCreating"/> are actually enforced by the
/// test, not silently ignored the way InMemory would.
/// </summary>
public sealed class MomosDbContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private MomosDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _db = NewContext();
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _container.DisposeAsync();
    }

    private MomosDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MomosDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    [Fact]
    public async Task SavesAndReloadsAProjectRoundTrip()
    {
        var project = new Project
        {
            Name = "test-project",
            Purpose = "purpose",
            Vision = "vision",
            Scope = "scope",
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        using var freshDb = NewContext();
        var reloaded = await freshDb.Projects.FindAsync(project.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("test-project", reloaded.Name);
    }

    [Fact]
    public async Task PersistsInspectionRequestStatusAsString()
    {
        var project = new Project { Name = "p", Purpose = "x", Vision = "x", Scope = "x" };
        _db.Projects.Add(project);
        var request = new InspectionRequest { ProjectId = project.Id, Status = InspectionRequestStatus.Running };
        _db.InspectionRequests.Add(request);
        await _db.SaveChangesAsync();

        var raw = await _db.Database.SqlQuery<string>(
            $"SELECT \"Status\" FROM \"InspectionRequests\" WHERE \"Id\" = {request.Id.ToString()}").ToListAsync();

        Assert.Equal("Running", Assert.Single(raw));
    }

    [Fact]
    public async Task RejectsASecondReportForTheSameInspectionRequest()
    {
        Guid requestId;
        using (var seedDb = NewContext())
        {
            var project = new Project { Name = "p", Purpose = "x", Vision = "x", Scope = "x" };
            seedDb.Projects.Add(project);
            var request = new InspectionRequest { ProjectId = project.Id };
            seedDb.InspectionRequests.Add(request);
            await seedDb.SaveChangesAsync();
            requestId = request.Id;
        }

        using (var firstReportDb = NewContext())
        {
            firstReportDb.InspectionReports.Add(new InspectionReport { InspectionRequestId = requestId });
            await firstReportDb.SaveChangesAsync();
        }

        using (var secondReportDb = NewContext())
        {
            secondReportDb.InspectionReports.Add(new InspectionReport { InspectionRequestId = requestId });
            await Assert.ThrowsAsync<DbUpdateException>(() => secondReportDb.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task DeletingAReportCascadesToItsFindings()
    {
        Guid reportId;
        using (var seedDb = NewContext())
        {
            var project = new Project { Name = "p", Purpose = "x", Vision = "x", Scope = "x" };
            seedDb.Projects.Add(project);
            var request = new InspectionRequest { ProjectId = project.Id };
            seedDb.InspectionRequests.Add(request);
            var report = new InspectionReport { InspectionRequestId = request.Id };
            report.Findings.Add(new Finding
            {
                InspectionReportId = report.Id,
                Category = FindingCategory.FunctionalDefect,
                Description = "desc",
                Evidence = "evidence",
            });
            seedDb.InspectionReports.Add(report);
            await seedDb.SaveChangesAsync();
            reportId = report.Id;
        }

        using (var deleteDb = NewContext())
        {
            var report = await deleteDb.InspectionReports.SingleAsync(r => r.Id == reportId);
            deleteDb.InspectionReports.Remove(report);
            await deleteDb.SaveChangesAsync();
        }

        Assert.Empty(await _db.Findings.ToListAsync());
    }
}
