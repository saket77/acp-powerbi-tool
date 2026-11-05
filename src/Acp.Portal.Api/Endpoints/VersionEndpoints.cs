using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.Projects;

namespace Acp.Portal.Api.Endpoints;

public static class VersionEndpoints
{
    /// <summary>Uploading and releasing Databooks. Consultants only.</summary>
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/acp/projects/{key}/versions")
            .RequireAuthorization(PortalClaims.AcpStaffPolicy);

        group.MapGet("/", async (
            string key,
            ReportVersions versions,
            CancellationToken cancellationToken) =>
        {
            var all = await versions.ListAsync(key, cancellationToken);
            return all is null ? Results.NotFound() : Results.Ok(all);
        });

        group.MapPost("/", async (
            string key,
            IFormFile file,
            ClaimsPrincipal principal,
            ReportVersions versions,
            CancellationToken cancellationToken) =>
        {
            if (!file.FileName.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["A Databook is a .pbix file."],
                });
            }

            await using var content = file.OpenReadStream();

            var version = await versions.ImportAsync(
                key, content, file.FileName, principal.PortalUserId()!.Value, cancellationToken);

            return version is null ? Results.NotFound() : Results.Ok(version);
        })
        // The session cookie is SameSite=Strict, so a cross-site form post carries no session
        // and this endpoint cannot be reached by one. ASP.NET Core's antiforgery filter for
        // form uploads would otherwise need a token the SPA has no other use for.
        .DisableAntiforgery();

        group.MapPost("/{version:int}/release", async (
            string key,
            int version,
            ReleaseRequest? request,
            ClaimsPrincipal principal,
            ReportVersions versions,
            CancellationToken cancellationToken) =>
        {
            var released = await versions.ReleaseAsync(
                key, version, request?.Note, principal.PortalUserId()!.Value, cancellationToken);

            return released is null ? Results.NotFound() : Results.Ok(released);
        });

        return app;
    }
}

/// <param name="Note">What changed in this release. Clients see it on their Databook.</param>
public sealed record ReleaseRequest(string? Note);
