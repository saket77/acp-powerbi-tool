using System.Security.Cryptography;
using System.Text;
using Acp.Portal.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// Stands in for Power BI when there is no Fabric capacity — which is most of the time, since a
/// capacity is the first thing in this project that bills by the second.
/// </summary>
/// <remarks>
/// <para>
/// Be clear about what this does and does not prove. Everything on the portal's side of the
/// line is exercised exactly as in production: the grant lookup, the company codes read from the
/// database, the identity composition and its limits, the audit entry, the 404s. What it cannot
/// prove is the half that lives inside Power BI — that a token naming one report is refused for
/// another, and that the model's row-level security actually filters to the codes in the
/// identity. Those are Gate 1, and no fake can stand in for them.
/// </para>
/// <para>
/// So it is deliberately not a mock that always says yes. It applies the same identity limits,
/// and the screens label themselves as simulated, because a green tick here would be the most
/// expensive kind of wrong.
/// </para>
/// </remarks>
public sealed class FakePowerBiClient(
    TimeProvider clock,
    ILogger<FakePowerBiClient> logger) : IPowerBiClient
{
    public Task<PowerBiProfile> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Power BI is simulated: no profile was created for {Name}.", displayName);
        return Task.FromResult(new PowerBiProfile(Stable("profile", displayName), displayName));
    }

    public Task<PowerBiWorkspace> CreateWorkspaceAsync(
        string profileId, string name, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Power BI is simulated: no workspace was created for {Name}.", name);
        return Task.FromResult(new PowerBiWorkspace(Stable("workspace", name), name));
    }

    public Task AssignToCapacityAsync(
        string profileId, string workspaceId, string capacityId, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Power BI is simulated: workspace {Workspace} was not assigned to a capacity.", workspaceId);
        return Task.CompletedTask;
    }

    public async Task<PowerBiImport> ImportReportAsync(
        PowerBiImportRequest request, CancellationToken cancellationToken = default)
    {
        // Drain the upload. A consultant dropping a real 40 MB .pbix should see the same
        // behaviour here as in production, including how long the transfer takes.
        var length = await DrainAsync(request.Content, cancellationToken);

        logger.LogWarning(
            "Power BI is simulated: accepted and discarded {Bytes} bytes for {Name}.",
            length, request.DatasetDisplayName);

        var reportId = Stable("report", request.DatasetDisplayName);

        return new PowerBiImport(
            Stable("import", request.DatasetDisplayName),
            reportId,
            Stable("dataset", request.DatasetDisplayName),
            $"https://app.powerbi.example/reportEmbed?reportId={reportId}");
    }

    public Task<PowerBiEmbedTarget> GetReportAsync(
        string profileId, string workspaceId, string reportId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PowerBiEmbedTarget(
            reportId,
            Stable("dataset", reportId),
            $"https://app.powerbi.example/reportEmbed?reportId={reportId}"));

    public Task<PowerBiEmbedToken> GenerateEmbedTokenAsync(
        PowerBiEmbedTokenRequest request, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Power BI is simulated: a token would have been issued for report {Report} as "
            + "role(s) {Roles} with identity {Identity}.",
            request.ReportId,
            string.Join(",", request.Identity.Roles),
            request.Identity.CustomData ?? request.Identity.Username);

        // Opaque, like the real one. The screens read the scope from the API response, not by
        // picking the token apart, so that nothing starts depending on a shape only the fake has.
        return Task.FromResult(new PowerBiEmbedToken(
            "simulated." + Guid.NewGuid().ToString("N"),
            clock.GetUtcNow().AddMinutes(request.LifetimeMinutes)));
    }

    private static async Task<long> DrainAsync(Stream content, CancellationToken cancellationToken)
    {
        if (content.CanSeek) return content.Length;

        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0) total += read;

        return total;
    }

    /// <summary>
    /// Derived from the name so that provisioning the same client twice yields the same ids, the
    /// way Power BI would. Prefixed so that an id which escapes into a log or a screen is
    /// obviously not real.
    /// </summary>
    private static string Stable(string kind, string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{seed}"));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return $"simulated-{kind}-{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
