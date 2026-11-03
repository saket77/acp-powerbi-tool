using Acp.Portal.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Acp.Portal.Api;

/// <summary>
/// Turns a broken rule into a 409 the screen can read out loud.
/// </summary>
/// <remarks>
/// Centralised so that no endpoint has to remember. The messages are written for a consultant
/// — "that key is already taken", "make somebody else a lead first" — because the only person
/// who ever sees one is the person who can act on it.
/// </remarks>
public sealed class PortalRuleExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not PortalRuleException rule) return false;

        context.Response.StatusCode = StatusCodes.Status409Conflict;

        await context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "That is not allowed",
                Detail = rule.Message,
            },
            cancellationToken);

        return true;
    }
}
