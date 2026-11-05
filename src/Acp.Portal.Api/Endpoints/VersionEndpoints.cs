using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.Projects;

namespace Acp.Portal.Api.Endpoints;

public static class VersionEndpoints
{
    /// <summary>
    /// Uploading and releasing Databooks. Consultants only.
    /// </summary>
    /// <remarks>
    /// Uploading is two calls with a direct browser-to-Blob transfer in between. The file never
    /// touches this process, which is the point: a 200 MB Databook on a slow connection would
    /// otherwise hold a request open for as long as it takes.
    /// </remarks>
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        var versions = app.MapGroup("/api/acp/projects/{key}/versions")
            .RequireAuthorization(PortalClaims.AcpStaffPolicy);

        versions.MapGet("/", async (
            string key,
            ReportVersions reportVersions,
            CancellationToken cancellationToken) =>
        {
            var all = await reportVersions.ListAsync(key, cancellationToken);
            return all is null ? Results.NotFound() : Results.Ok(all);
        });

        versions.MapPost("/{version:int}/release", async (
            string key,
            int version,
            ReleaseRequest? request,
            ClaimsPrincipal principal,
            ReportVersions reportVersions,
            CancellationToken cancellationToken) =>
        {
            var released = await reportVersions.ReleaseAsync(
                key, version, request?.Note, principal.PortalUserId()!.Value, cancellationToken);

            return released is null ? Results.NotFound() : Results.Ok(released);
        });

        var uploads = app.MapGroup("/api/acp/projects/{key}/uploads")
            .RequireAuthorization(PortalClaims.AcpStaffPolicy);

        uploads.MapPost("/", async (
            string key,
            StartUploadRequest request,
            ClaimsPrincipal principal,
            ReportVersions reportVersions,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.FileName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["fileName"] = ["Name the file you are uploading."],
                });
            }

            var staged = await reportVersions.StageAsync(
                key, request.FileName, principal.PortalUserId()!.Value, cancellationToken);

            return staged is null ? Results.NotFound() : Results.Ok(staged);
        });

        uploads.MapPost("/{uploadId:guid}/complete", async (
            string key,
            Guid uploadId,
            ClaimsPrincipal principal,
            ReportVersions reportVersions,
            CancellationToken cancellationToken) =>
        {
            var version = await reportVersions.CompleteAsync(
                key, uploadId, principal.PortalUserId()!.Value, cancellationToken);

            return version is null ? Results.NotFound() : Results.Ok(version);
        });

        return app;
    }
}

/// <param name="FileName">Used for the extension check and the blob's name. Never trusted as a path.</param>
public sealed record StartUploadRequest(string? FileName);

/// <param name="Note">What changed in this release. Clients see it on their Databook.</param>
public sealed record ReleaseRequest(string? Note);
