using System.Security.Claims;
using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Domain.Identity;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.Identity;

/// <summary>
/// Turns "someone signed in" into "this row in Users". The one place an identity provider's
/// word is translated into the portal's own idea of a person.
/// </summary>
/// <remarks>
/// Runs once per sign-in, not per request. Everything after it — every grant check, every
/// embed token — reads the database and never a claim. That is what makes swapping the identity
/// provider a change to this file and nothing else.
/// </remarks>
public sealed class IdentityResolver(
    PortalDbContext db,
    IOptions<PortalAuthOptions> options,
    TimeProvider clock,
    ILogger<IdentityResolver> logger) : IIdentityResolver
{
    private readonly PortalAuthOptions _options = options.Value;

    public async Task<User?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (!EntraClaims.TryRead(principal, out var identity))
        {
            logger.LogWarning(
                "Rejected a sign-in: the principal carried no usable object id or email. Claims present: {Claims}",
                string.Join(", ", principal.Claims.Select(c => c.Type)));
            return null;
        }

        var kind = Classify(identity, _options);
        var now = clock.GetUtcNow();

        var user = await db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == identity.ObjectId, cancellationToken)
            ?? await MatchPendingInvitationAsync(identity, cancellationToken);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                EntraObjectId = identity.ObjectId,
                Email = identity.Email,
                DisplayName = identity.DisplayName,
                Kind = kind,
                CreatedAt = now,
            };

            db.Users.Add(user);
            Audit(user, "user.created", new { identity.IsGuest, kind = kind.ToString() }, now);

            logger.LogInformation(
                "First sign-in for {Email}, created as {Kind}. No grants yet, so they will see an empty portal.",
                identity.Email, kind);
        }
        else
        {
            // The provider is authoritative about a person's name and address, and both change.
            user.EntraObjectId = identity.ObjectId;
            user.Email = identity.Email;
            user.DisplayName = identity.DisplayName;
            ApplyKind(user, kind, now);
        }

        user.LastSeenAt = now;
        Audit(user, "user.signed-in", new { kind = user.Kind.ToString() }, now);

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Who counts as ACP staff. Four ways to fail and one way to pass, because the consequence
    /// of guessing wrong in one direction is an inconvenience and in the other is a stranger on
    /// the consultant screens.
    /// </summary>
    internal static UserKind Classify(ExternalIdentity identity, PortalAuthOptions options)
    {
        if (identity.IsGuest) return UserKind.ClientUser;

        if (!string.IsNullOrWhiteSpace(options.HomeTenantId)
            && !string.Equals(identity.TenantId, options.HomeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return UserKind.ClientUser;
        }

        if (options.StaffEmailDomains.Length > 0 && !options.IsStaffEmailDomain(identity.Email))
        {
            return UserKind.ClientUser;
        }

        return UserKind.AcpStaff;
    }

    /// <summary>
    /// A client who was invited before they ever signed in may already have a row, created by
    /// the invitation with a placeholder id. Matching that row by email is safe precisely
    /// because the email came out of a token this directory issued — but only for placeholder
    /// rows, so that a real account can never be taken over by someone who acquires its address.
    /// </summary>
    private async Task<User?> MatchPendingInvitationAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var pending = await db.Users
            .Where(u => u.Email == identity.Email)
            .FirstOrDefaultAsync(cancellationToken);

        if (pending is null) return null;

        if (!PendingObjectId.IsPending(pending.EntraObjectId))
        {
            logger.LogWarning(
                "Two accounts claim {Email}: existing object id {Existing}, signing in as {Incoming}. "
                + "Treating the incoming one as a separate person.",
                identity.Email, pending.EntraObjectId, identity.ObjectId);
            return null;
        }

        logger.LogInformation("Matched {Email} to the invitation created for them.", identity.Email);
        return pending;
    }

    /// <summary>
    /// Privileges are never granted by signing in — only taken away. Someone whose row says
    /// client stays a client even if their address later looks like staff, because the row was
    /// written by a person and the claim was not.
    /// </summary>
    private void ApplyKind(User user, UserKind classified, DateTimeOffset now)
    {
        if (user.Kind == classified) return;

        if (classified == UserKind.AcpStaff)
        {
            logger.LogWarning(
                "{Email} signs in looking like ACP staff but is recorded as a client user. "
                + "Leaving them as a client user; change the row deliberately if this is wrong.",
                user.Email);
            return;
        }

        logger.LogWarning("{Email} no longer qualifies as ACP staff. Downgrading to client user.", user.Email);
        user.Kind = classified;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        Audit(user, "user.kind-changed", new { from = UserKind.AcpStaff.ToString(), to = classified.ToString() }, now);
    }

    private void Audit(User user, string action, object detail, DateTimeOffset now) =>
        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = user.Id,
            Action = action,
            Detail = JsonSerializer.Serialize(detail),
        });
}
