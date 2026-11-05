using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.Staging;

/// <summary>
/// The private container a <c>.pbix</c> passes through on its way to Power BI.
/// </summary>
/// <remarks>
/// <para>
/// The browser uploads straight to Blob rather than through the API. That keeps a 200 MB file
/// off the web app's request pipeline entirely, which matters more than it sounds: the
/// alternative is every upload occupying a request thread and a request-size limit for as long
/// as the slowest consultant's connection takes.
/// </para>
/// <para>
/// The price is handing the browser a credential, so the credential is as small as one can be:
/// <b>create and write only</b>, <b>one blob</b>, and <b>twenty minutes</b>. It cannot read, it
/// cannot delete, it cannot list, and it names a blob the portal chose. Somebody who steals it
/// can overwrite one file nobody has imported yet.
/// </para>
/// </remarks>
public sealed class PbixStaging(
    BlobServiceClient blobs,
    IOptions<StorageOptions> options,
    TimeProvider clock,
    ILogger<PbixStaging> logger)
{
    private readonly StorageOptions _options = options.Value;

    /// <summary>Where this version's file will live. Chosen here, never by the browser.</summary>
    public static string BlobPathFor(string projectKey, int version, Guid versionId, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var safeExtension = extension.Equals(".pbix", StringComparison.OrdinalIgnoreCase) ? ".pbix" : ".bin";

        // The version id leads so that two uploads of the same version number — a retry after a
        // failure, say — cannot collide, and so that nothing user-supplied decides where the
        // file lands. The original name follows, sanitised, purely so that somebody looking at
        // the container by hand can tell what they are looking at.
        return $"{projectKey}/v{version}/{versionId:N}-{Sanitize(Path.GetFileNameWithoutExtension(fileName))}{safeExtension}";
    }

    private static string Sanitize(string stem)
    {
        var kept = new string(stem.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        return kept.Length switch
        {
            0 => "databook",
            > 40 => kept[..40],
            _ => kept,
        };
    }

    public async Task<StagedUpload> AuthorizeUploadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var container = blobs.GetBlobContainerClient(_options.UploadsContainer);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var blob = container.GetBlobClient(blobPath);
        var expiresAt = clock.GetUtcNow().AddMinutes(_options.SasLifetimeMinutes);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobPath,
            Resource = "b",
            // Backdated a little: a client whose clock runs fast would otherwise be refused for
            // the first minute of a credential it just received.
            StartsOn = clock.GetUtcNow().AddMinutes(-5),
            ExpiresOn = expiresAt,
        };

        builder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);

        var uri = _options.UsesSharedKey
            ? blob.GenerateSasUri(builder)
            : await DelegatedUriAsync(blob, builder, expiresAt, cancellationToken);

        logger.LogInformation("Issued a write-only upload URL for {Blob}, valid until {Expiry:u}.", blobPath, expiresAt);

        return new StagedUpload(uri, expiresAt, _options.MaxUploadMegabytes);
    }

    /// <summary>
    /// Signs with a key Azure issues to the portal's managed identity rather than with an
    /// account key, because the deployed storage account has account keys switched off entirely.
    /// </summary>
    private async Task<Uri> DelegatedUriAsync(
        BlobClient blob, BlobSasBuilder builder, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var key = await blobs.GetUserDelegationKeyAsync(
            builder.StartsOn, expiresAt, cancellationToken);

        var parameters = builder.ToSasQueryParameters(key.Value, blobs.AccountName);

        return new UriBuilder(blob.Uri) { Query = parameters.ToString() }.Uri;
    }

    /// <summary>Null when the browser never finished its upload.</summary>
    public async Task<long?> SizeAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var blob = blobs.GetBlobContainerClient(_options.UploadsContainer).GetBlobClient(blobPath);

        if (!await blob.ExistsAsync(cancellationToken)) return null;

        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return properties.Value.ContentLength;
    }

    public async Task<Stream> OpenAsync(string blobPath, CancellationToken cancellationToken = default) =>
        await blobs.GetBlobContainerClient(_options.UploadsContainer)
            .GetBlobClient(blobPath)
            .OpenReadAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Deletes the staged file once Power BI has it. Staging is a corridor, not an archive —
    /// Power BI holds the model from here on, and the fewer copies of a client's data exist the
    /// shorter the list of places it can leak from.
    /// </summary>
    public async Task DiscardAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        await blobs.GetBlobContainerClient(_options.UploadsContainer)
            .GetBlobClient(blobPath)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken);

        logger.LogInformation("Discarded the staged file {Blob}.", blobPath);
    }

    /// <summary>
    /// Lets the browser PUT to Azurite from the dev server's origin.
    /// </summary>
    /// <remarks>
    /// Only ever called against a shared-key account, which means Azurite. The deployed account
    /// gets its CORS rules from infra/main.bicep, where they name the real origin — doing it
    /// from application code there would let a deployment quietly widen them.
    /// </remarks>
    public async Task AllowLocalBrowserUploadsAsync(string origin, CancellationToken cancellationToken = default)
    {
        if (!_options.UsesSharedKey) return;

        var properties = await blobs.GetPropertiesAsync(cancellationToken);

        properties.Value.Cors =
        [
            new BlobCorsRule
            {
                AllowedOrigins = origin,
                AllowedMethods = "PUT,OPTIONS",
                AllowedHeaders = "*",
                ExposedHeaders = "*",
                MaxAgeInSeconds = 3600,
            },
        ];

        await blobs.SetPropertiesAsync(properties.Value, cancellationToken);
        logger.LogInformation("Allowed browser uploads from {Origin} on the local storage emulator.", origin);
    }
}

/// <param name="MaxMegabytes">Enforced by the browser before it starts, and by the portal after.</param>
public sealed record StagedUpload(Uri UploadUrl, DateTimeOffset ExpiresAt, int MaxMegabytes);
