using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Acp.Portal.Tests;

/// <summary>
/// What the development sign-in looks like once it is switched off: absent.
/// </summary>
/// <remarks>
/// The endpoints are mapped inside the branch that runs only in
/// <c>AuthMode.Development</c>, so in any other mode they are not routes returning 403 — they
/// are routes that do not exist. That is a stronger guarantee than a check inside a handler,
/// and this is the test that keeps it true.
/// </remarks>
public sealed class EntraModeTests : IDisposable
{
    private readonly PortalAppFactory _factory = new(new Dictionary<string, string?>
    {
        ["Auth:Mode"] = "Entra",
        ["Auth:StaffEmailDomains:0"] = "acp.test",
        ["Auth:HomeTenantId"] = PortalAppFactory.HomeTenant,
        ["Auth:InviteRedirectUrl"] = "https://portal.test/",
        ["Entra:TenantId"] = PortalAppFactory.HomeTenant,
        ["Entra:ClientId"] = "22222222-2222-2222-2222-222222222222",
        ["Entra:ClientSecret"] = "not-a-real-secret",
    },
    StubTheDiscoveryDocument);

    /// <summary>
    /// Hands the handler the endpoints it would otherwise fetch from the tenant. Without this
    /// a challenge goes to the network, and a test that needs the internet to tell you your
    /// redirect is safe is not a test you will keep running.
    /// </summary>
    private static void StubTheDiscoveryDocument(IServiceCollection services) =>
        services.PostConfigure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            options =>
            {
                var tenant = PortalAppFactory.HomeTenant;
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = $"https://login.microsoftonline.com/{tenant}/v2.0",
                    AuthorizationEndpoint =
                        $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                    TokenEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                    EndSessionEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/logout",
                };

                // Both, and the manager especially: the handler reaches for the manager first
                // and only falls back to Configuration, so setting Configuration alone still
                // goes to the network.
                options.Configuration = configuration;
                options.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/api/auth/dev-accounts")]
    public async Task TheDevelopmentPickerIsNotThere(string path)
    {
        using var client = NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task TheDevelopmentSignInIsNotThere()
    {
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/auth/dev-login", new { key = "staff" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MePointsTheFrontEndAtMicrosoft()
    {
        using var client = NewClient();

        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Entra", body.GetProperty("authMode").GetString());
        Assert.Equal("/api/auth/login", body.GetProperty("signInUrl").GetString());
    }

    [Fact]
    public async Task SigningInHandsOverToTheIdentityProvider()
    {
        using var client = NewClient();

        var response = await client.GetAsync("/api/auth/login?returnUrl=/projects");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("login.microsoftonline.com", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task AReturnUrlCannotLeaveTheSite()
    {
        using var client = NewClient();

        // The classic open redirect: a link on a domain the reader trusts, landing them
        // somewhere else immediately after they typed a password.
        var response = await client.GetAsync("/api/auth/login?returnUrl=https://evil.test/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("evil.test", Uri.UnescapeDataString(response.Headers.Location!.ToString()));
    }

    public void Dispose() => _factory.Dispose();
}
