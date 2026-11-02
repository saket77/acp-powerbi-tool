namespace Acp.Portal.Infrastructure.Identity;

/// <summary>
/// Bound from the <c>Entra</c> configuration section. The same section is handed to
/// Microsoft.Identity.Web, so the property names match what it expects.
/// </summary>
/// <remarks>
/// Every value here comes from scripts/tenant-setup.md. None of them is a secret except
/// <see cref="ClientSecret"/>, which belongs in config/entra.local.json (git-ignored) or in
/// user-secrets, and which a deployed environment does not have at all — there the portal
/// authenticates as its managed identity.
/// </remarks>
public sealed class EntraOptions
{
    public const string SectionName = "Entra";

    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    public string TenantId { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>Local development only. Empty in Azure, where a managed identity replaces it.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Must be registered on the app registration as a redirect URI, exactly.</summary>
    public string CallbackPath { get; set; } = "/api/auth/callback";

    public string SignedOutCallbackPath { get; set; } = "/api/auth/signed-out";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}
