using Microsoft.EntityFrameworkCore;
using Momos.Host.Domain;

namespace Momos.Host.Data;

public sealed class MomosDbContext(DbContextOptions<MomosDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<InspectionRequest> InspectionRequests => Set<InspectionRequest>();
    public DbSet<InspectionReport> InspectionReports => Set<InspectionReport>();
    public DbSet<Finding> Findings => Set<Finding>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite's default Guid-to-TEXT mapping uses an uppercase representation, which
        // does not match Guid.ToString()'s lowercase output. Without this, any raw-SQL or
        // cross-entity FK comparison built from a C# Guid's default string form silently
        // fails to match the stored value. Force a single, consistent (lowercase) format.
        configurationBuilder.Properties<Guid>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InspectionRequest>()
            .Property(r => r.Status)
            .HasConversion<string>();

        modelBuilder.Entity<Finding>()
            .Property(f => f.Category)
            .HasConversion<string>();

        modelBuilder.Entity<InspectionReport>()
            .HasIndex(r => r.InspectionRequestId)
            .IsUnique();

        modelBuilder.Entity<InspectionReport>()
            .HasMany(r => r.Findings)
            .WithOne()
            .HasForeignKey(f => f.InspectionReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
