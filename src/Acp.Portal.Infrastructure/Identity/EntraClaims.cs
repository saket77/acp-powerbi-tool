using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Acp.Portal.Domain.Identity;

namespace Acp.Portal.Infrastructure.Identity;

/// <summary>
/// Reads an <see cref="ExternalIdentity"/> out of a signed-in principal. The only place in the
/// portal that knows what an Entra claim is called.
/// </summary>
/// <remarks>
/// Every value is looked up under several names on purpose. Whether a claim arrives as
/// <c>oid</c> or as the long <c>schemas.microsoft.com</c> URI depends on whether inbound claim
/// mapping is on, which depends on the handler, which changes between library versions. Reading
/// both is three lines; discovering the difference in a deployed environment is an afternoon.
/// </remarks>
public static class EntraClaims
{
    public const string ObjectIdClaim = "oid";
    public const string ObjectIdClaimLong = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    public const string TenantIdClaim = "tid";
    public const string TenantIdClaimLong = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>Entra's <c>acct</c> optional claim: 0 is a member of the directory, 1 is a guest.</summary>
    public const string AccountTypeClaim = "acct";

    /// <summary>Present when the account signed in somewhere other than this directory — i.e. a guest.</summary>
    public const string IdentityProviderClaim = "idp";

    private static readonly string[] ObjectIdNames = [ObjectIdClaim, ObjectIdClaimLong, ClaimTypes.NameIdentifier, "sub"];
    private static readonly string[] TenantIdNames = [TenantIdClaim, TenantIdClaimLong];
    private static readonly string[] EmailNames = ["preferred_username", "email", ClaimTypes.Email, ClaimTypes.Upn, "upn"];
    private static readonly string[] NameNames = ["name", ClaimTypes.Name, ClaimTypes.GivenName];

    /// <summary>
    /// Builds an identity from a principal, or returns false when the principal carries no
    /// stable id or no email. False means "not usable as a sign-in", never "an error happened" —
    /// the caller turns it into a plain 401.
    /// </summary>
    public static bool TryRead(ClaimsPrincipal principal, [NotNullWhen(true)] out ExternalIdentity? identity)
    {
        identity = null;

        var objectId = First(principal, ObjectIdNames);
        if (string.IsNullOrWhiteSpace(objectId)) return false;

        var email = First(principal, EmailNames);
        if (string.IsNullOrWhiteSpace(email)) return false;

        var displayName = First(principal, NameNames);

        identity = new ExternalIdentity(
            ObjectId: objectId.Trim(),
            Email: NormalizeEmail(email),
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? email.Trim() : displayName.Trim(),
            TenantId: First(principal, TenantIdNames)?.Trim(),
            IsGuest: LooksLikeGuest(principal, email));

        return true;
    }

    /// <summary>
    /// Emails are compared, not displayed, so they are lower-cased once here and never again.
    /// Invariant culture deliberately: the Turkish I would otherwise make <c>I</c> and <c>ı</c>
    /// the same address on a machine with a Turkish locale and different everywhere else.
    /// </summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// Three independent signals, any of which means external. A guest is never ACP staff, so
    /// this errs towards saying yes: a false positive costs a staff member their staff rights
    /// and is noticed in a minute, a false negative hands a stranger the consultant screens.
    /// </summary>
    private static bool LooksLikeGuest(ClaimsPrincipal principal, string email)
    {
        // The explicit claim, when the app registration asks for it.
        if (principal.FindFirst(AccountTypeClaim)?.Value is "1") return true;

        // B2B guests get a mangled UPN in the inviting directory: alice_outlook.com#EXT#@acp.de
        if (email.Contains("#EXT#", StringComparison.OrdinalIgnoreCase)) return true;

        // Signed in somewhere else and redeemed an invitation here.
        if (!string.IsNullOrWhiteSpace(principal.FindFirst(IdentityProviderClaim)?.Value)) return true;

        return false;
    }

    private static string? First(ClaimsPrincipal principal, string[] names)
    {
        foreach (var name in names)
        {
            var value = principal.FindFirst(name)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }
}
