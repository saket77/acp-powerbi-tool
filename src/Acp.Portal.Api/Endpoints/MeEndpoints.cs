using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.Identity;

namespace Acp.Portal.Api.Endpoints;

public static class MeEndpoints
{
    /// <summary>
    /// Who the caller is, or a plain 401 saying how to become somebody.
    /// </summary>
    /// <remarks>
    /// The front end has no other way to know whether it is signed in: the session is an
    /// HttpOnly cookie, so JavaScript cannot look. Answering the 401 with a small body rather
    /// than an empty one is what lets the sign-in screen know whether to offer a Microsoft
    /// button or the development picker, without the front end being configured separately.
    /// </remarks>
    public static IEndpointRouteBuilder MapMeEndpoint(this IEndpointRouteBuilder app, AuthMode mode)
    {
        app.MapGet("/api/me", (ClaimsPrincipal principal) =>
        {
            var userId = principal.PortalUserId();

            if (userId is null)
            {
                return Results.Json(new
                {
                    authenticated = false,
                    authMode = mode.ToString(),
                    signInUrl = mode == AuthMode.Entra ? "/api/auth/login" : null,
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new
            {
                authenticated = true,
                authMode = mode.ToString(),
                userId,
                displayName = principal.FindFirstValue(ClaimTypes.Name),
                email = principal.FindFirstValue(ClaimTypes.Email),
                kind = principal.FindFirst(PortalClaims.Kind)?.Value,
                objectId = principal.FindFirst(PortalClaims.ObjectId)?.Value,
            });
        }).AllowAnonymous();

        return app;
    }
}
