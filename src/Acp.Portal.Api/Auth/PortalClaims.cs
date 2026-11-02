using System.Security.Claims;
using Acp.Portal.Domain;

namespace Acp.Portal.Api.Auth;

/// <summary>
/// What the session cookie carries, and the only claims any authorization decision reads.
/// </summary>
/// <remarks>
/// The cookie holds these five values and nothing from the identity provider — no access token,
/// no refresh token, no id token. That is what "no token is visible in the browser" means in
/// practice: there is no token anywhere to be visible, because the portal does not need one
/// after sign-in. It calls Microsoft Graph and Power BI as itself, never as the person.
/// </remarks>
public static class PortalClaims
{
    /// <summary>The portal's own user id. The key to every grant.</summary>
    public const string UserId = "acp:uid";

    /// <summary><see cref="UserKind"/> as text.</summary>
    public const string Kind = "acp:kind";

    /// <summary>Copy of <c>Users.SecurityStamp</c> at sign-in. Checked on every request.</summary>
    public const string SecurityStamp = "acp:stamp";

    /// <summary>The directory's object id. Kept for support and audit, never for authorization.</summary>
    public const string ObjectId = "acp:oid";

    public const string AcpStaffPolicy = "AcpStaff";

    public static Guid? PortalUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(UserId)?.Value, out var id) ? id : null;

    public static UserKind? PortalKind(this ClaimsPrincipal principal) =>
        Enum.TryParse<UserKind>(principal.FindFirst(Kind)?.Value, out var kind) ? kind : null;

    public static bool IsAcpStaff(this ClaimsPrincipal principal) =>
        principal.PortalKind() == UserKind.AcpStaff;
}
