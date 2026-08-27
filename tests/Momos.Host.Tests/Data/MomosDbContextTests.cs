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

        var raw = await _db.Database.SqlQueryRaw<string>(
            $"SELECT Status FROM InspectionRequests WHERE Id = '{request.Id}'").ToListAsync();

        Assert.Equal("Running", Assert.Single(raw));
    }

    [Fact]
    public async Task RejectsASecondReportForTheSameInspectionRequest()
    {
        var project = new Project { Name = "p", Purpose = "x", Vision = "x", Scope = "x" };
        _db.Projects.Add(project);
        var request = new InspectionRequest { ProjectId = project.Id };
        _db.InspectionRequests.Add(request);
        await _db.SaveChangesAsync();

        _db.InspectionReports.Add(new InspectionReport { InspectionRequestId = request.Id });
        await _db.SaveChangesAsync();

        _db.InspectionReports.Add(new InspectionReport { InspectionRequestId = request.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingAReportCascadesToItsFindings()
    {
        var project = new Project { Name = "p", Purpose = "x", Vision = "x", Scope = "x" };
        _db.Projects.Add(project);
        var request = new InspectionRequest { ProjectId = project.Id };
        _db.InspectionRequests.Add(request);
        var report = new InspectionReport { InspectionRequestId = request.Id };
        report.Findings.Add(new Finding
        {
            InspectionReportId = report.Id,
            Category = FindingCategory.FunctionalDefect,
            Description = "desc",
            Evidence = "evidence",
        });
        _db.InspectionReports.Add(report);
        await _db.SaveChangesAsync();

        _db.InspectionReports.Remove(report);
        await _db.SaveChangesAsync();

        Assert.Empty(_db.Findings);
    }
}
