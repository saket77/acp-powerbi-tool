using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.Projects;

namespace Acp.Portal.Api.Endpoints;

public static class ProjectEndpoints
{
    /// <summary>
    /// The projects the caller may see. The same two endpoints serve consultants and clients.
    /// </summary>
    /// <remarks>
    /// There is no "if staff" anywhere in here, and no filter by user either. Row-level security
    /// has already removed every project the caller holds no grant on, so the consultant screens
    /// and the client screens read the same rows through the same query and simply get different
    /// answers. A 404 from the detail endpoint means "no such project, or not yours" — the
    /// database does not distinguish, so neither can this.
    /// </remarks>
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapGet("/", async (ProjectDirectory projects, CancellationToken cancellationToken) =>
            Results.Ok(await projects.ListAsync(cancellationToken)));

        group.MapGet("/{key}/summary", async (
            string key,
            ProjectDirectory projects,
            CancellationToken cancellationToken) =>
        {
            var project = await projects.GetAsync(key, cancellationToken);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        return app;
    }

    /// <summary>Creating projects and reading them with their grants. Consultants only.</summary>
    public static IEndpointRouteBuilder MapAcpProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/acp/projects").RequireAuthorization(PortalClaims.AcpStaffPolicy);

        group.MapPost("/", async (
            CreateProjectRequest request,
            ClaimsPrincipal principal,
            ProjectDirectory projects,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.ClientSlug) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["A project needs a client and a name."],
                });
            }

            // Default to a year out rather than refusing: an end date is a contractual fact the
            // consultant may not have to hand when they are setting the project up.
            var endDate = request.EndDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);

            var project = await projects.CreateAsync(
                request.ClientSlug,
                request.Key ?? "",
                request.Name,
                endDate,
                principal.PortalUserId()!.Value,
                cancellationToken);

            return Results.Created($"/api/acp/projects/{project.Key}", await projects.GetAsync(project.Key, cancellationToken));
        });

        group.MapGet("/{key}", async (
            string key,
            ProjectDirectory projects,
            GrantService grants,
            CancellationToken cancellationToken) =>
        {
            var project = await projects.GetAsync(key, cancellationToken);
            if (project is null) return Results.NotFound();

            var people = await grants.ListAsync(key, cancellationToken);

            return Results.Ok(new { project, grants = people ?? [] });
        });

        return app;
    }
}

/// <param name="Key">Optional. Derived from the name when left out.</param>
/// <param name="EndDate">Optional. A year from today when left out.</param>
public sealed record CreateProjectRequest(string? ClientSlug, string? Key, string? Name, DateOnly? EndDate);
