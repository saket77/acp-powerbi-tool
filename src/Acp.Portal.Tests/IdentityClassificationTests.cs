using System.Security.Claims;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Identity;
using Acp.Portal.Infrastructure.Identity;

namespace Acp.Portal.Tests;

/// <summary>
/// The one decision that separates a consultant from a client. Tested directly, because
/// reaching it only through HTTP would make its edges hard to see.
/// </summary>
public sealed class IdentityClassificationTests
{
    private const string Home = "11111111-1111-1111-1111-111111111111";

    private static PortalAuthOptions Options(params string[] staffDomains) => new()
    {
        HomeTenantId = Home,
        StaffEmailDomains = staffDomains,
    };

    [Theory]
    // Member of the home tenant on a staff domain: the only way to be staff.
    [InlineData("a@acp.test", Home, false, UserKind.AcpStaff)]
    // A guest is never staff, whatever their address or tenant says.
    [InlineData("a@acp.test", Home, true, UserKind.ClientUser)]
    // Right domain, wrong directory.
    [InlineData("a@acp.test", "99999999-9999-9999-9999-999999999999", false, UserKind.ClientUser)]
    // Right directory, wrong domain.
    [InlineData("a@elsewhere.test", Home, false, UserKind.ClientUser)]
    // No tenant claim at all.
    [InlineData("a@acp.test", null, false, UserKind.ClientUser)]
    public void ClassifiesPeopleTheRestrictiveWay(string email, string? tenant, bool guest, UserKind expected)
    {
        var identity = new ExternalIdentity("oid", email, "Someone", tenant, guest);

        Assert.Equal(expected, IdentityResolver.Classify(identity, Options("acp.test")));
    }

    [Fact]
    public void WithNoStaffDomainsConfigured_AnyMemberOfTheHomeTenantIsStaff()
    {
        // The documented default for a tenant that exists only for ACP — and the reason a
        // deployed environment should always set StaffEmailDomains anyway.
        var identity = new ExternalIdentity("oid", "anyone@anywhere.test", "Someone", Home, IsGuest: false);

        Assert.Equal(UserKind.AcpStaff, IdentityResolver.Classify(identity, Options()));
    }

    [Theory]
    [InlineData("alice_outlook.com#EXT#@acp.onmicrosoft.com")]
    [InlineData("ALICE_OUTLOOK.COM#ext#@acp.onmicrosoft.com")]
    public void AMangledGuestUpnIsRecognisedWithoutTheAcctClaim(string upn)
    {
        // Entra only emits `acct` when the app registration asks for it, so the shape of a
        // B2B guest's UPN has to be enough on its own.
        var principal = Principal(("oid", "x"), ("preferred_username", upn), ("name", "Alice"));

        Assert.True(EntraClaims.TryRead(principal, out var identity));
        Assert.True(identity.IsGuest);
    }

    [Fact]
    public void SomeoneWhoSignedInSomewhereElseIsAGuest()
    {
        var principal = Principal(
            ("oid", "x"), ("preferred_username", "a@b.test"), ("name", "A"), ("idp", "live.com"));

        Assert.True(EntraClaims.TryRead(principal, out var identity));
        Assert.True(identity.IsGuest);
    }

    [Fact]
    public void TheLongClaimNamesAreReadToo()
    {
        // Whether these arrive short or long depends on inbound claim mapping, which depends on
        // the handler, which changes between library versions.
        var principal = Principal(
            (EntraClaims.ObjectIdClaimLong, "the-oid"),
            (EntraClaims.TenantIdClaimLong, Home),
            (ClaimTypes.Email, "Person@ACP.test"),
            (ClaimTypes.Name, "A Person"));

        Assert.True(EntraClaims.TryRead(principal, out var identity));
        Assert.Equal("the-oid", identity.ObjectId);
        Assert.Equal(Home, identity.TenantId);
        Assert.Equal("person@acp.test", identity.Email);
    }

    [Fact]
    public void APrincipalWithNoStableIdIsNotASignIn()
    {
        var principal = Principal(("name", "Nobody"), ("preferred_username", "n@b.test"));

        Assert.False(EntraClaims.TryRead(principal, out _));
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}
