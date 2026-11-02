using System.Security.Claims;
using Acp.Portal.Domain.Abstractions;

namespace Acp.Portal.Api.Auth;

/// <summary>
/// Trades a principal from an identity provider for the portal's own, slim one.
/// </summary>
/// <remarks>
/// Both sign-in routes end here: the real OpenID Connect callback and the development account
/// picker. That is the point of it. The development loop does not bypass identity resolution,
/// classification or the security stamp — it only replaces where the claims came from, so what
/// runs on a laptop is the same code that runs in Azure.
/// </remarks>
public sealed class PortalPrincipalFactory(IIdentityResolver resolver)
{
    /// <summary>
    /// Returns null when the incoming principal cannot be resolved to a person. The caller
    /// turns that into a failed sign-in, never into an exception: an unusable token is an
    /// ordinary thing for an endpoint to see and not an outage.
    /// </summary>
    public async Task<ClaimsPrincipal?> CreateAsync(
        ClaimsPrincipal external,
        string authenticationType,
        CancellationToken cancellationToken = default)
    {
        var user = await resolver.ResolveAsync(external, cancellationToken);
        if (user is null) return null;

        var identity = new ClaimsIdentity(
            authenticationType,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        identity.AddClaim(new Claim(PortalClaims.UserId, user.Id.ToString()));
        identity.AddClaim(new Claim(PortalClaims.Kind, user.Kind.ToString()));
        identity.AddClaim(new Claim(PortalClaims.SecurityStamp, user.SecurityStamp));
        identity.AddClaim(new Claim(PortalClaims.ObjectId, user.EntraObjectId));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));

        return new ClaimsPrincipal(identity);
    }
}
