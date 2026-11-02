using Acp.Portal.Api.Auth;
using Acp.Portal.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, AuthMode mode)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();

        // Signing out is a POST reached by submitting a real form, not by fetch. A browser
        // follows the redirect chain through the identity provider and back; fetch cannot,
        // because it is not allowed to read a cross-origin Location header.
        group.MapPost("/logout", (HttpContext http) =>
        {
            var properties = new AuthenticationProperties { RedirectUri = "/" };

            return mode == AuthMode.Entra
                ? Results.SignOut(properties, [
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    OpenIdConnectDefaults.AuthenticationScheme])
                : Results.SignOut(properties, [CookieAuthenticationDefaults.AuthenticationScheme]);
        });

        if (mode == AuthMode.Entra)
        {
            MapEntraSignIn(group);
        }
        else
        {
            MapDevelopmentSignIn(group);
        }

        return app;
    }

    private static void MapEntraSignIn(IEndpointRouteBuilder group)
    {
        // A plain link the browser follows. Everything after this happens at Microsoft until
        // the callback comes back to Entra:CallbackPath.
        group.MapGet("/login", ([FromQuery] string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = LocalRedirect.Sanitize(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]));
    }

    /// <summary>
    /// The no-tenant sign-in. These two endpoints are mapped only in
    /// <see cref="AuthMode.Development"/>, so in any other mode they are not routes that return
    /// 403 — they are routes that do not exist.
    /// </summary>
    private static void MapDevelopmentSignIn(IEndpointRouteBuilder group)
    {
        group.MapGet("/dev-accounts", async (
            DevelopmentAccountDirectory directory,
            CancellationToken cancellationToken) =>
            Results.Ok(await directory.ListAsync(cancellationToken)));

        group.MapPost("/dev-login", async (
            DevelopmentSignInRequest request,
            DevelopmentAccountDirectory directory,
            PortalPrincipalFactory factory,
            HttpContext http,
            IOptions<PortalAuthOptions> options,
            CancellationToken cancellationToken) =>
        {
            var external = await directory.BuildPrincipalAsync(request.Key, cancellationToken);
            if (external is null) return Results.NotFound();

            // Same factory, same resolver, same classification as a real token. Only the source
            // of the claims differs.
            var principal = await factory.CreateAsync(
                external, CookieAuthenticationDefaults.AuthenticationScheme, cancellationToken);

            if (principal is null) return Results.Problem("That account could not be resolved to a person.");

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(options.Value.SessionLifetimeMinutes),
                });

            return Results.NoContent();
        });
    }
}

public sealed record DevelopmentSignInRequest(string Key);
