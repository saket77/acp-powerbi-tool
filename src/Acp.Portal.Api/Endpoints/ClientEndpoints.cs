using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.PowerBi;
using Acp.Portal.Infrastructure.Projects;

namespace Acp.Portal.Api.Endpoints;

public static class ClientEndpoints
{
    public static IEndpointRouteBuilder MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/acp/clients").RequireAuthorization(PortalClaims.AcpStaffPolicy);

        group.MapPost("/", async (
            CreateClientRequest request,
            ClaimsPrincipal principal,
            ClientDirectory clients,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["A client needs a name."],
                });
            }

            var companies = (request.Companies ?? [])
                .Select(c => new NewCompany(c.Code ?? "", c.Name ?? ""))
                .ToList();

            var client = await clients.CreateAsync(
                request.Name,
                request.Slug,
                companies,
                principal.PortalUserId()!.Value,
                cancellationToken);

            return Results.Created($"/api/acp/clients/{client.Slug}", await clients.GetAsync(client.Slug, cancellationToken));
        });

        group.MapGet("/", async (ClientDirectory clients, CancellationToken cancellationToken) =>
            Results.Ok(await clients.ListAsync(cancellationToken)));

        group.MapPost("/{slug}/provision", async (
            string slug,
            ClaimsPrincipal principal,
            ClientProvisioning provisioning,
            ClientDirectory clients,
            CancellationToken cancellationToken) =>
        {
            var client = await provisioning.ProvisionAsync(
                slug, principal.PortalUserId()!.Value, cancellationToken);

            return client is null
                ? Results.NotFound()
                : Results.Ok(await clients.GetAsync(slug, cancellationToken));
        });

        group.MapGet("/{slug}", async (
            string slug,
            ClientDirectory clients,
            CancellationToken cancellationToken) =>
        {
            var client = await clients.GetAsync(slug, cancellationToken);
            return client is null ? Results.NotFound() : Results.Ok(client);
        });

        return app;
    }
}

/// <param name="Slug">Optional. Derived from the name when left out.</param>
/// <param name="Companies">The <c>Ges.</c> codes. At least one, or no client grant can be made.</param>
public sealed record CreateClientRequest(
    string? Name,
    string? Slug,
    List<NewCompanyRequest>? Companies);

public sealed record NewCompanyRequest(string? Code, string? Name);
