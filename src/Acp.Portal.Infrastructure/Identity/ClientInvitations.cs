using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.Identity;

/// <summary>Raised when an invitation is well-formed but must not be sent.</summary>
public sealed class InvitationRefusedException(string reason) : PortalRuleException(reason);

/// <summary>
/// Inviting a client contact: one directory call and one row, in that order.
/// </summary>
/// <remarks>
/// The order matters. The directory call can fail for reasons outside the portal — a missing
/// permission, a blocked domain — and a row written before it would leave a person who exists
/// in the portal and nowhere else. Writing it afterwards means a failed invitation leaves no
/// trace, which is the state a consultant can simply retry from.
/// </remarks>
public sealed class ClientInvitations(
    PortalDbContext db,
    IGuestInviter inviter,
    IOptions<PortalAuthOptions> options,
    TimeProvider clock,
    ILogger<ClientInvitations> logger)
{
    public async Task<InvitationOutcome> InviteAsync(
        string email,
        string? displayName,
        Guid invitedBy,
        CancellationToken cancellationToken = default)
    {
        var address = EntraClaims.NormalizeEmail(email);
        var name = string.IsNullOrWhiteSpace(displayName)
            ? address[..address.IndexOf('@')]
            : displayName.Trim();

        // The address is checked before the database is, because a colleague who has never
        // signed in has no row to find. Entra would reject the invitation anyway — you cannot
        // invite a member of your own directory as a guest of it — but refusing here says why,
        // and says it without a round trip.
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == address, cancellationToken);

        if (options.Value.IsStaffEmailDomain(address) || existing is { Kind: UserKind.AcpStaff })
        {
            // ACP staff already reach every consultant screen. Making one a client contact as
            // well would give a single row two meanings, and every later authorization question
            // would have to ask which one applied.
            throw new InvitationRefusedException(
                $"{address} is an ACP address. Staff sign in as members of the directory and "
                + "cannot also be invited as a client contact.");
        }

        var result = await inviter.InviteAsync(
            new GuestInvitation(address, name, options.Value.InviteRedirectUrl),
            cancellationToken);

        var now = clock.GetUtcNow();

        // The directory may know this person under an id we have already seen, under a row we
        // found by address, or not at all. Prefer the id: it is the thing that cannot change.
        var user = await db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == result.ObjectId, cancellationToken)
            ?? existing;

        var alreadyExisted = user is not null;

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                EntraObjectId = result.ObjectId,
                Email = address,
                DisplayName = name,
                Kind = UserKind.ClientUser,
                CreatedAt = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.EntraObjectId = result.ObjectId;
            user.Email = address;
            user.DisplayName = name;
        }

        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = invitedBy,
            Action = alreadyExisted ? "user.re-invited" : "user.invited",
            // The address is the point of the record. The redeem URL is a bearer credential for
            // the invitation and never goes in a log.
            Detail = JsonSerializer.Serialize(new { email = address, invitedUserId = user.Id }),
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("{Email} invited as a client contact by {InvitedBy}.", address, invitedBy);

        return new InvitationOutcome(user, result.RedeemUrl, alreadyExisted);
    }
}

/// <param name="AlreadyExisted">
/// True when the person was already known. Re-inviting is allowed and is the normal fix for a
/// lost invitation email, so this is information for the screen rather than an error.
/// </param>
public sealed record InvitationOutcome(User User, string RedeemUrl, bool AlreadyExisted);
