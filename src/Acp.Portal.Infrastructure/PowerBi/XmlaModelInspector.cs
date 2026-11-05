using Acp.Portal.Domain.Abstractions;
using Azure.Core;
using Microsoft.AnalysisServices.AdomdClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tabular = Microsoft.AnalysisServices.Tabular;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// Reads a semantic model's metadata with AMO and its company codes with a DAX query.
/// </summary>
/// <remarks>
/// <para>
/// Two libraries because they do two jobs. AMO reads the model's <i>shape</i> — roles, tables,
/// columns, which tables a role filters — without evaluating anything. ADOMD runs the one query
/// the checks need, because the company codes are data and no amount of metadata will say what
/// rows a table holds.
/// </para>
/// <para>
/// <b>Not run against Power BI.</b> Written against the published API with no capacity to check
/// it. It compiles, which means the AMO and ADOMD calls are at least real; whether the
/// connection string is right is Gate 1. The likeliest thing to be wrong is the identity — see
/// <see cref="XmlaIdentity"/>, where profiles over XMLA are undocumented and the default is
/// therefore the plain application.
/// </para>
/// </remarks>
public sealed class XmlaModelInspector(
    TokenCredential credential,
    IOptions<PowerBiOptions> options,
    ILogger<XmlaModelInspector> logger) : IModelInspector
{
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly PowerBiOptions _options = options.Value;

    public async Task<ModelDescription> InspectAsync(
        ModelLocation location, CancellationToken cancellationToken = default)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var connectionString = ConnectionStringFor(location);

        logger.LogInformation(
            "Inspecting {Dataset} in {Workspace} over XMLA as the {Identity}.",
            location.DatasetName, location.WorkspaceName, _options.XmlaConnectAs);

        try
        {
            var (roles, tables) = ReadShape(connectionString, token, location);
            var version = ReadTemplateVersion(connectionString, token);
            var codes = ReadCompanyCodes(connectionString, token);

            return new ModelDescription(roles, tables, version, codes);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "XMLA inspection of {Dataset} failed.", location.DatasetName);

            throw new PortalRuleException(
                "The portal could not read the model over XMLA. The usual causes are the XMLA "
                + "endpoint being set to Read rather than Read Write (scripts/tenant-setup.md "
                + "step 5), or the identity not having access to the workspace. "
                + (_options.XmlaConnectAs == XmlaIdentity.Profile
                    ? "This environment is set to connect as the service principal profile, which "
                      + "is undocumented — try PowerBi:XmlaConnectAs=Application. "
                    : "")
                + "Power BI said: " + e.Message);
        }
    }

    private (List<string> Roles, List<ModelTable> Tables) ReadShape(
        string connectionString, AccessToken token, ModelLocation location)
    {
        using var server = new Tabular.Server();
        server.AccessToken = new(token.Token, token.ExpiresOn);
        server.Connect(connectionString);

        var database = server.Databases.FindByName(location.DatasetName)
            ?? throw new InvalidOperationException(
                $"The workspace has no semantic model named '{location.DatasetName}'.");

        var model = database.Model;
        var roles = model.Roles.Select(r => r.Name).ToList();

        var clientRole = model.Roles
            .FirstOrDefault(r => r.Name.Equals(_options.ClientRole, StringComparison.OrdinalIgnoreCase));

        // A table counts as filtered when the Client role carries a non-empty row filter for it.
        // An empty expression is the same as no filter at all, which is the subtle version of
        // this failure and the reason the emptiness is checked rather than just the presence.
        var filtered = clientRole is null
            ? []
            : clientRole.TablePermissions
                .Where(p => !string.IsNullOrWhiteSpace(p.FilterExpression))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = model.Tables
            .Select(t => new ModelTable(
                t.Name,
                t.Columns.Select(c => c.Name).ToList(),
                filtered.Contains(t.Name)))
            .ToList();

        return (roles, tables);
    }

    private decimal? ReadTemplateVersion(string connectionString, AccessToken token)
    {
        var dax = $"EVALUATE ROW(\"Version\", [{_options.TemplateVersionMeasure}])";

        try
        {
            using var reader = Query(connectionString, token, dax);
            if (!reader.Read() || reader.IsDBNull(0)) return null;

            return Convert.ToDecimal(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (AdomdErrorResponseException)
        {
            // The measure does not exist. That is a check failure, not an outage, so it comes
            // back as "no version" and TemplateChecks writes the sentence about it.
            return null;
        }
    }

    private List<string> ReadCompanyCodes(string connectionString, AccessToken token)
    {
        // Read as the model's own identity, with no effective identity attached, so this sees
        // every company in the file. That is the point: the check compares what the file holds
        // against what the project is allowed to hold.
        var dax =
            $"EVALUATE SELECTCOLUMNS(VALUES('{_options.CompaniesTable}'[{_options.CompanyCodeColumn}]), "
            + $"\"Code\", '{_options.CompaniesTable}'[{_options.CompanyCodeColumn}])";

        try
        {
            using var reader = Query(connectionString, token, dax);
            var codes = new List<string>();

            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) codes.Add(reader.GetValue(0).ToString()!.Trim());
            }

            return codes;
        }
        catch (AdomdErrorResponseException)
        {
            // No such table or column. TemplateChecks says so in its own words.
            return [];
        }
    }

    private static AdomdDataReader Query(string connectionString, AccessToken token, string dax)
    {
        var connection = new AdomdConnection(connectionString)
        {
            // Target-typed: AMO and ADOMD each own a token type and they are not the same one.
            AccessToken = new(token.Token, token.ExpiresOn),
        };

        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = dax;

        // CloseConnection so that disposing the reader closes the connection with it; the
        // connection is otherwise unreachable from the caller.
        return command.ExecuteReader(System.Data.CommandBehavior.CloseConnection);
    }

    private string ConnectionStringFor(ModelLocation location)
    {
        var dataSource = $"{_options.XmlaBaseUrl}/{location.WorkspaceName}";

        var connection =
            $"Provider=MSOLAP;Data Source={dataSource};Initial Catalog={location.DatasetName};";

        // Undocumented, and here to be tried rather than relied on. If Gate 1 shows that XMLA
        // ignores or rejects it, the answer is XmlaConnectAs=Application, which is the default.
        return _options.XmlaConnectAs == XmlaIdentity.Profile
            ? connection + $"Profile Id={location.ProfileId};"
            : connection;
    }
}
