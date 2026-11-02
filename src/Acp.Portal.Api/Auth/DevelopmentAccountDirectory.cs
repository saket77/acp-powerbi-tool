using System.Security.Claims;
using Acp.Portal.Domain;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Api.Auth;

/// <summary>
/// Who you can sign in as while there is no tenant: the roster in configuration, plus every
/// client contact the portal has invited.
/// </summary>
/// <remarks>
/// Registered only when <see cref="AuthMode.Development"/> is on, and the endpoints that use it
/// are only mapped then — so in any other mode this surface does not exist to be reached. The
/// principals it builds carry ordinary Entra claim names, which means they are read by the same
/// <see cref="EntraClaims"/> code a real token goes through.
/// </remarks>
public sealed class DevelopmentAccountDirectory(PortalDbContext db, IOptions<PortalAuthOptions> options)
{
    private const string InvitedKeyPrefix = "invited:";

    public async Task<IReadOnlyList<DevelopmentAccountView>> ListAsync(CancellationToken cancellationToken)
    {
        var roster = options.Value.Development.Accounts;

        var accounts = roster
            .Select(a => new DevelopmentAccountView(a.Key, a.Email, a.DisplayName, a.Guest, "configured"))
            .ToList();

        var known = roster.Select(a => a.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Invited clients appear here the moment a consultant invites them, which is what makes
        // the invite → sign-in → "Kind: ClientUser" loop testable with no identity provider.
        var invited = await db.Users
            .AsNoTracking()
            .Where(u => u.Kind == UserKind.ClientUser)
            .OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(cancellationToken);

        accounts.AddRange(invited
            .Where(u => !known.Contains(u.Email))
            .Select(u => new DevelopmentAccountView(
                InvitedKeyPrefix + u.Id, u.Email, u.DisplayName, Guest: true, "invited")));

        return accounts;
    }

    /// <summary>
    /// Builds the principal an identity provider would have produced for this person, or null
    /// when the key names nobody.
    /// </summary>
    public async Task<ClaimsPrincipal?> BuildPrincipalAsync(string key, CancellationToken cancellationToken)
    {
        var configured = options.Value.Development.Accounts
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

        if (configured is not null)
        {
            return Build(
                configured.ObjectId,
                configured.Email,
                configured.DisplayName,
                configured.TenantId ?? options.Value.HomeTenantId,
                configured.Guest);
        }

        if (!key.StartsWith(InvitedKeyPrefix, StringComparison.Ordinal)) return null;
        if (!Guid.TryParse(key[InvitedKeyPrefix.Length..], out var userId)) return null;

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Kind == UserKind.ClientUser, cancellationToken);

        return user is null
            ? null
            : Build(user.EntraObjectId, user.Email, user.DisplayName, tenantId: null, guest: true);
    }

    private static ClaimsPrincipal Build(
        string objectId,
        string email,
        string displayName,
        string? tenantId,
        bool guest)
    {
        var identity = new ClaimsIdentity(AuthenticationSetup.DevelopmentAuthenticationType);

        identity.AddClaim(new Claim(EntraClaims.ObjectIdClaim, objectId));
        identity.AddClaim(new Claim("preferred_username", email));
        identity.AddClaim(new Claim("name", displayName));

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            identity.AddClaim(new Claim(EntraClaims.TenantIdClaim, tenantId));
        }

        // Entra's own way of saying "guest". Using the real claim rather than a flag of our own
        // means the classification code under test here is the classification code in production.
        identity.AddClaim(new Claim(EntraClaims.AccountTypeClaim, guest ? "1" : "0"));

        return new ClaimsPrincipal(identity);
    }
}

public sealed record DevelopmentAccountView(
    string Key,
    string Email,
    string DisplayName,
    bool Guest,
    string Source);
