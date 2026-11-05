using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// Describes a model that does not exist, for when there is no capacity to read one from.
/// </summary>
/// <remarks>
/// <para>
/// It returns a <i>passing</i> model by default, built from the project's own companies — which
/// is the only thing it can honestly do, because there is no file to disagree with. That means
/// the happy path runs end to end locally and the checks never fire, which would be useless on
/// its own.
/// </para>
/// <para>
/// So the file name is a lever: a <c>.pbix</c> whose name contains <c>no-client-role</c>,
/// <c>old-template</c>, <c>no-companies</c>, <c>extra-company</c> or <c>unfiltered</c> comes
/// back describing a model broken in exactly that way. That makes every failure message
/// reachable from the upload screen without a capacity, which is the point — the messages are
/// the part a consultant actually experiences, and they are worth seeing before a client does.
/// </para>
/// </remarks>
public sealed class FakeModelInspector(
    PortalDbContext db,
    IOptions<PowerBiOptions> options,
    ILogger<FakeModelInspector> logger) : IModelInspector
{
    private readonly PowerBiOptions _options = options.Value;

    public async Task<ModelDescription> InspectAsync(
        ModelLocation location, CancellationToken cancellationToken = default)
    {
        // The file's own name decides which shape of model comes back. That is what makes
        // every failure message reachable from the upload screen with no capacity in sight.
        var hint = location.FileName.ToLowerInvariant();

        // The codes a correct file would contain: exactly the project's own.
        var codes = await db.Companies
            .Where(c => c.Client!.Projects.Any(p => p.Key == ProjectKeyOf(location.DatasetName)))
            .Select(c => c.Code)
            .ToListAsync(cancellationToken);

        logger.LogWarning(
            "Power BI is simulated: describing {Dataset} from the project's own companies rather "
            + "than from a real model.", location.DatasetName);

        var roles = hint.Contains("no-client-role")
            ? new List<string> { _options.AcpRole }
            : [_options.ClientRole, _options.AcpRole];

        decimal? version = hint.Contains("old-template") ? 1.4m
            : hint.Contains("no-template-version") ? null
            : _options.MinimumTemplateVersion;

        var tables = new List<ModelTable>
        {
            new(_options.CompaniesTable, [_options.CompanyCodeColumn, "Name"], FilteredByClientRole: true),
            new("Financials", ["Company", "Amount"], FilteredByClientRole: !hint.Contains("unfiltered")),
            new("ACP Internal", ["Margin"], FilteredByClientRole: false),
        };

        if (hint.Contains("no-companies")) tables.RemoveAt(0);
        if (hint.Contains("extra-company")) codes = [.. codes, "ZZ99"];

        return new ModelDescription(roles, tables, version, codes);
    }

    /// <summary>The dataset is named "{projectKey} v{n}" when the portal imports it.</summary>
    private static string ProjectKeyOf(string datasetName) =>
        datasetName.Split(' ', 2)[0];
}
