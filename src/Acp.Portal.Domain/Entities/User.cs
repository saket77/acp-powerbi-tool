namespace Acp.Portal.Domain.Entities;

/// <summary>
/// Someone who can sign in. ACP staff and client contacts are the same kind of record,
/// distinguished by <see cref="Kind"/> — what they may actually see comes from their grants,
/// never from their kind.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>
    /// The stable object id from Entra. This is the only thing the identity provider tells us
    /// that we keep; everything about what this person may see lives in <see cref="ProjectGrant"/>.
    /// Moving clients to a separate External ID tenant later changes which claim fills this
    /// field and nothing else.
    /// </summary>
    public required string EntraObjectId { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public UserKind Kind { get; set; }

    /// <summary>
    /// Changes whenever this person's access changes. The session cookie carries the value it
    /// was issued with, so bumping this signs the person out on their very next request —
    /// which is the wireframe's rule that a revoked grant takes effect immediately rather than
    /// whenever the cookie happens to expire.
    /// </summary>
    /// <remarks>
    /// Phase 2 only issues and checks it. Phase 3 is what bumps it, from the grant endpoints.
    /// </remarks>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}
