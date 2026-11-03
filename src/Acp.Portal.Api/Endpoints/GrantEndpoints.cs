using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Domain;
using Acp.Portal.Infrastructure.Projects;

namespace Acp.Portal.Api.Endpoints;

public static class GrantEndpoints
{
    /// <summary>
    /// Who may see a project. Consultants only, and only leads may change anything.
    /// </summary>
    /// <remarks>
    /// Every one of these resolves the project through the caller's own grant first, so a
    /// consultant who is not on a project gets 404 from all of them — including the ones that
    /// would otherwise have told them it exists by refusing differently.
    /// </remarks>
    public static IEndpointRouteBuilder MapGrantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/acp/projects/{key}/grants")
            .RequireAuthorization(PortalClaims.AcpStaffPolicy);

        group.MapGet("/", async (
            string key,
            GrantService grants,
            CancellationToken cancellationToken) =>
        {
            var people = await grants.ListAsync(key, cancellationToken);
            return people is null ? Results.NotFound() : Results.Ok(people);
        });

        group.MapPost("/", async (
            string key,
            SetGrantRequest request,
            ClaimsPrincipal principal,
            GrantService grants,
            CancellationToken cancellationToken) =>
        {
            if (request.UserId is null || !Enum.TryParse<ProjectRole>(request.Role, ignoreCase: true, out var role))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["role"] = ["Name a person and one of Lead, Member, Viewer or Client."],
                });
            }

            var grant = await grants.SetAsync(
                key,
                request.UserId.Value,
                role,
                request.EndDate,
                request.CompanyIds ?? [],
                principal.PortalUserId()!.Value,
                cancellationToken);

            return grant is null ? Results.NotFound() : Results.Ok(grant);
        });

        group.MapDelete("/{grantId:guid}", async (
            string key,
            Guid grantId,
            ClaimsPrincipal principal,
            GrantService grants,
            CancellationToken cancellationToken) =>
        {
            var removed = await grants.RevokeAsync(
                key, grantId, principal.PortalUserId()!.Value, cancellationToken);

            return removed ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}

/// <param name="CompanyIds">
/// Required for role <c>Client</c>, forbidden otherwise. Must be companies of this project's own
/// client — from Phase 4 these become the codes inside an embed token.
/// </param>
public sealed record SetGrantRequest(
    Guid? UserId,
    string? Role,
    DateOnly? EndDate,
    List<Guid>? CompanyIds);
