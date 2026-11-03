namespace Acp.Portal.Domain.Abstractions;

/// <summary>
/// Who the database should answer as, for the current unit of work.
/// </summary>
/// <param name="UserId">
/// The portal user id, or null when nobody is signed in. Null means the database shows nothing,
/// not everything — the row-level security predicate fails closed.
/// </param>
/// <param name="Unfiltered">
/// True only for work that legitimately has no user behind it: the background import worker,
/// and test fixtures setting up the world they are about to check. Never true for a request.
/// </param>
public readonly record struct PortalCaller(Guid? UserId, bool Unfiltered)
{
    /// <summary>Nobody, and not privileged. What an unauthenticated request looks like.</summary>
    public static readonly PortalCaller Anonymous = new(null, false);
}

/// <summary>
/// The seam between "who is making this request" and "what the database will show".
/// </summary>
/// <remarks>
/// Deliberately separate from the session: by the time a query runs, the only thing that matters
/// is a user id, and taking it from here rather than from a <c>ClaimsPrincipal</c> is what lets
/// the background worker and the test fixtures use the same data access code.
/// </remarks>
public interface ICurrentUserAccessor
{
    PortalCaller Current { get; }
}
