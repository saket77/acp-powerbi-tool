namespace Acp.Portal.Domain.Abstractions;

/// <summary>
/// Reads a semantic model's shape so the portal can check it against the Databook template.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPowerBiClient"/> because it speaks a different protocol to a
/// different endpoint: the REST API manages items, XMLA reads inside them. The REST API's
/// Execute Queries cannot do this job — it refuses service principals on models with row-level
/// security, and every Databook has row-level security.
/// </remarks>
public interface IModelInspector
{
    Task<ModelDescription> InspectAsync(ModelLocation location, CancellationToken cancellationToken = default);
}

/// <param name="WorkspaceName">
/// XMLA addresses a workspace by name, not by id — the one place in the system where a Power BI
/// name rather than an id is load-bearing.
/// </param>
/// <param name="FileName">
/// What the file was called when it was uploaded. Not used to find anything — the ids do that —
/// but it is what a failure message should name, because it is what the consultant recognises.
/// </param>
public sealed record ModelLocation(
    string ProfileId,
    string WorkspaceId,
    string WorkspaceName,
    string DatasetId,
    string DatasetName,
    string FileName);

/// <param name="TemplateVersion">Null when the model has no such measure at all.</param>
/// <param name="CompanyCodes">The codes actually in the model, as distinct from the ones on the grants.</param>
public sealed record ModelDescription(
    IReadOnlyList<string> Roles,
    IReadOnlyList<ModelTable> Tables,
    decimal? TemplateVersion,
    IReadOnlyList<string> CompanyCodes);

/// <param name="FilteredByClientRole">
/// True when the Client role carries a row filter for this table. A table without one is a table
/// a client would see in full, which for anything but a lookup is the whole failure mode the
/// checks exist to catch.
/// </param>
public sealed record ModelTable(
    string Name,
    IReadOnlyList<string> Columns,
    bool FilteredByClientRole);
