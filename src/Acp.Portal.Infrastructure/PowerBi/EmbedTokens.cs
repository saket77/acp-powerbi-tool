using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// Mints the token a browser uses to render one report. The narrowest point in the whole system.
/// </summary>
/// <remarks>
/// <para>
/// Every value in the request comes from the database: the profile from the client row, the
/// workspace from the client row, the report and model from the released version, the company
/// codes from the viewer's own grant. Nothing is read from the request except which project is
/// being asked for, and that is resolved through row-level security, so a project the caller
/// holds no grant on does not exist to this code.
/// </para>
/// <para>
/// Walls 3, 4 and 5 all land here: the token is minted <i>as the client's profile</i>, scoped to
/// <i>one report and one model</i>, carrying <i>the viewer's own codes</i>. Walls 4 and 5 are
/// enforced by Power BI rather than by this file, which is precisely why Gate 1 exists.
/// </para>
/// </remarks>
public sealed class EmbedTokens(
    PortalDbContext db,
    IPowerBiClient powerBi,
    IOptions<PowerBiOptions> options,
    TimeProvider clock,
    ILogger<EmbedTokens> logger)
{
    private static readonly ProjectRole[] AcpRoles = [ProjectRole.Lead, ProjectRole.Member, ProjectRole.Viewer];

    private readonly PowerBiOptions _options = options.Value;

    /// <summary>
    /// Null means "no such project, or not yours" — the caller turns that into a 404 without
    /// knowing which, because row-level security already decided and did not say.
    /// </summary>
    public async Task<EmbedOutcome?> CreateAsync(
        string projectKey,
        bool preferDraft,
        Guid? viewAsUserId,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.Key == projectKey, cancellationToken);

        if (project is null) return null;

        var grant = await db.ProjectGrants
            .Include(g => g.Companies).ThenInclude(c => c.Company)
            .Include(g => g.User)
            .FirstOrDefaultAsync(g => g.ProjectId == project.Id && g.UserId == actingUser, cancellationToken);

        // Row-level security let the project through, so a grant exists. If it somehow does
        // not, treat it as not found rather than guessing.
        if (grant is null) return null;

        var isAcp = AcpRoles.Contains(grant.Role);
        var client = project.Client!;

        if (!client.IsProvisioned)
        {
            return new EmbedUnavailable(
                isAcp
                    ? $"{client.Name} has no Power BI workspace yet. Provision the client first."
                    : "This Databook is not ready yet.");
        }

        if (!isAcp && project.EndDate < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
        {
            return new EmbedUnavailable("This project has ended.");
        }

        var version = await PickVersionAsync(project.Id, isAcp, preferDraft, cancellationToken);
        if (version is null)
        {
            return new EmbedUnavailable(
                isAcp
                    ? "Nothing has been uploaded for this project yet."
                    : "Nothing has been released for this project yet.");
        }

        var identity = await ComposeIdentityAsync(project, grant, isAcp, viewAsUserId, cancellationToken);

        var token = await powerBi.GenerateEmbedTokenAsync(
            new PowerBiEmbedTokenRequest(
                client.PowerBiProfileId!,
                client.PowerBiWorkspaceId!,
                version.PowerBiReportId!,
                version.PowerBiDatasetId!,
                identity.Identity,
                _options.EmbedTokenLifetimeMinutes),
            cancellationToken);

        db.AuditEvents.Add(new AuditEvent
        {
            At = clock.GetUtcNow(),
            UserId = actingUser,
            Action = "token.issued",
            ProjectId = project.Id,
            // The token itself never appears here. Everything else does, because an auditor
            // asking "what could this person see at 14:05" needs exactly these fields.
            Detail = JsonSerializer.Serialize(new
            {
                version = version.Version,
                reportId = version.PowerBiReportId,
                roles = identity.Identity.Roles,
                companies = identity.Companies,
                viewAs = identity.ViewingAs,
                draft = version.State != ReportVersionState.Released,
                simulated = _options.Mode == PowerBiMode.Fake,
            }),
        });

        await db.SaveChangesAsync(cancellationToken);

        var target = await powerBi.GetReportAsync(
            client.PowerBiProfileId!, client.PowerBiWorkspaceId!, version.PowerBiReportId!, cancellationToken);

        logger.LogInformation(
            "Issued an embed token for {Project} v{Version} to {User}, expiring {Expiry:u}.",
            project.Key, version.Version, actingUser, token.ExpiresAt);

        return new EmbedReady(
            Provider: _options.Mode.ToString(),
            EmbedUrl: target.EmbedUrl,
            ReportId: version.PowerBiReportId!,
            Token: token.Token,
            ExpiresAt: token.ExpiresAt,
            Version: version.Version,
            IsDraft: version.State != ReportVersionState.Released,
            // Consultants are shown exactly what filter they are looking through — which is the
            // whole value of "view as". Clients are shown their report, not a description of
            // the rules that produced it.
            Scope: isAcp
                ? new EmbedScope(
                    client.PowerBiWorkspaceId!,
                    version.PowerBiDatasetId!,
                    identity.Identity.Roles,
                    identity.Identity.Username,
                    identity.Identity.CustomData,
                    identity.Companies)
                : null);
    }

    private async Task<ReportVersion?> PickVersionAsync(
        Guid projectId, bool isAcp, bool preferDraft, CancellationToken cancellationToken)
    {
        var versions = db.ReportVersions.Where(v => v.ProjectId == projectId);

        if (!isAcp)
        {
            // A client sees exactly one thing: what somebody deliberately released.
            return await versions
                .FirstOrDefaultAsync(v => v.State == ReportVersionState.Released, cancellationToken);
        }

        if (preferDraft)
        {
            var draft = await versions
                .Where(v => v.State == ReportVersionState.Draft)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken);

            if (draft is not null) return draft;
        }

        return await versions.FirstOrDefaultAsync(v => v.State == ReportVersionState.Released, cancellationToken)
            ?? await versions
                .Where(v => v.State == ReportVersionState.Draft)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<ComposedIdentity> ComposeIdentityAsync(
        Project project,
        ProjectGrant grant,
        bool isAcp,
        Guid? viewAsUserId,
        CancellationToken cancellationToken)
    {
        if (!isAcp)
        {
            if (viewAsUserId is not null)
            {
                // A client asking to be somebody else is not a mistake worth accommodating.
                throw new PortalRuleException("Only ACP staff can preview as another person.");
            }

            return Compose(grant);
        }

        if (viewAsUserId is null)
        {
            return new ComposedIdentity(
                EffectiveIdentityFactory.ForAcp(grant.User!.Email, _options), [], null);
        }

        // The grant has to be on *this* project. Without that clause a consultant could borrow
        // a client's identity from another engagement and look through it here.
        var target = await db.ProjectGrants
            .Include(g => g.Companies).ThenInclude(c => c.Company)
            .Include(g => g.User)
            .FirstOrDefaultAsync(
                g => g.ProjectId == project.Id
                     && g.UserId == viewAsUserId.Value
                     && g.Role == ProjectRole.Client,
                cancellationToken);

        if (target is null)
        {
            throw new PortalRuleException("That person has no client access to this project.");
        }

        return Compose(target);
    }

    private ComposedIdentity Compose(ProjectGrant grant)
    {
        var codes = grant.Companies
            .Select(c => c.Company!.Code)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ComposedIdentity(
            EffectiveIdentityFactory.ForClient(grant.User!.Email, codes, _options),
            codes,
            grant.User.Email);
    }

    private sealed record ComposedIdentity(
        PowerBiEffectiveIdentity Identity,
        IReadOnlyList<string> Companies,
        string? ViewingAs);
}

public abstract record EmbedOutcome;

/// <param name="ExpiresAt">
/// Power BI's answer, not ours. The browser schedules its renewal from this and never from the
/// lifetime that was requested.
/// </param>
/// <param name="Scope">What the token actually says. Shown to ACP staff only.</param>
public sealed record EmbedReady(
    string Provider,
    string EmbedUrl,
    string ReportId,
    string Token,
    DateTimeOffset ExpiresAt,
    int Version,
    bool IsDraft,
    EmbedScope? Scope) : EmbedOutcome;

/// <summary>A normal state, not an error: there is simply nothing to show this person yet.</summary>
public sealed record EmbedUnavailable(string Reason) : EmbedOutcome;

public sealed record EmbedScope(
    string WorkspaceId,
    string DatasetId,
    IReadOnlyList<string> Roles,
    string Username,
    string? CustomData,
    IReadOnlyList<string> Companies);
