using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acp.Portal.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Tests;

/// <summary>
/// Phase 2's definition of done, as tests: the right people get in, the wrong people do not,
/// and the browser never holds anything but an opaque cookie.
/// </summary>
public sealed class AuthenticationTests : IDisposable
{
    private readonly PortalAppFactory _factory = new();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string key) =>
        client.PostAsJsonAsync("/api/auth/dev-login", new { key });

    [Fact]
    public async Task Me_Is401_AndNamesTheSignInMethod_WhenNobodyIsSignedIn()
    {
        using var client = NewClient();

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal("Development", body.GetProperty("authMode").GetString());
    }

    [Fact]
    public async Task AMemberOfTheHomeTenantOnAStaffDomain_IsAcpStaff()
    {
        using var client = NewClient();

        await SignInAsync(client, PortalAppFactory.StaffKey);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");

        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal(nameof(UserKind.AcpStaff), me.GetProperty("kind").GetString());
        Assert.Equal("staff@acp.test", me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task AGuestSigningInWithTheirOwnAccount_IsAClientUser()
    {
        using var client = NewClient();

        await SignInAsync(client, PortalAppFactory.ClientKey);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal(nameof(UserKind.ClientUser), me.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AMemberOfAnotherDirectory_IsAClientUser_EvenOnAStaffEmailDomain()
    {
        using var client = NewClient();

        // The email says acp.test. The tenant says somewhere else. The tenant wins, which is
        // the whole point of checking it.
        await SignInAsync(client, PortalAppFactory.OutsiderKey);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal(nameof(UserKind.ClientUser), me.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TheSessionIsAnOpaqueHttpOnlyCookie()
    {
        using var client = NewClient();

        var response = await SignInAsync(client, PortalAppFactory.StaffKey);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var session = Assert.Single(cookies, c => c.StartsWith("acp.session=", StringComparison.Ordinal));

        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", session, StringComparison.OrdinalIgnoreCase);

        // Nothing from an identity provider travels to the browser: the sign-in response has a
        // cookie and no body at all.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ConsultantEndpointsAreClosedToClients()
    {
        using var client = NewClient();
        await SignInAsync(client, PortalAppFactory.ClientKey);

        var response = await client.GetAsync("/api/acp/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConsultantEndpointsAnswer401RatherThanRedirecting()
    {
        using var client = NewClient();

        var response = await client.GetAsync("/api/acp/users");

        // A 302 towards Microsoft inside a fetch is a CORS error the front end cannot read.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SigningOutEndsTheSession()
    {
        using var client = NewClient();
        await SignInAsync(client, PortalAppFactory.StaffKey);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var after = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ChangingTheSecurityStampEndsTheSessionOnTheNextRequest()
    {
        using var client = NewClient();
        await SignInAsync(client, PortalAppFactory.StaffKey);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        // Stands in for Phase 3 revoking a grant.
        await _factory.WithDatabaseAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == "staff@acp.test");
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task AnUnknownDevelopmentAccountIsNotFound()
    {
        using var client = NewClient();

        var response = await SignInAsync(client, "no-such-person");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
