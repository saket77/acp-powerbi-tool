using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acp.Portal.Tests.Harness;
using Microsoft.Data.SqlClient;

namespace Acp.Portal.Tests;

/// <summary>
/// Phase 3's definition of done: a client user asking for another client's project gets 404,
/// and the same is true with the application's filters out of the picture entirely, because SQL
/// refuses independently.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs against real SQL Server from docker-compose, built from the real
/// migrations. Row-level security is a database feature; an in-memory provider would have
/// agreed with every assertion below while enforcing none of them.
/// </para>
/// <para>
/// The world is built through the API rather than by writing rows, so the setup is itself a
/// test of the consultant endpoints, and nothing is true here that would not also be true for
/// somebody clicking through the screens.
/// </para>
/// </remarks>
public sealed class IsolationTests(IsolationFixture fixture) : IClassFixture<IsolationFixture>
{
    private SqlServerFixture Sql => fixture.Sql;
    private World _world => fixture.World;

    private Task<HttpClient> SignedInAsync(string key) => fixture.SignedInAsync(key);

    // ---------------------------------------------------------------- the walls

    [RequiresSqlFact]
    public async Task AClientSeesTheirOwnProjectAndNothingElse()
    {
        using var anna = await SignedInAsync(_world.AnnaKey);

        var projects = await anna.GetFromJsonAsync<JsonElement>("/api/projects");

        var keys = projects.EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        Assert.Equal(["muster-2026"], keys);
    }

    [RequiresSqlFact]
    public async Task AClientAskingForAnotherClientsProjectGets404()
    {
        using var anna = await SignedInAsync(_world.AnnaKey);

        var response = await anna.GetAsync("/api/projects/nordwind-2026/summary");

        // Not 403. Telling somebody a project exists but is not theirs is itself a disclosure.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AConsultantWhoIsNotOnTheProjectGets404Too()
    {
        using var otherLead = await SignedInAsync("lead-b");

        // Staff are not privileged here. They see projects because they hold a grant, exactly
        // like everybody else, so the wall has no exception to be widened later.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await otherLead.GetAsync("/api/acp/projects/muster-2026")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await otherLead.GetAsync("/api/acp/projects/muster-2026/grants")).StatusCode);
    }

    [RequiresSqlFact]
    public async Task TheLeadOnTheProjectDoesSeeIt()
    {
        using var lead = await SignedInAsync("lead-a");

        Assert.Equal(
            HttpStatusCode.OK,
            (await lead.GetAsync("/api/acp/projects/muster-2026")).StatusCode);
    }

    // ------------------------------------------------- the same, without the application

    [RequiresSqlFact]
    public async Task SqlRefusesOnItsOwn_WithNoApplicationFilterInvolved()
    {
        // No EF, no LINQ, no endpoint: a bare SELECT with nothing to narrow it, running as Anna.
        var keys = await Sql.AsUserAsync(_world.AnnaUserId, ReadProjectKeys);

        Assert.Equal(["muster-2026"], keys);
    }

    [RequiresSqlFact]
    public async Task SqlShowsNothingAtAllToNobody()
    {
        // The predicate fails closed. An unstamped connection is not a privileged one.
        var keys = await Sql.AsUserAsync(userId: null, ReadProjectKeys);

        Assert.Empty(keys);
    }

    [RequiresSqlFact]
    public async Task AnExpiredGrantStopsWorkingInTheDatabase()
    {
        using var lead = await SignedInAsync("lead-a");

        // Yesterday: a perfectly ordinary way to end somebody's access today.
        await lead.PostAsJsonAsync("/api/acp/projects/muster-2026/grants", new
        {
            userId = _world.AnnaUserId,
            role = "Client",
            endDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
            companyIds = new[] { _world.MusterCompanyId },
        });

        var keys = await Sql.AsUserAsync(_world.AnnaUserId, ReadProjectKeys);
        Assert.Empty(keys);

        // Put it back, so the ordering of tests in this class cannot matter.
        await lead.PostAsJsonAsync("/api/acp/projects/muster-2026/grants", new
        {
            userId = _world.AnnaUserId,
            role = "Client",
            companyIds = new[] { _world.MusterCompanyId },
        });
    }

    [RequiresSqlFact]
    public async Task WritingIntoAnotherClientsProjectIsBlocked()
    {
        var nordwindProjectId = await Sql.AsUnfilteredAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM dbo.Projects WHERE [Key] = 'nordwind-2026'";
            return (Guid)(await command.ExecuteScalarAsync())!;
        });

        // The filter predicate hides rows; this one refuses to create them. Without it, a bug
        // could write a report version into a project the writer cannot even read.
        var blocked = await Assert.ThrowsAsync<SqlException>(() =>
            Sql.AsUserAsync(_world.AnnaUserId, async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO dbo.ReportVersions
                        (Id, ProjectId, Version, BlobPath, State, UploadedBy, UploadedAt)
                    VALUES
                        (NEWID(), @projectId, 1, 'x', 'Draft', @userId, SYSDATETIMEOFFSET());
                    """;
                command.Parameters.AddWithValue("@projectId", nordwindProjectId);
                command.Parameters.AddWithValue("@userId", _world.AnnaUserId);
                return await command.ExecuteNonQueryAsync();
            }));

        Assert.Contains("block predicate", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ revocation

    [RequiresSqlFact]
    public async Task RevokingAGrantEndsThatPersonsSessionOnTheirNextRequest()
    {
        using var lead = await SignedInAsync("lead-a");
        using var tom = await SignedInAsync(_world.TomKey);

        // Tom is on nordwind-2026 and can see it right now.
        Assert.Equal(HttpStatusCode.OK, (await tom.GetAsync("/api/projects/nordwind-2026/summary")).StatusCode);

        using var leadB = await SignedInAsync("lead-b");
        var grants = await leadB.GetFromJsonAsync<JsonElement>("/api/acp/projects/nordwind-2026/grants");
        var grantId = grants.EnumerateArray()
            .Single(g => g.GetProperty("userId").GetGuid() == _world.TomUserId)
            .GetProperty("id").GetGuid();

        var removed = await leadB.DeleteAsync($"/api/acp/projects/nordwind-2026/grants/{grantId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        // His cookie carries a security stamp that no longer matches, so the session is over
        // before the project is even looked for.
        Assert.Equal(HttpStatusCode.Unauthorized, (await tom.GetAsync("/api/projects")).StatusCode);

        // And signing back in does not bring it back.
        using var tomAgain = await SignedInAsync(_world.TomKey);
        var after = await tomAgain.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.Empty(after.EnumerateArray());
    }

    [RequiresSqlFact]
    public async Task TheLastLeadCannotRemoveThemselves()
    {
        using var lead = await SignedInAsync("lead-a");

        var grants = await lead.GetFromJsonAsync<JsonElement>("/api/acp/projects/muster-2026/grants");
        var ownGrantId = grants.EnumerateArray()
            .Single(g => g.GetProperty("role").GetString() == "Lead")
            .GetProperty("id").GetGuid();

        var response = await lead.DeleteAsync($"/api/acp/projects/muster-2026/grants/{ownGrantId}");

        // A project with no lead is one nobody can grant access to or release from, and the only
        // way back is a database edit.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ------------------------------------------------------------ grant rules

    [RequiresSqlFact]
    public async Task AClientCannotBeGivenAnotherClientsCompanyCodes()
    {
        using var lead = await SignedInAsync("lead-a");

        var response = await lead.PostAsJsonAsync("/api/acp/projects/muster-2026/grants", new
        {
            userId = _world.AnnaUserId,
            role = "Client",
            // Nordwind's company, on Muster's project. From Phase 4 this code would travel
            // inside an embed token and the model would honour it.
            companyIds = new[] { _world.NordwindCompanyId },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AClientContactCannotBeMadeAConsultant()
    {
        using var lead = await SignedInAsync("lead-a");

        var response = await lead.PostAsJsonAsync("/api/acp/projects/muster-2026/grants", new
        {
            userId = _world.AnnaUserId,
            role = "Lead",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [RequiresSqlFact]
    public async Task AClientCannotReachTheConsultantEndpointsAtAll()
    {
        using var anna = await SignedInAsync(_world.AnnaKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/acp/clients")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await anna.GetAsync("/api/acp/projects/muster-2026/grants")).StatusCode);
    }

    private static async Task<List<string>> ReadProjectKeys(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [Key] FROM dbo.Projects ORDER BY [Key]";

        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));

        return keys;
    }
}
