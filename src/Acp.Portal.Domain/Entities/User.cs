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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}
