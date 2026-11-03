using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Acp.Portal.Tests.Harness;

/// <summary>
/// Two clients, two projects, two consultants and two client contacts — built once, through the
/// API, against real SQL Server.
/// </summary>
/// <remarks>
/// Built through the endpoints rather than by writing rows, so the setup is itself a test of the
/// consultant screens and nothing is true here that would not also be true for somebody clicking
/// through them. It lives in a fixture because xunit makes a fresh test-class instance for every
/// test, and creating the same client twice is — correctly — a conflict.
/// </remarks>
public sealed class IsolationFixture : IAsyncLifetime
{
    private PortalAppFactory? _factory;

    public SqlServerFixture Sql { get; } = new();

    public World World { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Sql.InitializeAsync();
        if (!SqlServerFixture.IsAvailable) return;

        _factory = new PortalAppFactory(Roster(), connectionString: Sql.ConnectionString);
        World = await BuildAsync();
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    public HttpClient NewClient() =>
        _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<HttpClient> SignedInAsync(string key)
    {
        var client = NewClient();
        (await client.PostAsJsonAsync("/api/auth/dev-login", new { key })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<World> BuildAsync()
    {
        using var leadA = await SignedInAsync("lead-a");
        var musterCompany = await CreateClientAsync(leadA, "Muster Logistik", "muster", "M01", "M02");
        await CreateProjectAsync(leadA, "muster", "muster-2026", "Muster 2026");
        var anna = await InviteAsync(leadA, "anna@muster.example");
        await GrantClientAsync(leadA, "muster-2026", anna.UserId, musterCompany);

        using var leadB = await SignedInAsync("lead-b");
        var nordwindCompany = await CreateClientAsync(leadB, "Nordwind", "nordwind", "N01");
        await CreateProjectAsync(leadB, "nordwind", "nordwind-2026", "Nordwind 2026");
        var tom = await InviteAsync(leadB, "tom@nordwind.example");
        await GrantClientAsync(leadB, "nordwind-2026", tom.UserId, nordwindCompany);

        return new World(
            anna.UserId, anna.PickerKey,
            tom.UserId, tom.PickerKey,
            musterCompany, nordwindCompany);
    }

    private static async Task<Guid> CreateClientAsync(
        HttpClient staff, string name, string slug, params string[] codes)
    {
        var response = await staff.PostAsJsonAsync("/api/acp/clients", new
        {
            name,
            slug,
            companies = codes.Select(c => new { code = c, name = $"{name} {c}" }),
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("companies").EnumerateArray().First().GetProperty("id").GetGuid();
    }

    private static async Task CreateProjectAsync(
        HttpClient staff, string clientSlug, string key, string name) =>
        (await staff.PostAsJsonAsync("/api/acp/projects", new { clientSlug, key, name }))
            .EnsureSuccessStatusCode();

    private async Task<(Guid UserId, string PickerKey)> InviteAsync(HttpClient staff, string email)
    {
        var response = await staff.PostAsJsonAsync("/api/acp/invitations", new { email });
        response.EnsureSuccessStatusCode();

        var userId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("userId").GetGuid();

        using var anonymous = NewClient();
        var accounts = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/dev-accounts");

        var key = accounts.EnumerateArray()
            .Single(a => a.GetProperty("email").GetString() == email)
            .GetProperty("key").GetString()!;

        return (userId, key);
    }

    private static async Task GrantClientAsync(
        HttpClient staff, string projectKey, Guid userId, Guid companyId) =>
        (await staff.PostAsJsonAsync($"/api/acp/projects/{projectKey}/grants", new
        {
            userId,
            role = "Client",
            companyIds = new[] { companyId },
        })).EnsureSuccessStatusCode();

    private static Dictionary<string, string?> Roster() => new()
    {
        ["Auth:Mode"] = "Development",
        ["Auth:HomeTenantId"] = PortalAppFactory.HomeTenant,
        ["Auth:StaffEmailDomains:0"] = "acp.test",
        ["Auth:InviteRedirectUrl"] = "http://localhost/",

        ["Auth:Development:Accounts:0:Key"] = "lead-a",
        ["Auth:Development:Accounts:0:ObjectId"] = "oid-lead-a",
        ["Auth:Development:Accounts:0:Email"] = "lead.a@acp.test",
        ["Auth:Development:Accounts:0:DisplayName"] = "Lead A",
        ["Auth:Development:Accounts:0:Guest"] = "false",

        ["Auth:Development:Accounts:1:Key"] = "lead-b",
        ["Auth:Development:Accounts:1:ObjectId"] = "oid-lead-b",
        ["Auth:Development:Accounts:1:Email"] = "lead.b@acp.test",
        ["Auth:Development:Accounts:1:DisplayName"] = "Lead B",
        ["Auth:Development:Accounts:1:Guest"] = "false",
    };
}

public sealed record World(
    Guid AnnaUserId,
    string AnnaKey,
    Guid TomUserId,
    string TomKey,
    Guid MusterCompanyId,
    Guid NordwindCompanyId);
