using Acp.Portal.Domain.Abstractions;

namespace Acp.Portal.Api.Auth;

/// <summary>
/// Tells the data layer who is making this request, from the session cookie's claims.
/// </summary>
/// <remarks>
/// Notice what it never reports: <c>Unfiltered</c> is hard-coded false. A request is never
/// privileged, whoever is making it — ACP staff are subject to row-level security exactly as
/// clients are, and see projects because they hold grants on them, not because of who they are.
/// </remarks>
public sealed class HttpCurrentUserAccessor(IHttpContextAccessor accessor) : ICurrentUserAccessor
{
    public PortalCaller Current
    {
        get
        {
            var userId = accessor.HttpContext?.User.PortalUserId();
            return userId is null ? PortalCaller.Anonymous : new PortalCaller(userId, Unfiltered: false);
        }
    }
}
