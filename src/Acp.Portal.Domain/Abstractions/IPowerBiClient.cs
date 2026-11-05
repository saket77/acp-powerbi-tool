namespace Acp.Portal.Domain.Abstractions;

/// <summary>
/// Everything the portal asks of Power BI. Four provisioning calls, one import, one token.
/// </summary>
/// <remarks>
/// <para>
/// Every call carries a profile id, because every call is made <b>as the client's own service
/// principal profile</b> rather than as the application. That is isolation wall 3: a profile can
/// reach its own workspace and nothing else, so even a bug that passed the wrong workspace id
/// would be refused by Power BI rather than served.
/// </para>
/// <para>
/// Notice what is not here: no method takes anything a browser sent. Workspace ids, report ids,
/// dataset ids and company codes are all read from the portal's own database by the callers of
/// this interface.
/// </para>
/// </remarks>
public interface IPowerBiClient
{
    /// <summary>
    /// Creates the service principal profile that will own one client's workspace.
    /// </summary>
    /// <remarks>
    /// Profiles belong permanently to the identity that created them. A profile created by a
    /// local app registration is invisible to a deployed managed identity, which is why each
    /// environment owns its own and dev profiles are treated as disposable. See
    /// docs/implementation-plan.md §1.4.
    /// </remarks>
    Task<PowerBiProfile> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default);

    Task<PowerBiWorkspace> CreateWorkspaceAsync(string profileId, string name, CancellationToken cancellationToken = default);

    /// <summary>Puts the workspace on the Fabric capacity. Without this, embedding does not work at all.</summary>
    Task AssignToCapacityAsync(string profileId, string workspaceId, string capacityId, CancellationToken cancellationToken = default);

    /// <summary>Uploads a <c>.pbix</c> and waits for Power BI to finish importing it.</summary>
    Task<PowerBiImport> ImportReportAsync(PowerBiImportRequest request, CancellationToken cancellationToken = default);

    Task<PowerBiEmbedTarget> GetReportAsync(string profileId, string workspaceId, string reportId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a short-lived token for exactly one report and one semantic model, carrying the
    /// viewer's effective identity.
    /// </summary>
    Task<PowerBiEmbedToken> GenerateEmbedTokenAsync(PowerBiEmbedTokenRequest request, CancellationToken cancellationToken = default);
}

public sealed record PowerBiProfile(string Id, string DisplayName);

public sealed record PowerBiWorkspace(string Id, string Name);

public sealed record PowerBiImportRequest(
    string ProfileId,
    string WorkspaceId,
    string DatasetDisplayName,
    Stream Content,
    string FileName);

public sealed record PowerBiImport(string ImportId, string ReportId, string DatasetId, string EmbedUrl);

public sealed record PowerBiEmbedTarget(string ReportId, string DatasetId, string EmbedUrl);

public sealed record PowerBiEmbedTokenRequest(
    string ProfileId,
    string WorkspaceId,
    string ReportId,
    string DatasetId,
    PowerBiEffectiveIdentity Identity,
    int LifetimeMinutes);

/// <summary>
/// Who Power BI should think is looking, which is what the model's row-level security filters on.
/// </summary>
/// <param name="Username">
/// What <c>USERPRINCIPALNAME()</c> returns inside the model. For a client this is their company
/// codes joined by <c>|</c>; for ACP staff it is their email. Capped at 256 ASCII characters
/// with no spaces — see <c>EffectiveIdentityFactory</c>, which refuses rather than truncates.
/// </param>
/// <param name="CustomData">
/// The fallback if <paramref name="Username"/> turns out not to carry the codes — open question
/// A3. Set one or the other, never both.
/// </param>
public sealed record PowerBiEffectiveIdentity(
    string Username,
    IReadOnlyList<string> Roles,
    string? CustomData = null);

/// <param name="ExpiresAt">
/// Power BI's own answer, not a local calculation. The token's life is capped by whatever is
/// left on the Entra token used to mint it, so a 30-minute request can come back shorter and
/// the renewal schedule has to follow this value rather than the request.
/// </param>
public sealed record PowerBiEmbedToken(string Token, DateTimeOffset ExpiresAt);
