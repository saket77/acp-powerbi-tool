using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.PowerBi;
using Microsoft.AspNetCore.Mvc;

namespace Acp.Portal.Api.Endpoints;

public static class EmbedEndpoints
{
    /// <summary>
    /// The token a browser needs to render one report, for whoever is asking.
    /// </summary>
    /// <remarks>
    /// One endpoint for consultants and clients. What differs is not the route but what comes
    /// back: a client gets the released version filtered to their own company codes, a
    /// consultant gets the draft and a description of the filter they are looking through.
    /// Which of those you are is read from your grant, never from the request.
    /// </remarks>
    public static IEndpointRouteBuilder MapEmbedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{key}/embed", async (
            string key,
            [FromQuery] bool? draft,
            [FromQuery] Guid? viewAs,
            ClaimsPrincipal principal,
            EmbedTokens tokens,
            CancellationToken cancellationToken) =>
        {
            var outcome = await tokens.CreateAsync(
                key,
                preferDraft: draft ?? false,
                viewAsUserId: viewAs,
                actingUser: principal.PortalUserId()!.Value,
                cancellationToken);

            return outcome switch
            {
                null => Results.NotFound(),

                // Not an error. "Nothing released yet" is the state most projects are in for
                // most of their life, and the screen has a panel for it.
                EmbedUnavailable unavailable => Results.Ok(new
                {
                    status = "unavailable",
                    unavailable.Reason,
                }),

                EmbedReady ready => Results.Ok(new
                {
                    status = "ready",
                    ready.Provider,
                    ready.EmbedUrl,
                    ready.ReportId,
                    ready.Token,
                    ready.ExpiresAt,
                    ready.Version,
                    ready.IsDraft,
                    ready.Scope,
                }),

                _ => Results.Problem("Unrecognised embed outcome."),
            };
        }).RequireAuthorization();

        return app;
    }
}
