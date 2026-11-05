using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Acp.Portal.Domain.Abstractions;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// The real Power BI REST API.
/// </summary>
/// <remarks>
/// <para>
/// Not the Power BI .NET SDK. The SDK is a generated client over an API surface of which this
/// uses six calls, it carries its own authentication stack, and its profile support works by
/// constructing a differently-configured client per profile — which is the opposite of what this
/// needs, since the profile changes per request here. Six typed calls against HttpClient are
/// smaller, and the failures are legible.
/// </para>
/// <para>
/// <b>Nothing in this file has been run against Power BI.</b> It was written against the
/// published API shapes while no Fabric capacity existed. Gate 1 is where it first meets the
/// real service; the failures most likely to show up there are listed in
/// docs/implementation-plan.md under Phase 4.
/// </para>
/// </remarks>
public sealed class PowerBiRestClient(
    HttpClient http,
    TokenCredential credential,
    IOptions<PowerBiOptions> options,
    ILogger<PowerBiRestClient> logger) : IPowerBiClient
{
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly PowerBiOptions _options = options.Value;

    public async Task<PowerBiProfile> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        // Created as the application itself — there is no profile yet to act as.
        var profile = await SendAsync<ProfileResponse>(
            HttpMethod.Post, "profiles", profileId: null,
            body: new { displayName }, cancellationToken);

        logger.LogInformation("Created service principal profile {Profile} for {Name}.", profile.Id, displayName);

        return new PowerBiProfile(profile.Id, profile.DisplayName ?? displayName);
    }

    public async Task<PowerBiWorkspace> CreateWorkspaceAsync(
        string profileId, string name, CancellationToken cancellationToken = default)
    {
        var group = await SendAsync<GroupResponse>(
            HttpMethod.Post, "groups?workspaceV2=True", profileId,
            body: new { name }, cancellationToken);

        logger.LogInformation("Created workspace {Workspace} owned by profile {Profile}.", group.Id, profileId);

        return new PowerBiWorkspace(group.Id, group.Name ?? name);
    }

    public async Task AssignToCapacityAsync(
        string profileId, string workspaceId, string capacityId, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            HttpMethod.Post, $"groups/{workspaceId}/AssignToCapacity", profileId,
            // capacityId, confirmed against the REST reference. The admin-scoped sibling of this
            // call names the same thing capacityObjectId, which is an easy and silent mix-up.
            body: new { capacityId }, cancellationToken);

        logger.LogInformation("Assigned workspace {Workspace} to capacity {Capacity}.", workspaceId, capacityId);
    }

    public async Task<PowerBiImport> ImportReportAsync(
        PowerBiImportRequest request, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(request.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", request.FileName);

        var path = $"groups/{request.WorkspaceId}/imports"
            + $"?datasetDisplayName={Uri.EscapeDataString(request.DatasetDisplayName)}"
            + "&nameConflict=CreateOrOverwrite";

        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        await AuthorizeAsync(message, request.ProfileId, cancellationToken);

        using var response = await http.SendAsync(message, cancellationToken);
        var started = await ReadAsync<ImportResponse>(response, cancellationToken);

        return await PollImportAsync(request, started.Id, cancellationToken);
    }

    public async Task<PowerBiEmbedTarget> GetReportAsync(
        string profileId, string workspaceId, string reportId, CancellationToken cancellationToken = default)
    {
        var report = await SendAsync<ReportResponse>(
            HttpMethod.Get, $"groups/{workspaceId}/reports/{reportId}", profileId,
            body: null, cancellationToken);

        return new PowerBiEmbedTarget(report.Id, report.DatasetId ?? "", report.EmbedUrl ?? "");
    }

    public async Task<PowerBiEmbedToken> GenerateEmbedTokenAsync(
        PowerBiEmbedTokenRequest request, CancellationToken cancellationToken = default)
    {
        var identity = new IdentityRequest(
            request.Identity.Username,
            [.. request.Identity.Roles],
            [request.DatasetId],
            request.Identity.CustomData);

        var body = new GenerateTokenRequest(
            // xmlaPermissions Off: a viewer's token must not be usable to query the model
            // directly. The portal's own XMLA access in Phase 5 runs as the profile, not as this.
            [new DatasetRequest(request.DatasetId, "Off")],
            [new ReportRequest(request.ReportId, AllowEdit: false)],
            [new WorkspaceRequest(request.WorkspaceId)],
            [identity],
            request.LifetimeMinutes);

        var token = await SendAsync<GenerateTokenResponse>(
            HttpMethod.Post, "GenerateToken", request.ProfileId, body, cancellationToken);

        var expiresAt = token.Expiration;

        // The embed token cannot outlive the Entra token used to mint it, and that one is
        // cached and refreshed on its own schedule — so a 30-minute request legitimately comes
        // back shorter. Callers use what came back, never what was asked for.
        var granted = expiresAt - DateTimeOffset.UtcNow;
        if (granted.TotalMinutes < request.LifetimeMinutes - 2)
        {
            logger.LogWarning(
                "Asked Power BI for {Requested} minutes and got {Granted:F0}. The Entra token was "
                + "near the end of its own life; the viewer will simply renew sooner.",
                request.LifetimeMinutes, granted.TotalMinutes);
        }

        return new PowerBiEmbedToken(token.Token, expiresAt);
    }

    private async Task<PowerBiImport> PollImportAsync(
        PowerBiImportRequest request, string importId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.ImportTimeoutSeconds);
        var delay = TimeSpan.FromSeconds(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 15));

            var state = await SendAsync<ImportResponse>(
                HttpMethod.Get, $"groups/{request.WorkspaceId}/imports/{importId}", request.ProfileId,
                body: null, cancellationToken);

            switch (state.ImportState)
            {
                case "Succeeded":
                    var report = state.Reports?.FirstOrDefault()
                        ?? throw new PortalRuleException(
                            "Power BI imported the file but it contains no report. A Databook must "
                            + "be a .pbix with a report page, not a dataset-only file.");

                    var dataset = state.Datasets?.FirstOrDefault()
                        ?? throw new PortalRuleException(
                            "Power BI imported the file but it contains no semantic model.");

                    return new PowerBiImport(importId, report.Id, dataset.Id, report.EmbedUrl ?? "");

                case "Failed":
                    throw new PortalRuleException(
                        "Power BI could not import this file. The usual causes are a corrupt "
                        + ".pbix, a file saved by a much newer Power BI Desktop, or a live "
                        + "connection the service cannot resolve.");
            }
        }

        throw new PortalRuleException(
            $"Power BI was still importing after {_options.ImportTimeoutSeconds} seconds. The file "
            + "may still arrive; check the workspace before uploading it again.");
    }

    private async Task AuthorizeAsync(HttpRequestMessage message, string? profileId, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        // Per request, never on the client. Setting it in both places is documented to fail
        // with an unauthorized error rather than anything that names the real problem.
        if (profileId is not null)
        {
            message.Headers.Add("X-PowerBI-profile-id", profileId);
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, string? profileId, object? body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        if (body is not null) message.Content = JsonContent.Create(body);

        await AuthorizeAsync(message, profileId, cancellationToken);

        using var response = await http.SendAsync(message, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task SendAsync(
        HttpMethod method, string path, string? profileId, object? body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        if (body is not null) message.Content = JsonContent.Create(body);

        await AuthorizeAsync(message, profileId, cancellationToken);

        using var response = await http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("Power BI returned success with no body.");
    }

    /// <summary>
    /// The three failures that actually happen, named with their fix. All three otherwise arrive
    /// as a bare status code with a GUID in it.
    /// </summary>
    private async Task ThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var url = response.RequestMessage?.RequestUri?.PathAndQuery;

        logger.LogError("Power BI {Status} on {Path}: {Body}", (int)response.StatusCode, url, body);

        throw new PortalRuleException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Power BI refused the request. Most often this means one of the tenant settings in "
                + "scripts/tenant-setup.md step 5 is off — 'service principals can use Fabric APIs' "
                + "or 'service principals can create and use profiles' — or the app registration is "
                + "not in the security group. Changes there take about 15 minutes. Power BI said: " + body,

            HttpStatusCode.NotFound =>
                "Power BI has no such item. If this is a workspace or a report the portal created "
                + "earlier, the likely cause is that it was created by a different identity: "
                + "profiles belong permanently to whoever made them. Power BI said: " + body,

            (HttpStatusCode)429 =>
                "Power BI is rate-limiting the portal. Wait a minute and retry.",

            HttpStatusCode.ServiceUnavailable =>
                "The capacity is unavailable. If it is paused, resume it with "
                + "`scripts/capacity.sh resume`. Power BI said: " + body,

            _ => $"Power BI returned {(int)response.StatusCode} for {url}. It said: {body}",
        });
    }

    private sealed record ProfileResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")] string? DisplayName);

    private sealed record GroupResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record ImportResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("importState")] string? ImportState,
        [property: JsonPropertyName("reports")] List<ReportResponse>? Reports,
        [property: JsonPropertyName("datasets")] List<DatasetResponse>? Datasets);

    private sealed record ReportResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("datasetId")] string? DatasetId,
        [property: JsonPropertyName("embedUrl")] string? EmbedUrl);

    private sealed record DatasetResponse([property: JsonPropertyName("id")] string Id);

    private sealed record GenerateTokenRequest(
        [property: JsonPropertyName("datasets")] DatasetRequest[] Datasets,
        [property: JsonPropertyName("reports")] ReportRequest[] Reports,
        [property: JsonPropertyName("targetWorkspaces")] WorkspaceRequest[] TargetWorkspaces,
        [property: JsonPropertyName("identities")] IdentityRequest[] Identities,
        [property: JsonPropertyName("lifetimeInMinutes")] int LifetimeInMinutes);

    private sealed record DatasetRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("xmlaPermissions")] string XmlaPermissions);

    private sealed record ReportRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("allowEdit")] bool AllowEdit);

    private sealed record WorkspaceRequest([property: JsonPropertyName("id")] string Id);

    private sealed record IdentityRequest(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("roles")] string[] Roles,
        [property: JsonPropertyName("datasets")] string[] Datasets,
        [property: JsonPropertyName("customData")] string? CustomData);

    private sealed record GenerateTokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("tokenId")] string? TokenId,
        [property: JsonPropertyName("expiration")] DateTimeOffset Expiration);
}
