using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Acp.Portal.Tests.Harness;

/// <summary>
/// A provisioned client with a released Databook, and a second client to try to reach it from.
/// </summary>
/// <remarks>
/// Power BI is simulated, so nothing here proves that Power BI honours a token. What it does
/// exercise is every decision the portal makes before handing one over: which version, whose
/// company codes, which profile and workspace, and who is allowed to ask.
/// </remarks>
public sealed class PowerBiFixture : IAsyncLifetime
{
    private PortalAppFactory? _factory;

    public SqlServerFixture Sql { get; } = new("AcpPortal_PowerBi_Tests");

    public PowerBiWorld World { get; private set; } = null!;

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

    /// <summary>A .pbix in name only. The simulated Power BI drains the stream and discards it.</summary>
    public static MultipartFormDataContent Pbix(string fileName = "databook.pbix")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("not really a pbix"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);

        return content;
    }

    private async Task<PowerBiWorld> BuildAsync()
    {
        using var leadA = await SignedInAsync("lead-a");
        using var leadB = await SignedInAsync("lead-b");

        var musterCompanies = await CreateClientAsync(leadA, "Muster", "muster", "M01", "M02");
        await Provision(leadA, "muster");
        await CreateProjectAsync(leadA, "muster", "muster-2026", "Muster 2026");

        // lead-b has to be on the project to release, and has to be somebody other than the
        // uploader for the four-eyes rule to be satisfiable at all.
        var leadBId = await UserIdAsync(leadA, "lead.b@acp.test");
        await GrantAsync(leadA, "muster-2026", new { userId = leadBId, role = "Lead" });

        var anna = await InviteAsync(leadA, "anna@muster.example");
        await GrantAsync(leadA, "muster-2026", new
        {
            userId = anna.UserId,
            role = "Client",
            companyIds = new[] { musterCompanies[0] },
        });

        // lead-a uploads, lead-b releases.
        (await leadA.PostAsync("/api/acp/projects/muster-2026/versions", Pbix())).EnsureSuccessStatusCode();
        (await leadB.PostAsJsonAsync("/api/acp/projects/muster-2026/versions/1/release", new { note = "First cut" }))
            .EnsureSuccessStatusCode();

        // A second client, provisioned but with nothing released, to reach across to.
        var nordwindCompanies = await CreateClientAsync(leadB, "Nordwind", "nordwind", "N01");
        await Provision(leadB, "nordwind");
        await CreateProjectAsync(leadB, "nordwind", "nordwind-2026", "Nordwind 2026");
        var tom = await InviteAsync(leadB, "tom@nordwind.example");
        await GrantAsync(leadB, "nordwind-2026", new
        {
            userId = tom.UserId,
            role = "Client",
            companyIds = new[] { nordwindCompanies[0] },
        });

        return new PowerBiWorld(
            anna.UserId, anna.PickerKey,
            tom.UserId, tom.PickerKey,
            leadBId,
            musterCompanies, nordwindCompanies);
    }

    private static async Task Provision(HttpClient staff, string slug) =>
        (await staff.PostAsync($"/api/acp/clients/{slug}/provision", content: null)).EnsureSuccessStatusCode();

    private static async Task<Guid[]> CreateClientAsync(
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

        return body.GetProperty("companies").EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid())
            .ToArray();
    }

    private static async Task CreateProjectAsync(
        HttpClient staff, string clientSlug, string key, string name) =>
        (await staff.PostAsJsonAsync("/api/acp/projects", new { clientSlug, key, name }))
            .EnsureSuccessStatusCode();

    private static async Task GrantAsync(HttpClient staff, string projectKey, object grant) =>
        (await staff.PostAsJsonAsync($"/api/acp/projects/{projectKey}/grants", grant))
            .EnsureSuccessStatusCode();

    private static async Task<Guid> UserIdAsync(HttpClient staff, string email)
    {
        var users = await staff.GetFromJsonAsync<JsonElement>("/api/acp/users");

        return users.EnumerateArray()
            .Single(u => u.GetProperty("email").GetString() == email)
            .GetProperty("id").GetGuid();
    }

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

    private static Dictionary<string, string?> Roster() => new()
    {
        ["Auth:Mode"] = "Development",
        ["Auth:HomeTenantId"] = PortalAppFactory.HomeTenant,
        ["Auth:StaffEmailDomains:0"] = "acp.test",
        ["Auth:InviteRedirectUrl"] = "http://localhost/",
        ["PowerBi:Mode"] = "Fake",

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

public sealed record PowerBiWorld(
    Guid AnnaUserId,
    string AnnaKey,
    Guid TomUserId,
    string TomKey,
    Guid LeadBUserId,
    Guid[] MusterCompanies,
    Guid[] NordwindCompanies);
