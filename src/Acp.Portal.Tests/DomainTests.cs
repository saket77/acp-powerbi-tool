using Acp.Portal.Domain;
using Acp.Portal.Domain.Entities;

namespace Acp.Portal.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Client_IsNotProvisioned_UntilBothPowerBiIdsAreSet()
    {
        var client = new Client { Name = "Muster group", Slug = "muster-group" };

        Assert.False(client.IsProvisioned);

        client.PowerBiProfileId = "3f9a-c21e";
        Assert.False(client.IsProvisioned);

        client.PowerBiWorkspaceId = "ws-1042";
        Assert.True(client.IsProvisioned);
    }

    [Theory]
    [InlineData(ReportVersionState.Importing, false)]
    [InlineData(ReportVersionState.CheckFailed, false)]
    [InlineData(ReportVersionState.Draft, false)]
    [InlineData(ReportVersionState.Released, true)]
    [InlineData(ReportVersionState.Superseded, false)]
    public void OnlyAReleasedVersionIsVisibleToClients(ReportVersionState state, bool expected)
    {
        var version = new ReportVersion { BlobPath = "uploads/x.pbix", State = state };

        Assert.Equal(expected, version.IsVisibleToClients);
    }

    [Fact]
    public void ADraftIsNotVisible_EvenAfterItsChecksPassed()
    {
        // The distinction the four-eyes rule rests on: passing the checks produces a Draft,
        // and only a second person's release makes it reachable by a client.
        var version = new ReportVersion
        {
            BlobPath = "uploads/good.pbix",
            State = ReportVersionState.Draft,
            UploadedBy = Guid.NewGuid(),
        };

        Assert.False(version.IsVisibleToClients);
        Assert.Null(version.ReleasedBy);
    }
}
