using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acp.Portal.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Tests;

/// <summary>
/// The invite path, end to end on the development sign-in: a consultant invites someone, and
/// that someone can then sign in as themselves and is classified as a client.
/// </summary>
public sealed class InvitationTests : IDisposable
{
    private readonly PortalAppFactory _factory = new();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> SignedInAsync(string key)
    {
        var client = NewClient();
        await client.PostAsJsonAsync("/api/auth/dev-login", new { key });
        return client;
    }

    [Fact]
    public async Task AConsultantCanInviteAClientContact()
    {
        using var staff = await SignedInAsync(PortalAppFactory.StaffKey);

        var response = await staff.PostAsJsonAsync("/api/acp/invitations", new
        {
            email = "Neue.Person@Beispiel.test",
            displayName = "Neue Person",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nameof(UserKind.ClientUser), body.GetProperty("kind").GetString());

        // Addresses are compared, so they are stored the one way they can be compared.
        Assert.Equal("neue.person@beispiel.test", body.GetProperty("email").GetString());
        Assert.False(body.GetProperty("alreadyExisted").GetBoolean());
    }

    [Fact]
    public async Task AnInvitedPersonCanSignInAndIsAClientUser()
    {
        using var staff = await SignedInAsync(PortalAppFactory.StaffKey);
        await staff.PostAsJsonAsync("/api/acp/invitations", new { email = "invitee@client.test" });

        // They appear in the development picker, which is what stands in for the invitation
        // email until there is a tenant to send one from.
        using var anonymous = NewClient();
        var accounts = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/dev-accounts");

        var key = accounts.EnumerateArray()
            .Single(a => a.GetProperty("email").GetString() == "invitee@client.test")
            .GetProperty("key").GetString();

        using var invitee = NewClient();
        await invitee.PostAsJsonAsync("/api/auth/dev-login", new { key });

        var me = await invitee.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(nameof(UserKind.ClientUser), me.GetProperty("kind").GetString());
        Assert.Equal("invitee@client.test", me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task InvitingTheSamePersonTwiceIsAllowedAndDoesNotDuplicateThem()
    {
        using var staff = await SignedInAsync(PortalAppFactory.StaffKey);

        await staff.PostAsJsonAsync("/api/acp/invitations", new { email = "twice@client.test" });
        var second = await staff.PostAsJsonAsync("/api/acp/invitations", new { email = "twice@client.test" });

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("alreadyExisted").GetBoolean());

        await _factory.WithDatabaseAsync(async db =>
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == "twice@client.test")));
    }

    [Theory]
    // Somebody who has signed in, so there is a row saying they are staff.
    [InlineData("staff@acp.test")]
    // A colleague who has never signed in and therefore has no row at all. The address alone
    // has to be enough, or a consultant can quietly turn a colleague into a client contact.
    [InlineData("never.signed.in@acp.test")]
    public async Task StaffCannotBeInvitedAsAClientContact(string email)
    {
        using var staff = await SignedInAsync(PortalAppFactory.StaffKey);

        // One row must not mean two things: every later authorization question would have to
        // ask which meaning applied.
        var response = await staff.PostAsJsonAsync("/api/acp/invitations", new { email });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AClientCannotInviteAnyone()
    {
        using var client = await SignedInAsync(PortalAppFactory.ClientKey);

        var response = await client.PostAsJsonAsync("/api/acp/invitations", new { email = "friend@client.test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAddressThatIsNotAnAddressIsRejected()
    {
        using var staff = await SignedInAsync(PortalAppFactory.StaffKey);

        var response = await staff.PostAsJsonAsync("/api/acp/invitations", new { email = "not-an-address" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvitingIsRecordedInTheAuditLogWithoutTheRedeemLink()
    {
        using var staff = await SignedInAsync(PortalAppFactory.StaffKey);
        await staff.PostAsJsonAsync("/api/acp/invitations", new { email = "audited@client.test" });

        await _factory.WithDatabaseAsync(async db =>
        {
            var invited = await db.AuditEvents.SingleAsync(e => e.Action == "user.invited");

            Assert.Contains("audited@client.test", invited.Detail);

            // The redeem URL is a bearer credential for the invitation. It is returned to the
            // consultant who asked for it and written down nowhere.
            Assert.DoesNotContain("redeem", invited.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(invited.UserId);
        });
    }

    public void Dispose() => _factory.Dispose();
}
