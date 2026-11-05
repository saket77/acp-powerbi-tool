using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acp.Portal.Tests.Harness;
using Microsoft.Data.SqlClient;

namespace Acp.Portal.Tests;

/// <summary>
/// Everything the portal decides before it hands over an embed token.
/// </summary>
/// <remarks>
/// <para>
/// Power BI is simulated here, so none of this proves that Power BI honours a token — that a
/// token naming one report is refused for another, or that the model filters to the codes in the
/// identity. Those are Gate 1 and no test on this side of the line can stand in for them.
/// </para>
/// <para>
/// What it does prove is the half that is ours: which version a given person gets, whose company
/// codes end up in the identity, who may preview as whom, and that the token is never written
/// down. Every one of those is a way a client could be shown the wrong data, and none of them
/// needs a capacity to check.
/// </para>
/// </remarks>
public sealed class PowerBiTests(PowerBiFixture fixture) : IClassFixture<PowerBiFixture>
{
    private PowerBiWorld World => fixture.World;

    // ------------------------------------------------------------ what each person gets

    [RequiresSqlFact]
    public async Task AClientGetsTheReleasedVersionFilteredToTheirOwnCompanies()
    {
        using var anna = await fixture.SignedInAsync(World.AnnaKey);

        var embed = await anna.GetFromJsonAsync<JsonElement>("/api/projects/muster-2026/embed");

        Assert.Equal("ready", embed.GetProperty("status").GetString());
        Assert.False(embed.GetProperty("isDraft").GetBoolean());

        // A client is shown their report, not a description of the rules that produced it.
        Assert.Equal(JsonValueKind.Null, embed.GetProperty("scope").ValueKind);

        // The codes that actually went into the identity, read back from the audit log, which
        // is the only place a client's own token scope is written down.
        var issued = await LatestAuditAsync("token.issued");
        Assert.Contains("\"M01\"", issued);
        Assert.DoesNotContain("M02", issued);
    }

    [RequiresSqlFact]
    public async Task AConsultantSeesTheFilterTheyAreLookingThrough()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var embed = await lead.GetFromJsonAsync<JsonElement>("/api/projects/muster-2026/embed");
        var scope = embed.GetProperty("scope");

        Assert.Equal("ACP", scope.GetProperty("roles").EnumerateArray().Single().GetString());
        Assert.Equal("lead.a@acp.test", scope.GetProperty("username").GetString());
    }

    [RequiresSqlFact]
    public async Task ViewAsPutsAConsultantBehindTheClientsOwnFilter()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var embed = await lead.GetFromJsonAsync<JsonElement>(
            $"/api/projects/muster-2026/embed?viewAs={World.AnnaUserId}");

        var scope = embed.GetProperty("scope");

        Assert.Equal("Client", scope.GetProperty("roles").EnumerateArray().Single().GetString());
        Assert.Equal("M01", scope.GetProperty("username").GetString());
        Assert.Equal("M01", scope.GetProperty("companies").EnumerateArray().Single().GetString());
    }

    // ------------------------------------------------------------ who may ask for what

    [RequiresSqlFact]
    public async Task AClientCannotReachAnotherClientsReport()
    {
        using var anna = await fixture.SignedInAsync(World.AnnaKey);

        var response = await anna.GetAsync("/api/projects/nordwind-2026/embed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AConsultantCannotPreviewAsSomebodyFromAnotherProject()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        // Tom is a client — on Nordwind's project, not this one. Without the per-project clause
        // on the lookup, this would borrow his identity and look through it here.
        var response = await lead.GetAsync($"/api/projects/muster-2026/embed?viewAs={World.TomUserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AConsultantCannotPreviewAsAnotherConsultant()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var response = await lead.GetAsync($"/api/projects/muster-2026/embed?viewAs={World.LeadBUserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AClientCannotPreviewAsAnybody()
    {
        using var anna = await fixture.SignedInAsync(World.AnnaKey);

        var response = await anna.GetAsync($"/api/projects/muster-2026/embed?viewAs={World.TomUserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task NothingReleasedYetIsAnOrdinaryAnswer()
    {
        using var tom = await fixture.SignedInAsync(World.TomKey);

        var embed = await tom.GetFromJsonAsync<JsonElement>("/api/projects/nordwind-2026/embed");

        // Not a 404 and not an error: this is the state most projects are in most of the time.
        Assert.Equal("unavailable", embed.GetProperty("status").GetString());
        Assert.Contains("released", embed.GetProperty("reason").GetString()!);
    }

    // ------------------------------------------------------------ the audit log

    [RequiresSqlFact]
    public async Task TheTokenItselfIsNeverWrittenDown()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var embed = await lead.GetFromJsonAsync<JsonElement>("/api/projects/muster-2026/embed");
        var token = embed.GetProperty("token").GetString()!;

        var issued = await LatestAuditAsync("token.issued");

        // Everything an auditor needs to answer "what could this person see at 14:05" — and
        // nothing that would let them see it themselves.
        Assert.DoesNotContain(token, issued, StringComparison.Ordinal);
        Assert.Contains("reportId", issued);
        Assert.Contains("roles", issued);
    }

    // ------------------------------------------------------------ release

    [RequiresSqlFact]
    public async Task TheUploaderCannotReleaseTheirOwnVersion()
    {
        using var leadA = await fixture.SignedInAsync("lead-a");

        var upload = await leadA.PostAsync("/api/acp/projects/muster-2026/versions", PowerBiFixture.Pbix());
        upload.EnsureSuccessStatusCode();

        var version = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        var response = await leadA.PostAsJsonAsync(
            $"/api/acp/projects/muster-2026/versions/{version}/release", new { note = "mine" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("somebody else", await response.Content.ReadAsStringAsync());
    }

    [RequiresSqlFact]
    public async Task ReleasingSupersedesWhateverWasReleasedBefore()
    {
        using var leadA = await fixture.SignedInAsync("lead-a");
        using var leadB = await fixture.SignedInAsync("lead-b");

        var upload = await leadB.PostAsync("/api/acp/projects/muster-2026/versions", PowerBiFixture.Pbix());
        upload.EnsureSuccessStatusCode();

        var version = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        (await leadA.PostAsJsonAsync(
            $"/api/acp/projects/muster-2026/versions/{version}/release", new { note = "Newer" }))
            .EnsureSuccessStatusCode();

        var versions = await leadA.GetFromJsonAsync<JsonElement>("/api/acp/projects/muster-2026/versions");
        var states = versions.EnumerateArray()
            .ToDictionary(v => v.GetProperty("version").GetInt32(), v => v.GetProperty("state").GetString());

        // The filtered unique index allows exactly one, so this is also a check that the
        // supersede and the promote happened in the right order.
        Assert.Equal("Released", states[version]);
        Assert.Single(states.Values, s => s == "Released");
    }

    [RequiresSqlFact]
    public async Task AVersionCannotBeReleasedTwice()
    {
        using var leadA = await fixture.SignedInAsync("lead-a");
        using var leadB = await fixture.SignedInAsync("lead-b");

        var upload = await leadB.PostAsync("/api/acp/projects/muster-2026/versions", PowerBiFixture.Pbix());
        var version = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32();

        (await leadA.PostAsJsonAsync($"/api/acp/projects/muster-2026/versions/{version}/release", new { }))
            .EnsureSuccessStatusCode();

        var again = await leadA.PostAsJsonAsync(
            $"/api/acp/projects/muster-2026/versions/{version}/release", new { });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    // ------------------------------------------------------------ provisioning

    [RequiresSqlFact]
    public async Task ProvisioningTwiceIsRefused()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        // Doing it again would create a second profile and strand the first — and a profile
        // belongs forever to the identity that made it, so nothing could clean it up.
        var response = await lead.PostAsync("/api/acp/clients/muster/provision", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AnUnprovisionedClientCannotTakeAnUpload()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        await lead.PostAsJsonAsync("/api/acp/clients", new
        {
            name = "Unprovisioned",
            slug = "unprovisioned",
            companies = new[] { new { code = "U01", name = "U one" } },
        });
        await lead.PostAsJsonAsync("/api/acp/projects", new
        {
            clientSlug = "unprovisioned",
            key = "unprovisioned-2026",
            name = "Unprovisioned 2026",
        });

        var response = await lead.PostAsync(
            "/api/acp/projects/unprovisioned-2026/versions", PowerBiFixture.Pbix());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("workspace", await response.Content.ReadAsStringAsync());
    }

    [RequiresSqlFact]
    public async Task ACompanyCodeWithASpaceIsRefusedWhenTheClientIsCreated()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        // Power BI rejects spaces in the identity that carries these codes. Catching it here
        // means it cannot surface months later on the one call that matters.
        var response = await lead.PostAsJsonAsync("/api/acp/clients", new
        {
            name = "Spacey",
            slug = "spacey",
            companies = new[] { new { code = "M 01", name = "Spacey one" } },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("space", await response.Content.ReadAsStringAsync());
    }

    private async Task<string> LatestAuditAsync(string action) =>
        await fixture.Sql.AsUnfilteredAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT TOP 1 Detail FROM dbo.AuditEvents WHERE Action = @action ORDER BY Id DESC";
            command.Parameters.Add(new SqlParameter("@action", action));

            return (string)(await command.ExecuteScalarAsync())!;
        });
}
