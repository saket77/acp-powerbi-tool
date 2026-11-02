using System.Net.Mail;
using System.Security.Claims;
using Acp.Portal.Api.Auth;
using Acp.Portal.Domain;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Api.Endpoints;

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // Everything under /api/acp is for consultants. The policy is on the group rather than
        // on each endpoint so that a new one cannot be added without it.
        var group = app.MapGroup("/api/acp").RequireAuthorization(PortalClaims.AcpStaffPolicy);

        group.MapPost("/invitations", async (
            InviteClientContactRequest request,
            ClaimsPrincipal principal,
            ClientInvitations invitations,
            CancellationToken cancellationToken) =>
        {
            if (!MailAddress.TryCreate(request.Email?.Trim(), out var address))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["email"] = ["Enter an email address."],
                });
            }

            var invitedBy = principal.PortalUserId()!.Value;

            try
            {
                var outcome = await invitations.InviteAsync(
                    address.Address, request.DisplayName, invitedBy, cancellationToken);

                return Results.Created($"/api/acp/users/{outcome.User.Id}", new
                {
                    userId = outcome.User.Id,
                    email = outcome.User.Email,
                    displayName = outcome.User.DisplayName,
                    kind = outcome.User.Kind.ToString(),
                    outcome.AlreadyExisted,
                    // Returned so a consultant can pass the link on when the email does not
                    // arrive, which with personal Microsoft accounts is common enough to plan for.
                    redeemUrl = outcome.RedeemUrl,
                });
            }
            catch (InvitationRefusedException refused)
            {
                return Results.Problem(refused.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // The list a consultant checks after inviting someone. Phase 3 replaces it with the
        // per-project grant screens; for now it is how you see that an invitation landed.
        group.MapGet("/users", async (PortalDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.Users
                .AsNoTracking()
                .OrderBy(u => u.Kind)
                .ThenBy(u => u.Email)
                .Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.DisplayName,
                    Kind = u.Kind.ToString(),
                    u.CreatedAt,
                    u.LastSeenAt,
                    HasSignedIn = u.LastSeenAt != null,
                })
                .ToListAsync(cancellationToken)));

        return app;
    }
}

/// <param name="DisplayName">Optional. Falls back to the part of the address before the @.</param>
public sealed record InviteClientContactRequest(string? Email, string? DisplayName);
