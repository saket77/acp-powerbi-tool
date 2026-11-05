using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Acp.Portal.Infrastructure.Staging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// The whole upload: stage, PUT the bytes straight to Azurite with the issued SAS, tell the
    /// portal it finished, then run the queued job.
    /// </summary>
    /// <remarks>
    /// The PUT uses a bare <see cref="HttpClient"/> rather than the test server's, because the
    /// point of this path is that the file does <i>not</i> go through the application. If the
    /// SAS were wrong, or write-only in a way that forbade the write, this is where it would show.
    /// </remarks>
    public async Task<int> UploadAsync(HttpClient staff, string projectKey, string fileName = "databook.pbix")
    {
        var staged = await StageAsync(staff, projectKey, fileName);

        await PutAsync(staged.GetProperty("uploadUrl").GetString()!, "not really a pbix, and it does not need to be");

        var uploadId = staged.GetProperty("uploadId").GetGuid();
        (await staff.PostAsJsonAsync(
            $"/api/acp/projects/{projectKey}/uploads/{uploadId}/complete", new { }))
            .EnsureSuccessStatusCode();

        await DrainAsync();

        return staged.GetProperty("version").GetInt32();
    }

    public static async Task<JsonElement> StageAsync(HttpClient staff, string projectKey, string fileName)
    {
        var response = await staff.PostAsJsonAsync($"/api/acp/projects/{projectKey}/uploads", new { fileName });
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Writes to Blob exactly as a browser would: a plain PUT with the block-blob header.</summary>
    public static async Task<System.Net.HttpStatusCode> PutAsync(string sasUrl, string body)
    {
        using var direct = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, sasUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");

        using var response = await direct.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Runs whatever is on the queue, now. The hosted worker is removed in tests so that an
    /// import is something a test caused rather than something it waited for.
    /// </summary>
    /// <summary>Puts a job back on the queue, to stand in for a redelivered message.</summary>
    public async Task RequeueAsync(Guid jobId)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ImportQueue>().EnqueueAsync(jobId);
    }

    public T Resolve<T>(IServiceScope scope) where T : notnull =>
        scope.ServiceProvider.GetRequiredService<T>();

    public IServiceScope NewScope() => _factory!.Services.CreateScope();

    public async Task DrainAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();

        var queue = scope.ServiceProvider.GetRequiredService<ImportQueue>();
        var pipeline = scope.ServiceProvider.GetRequiredService<UploadPipeline>();

        while (await queue.ReceiveAsync() is { } message)
        {
            if (!await pipeline.ProcessAsync(message.JobId)) break;
            await queue.CompleteAsync(message);
        }
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
        var first = await UploadAsync(leadA, "muster-2026");
        (await leadB.PostAsJsonAsync(
            $"/api/acp/projects/muster-2026/versions/{first}/release", new { note = "First cut" }))
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

        // Its own container and queue, so a test run and a dev server pointed at the same
        // Azurite cannot drain each other's work.
        ["Storage:UploadsContainer"] = "test-pbix-uploads",
        ["Storage:ImportQueue"] = "test-pbix-imports",

        // Small enough that a test can exceed it with a couple of megabytes rather than a real
        // Databook, and large enough that every other test is unaffected.
        ["Storage:MaxUploadMegabytes"] = "1",

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

/// <summary>
/// Both Power BI suites share one fixture, and therefore one database and one world.
/// </summary>
/// <remarks>
/// An <c>IClassFixture</c> is built once per test <i>class</i>, so two classes asking for this
/// would each drop and recreate the same database — in parallel. A collection fixture is built
/// once for all of them, and xunit runs a collection's classes one after another.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PowerBiCollection : ICollectionFixture<PowerBiFixture>
{
    public const string Name = "power-bi";
}

public sealed record PowerBiWorld(
    Guid AnnaUserId,
    string AnnaKey,
    Guid TomUserId,
    string TomKey,
    Guid LeadBUserId,
    Guid[] MusterCompanies,
    Guid[] NordwindCompanies);
