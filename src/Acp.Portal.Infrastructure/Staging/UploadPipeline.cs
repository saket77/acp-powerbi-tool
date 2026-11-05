using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.PowerBi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.Staging;

/// <summary>
/// What happens to one uploaded Databook: import it, read the model, check it against the
/// template, and leave the version either a draft or refused with a reason.
/// </summary>
/// <remarks>
/// <para>
/// Runs with no user behind it, so it opens an <see cref="UnfilteredScope"/> — the one place on
/// a non-request path where that is correct. Row-level security exists to stop a person seeing
/// another client's rows; a queue message is not a person.
/// </para>
/// <para>
/// Idempotent on redelivery. A queue guarantees at-least-once, so the job row is the record of
/// what has already happened and a message for a job that is already done is simply deleted.
/// </para>
/// </remarks>
public sealed class UploadPipeline(
    PortalDbContext db,
    PbixStaging staging,
    IPowerBiClient powerBi,
    IModelInspector inspector,
    IOptions<PowerBiOptions> powerBiOptions,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock,
    ILogger<UploadPipeline> logger)
{
    /// <summary>True when the message should be deleted — done, or failed for good.</summary>
    public async Task<bool> ProcessAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var _ = UnfilteredScope.Enter();

        var job = await db.UploadJobs
            .Include(j => j.ReportVersion!).ThenInclude(v => v.Project!).ThenInclude(p => p.Client)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
        {
            logger.LogWarning("Import job {Job} no longer exists; dropping the message.", jobId);
            return true;
        }

        if (job.State == UploadJobState.Done) return true;

        var version = job.ReportVersion!;
        var project = version.Project!;
        var client = project.Client!;
        var now = clock.GetUtcNow();

        job.State = UploadJobState.Processing;
        job.Attempts++;
        job.LockedUntil = now.AddSeconds(storageOptions.Value.VisibilityTimeoutSeconds);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var import = await ImportAsync(client, project, version, cancellationToken);
            var failures = await CheckAsync(client, project, version, import, cancellationToken);

            version.State = failures.Count == 0 ? ReportVersionState.Draft : ReportVersionState.CheckFailed;
            version.CheckFailureMessage = failures.Count == 0 ? null : TemplateChecks.Summarise(failures);

            job.State = UploadJobState.Done;
            job.CompletedAt = clock.GetUtcNow();
            job.LastError = null;
            job.LockedUntil = null;

            db.AuditEvents.Add(new AuditEvent
            {
                At = clock.GetUtcNow(),
                UserId = version.UploadedBy,
                Action = failures.Count == 0 ? "version.checked" : "version.check-failed",
                ProjectId = project.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    version = version.Version,
                    reportId = version.PowerBiReportId,
                    failed = failures.Select(f => f.Check),
                }),
            });

            await db.SaveChangesAsync(cancellationToken);

            // Staging is a corridor, not an archive: Power BI holds the model from here, and
            // the fewer copies of a client's data exist the shorter the list of places it can
            // leak from. Deleted whether the checks passed or not — a refused file is still
            // their data.
            await staging.DiscardAsync(version.BlobPath, cancellationToken);

            logger.LogInformation(
                "{Project} v{Version} finished as {State}.", project.Key, version.Version, version.State);

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return await RecordFailureAsync(job, version, project, e, cancellationToken);
        }
    }

    private async Task<PowerBiImport> ImportAsync(
        Client client, Project project, ReportVersion version, CancellationToken cancellationToken)
    {
        // A retry of a job that already imported would otherwise create a second report. The
        // import name is stable and nameConflict is CreateOrOverwrite, so Power BI replaces
        // rather than duplicates — but skipping the upload entirely is cheaper and clearer.
        if (version.PowerBiReportId is not null && version.PowerBiDatasetId is not null)
        {
            logger.LogInformation(
                "{Project} v{Version} was already imported; re-running the checks only.",
                project.Key, version.Version);

            return new PowerBiImport("", version.PowerBiReportId, version.PowerBiDatasetId, "");
        }

        await using var content = await staging.OpenAsync(version.BlobPath, cancellationToken);

        var import = await powerBi.ImportReportAsync(
            new PowerBiImportRequest(
                client.PowerBiProfileId!,
                client.PowerBiWorkspaceId!,
                DatasetNameFor(project, version),
                content,
                Path.GetFileName(version.BlobPath)),
            cancellationToken);

        version.PowerBiReportId = import.ReportId;
        version.PowerBiDatasetId = import.DatasetId;
        await db.SaveChangesAsync(cancellationToken);

        return import;
    }

    private async Task<IReadOnlyList<TemplateCheckFailure>> CheckAsync(
        Client client,
        Project project,
        ReportVersion version,
        PowerBiImport import,
        CancellationToken cancellationToken)
    {
        var expectedCodes = await db.Companies
            .Where(c => c.ClientId == client.Id)
            .Select(c => c.Code)
            .ToListAsync(cancellationToken);

        var model = await inspector.InspectAsync(
            new ModelLocation(
                client.PowerBiProfileId!,
                client.PowerBiWorkspaceId!,
                $"ACP {client.Name}",
                import.DatasetId,
                DatasetNameFor(project, version),
                Path.GetFileName(version.BlobPath)),
            cancellationToken);

        return TemplateChecks.Run(model, expectedCodes, powerBiOptions.Value);
    }

    private async Task<bool> RecordFailureAsync(
        UploadJob job,
        ReportVersion version,
        Project project,
        Exception error,
        CancellationToken cancellationToken)
    {
        var giveUp = job.Attempts >= storageOptions.Value.MaxAttempts;

        job.State = giveUp ? UploadJobState.Failed : UploadJobState.Queued;
        job.LastError = error.Message;
        job.LockedUntil = null;

        if (giveUp)
        {
            job.CompletedAt = clock.GetUtcNow();

            // The consultant gets a sentence they can act on. The exception text is a technical
            // detail for the platform owner's health page and stays on the job row.
            version.State = ReportVersionState.CheckFailed;
            version.CheckFailureMessage =
                "The portal could not import this file after several attempts. "
                + "Nothing is wrong with your upload as far as it got — tell the platform owner.";
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogError(
            error,
            "Import of {Project} v{Version} failed on attempt {Attempt}{Final}.",
            project.Key, version.Version, job.Attempts, giveUp ? " — giving up" : "");

        // Not giving up means leaving the message alone, so the queue hands it back when the
        // visibility timeout expires.
        return giveUp;
    }

    /// <summary>
    /// What the semantic model is called in Power BI. The project key leads so that the fake
    /// inspector — and a human looking at the workspace — can tell which project it belongs to.
    /// </summary>
    public static string DatasetNameFor(Project project, ReportVersion version) =>
        $"{project.Key} v{version.Version}";
}
