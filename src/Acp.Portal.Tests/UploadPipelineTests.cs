using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace Acp.Portal.Tests;

/// <summary>
/// The upload pipeline, against the real Azurite from docker-compose: a real SAS, a real
/// browser-shaped PUT, a real queue message, and the worker's own code running the job.
/// </summary>
/// <remarks>
/// Power BI is still simulated — there is no capacity — so what these prove is the staging half:
/// that the credential handed to a browser is as small as it is supposed to be, that a file
/// which never arrives is noticed, that the checks reach the consultant as sentences, and that
/// the staged copy does not outlive the import.
/// </remarks>
[Collection(PowerBiCollection.Name)]
public sealed class UploadPipelineTests(PowerBiFixture fixture)
{
    // ------------------------------------------------------------ the credential

    [RequiresLocalServicesFact]
    public async Task TheUploadUrlCanWriteAndNothingElse()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        var staged = await PowerBiFixture.StageAsync(lead, "muster-2026", "scoped.pbix");
        var url = staged.GetProperty("uploadUrl").GetString()!;

        Assert.Equal(HttpStatusCode.Created, await PowerBiFixture.PutAsync(url, "hello"));

        // Write-only: whoever holds this cannot read back what they or anybody else wrote.
        using var direct = new HttpClient();
        var read = await direct.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [RequiresLocalServicesFact]
    public async Task TheUploadUrlNamesOneBlobAndOnlyThatOne()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        var staged = await PowerBiFixture.StageAsync(lead, "muster-2026", "one-blob.pbix");
        var url = new Uri(staged.GetProperty("uploadUrl").GetString()!);

        // Same signature, different blob. A SAS signed for one blob says so inside the
        // signature, so moving it is not a matter of editing the path.
        var elsewhere = new UriBuilder(url) { Path = "/devstoreaccount1/test-pbix-uploads/somebody-elses.pbix" }.Uri;

        Assert.Equal(HttpStatusCode.Forbidden, await PowerBiFixture.PutAsync(elsewhere.ToString(), "hello"));
    }

    [RequiresLocalServicesFact]
    public async Task AFileThatNeverArrivesIsNoticed()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        var staged = await PowerBiFixture.StageAsync(lead, "muster-2026", "never-sent.pbix");
        var uploadId = staged.GetProperty("uploadId").GetGuid();

        // The browser says it finished; the storage account is what decides.
        var response = await lead.PostAsJsonAsync(
            $"/api/acp/projects/muster-2026/uploads/{uploadId}/complete", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("did not arrive", await response.Content.ReadAsStringAsync());
    }

    [RequiresLocalServicesFact]
    public async Task AFileOverTheLimitIsRefusedAndDiscarded()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        var staged = await PowerBiFixture.StageAsync(lead, "muster-2026", "too-big.pbix");

        await PowerBiFixture.PutAsync(
            staged.GetProperty("uploadUrl").GetString()!,
            new string('x', 2 * 1024 * 1024));

        var uploadId = staged.GetProperty("uploadId").GetGuid();
        var response = await lead.PostAsJsonAsync(
            $"/api/acp/projects/muster-2026/uploads/{uploadId}/complete", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("limit is 1 MB", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------ the checks, as a consultant meets them

    [RequiresLocalServicesTheory]
    [InlineData("no-client-role.pbix", "This file has no Client role. Use template 1.6.")]
    [InlineData("old-template.pbix", "built on template 1.4")]
    [InlineData("no-companies.pbix", "has no 'Companies' table")]
    [InlineData("extra-company.pbix", "'ZZ99'")]
    [InlineData("unfiltered.pbix", "would see all of that table")]
    public async Task ABrokenFileComesBackWithASentenceNamingTheFix(string fileName, string expected)
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var version = await fixture.UploadAsync(lead, "muster-2026", fileName);
        var versions = await lead.GetFromJsonAsync<JsonElement>("/api/acp/projects/muster-2026/versions");

        var row = versions.EnumerateArray().Single(v => v.GetProperty("version").GetInt32() == version);

        Assert.Equal(nameof(ReportVersionState.CheckFailed), row.GetProperty("state").GetString());
        Assert.Contains(expected, row.GetProperty("checkFailureMessage").GetString()!);
    }

    [RequiresLocalServicesFact]
    public async Task AFileThatMeetsTheContractReachesDraftUnaided()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var version = await fixture.UploadAsync(lead, "muster-2026");
        var versions = await lead.GetFromJsonAsync<JsonElement>("/api/acp/projects/muster-2026/versions");

        var row = versions.EnumerateArray().Single(v => v.GetProperty("version").GetInt32() == version);

        Assert.Equal(nameof(ReportVersionState.Draft), row.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("checkFailureMessage").ValueKind);
    }

    [RequiresLocalServicesFact]
    public async Task ARefusedFileIsStillDiscardedFromStaging()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        await fixture.UploadAsync(lead, "muster-2026", "no-client-role.pbix");

        // Staging is a corridor, not an archive. A refused Databook is still a client's data,
        // so it goes too.
        Assert.Empty(await StagedBlobsAsync());
    }

    [RequiresLocalServicesFact]
    public async Task TheStagedCopyDoesNotOutliveTheImport()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        await fixture.UploadAsync(lead, "muster-2026");

        Assert.Empty(await StagedBlobsAsync());
    }

    // ------------------------------------------------------------ at-least-once delivery

    [RequiresLocalServicesFact]
    public async Task ARedeliveredMessageDoesNotImportTwice()
    {
        using var lead = await fixture.SignedInAsync("lead-a");
        var version = await fixture.UploadAsync(lead, "muster-2026");

        Guid jobId;
        int versionsBefore;

        using (var scope = fixture.NewScope())
        {
            var db = fixture.Resolve<PortalDbContext>(scope);
            using var _ = Acp.Portal.Infrastructure.Data.UnfilteredScope.Enter();

            jobId = await db.UploadJobs
                .Where(j => j.ReportVersion!.Version == version)
                .Select(j => j.Id)
                .FirstAsync();

            versionsBefore = await db.ReportVersions.CountAsync();
        }

        // A queue promises at-least-once, so the job row is the record of what already happened.
        await fixture.RequeueAsync(jobId);
        await fixture.DrainAsync();

        using (var scope = fixture.NewScope())
        {
            var db = fixture.Resolve<PortalDbContext>(scope);
            using var _ = Acp.Portal.Infrastructure.Data.UnfilteredScope.Enter();

            Assert.Equal(versionsBefore, await db.ReportVersions.CountAsync());
            Assert.Equal(UploadJobState.Done, await db.UploadJobs.Where(j => j.Id == jobId)
                .Select(j => j.State).FirstAsync());
        }
    }

    [RequiresLocalServicesFact]
    public async Task AViewerCannotUpload()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        // lead-b is a Lead on this project; make somebody a Viewer and check they cannot.
        await lead.PostAsJsonAsync("/api/acp/projects/muster-2026/grants", new
        {
            userId = fixture.World.LeadBUserId,
            role = "Viewer",
        });

        using var viewer = await fixture.SignedInAsync("lead-b");
        var response = await viewer.PostAsJsonAsync(
            "/api/acp/projects/muster-2026/uploads", new { fileName = "nope.pbix" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("lead or a member", await response.Content.ReadAsStringAsync());

        // Put them back, so the order tests run in cannot matter.
        await lead.PostAsJsonAsync("/api/acp/projects/muster-2026/grants", new
        {
            userId = fixture.World.LeadBUserId,
            role = "Lead",
        });
    }

    [RequiresLocalServicesFact]
    public async Task OnlyAPbixIsAccepted()
    {
        using var lead = await fixture.SignedInAsync("lead-a");

        var response = await lead.PostAsJsonAsync(
            "/api/acp/projects/muster-2026/uploads", new { fileName = "quarterly.xlsx" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<List<string>> StagedBlobsAsync()
    {
        using var scope = fixture.NewScope();
        var blobs = fixture.Resolve<Azure.Storage.Blobs.BlobServiceClient>(scope)
            .GetBlobContainerClient("test-pbix-uploads");

        var names = new List<string>();
        await foreach (var blob in blobs.GetBlobsAsync()) names.Add(blob.Name);

        // Uploads that were refused before the pipeline ran leave their blob behind on purpose
        // — the consultant may retry the complete call. Only imported ones must be gone.
        return names.Where(n => !n.Contains("never-sent") && !n.Contains("too-big")
                                && !n.Contains("scoped") && !n.Contains("one-blob")).ToList();
    }
}
