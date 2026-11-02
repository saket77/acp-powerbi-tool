namespace Acp.Portal.Domain.Identity;

/// <summary>
/// Everything the portal is willing to learn from an identity provider: who you are, and
/// whether you are one of ours. Nothing here decides what you may see.
/// </summary>
/// <remarks>
/// This type exists so that the claim parsing happens in exactly one place and so that the
/// development sign-in can produce the same shape as Entra does. If a second identity provider
/// ever appears (Entra External ID for clients, per docs/implementation-plan.md §1.2), it
/// produces one of these and the rest of the system cannot tell the difference.
/// </remarks>
/// <param name="ObjectId">
/// The provider's stable identifier for this person. Survives name and email changes.
/// </param>
/// <param name="Email">Lower-cased. Used to match a person to an invitation sent before they first signed in.</param>
/// <param name="DisplayName">For showing. Never for matching.</param>
/// <param name="TenantId">The directory that issued the token, if it said.</param>
/// <param name="IsGuest">
/// True when the provider says this is an external account in the directory rather than a
/// member of it. A guest is never ACP staff.
/// </param>
public sealed record ExternalIdentity(
    string ObjectId,
    string Email,
    string DisplayName,
    string? TenantId,
    bool IsGuest);
