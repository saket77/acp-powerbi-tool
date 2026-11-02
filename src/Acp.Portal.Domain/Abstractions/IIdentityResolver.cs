using System.Security.Claims;
using Acp.Portal.Domain.Entities;

namespace Acp.Portal.Domain.Abstractions;

/// <summary>
/// The single seam between "who signed in" and everything else.
/// </summary>
/// <remarks>
/// The MVP signs clients in as B2B guests of ACP's own tenant; production is likely to move
/// them to a separate Entra External ID tenant. That change rewrites one implementation of
/// this interface — which claim carries the stable id, and how a new user is matched to an
/// invitation — and touches nothing else, because no authorization decision anywhere reads a
/// claim. See docs/implementation-plan.md §1.2.
/// </remarks>
public interface IIdentityResolver
{
    /// <summary>
    /// Maps an authenticated principal to the portal's own user record, creating it on first
    /// sign-in. Returns null when the principal carries no usable stable identifier, which the
    /// caller must treat as not signed in rather than as an error.
    /// </summary>
    Task<User?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
