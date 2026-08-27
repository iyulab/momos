using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Momos.Host.Data;
using Momos.Host.Domain;

namespace Momos.Host.Tests.Data;

/// <summary>
/// Exercises the real SQLite provider (not EF Core's InMemory provider) so the
/// unique-index and cascade-delete constraints configured in
/// <see cref="MomosDbContext.OnModelCreating"/> are actually enforced by the
/// test, not silently ignored the way InMemory would.
/// </summary>
public sealed class MomosDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MomosDbContext _db;

    public MomosDbContextTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<MomosDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new MomosDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

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

        using var freshDb = new MomosDbContext(new DbContextOptionsBuilder<MomosDbContext>().UseSqlite(_connection).Options);
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
            $"SELECT Status FROM InspectionRequests WHERE Id = {request.Id.ToString()}").ToListAsync();

        Assert.Equal("Running", Assert.Single(raw));
    }

    [Fact]
    public async Task RejectsASecondReportForTheSameInspectionRequest()
    {
        // The two InspectionReport inserts must go through separate DbContext instances.
        // In the same tracked context, EF's required-one-to-one fixup would treat the
        // second Add as replacing the first (deleting it) rather than ever reaching the
        // database — so this wouldn't hit the unique index at all. Two independent
        // contexts is also the realistic production shape (two concurrent requests).
        Guid requestId;
        using (var seedDb = new MomosDbContext(new DbContextOptionsBuilder<MomosDbContext>().UseSqlite(_connection).Options))
        {
            var project = new Project { Name = "p", Purpose = "x", Vision = "x", Scope = "x" };
            seedDb.Projects.Add(project);
            var request = new InspectionRequest { ProjectId = project.Id };
            seedDb.InspectionRequests.Add(request);
            await seedDb.SaveChangesAsync();
            requestId = request.Id;
        }

        using (var firstReportDb = new MomosDbContext(new DbContextOptionsBuilder<MomosDbContext>().UseSqlite(_connection).Options))
        {
            firstReportDb.InspectionReports.Add(new InspectionReport { InspectionRequestId = requestId });
            await firstReportDb.SaveChangesAsync();
        }

        using (var secondReportDb = new MomosDbContext(new DbContextOptionsBuilder<MomosDbContext>().UseSqlite(_connection).Options))
        {
            secondReportDb.InspectionReports.Add(new InspectionReport { InspectionRequestId = requestId });
            await Assert.ThrowsAsync<DbUpdateException>(() => secondReportDb.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task DeletingAReportCascadesToItsFindings()
    {
        // Seed through one context, then dispose it so the Finding is not in any
        // change tracker below — this exercises the database-level ON DELETE CASCADE
        // constraint itself, not EF's client-side cascade of a tracked graph.
        Guid reportId;
        using (var seedDb = new MomosDbContext(new DbContextOptionsBuilder<MomosDbContext>().UseSqlite(_connection).Options))
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

        using (var deleteDb = new MomosDbContext(new DbContextOptionsBuilder<MomosDbContext>().UseSqlite(_connection).Options))
        {
            var report = await deleteDb.InspectionReports.SingleAsync(r => r.Id == reportId);
            deleteDb.InspectionReports.Remove(report);
            await deleteDb.SaveChangesAsync();
        }

        Assert.Empty(await _db.Findings.ToListAsync());
    }
}
