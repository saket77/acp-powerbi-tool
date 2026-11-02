using Acp.Portal.Domain.Entities;

namespace Acp.Portal.Domain.Abstractions;

/// <summary>
/// Invites an external person into the directory so they can sign in with their own Microsoft
/// account. The only write the portal ever makes to an identity provider.
/// </summary>
/// <remarks>
/// Inviting does not grant anything. It creates a <see cref="User"/> row with no grants, which
/// means the invitee signs in successfully and sees an empty portal until someone grants them a
/// project. That separation is deliberate: invitation is about identity, grants are about access,
/// and the two are reviewed by different people.
/// </remarks>
public interface IGuestInviter
{
    Task<GuestInvitationResult> InviteAsync(
        GuestInvitation invitation,
        CancellationToken cancellationToken = default);
}

/// <param name="Email">Where the invitation goes. Any Microsoft account, work or personal.</param>
/// <param name="DisplayName">How the person appears in the directory and the portal.</param>
/// <param name="RedirectUrl">Where the invitee lands after accepting. Must be the portal's own origin.</param>
public sealed record GuestInvitation(string Email, string DisplayName, string RedirectUrl);

/// <param name="ObjectId">
/// The directory's id for the invited account. Recorded immediately, so that when the person
/// does sign in they are matched by id rather than by the email they happened to use.
/// </param>
/// <param name="RedeemUrl">
/// The link that accepts the invitation. Microsoft also emails it; it is returned so the person
/// doing the inviting can pass it on when the email does not arrive.
/// </param>
public sealed record GuestInvitationResult(string ObjectId, string RedeemUrl);
