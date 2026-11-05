using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.Projects;

/// <summary>
/// Uploaded Databooks and what became of them.
/// </summary>
/// <remarks>
/// <para>
/// Importing here streams the file straight to Power BI. Phase 5 replaces that with the real
/// pipeline — a write-only SAS to private Blob storage, a queue, a background worker, and the
/// XMLA template checks — and this class keeps the release half unchanged. The direct path is
/// honest about itself: <c>BlobPath</c> records <c>direct://</c> so no later migration mistakes
/// these rows for staged ones.
/// </para>
/// <para>
/// Release is the four-eyes rule, and it is a database transaction rather than two updates,
/// because the filtered unique index allows exactly one released version per project and would
/// otherwise fail whichever update happened to go second.
/// </para>
/// </remarks>
public sealed class ReportVersions(
    PortalDbContext db,
    IPowerBiClient powerBi,
    TimeProvider clock,
    ILogger<ReportVersions> logger)
{
    private static readonly ProjectRole[] MayUpload = [ProjectRole.Lead, ProjectRole.Member];

    /// <summary>Null when the project is not visible to the caller.</summary>
    public async Task<IReadOnlyList<ReportVersionView>?> ListAsync(
        string projectKey, CancellationToken cancellationToken = default)
    {
        var projectId = await ProjectIdAsync(projectKey, cancellationToken);
        if (projectId is null) return null;

        return await db.ReportVersions
            .AsNoTracking()
            .Where(v => v.ProjectId == projectId.Value)
            .OrderByDescending(v => v.Version)
            .Select(v => new ReportVersionView(
                v.Version,
                v.State.ToString(),
                v.UploadedAt,
                db.Users.Where(u => u.Id == v.UploadedBy).Select(u => u.DisplayName).FirstOrDefault(),
                v.ReleasedAt,
                db.Users.Where(u => u.Id == v.ReleasedBy).Select(u => u.DisplayName).FirstOrDefault(),
                v.ReleaseNote,
                v.CheckFailureMessage))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Null when the project is not visible to the caller.</summary>
    public async Task<ReportVersionView?> ImportAsync(
        string projectKey,
        Stream content,
        string fileName,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.Key == projectKey, cancellationToken);

        if (project is null) return null;

        await RequireRoleAsync(project.Id, actingUser, MayUpload,
            "Only a lead or a member on this project can upload a Databook.", cancellationToken);

        var client = project.Client!;
        if (!client.IsProvisioned)
        {
            throw new PortalRuleException(
                $"{client.Name} has no Power BI workspace yet. Provision the client first.");
        }

        var nextVersion = await db.ReportVersions
            .Where(v => v.ProjectId == project.Id)
            .Select(v => (int?)v.Version)
            .MaxAsync(cancellationToken) ?? 0;

        nextVersion++;

        var import = await powerBi.ImportReportAsync(
            new PowerBiImportRequest(
                client.PowerBiProfileId!,
                client.PowerBiWorkspaceId!,
                $"{project.Key} v{nextVersion}",
                content,
                fileName),
            cancellationToken);

        var now = clock.GetUtcNow();

        var version = new ReportVersion
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Version = nextVersion,
            // Phase 5 stages uploads in private Blob storage and this becomes a real path.
            BlobPath = $"direct://{fileName}",
            PowerBiReportId = import.ReportId,
            PowerBiDatasetId = import.DatasetId,
            State = ReportVersionState.Draft,
            UploadedBy = actingUser,
            UploadedAt = now,
        };

        db.ReportVersions.Add(version);
        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "version.imported",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new { version = nextVersion, fileName, reportId = import.ReportId }),
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Imported {Project} v{Version} as report {Report}.",
            project.Key, nextVersion, import.ReportId);

        return (await ListAsync(projectKey, cancellationToken))!.First(v => v.Version == nextVersion);
    }

    /// <summary>
    /// Makes a draft the one clients see. Null when the project is not visible to the caller.
    /// </summary>
    public async Task<ReportVersionView?> ReleaseAsync(
        string projectKey,
        int version,
        string? note,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == projectKey, cancellationToken);
        if (project is null) return null;

        await RequireRoleAsync(project.Id, actingUser, [ProjectRole.Lead],
            "Only a lead on this project can release a Databook.", cancellationToken);

        var candidate = await db.ReportVersions
            .FirstOrDefaultAsync(v => v.ProjectId == project.Id && v.Version == version, cancellationToken);

        if (candidate is null) return null;

        if (candidate.State != ReportVersionState.Draft)
        {
            throw new PortalRuleException(
                $"Version {version} is {candidate.State}, and only a draft can be released.");
        }

        if (candidate.UploadedBy == actingUser)
        {
            // The four-eyes rule. The person who uploaded a Databook is the last person who
            // should decide it is fit for a client to see.
            throw new PortalRuleException(
                "You uploaded this version, so somebody else has to release it. That is the point "
                + "of the rule, not an oversight.");
        }

        var now = clock.GetUtcNow();
        var previous = await db.ReportVersions
            .FirstOrDefaultAsync(
                v => v.ProjectId == project.Id && v.State == ReportVersionState.Released, cancellationToken);

        // Built once, outside the retry below. Adding an entity EF is already tracking is a
        // no-op, so a retried attempt cannot write the audit entry twice.
        var audit = new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "version.released",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new
            {
                version,
                supersededVersion = previous?.Version,
                uploadedBy = candidate.UploadedBy,
            }),
        };

        // The connection retries on transient failures, and a retrying strategy refuses to let
        // anyone else start a transaction — it has no way to replay work it did not begin. So
        // the transaction goes inside the strategy rather than around it. Skipping this is a
        // 500 on every release, and only on a provider configured to retry.
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Two saves, in this order. At most one version per project may be Released — a
            // filtered unique index in the database says so — and writing both changes in one
            // save would let EF choose an order that trips it.
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            if (previous is not null)
            {
                previous.State = ReportVersionState.Superseded;
                await db.SaveChangesAsync(cancellationToken);
            }

            candidate.State = ReportVersionState.Released;
            candidate.ReleasedBy = actingUser;
            candidate.ReleasedAt = now;
            candidate.ReleaseNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            project.Status = ProjectStatus.Live;

            db.AuditEvents.Add(audit);
            await db.SaveChangesAsync(cancellationToken);

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        });

        logger.LogInformation("Released {Project} v{Version}, superseding v{Previous}.",
            project.Key, version, previous?.Version);

        return (await ListAsync(projectKey, cancellationToken))!.First(v => v.Version == version);
    }

    private async Task<Guid?> ProjectIdAsync(string projectKey, CancellationToken cancellationToken) =>
        await db.Projects
            .Where(p => p.Key == projectKey)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task RequireRoleAsync(
        Guid projectId,
        Guid actingUser,
        ProjectRole[] allowed,
        string refusal,
        CancellationToken cancellationToken)
    {
        var role = await db.ProjectGrants
            .Where(g => g.ProjectId == projectId && g.UserId == actingUser)
            .Select(g => (ProjectRole?)g.Role)
            .FirstOrDefaultAsync(cancellationToken);

        // Not a 404: row-level security already let them see the project, so hiding it now
        // would only confuse. They are simply not allowed to do this to it.
        if (role is null || !allowed.Contains(role.Value)) throw new PortalRuleException(refusal);
    }
}

public sealed record ReportVersionView(
    int Version,
    string State,
    DateTimeOffset UploadedAt,
    string? UploadedBy,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy,
    string? ReleaseNote,
    string? CheckFailureMessage);
