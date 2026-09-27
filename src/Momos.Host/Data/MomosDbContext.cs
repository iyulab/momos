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
    public DbSet<ProjectModel> ProjectModels => Set<ProjectModel>();
    public DbSet<ModelClaim> ModelClaims => Set<ModelClaim>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InspectionRequest>()
            .Property(r => r.Kind)
            .HasConversion<string>();

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

        modelBuilder.Entity<InspectionRequest>()
            .HasMany(r => r.ToolCalls)
            .WithOne()
            .HasForeignKey(t => t.InspectionRequestId)
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

        var projectModel = modelBuilder.Entity<ProjectModel>();
        projectModel.Property(m => m.Components).HasJsonColumn();
        projectModel.Property(m => m.Relations).HasJsonColumn();
        projectModel.Property(m => m.Patterns).HasJsonColumn();
        projectModel.Property(m => m.Decisions).HasJsonColumn();
        projectModel.Property(m => m.Intents).HasJsonColumn();
        projectModel.HasIndex(m => new { m.ProjectId, m.ModelVersion }).IsUnique();
        projectModel.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Restrict);
        projectModel.HasOne<InspectionRequest>().WithMany().HasForeignKey(m => m.AnalysisRequestId).OnDelete(DeleteBehavior.Restrict);
        projectModel.HasMany(m => m.Claims).WithOne().HasForeignKey(c => c.ProjectModelId).OnDelete(DeleteBehavior.Cascade);

        var claim = modelBuilder.Entity<ModelClaim>();
        claim.Property(c => c.Evidence).HasJsonColumn();
        claim.Property(c => c.Tier).HasConversion<string>();
        claim.Property(c => c.Confidence).HasConversion<string>();
        claim.Property(c => c.Status).HasConversion<string>();
        claim.Property(c => c.Origin).HasConversion<string>();
        claim.HasIndex(c => new { c.ProjectModelId, c.Key }).IsUnique();
    }
}
