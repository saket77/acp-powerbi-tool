using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.Staging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.Projects;

/// <summary>
/// Uploaded Databooks and what became of them.
/// </summary>
/// <remarks>
/// <para>
/// Uploading is two requests and a direct browser-to-Blob transfer in between. The portal hands
/// out a write-only URL for one blob, the browser uses it, and the portal is told when that is
/// done — at which point a queue message makes it somebody else's problem. The file itself never
/// passes through this process, which is what keeps a 200 MB upload off the request pipeline.
/// </para>
/// <para>
/// Release is the four-eyes rule, and it is a database transaction rather than two updates,
/// because the filtered unique index allows exactly one released version per project and would
/// otherwise fail whichever update happened to go second.
/// </para>
/// </remarks>
public sealed class ReportVersions(
    PortalDbContext db,
    PbixStaging staging,
    ImportQueue queue,
    IOptions<StorageOptions> storage,
    TimeProvider clock,
    ILogger<ReportVersions> logger)
{
    private static readonly ProjectRole[] MayUpload = [ProjectRole.Lead, ProjectRole.Member];

    private readonly StorageOptions _storage = storage.Value;

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

    /// <summary>
    /// Reserves the next version number and hands back a URL the browser can upload to.
    /// Null when the project is not visible to the caller.
    /// </summary>
    /// <remarks>
    /// The row is created before the file exists, in <see cref="ReportVersionState.Importing"/>.
    /// That is deliberate: it is what reserves the version number, and it means an upload that
    /// is started and abandoned is visible as an abandoned upload rather than invisible.
    /// </remarks>
    public async Task<StagedVersion?> StageAsync(
        string projectKey,
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

        if (!fileName.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
        {
            throw new PortalRuleException("A Databook is a .pbix file.");
        }

        var client = project.Client!;
        if (!client.IsProvisioned)
        {
            throw new PortalRuleException(
                $"{client.Name} has no Power BI workspace yet. Provision the client first.");
        }

        var nextVersion = (await db.ReportVersions
            .Where(v => v.ProjectId == project.Id)
            .Select(v => (int?)v.Version)
            .MaxAsync(cancellationToken) ?? 0) + 1;

        var now = clock.GetUtcNow();
        var versionId = Guid.NewGuid();
        var blobPath = PbixStaging.BlobPathFor(project.Key, nextVersion, versionId, fileName);

        db.ReportVersions.Add(new ReportVersion
        {
            Id = versionId,
            ProjectId = project.Id,
            Version = nextVersion,
            BlobPath = blobPath,
            State = ReportVersionState.Importing,
            UploadedBy = actingUser,
            UploadedAt = now,
        });

        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "version.staged",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new { version = nextVersion, fileName }),
        });

        await db.SaveChangesAsync(cancellationToken);

        var upload = await staging.AuthorizeUploadAsync(blobPath, cancellationToken);

        logger.LogInformation("Staged {Project} v{Version} for upload.", project.Key, nextVersion);

        return new StagedVersion(versionId, nextVersion, upload.UploadUrl, upload.ExpiresAt, upload.MaxMegabytes);
    }

    /// <summary>
    /// Told that the browser finished. Checks the file is really there, then queues the import.
    /// Null when the project or the upload is not visible to the caller.
    /// </summary>
    public async Task<ReportVersionView?> CompleteAsync(
        string projectKey,
        Guid uploadId,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == projectKey, cancellationToken);
        if (project is null) return null;

        await RequireRoleAsync(project.Id, actingUser, MayUpload,
            "Only a lead or a member on this project can upload a Databook.", cancellationToken);

        var version = await db.ReportVersions
            .FirstOrDefaultAsync(v => v.Id == uploadId && v.ProjectId == project.Id, cancellationToken);

        if (version is null) return null;

        if (version.State != ReportVersionState.Importing)
        {
            throw new PortalRuleException($"Version {version.Version} has already been imported.");
        }

        // The browser says it finished; the storage account is what decides. A SAS that was
        // issued and never used leaves no blob, and that is the common case here — somebody
        // closed the tab.
        var size = await staging.SizeAsync(version.BlobPath, cancellationToken)
            ?? throw new PortalRuleException(
                "The file did not arrive. Try the upload again — the link lasts about twenty minutes.");

        var limit = (long)_storage.MaxUploadMegabytes * 1024 * 1024;
        if (size > limit)
        {
            await staging.DiscardAsync(version.BlobPath, cancellationToken);

            throw new PortalRuleException(
                $"That file is {size / 1024 / 1024} MB and the limit is {_storage.MaxUploadMegabytes} MB.");
        }

        var now = clock.GetUtcNow();

        var job = new UploadJob
        {
            Id = Guid.NewGuid(),
            ReportVersionId = version.Id,
            State = UploadJobState.Queued,
            CreatedAt = now,
        };

        // One job per version — a unique index says so — so a consultant double-clicking
        // "done" cannot start two imports of the same file.
        db.UploadJobs.Add(job);
        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "version.uploaded",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new { version = version.Version, bytes = size }),
        });

        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(job.Id, cancellationToken);

        logger.LogInformation(
            "{Project} v{Version} uploaded ({Bytes} bytes) and queued for import.",
            project.Key, version.Version, size);

        return (await ListAsync(projectKey, cancellationToken))!.First(v => v.Version == version.Version);
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

/// <param name="UploadUrl">Write-only, one blob, about twenty minutes. See <c>PbixStaging</c>.</param>
public sealed record StagedVersion(
    Guid UploadId,
    int Version,
    Uri UploadUrl,
    DateTimeOffset ExpiresAt,
    int MaxMegabytes);

public sealed record ReportVersionView(
    int Version,
    string State,
    DateTimeOffset UploadedAt,
    string? UploadedBy,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy,
    string? ReleaseNote,
    string? CheckFailureMessage);
