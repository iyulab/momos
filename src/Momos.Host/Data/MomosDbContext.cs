using Microsoft.EntityFrameworkCore;
using Momos.Host.Domain;

namespace Momos.Host.Data;

public sealed class MomosDbContext(DbContextOptions<MomosDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<InspectionRequest> InspectionRequests => Set<InspectionRequest>();
    public DbSet<InspectionReport> InspectionReports => Set<InspectionReport>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Forces Guid columns to a string representation rather than PostgreSQL's native
        // uuid type. Originally added to work around SQLite's uppercase Guid-to-TEXT mapping,
        // which didn't match Guid.ToString()'s lowercase output and made hand-written raw SQL
        // that interpolates a C# Guid silently fail to match the stored value. That specific
        // mismatch doesn't exist on PostgreSQL, but this conversion is still what makes the
        // stored representation match Guid.ToString()'s lowercase form for any raw-SQL use.
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

        modelBuilder.Entity<InspectionReport>()
            .HasMany(r => r.ToolCalls)
            .WithOne()
            .HasForeignKey(t => t.InspectionReportId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: inspection history is an audit trail and should not
        // silently disappear through a project deletion. No DELETE endpoint exists yet;
        // revisit this once one does and a real product decision endorses cascading it.
        modelBuilder.Entity<InspectionRequest>()
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        // Required one-to-one: within a single DbContext/request lifetime, adding a second
        // InspectionReport for the same InspectionRequest silently replaces the first
        // (EF's relationship fixup deletes the orphaned dependent) rather than erroring. The
        // real protection against a duplicate report is the DB-level unique index below,
        // which two independent DbContext instances (e.g. two concurrent requests) will
        // both genuinely hit.
        modelBuilder.Entity<InspectionReport>()
            .HasOne<InspectionRequest>()
            .WithOne()
            .HasForeignKey<InspectionReport>(r => r.InspectionRequestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
