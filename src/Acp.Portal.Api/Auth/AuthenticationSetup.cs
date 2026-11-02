using Acp.Portal.Domain;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

namespace Acp.Portal.Api.Auth;

/// <summary>
/// Wires up sign-in. One cookie scheme, and either OpenID Connect or the development picker
/// feeding it.
/// </summary>
public static class AuthenticationSetup
{
    /// <summary>Marks a principal that came from the development picker rather than from Entra.</summary>
    public const string DevelopmentAuthenticationType = "DevelopmentSignIn";

    public static IServiceCollection AddPortalAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var auth = configuration.GetSection(PortalAuthOptions.SectionName).Get<PortalAuthOptions>() ?? new();

        services.AddScoped<PortalPrincipalFactory>();

        if (auth.Mode == AuthMode.Entra)
        {
            services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApp(configuration.GetSection(EntraOptions.SectionName));

            services.PostConfigure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => ConfigureOpenIdConnect(options, environment));
        }
        else
        {
            services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme);

            services.AddScoped<DevelopmentAccountDirectory>();
        }

        // Applied after whichever branch ran, so the session cookie is identical either way.
        services.PostConfigure<CookieAuthenticationOptions>(
            CookieAuthenticationDefaults.AuthenticationScheme,
            options => ConfigureSessionCookie(options, auth, environment));

        services.AddAuthorizationBuilder()
            .AddPolicy(PortalClaims.AcpStaffPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(PortalClaims.Kind, nameof(UserKind.AcpStaff)));

        return services;
    }

    private static void ConfigureSessionCookie(
        CookieAuthenticationOptions options,
        PortalAuthOptions auth,
        IWebHostEnvironment environment)
    {
        // __Host- is a browser-enforced promise: secure, path /, no Domain, so no sibling
        // subdomain can set or overwrite it. It requires HTTPS, which plain-http localhost is
        // not, hence the two names.
        options.Cookie.Name = environment.IsDevelopment() ? "acp.session" : "__Host-acp.session";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        // Strict has one visible consequence worth knowing: arriving from a link in an email
        // does not send the cookie, so the first screen looks signed out and a click on
        // "Sign in" puts it right. That is the trade the wireframe asks for, and it is the
        // right one — a client's report link is exactly what a phishing mail would imitate.
        options.ExpireTimeSpan = TimeSpan.FromMinutes(auth.SessionLifetimeMinutes);
        options.SlidingExpiration = true;

        // Everything this app serves under /api is read by fetch. A 302 towards an identity
        // provider inside an XHR becomes an opaque CORS failure, never a sign-in prompt, so
        // say plainly what happened and let the SPA decide what to show.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        options.Events.OnValidatePrincipal = ValidateSessionAsync;
    }

    /// <summary>
    /// Checks on every request that the session still matches the person it was issued to.
    /// </summary>
    /// <remarks>
    /// This is the wireframe's rule that a change to someone's access ends their session on the
    /// next request, and it is why <c>Users.SecurityStamp</c> exists. One indexed read per
    /// request is the price; for a portal with tens of users and a hard requirement that
    /// revocation be immediate, it is obviously worth paying. Phase 3 adds the code that bumps
    /// the stamp.
    /// </remarks>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var userId = context.Principal?.PortalUserId();
        var stamp = context.Principal?.FindFirst(PortalClaims.SecurityStamp)?.Value;
        var kind = context.Principal?.FindFirst(PortalClaims.Kind)?.Value;

        if (userId is null || stamp is null || kind is null)
        {
            await EndSessionAsync(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<PortalDbContext>();

        var current = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.SecurityStamp, u.Kind })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        // Deleted, re-stamped, or no longer the kind of person the cookie says: all three mean
        // the cookie describes a state of the world that no longer exists.
        if (current is null
            || !string.Equals(current.SecurityStamp, stamp, StringComparison.Ordinal)
            || !string.Equals(current.Kind.ToString(), kind, StringComparison.Ordinal))
        {
            await EndSessionAsync(context);
        }
    }

    private static async Task EndSessionAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options, IWebHostEnvironment environment)
    {
        // Nothing from the provider is kept. The portal calls Graph and Power BI as itself, so
        // an access token for the signed-in person would be a credential with no purpose, and
        // the safest place for one of those is nowhere.
        options.SaveTokens = false;

        if (!options.Scope.Contains("email")) options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";

        if (environment.IsDevelopment())
        {
            // form_post, the default, returns the authorization code in a cross-site POST, and
            // the correlation cookie that protects it then needs SameSite=None, which needs
            // Secure, which plain-http localhost does not have. A query-string response works
            // over http with SameSite=Lax. Development only: in a deployed environment the
            // default stands, and the code stays out of URLs and referrer headers.
            options.ResponseMode = "query";
        }

        var priorTokenValidated = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            await priorTokenValidated(context);

            var factory = context.HttpContext.RequestServices.GetRequiredService<PortalPrincipalFactory>();

            var principal = await factory.CreateAsync(
                context.Principal!,
                CookieAuthenticationDefaults.AuthenticationScheme,
                context.HttpContext.RequestAborted);

            if (principal is null)
            {
                context.Fail(
                    "The token carried no usable object id or email, so there is nobody to sign in as.");
                return;
            }

            // Replacing the principal here is what keeps the identity provider's claims out of
            // the cookie entirely. From this line on, the portal only knows its own user id.
            context.Principal = principal;
        };

        var priorRemoteFailure = options.Events.OnRemoteFailure;
        options.Events.OnRemoteFailure = async context =>
        {
            // Cancelling at the Microsoft prompt is a normal thing to do and should land back
            // on the portal, not on a stack trace.
            await priorRemoteFailure(context);
            if (context.Result?.Handled == true) return;

            context.Response.Redirect("/?signin=cancelled");
            context.HandleResponse();
        };
    }
}
