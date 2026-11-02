using Acp.Portal.Domain;
using Acp.Portal.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Infrastructure.Data;

/// <summary>
/// The portal's own store. Everything about who may see what lives here, not in Power BI and
/// not in any token.
/// </summary>
/// <remarks>
/// Phase 3 adds SQL row-level security predicates on <see cref="Projects"/> and
/// <see cref="ReportVersions"/>, driven by <c>SESSION_CONTEXT('UserId')</c>. The schema below is
/// shaped so those predicates are a join away from any row that needs them.
/// </remarks>
public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ProjectGrant> ProjectGrants => Set<ProjectGrant>();
    public DbSet<GrantCompany> GrantCompanies => Set<GrantCompany>();
    public DbSet<ReportVersion> ReportVersions => Set<ReportVersion>();
    public DbSet<UploadJob> UploadJobs => Set<UploadJob>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Enums are stored as text. The audit log and the health page are read by humans
        // during incidents, and "Released" beats "3" at three in the morning.
        b.Entity<Client>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            e.Property(x => x.PowerBiProfileId).HasMaxLength(100);
            e.Property(x => x.PowerBiWorkspaceId).HasMaxLength(100);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<Company>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Client).WithMany(c => c.Companies)
                .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);

            // A code identifies a legal entity within one client group, not globally.
            e.HasIndex(x => new { x.ClientId, x.Code }).IsUnique();
        });

        b.Entity<Project>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Client).WithMany(c => c.Projects)
                .HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.EntraObjectId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();

            // The stable identity from the IdP. One row per person, whatever they are called today.
            e.HasIndex(x => x.EntraObjectId).IsUnique();
        });

        b.Entity<ProjectGrant>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Project).WithMany(p => p.Grants)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

            // One grant per person per project. A role change is an update, not a second row,
            // so there is never a question about which one wins.
            e.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique();
        });

        b.Entity<GrantCompany>(e =>
        {
            e.HasKey(x => new { x.GrantId, x.CompanyId });
            e.HasOne(x => x.Grant).WithMany(g => g.Companies)
                .HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Company).WithMany()
                .HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReportVersion>(e =>
        {
            e.Property(x => x.BlobPath).HasMaxLength(500).IsRequired();
            e.Property(x => x.PowerBiReportId).HasMaxLength(100);
            e.Property(x => x.PowerBiDatasetId).HasMaxLength(100);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CheckFailureMessage).HasMaxLength(1000);
            e.Property(x => x.ReleaseNote).HasMaxLength(2000);
            e.HasOne(x => x.Project).WithMany(p => p.Versions)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();

            // At most one released version per project, enforced by the database rather than by
            // remembering to. Release is a transaction that supersedes the old row and promotes
            // the new one; if that ever races, this index fails the transaction instead of
            // showing a client two live versions.
            e.HasIndex(x => x.ProjectId)
                .IsUnique()
                .HasFilter("[State] = 'Released'")
                .HasDatabaseName("UX_ReportVersions_OneReleasedPerProject");
        });

        b.Entity<UploadJob>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(4000);
            e.HasOne(x => x.ReportVersion).WithMany()
                .HasForeignKey(x => x.ReportVersionId).OnDelete(DeleteBehavior.Cascade);

            // One job per version: a redelivered queue message must not start a second import.
            e.HasIndex(x => x.ReportVersionId).IsUnique();
        });

        b.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(4000);
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.ProjectId);
        });
    }
}
