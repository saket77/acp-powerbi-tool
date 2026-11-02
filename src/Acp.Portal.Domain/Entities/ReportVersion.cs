namespace Acp.Portal.Domain.Entities;

/// <summary>
/// One uploaded <c>.pbix</c> and what became of it. Releasing is a state change on this row
/// rather than anything in Power BI, which is what makes release atomic and roll-back cheap:
/// the previously released version becomes <see cref="ReportVersionState.Superseded"/> and its
/// Power BI ids stay valid.
/// </summary>
public sealed class ReportVersion
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>1, 2, 3… per project. What the client sees as "Version 2".</summary>
    public int Version { get; set; }

    /// <summary>Path in the private upload container. Staging only — Power BI hosts the model once imported.</summary>
    public required string BlobPath { get; set; }

    public string? PowerBiReportId { get; set; }

    public string? PowerBiDatasetId { get; set; }

    public ReportVersionState State { get; set; }

    /// <summary>
    /// Plain-English instruction when <see cref="State"/> is <see cref="ReportVersionState.CheckFailed"/>,
    /// e.g. "This file has no Client role. Use template 1.6." Written for a consultant, not an engineer.
    /// </summary>
    public string? CheckFailureMessage { get; set; }

    public Guid UploadedBy { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>
    /// Null until released. Must never equal <see cref="UploadedBy"/> — the four-eyes rule is
    /// enforced in the release handler and asserted in the isolation tests.
    /// </summary>
    public Guid? ReleasedBy { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>What changed in this release. Shown to clients on their analytics home.</summary>
    public string? ReleaseNote { get; set; }

    public bool IsVisibleToClients => State == ReportVersionState.Released;
}
