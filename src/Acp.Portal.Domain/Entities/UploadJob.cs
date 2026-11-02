namespace Acp.Portal.Domain.Entities;

/// <summary>
/// Tracks one import-and-check run. The queue message is the trigger; this row is the record,
/// so a redelivered message cannot start a second import of the same file.
/// </summary>
public sealed class UploadJob
{
    public Guid Id { get; set; }

    public Guid ReportVersionId { get; set; }

    public ReportVersion? ReportVersion { get; set; }

    public UploadJobState State { get; set; }

    public int Attempts { get; set; }

    /// <summary>Technical detail for the platform owner's health page. Never shown to a consultant.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Processing lock. A worker claims the job by setting this into the future; another worker
    /// seeing a lock that has not expired leaves it alone.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
