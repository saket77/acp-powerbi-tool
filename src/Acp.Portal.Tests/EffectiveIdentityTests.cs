using Acp.Portal.Infrastructure;
using Acp.Portal.Infrastructure.PowerBi;

namespace Acp.Portal.Tests;

/// <summary>
/// The last thing the portal decides before Power BI starts enforcing it.
/// </summary>
/// <remarks>
/// These are the limits that would otherwise be discovered in front of a client: a username one
/// character too long does not fail, it silently filters the model to whatever survived the
/// truncation, and the report looks complete. So the factory refuses, and this is what says so.
/// </remarks>
public sealed class EffectiveIdentityTests
{
    private static PowerBiOptions Options(EffectiveIdentityMode mode = EffectiveIdentityMode.Username) =>
        new() { IdentityMode = mode };

    [Fact]
    public void CodesTravelInTheUsername_SortedAndSeparated()
    {
        var identity = EffectiveIdentityFactory.ForClient(
            "anna@muster.example", ["M02", "M01", "M02"], Options());

        // Sorted and de-duplicated so that the same grant always produces the same identity —
        // which matters because it ends up in an audit log people compare across days.
        Assert.Equal("M01|M02", identity.Username);
        Assert.Equal(["Client"], identity.Roles);
        Assert.Null(identity.CustomData);
    }

    [Fact]
    public void TooManyCodesIsRefused_NotTruncated()
    {
        // 60 five-character codes plus separators is comfortably past Power BI's 256.
        var codes = Enumerable.Range(1, 60).Select(i => $"C{i:D4}").ToList();

        var refused = Assert.Throws<PortalRuleException>(
            () => EffectiveIdentityFactory.ForClient("a@b.test", codes, Options()));

        Assert.Contains("256", refused.Message);
        Assert.Contains("CustomData", refused.Message);
    }

    [Fact]
    public void TheCustomDataFallbackTakesTheCodesOutOfTheUsername()
    {
        // Open question A3's escape hatch, wired before anyone has had to use it.
        var identity = EffectiveIdentityFactory.ForClient(
            "anna@muster.example", ["M01", "M02"], Options(EffectiveIdentityMode.CustomData));

        Assert.Equal("anna@muster.example", identity.Username);
        Assert.Equal("M01|M02", identity.CustomData);
    }

    [Fact]
    public void AGrantWithNoCompaniesIsRefused()
    {
        var refused = Assert.Throws<PortalRuleException>(
            () => EffectiveIdentityFactory.ForClient("a@b.test", [], Options()));

        Assert.Contains("at least one company", refused.Message);
    }

    [Fact]
    public void AcpStaffGetTheAcpRoleAndTheirOwnName()
    {
        var identity = EffectiveIdentityFactory.ForAcp("lars.weber@acp.de", Options());

        Assert.Equal("lars.weber@acp.de", identity.Username);
        Assert.Equal(["ACP"], identity.Roles);
    }

    [Theory]
    [InlineData("M 01", "space")]
    [InlineData("MÜ1", "non-ASCII")]
    [InlineData("M|01", "the separator")]
    [InlineData("M,01", "a comma")]
    [InlineData("", "empty")]
    public void CodesThatPowerBiWouldRejectAreRefusedAtTheDoor(string code, string why)
    {
        // Checked when a company is created, so a bad code cannot reach a token at all.
        Assert.ThrowsAny<PortalRuleException>(
            () => EffectiveIdentityFactory.ValidateCompanyCode(code, Options()));

        Assert.NotEmpty(why);
    }

    [Fact]
    public void AnOrdinaryCodeIsAccepted()
    {
        EffectiveIdentityFactory.ValidateCompanyCode("M01", Options());
        EffectiveIdentityFactory.ValidateCompanyCode("DE-1234", Options());
    }
}
