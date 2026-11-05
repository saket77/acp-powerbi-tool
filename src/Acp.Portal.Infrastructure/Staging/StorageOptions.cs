using System.ComponentModel.DataAnnotations;

namespace Acp.Portal.Infrastructure.Staging;

/// <summary>
/// Where uploaded <c>.pbix</c> files wait, and how the import jobs reach the worker.
/// </summary>
/// <remarks>
/// Two ways to reach the account, and which one is in use decides how a SAS is signed.
/// <see cref="ConnectionString"/> means a key — Azurite locally, and nothing else, because the
/// deployed storage account sets <c>allowSharedKeyAccess: false</c>. In Azure the account is
/// reached by URL with the portal's managed identity, and a SAS has to be a user delegation SAS.
/// </remarks>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Azurite only. Empty everywhere else.</summary>
    public string? ConnectionString { get; set; }

    public string? BlobAccountUrl { get; set; }

    public string? QueueAccountUrl { get; set; }

    [Required]
    public string UploadsContainer { get; set; } = "pbix-uploads";

    [Required]
    public string ImportQueue { get; set; } = "pbix-imports";

    /// <summary>
    /// How long the browser has to finish its upload. Long enough for a large Databook on a bad
    /// connection, short enough that a leaked URL is worth very little.
    /// </summary>
    [Range(1, 120)]
    public int SasLifetimeMinutes { get; set; } = 20;

    /// <summary>
    /// How long a worker holds a job before the queue offers it to somebody else. Must comfortably
    /// exceed an import, because Power BI can take minutes on a large model.
    /// </summary>
    [Range(60, 3600)]
    public int VisibilityTimeoutSeconds { get; set; } = 900;

    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Largest file the portal will hand out a SAS for. A Databook is tens of megabytes; a
    /// gigabyte is somebody using the portal as a file share.
    /// </summary>
    [Range(1, 2048)]
    public int MaxUploadMegabytes { get; set; } = 512;

    public bool UsesSharedKey => !string.IsNullOrWhiteSpace(ConnectionString);
}
