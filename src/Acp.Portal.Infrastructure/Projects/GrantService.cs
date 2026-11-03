using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.Projects;

/// <summary>
/// Who may see a project, and — for client contacts — which companies inside it.
/// </summary>
/// <remarks>
/// Isolation wall 1. Every grant written here is what the row-level security predicate reads,
/// what the project screens resolve through, and from Phase 4 what fills the company codes in
/// an embed token. Nothing downstream re-derives access from anything else, which is why this
/// is the only file that writes to ProjectGrants.
/// </remarks>
public sealed class GrantService(
    PortalDbContext db,
    TimeProvider clock,
    ILogger<GrantService> logger)
{
    private static readonly ProjectRole[] AcpRoles = [ProjectRole.Lead, ProjectRole.Member, ProjectRole.Viewer];

    /// <summary>
    /// Adds or updates one person's grant. Returns null when the project is not visible to the
    /// person asking, which the caller turns into a 404.
    /// </summary>
    public async Task<GrantView?> SetAsync(
        string projectKey,
        Guid targetUserId,
        ProjectRole role,
        DateOnly? endDate,
        IReadOnlyList<Guid> companyIds,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        // Row-level security has already removed every project the caller holds no grant on, so
        // this returning null covers "no such project" and "not yours" together.
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == projectKey, cancellationToken);
        if (project is null) return null;

        await RequireLeadAsync(project.Id, actingUser, cancellationToken);

        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken)
            ?? throw new PortalRuleException("No such person. Invite them first.");

        RequireRoleFitsPerson(target, role);

        var companies = await ResolveCompaniesAsync(project, role, companyIds, cancellationToken);
        var now = clock.GetUtcNow();

        var grant = await db.ProjectGrants
            .Include(g => g.Companies)
            .FirstOrDefaultAsync(g => g.ProjectId == project.Id && g.UserId == target.Id, cancellationToken);

        var isNew = grant is null;

        if (grant is null)
        {
            grant = new ProjectGrant
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                UserId = target.Id,
                CreatedAt = now,
            };
            db.ProjectGrants.Add(grant);
        }

        grant.Role = role;
        grant.EndDate = endDate;

        grant.Companies.Clear();
        foreach (var company in companies)
        {
            grant.Companies.Add(new GrantCompany { GrantId = grant.Id, CompanyId = company.Id });
        }

        if (!isNew)
        {
            // Changing an existing grant ends that person's session on their very next request.
            // A brand-new grant deliberately does not: it only ever widens what someone can see,
            // and signing a client out at the moment they are given something to look at would
            // be confusing for no gain. Removal and narrowing are what have to bite immediately.
            target.SecurityStamp = Guid.NewGuid().ToString("N");
        }

        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = isNew ? "grant.added" : "grant.changed",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new
            {
                subject = target.Email,
                role = role.ToString(),
                endDate,
                companies = companies.Select(c => c.Code),
            }),
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Action} {Role} on {Project} for {Email}.",
            isNew ? "Granted" : "Changed", role, projectKey, target.Email);

        return await ViewAsync(grant.Id, cancellationToken);
    }

    /// <summary>
    /// Removes a grant. Returns false when the project is not visible to the person asking, or
    /// when the grant does not belong to it — both of which the caller turns into a 404.
    /// </summary>
    public async Task<bool> RevokeAsync(
        string projectKey,
        Guid grantId,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Key == projectKey, cancellationToken);
        if (project is null) return false;

        await RequireLeadAsync(project.Id, actingUser, cancellationToken);

        var grant = await db.ProjectGrants
            .Include(g => g.User)
            .FirstOrDefaultAsync(g => g.Id == grantId && g.ProjectId == project.Id, cancellationToken);

        if (grant is null) return false;

        if (grant.Role == ProjectRole.Lead)
        {
            var otherLeads = await db.ProjectGrants.CountAsync(
                g => g.ProjectId == project.Id && g.Role == ProjectRole.Lead && g.Id != grant.Id,
                cancellationToken);

            if (otherLeads == 0)
            {
                // A project with no lead is a project nobody can grant access to or release
                // from, and the only way back is a database edit.
                throw new PortalRuleException(
                    "This is the last lead on the project. Make somebody else a lead first.");
            }
        }

        var now = clock.GetUtcNow();

        // The one that must bite immediately: their session ends on their next request rather
        // than whenever the cookie would have expired.
        grant.User!.SecurityStamp = Guid.NewGuid().ToString("N");

        db.ProjectGrants.Remove(grant);
        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "grant.removed",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new { subject = grant.User.Email, role = grant.Role.ToString() }),
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Revoked {Role} on {Project} from {Email}.", grant.Role, projectKey, grant.User.Email);

        return true;
    }

    /// <summary>Null when the project is not visible to the caller.</summary>
    public async Task<IReadOnlyList<GrantView>?> ListAsync(
        string projectKey,
        CancellationToken cancellationToken = default)
    {
        var projectId = await db.Projects
            .Where(p => p.Key == projectKey)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectId is null) return null;

        return await db.ProjectGrants
            .AsNoTracking()
            .Where(g => g.ProjectId == projectId.Value)
            .OrderBy(g => g.Role).ThenBy(g => g.User!.DisplayName)
            .Select(g => new GrantView(
                g.Id,
                g.UserId,
                g.User!.DisplayName,
                g.User.Email,
                g.User.Kind.ToString(),
                g.Role.ToString(),
                g.EndDate,
                g.Companies.Select(c => c.Company!.Code).OrderBy(c => c).ToList()))
            .ToListAsync(cancellationToken);
    }

    private async Task RequireLeadAsync(Guid projectId, Guid actingUser, CancellationToken cancellationToken)
    {
        var isLead = await db.ProjectGrants.AnyAsync(
            g => g.ProjectId == projectId && g.UserId == actingUser && g.Role == ProjectRole.Lead,
            cancellationToken);

        if (!isLead)
        {
            // Not a 404: the caller can evidently see this project, so hiding it now would only
            // be confusing. They simply are not a lead on it.
            throw new PortalRuleException("Only a lead on this project can change who may see it.");
        }
    }

    private static void RequireRoleFitsPerson(User target, ProjectRole role)
    {
        var fits = target.Kind switch
        {
            UserKind.ClientUser => role == ProjectRole.Client,
            UserKind.AcpStaff => AcpRoles.Contains(role),
            _ => false,
        };

        if (!fits)
        {
            throw new PortalRuleException(
                $"{target.DisplayName} is {(target.Kind == UserKind.AcpStaff ? "ACP staff" : "a client contact")}, "
                + $"so the role '{role}' does not apply. "
                + (target.Kind == UserKind.AcpStaff ? "Use Lead, Member or Viewer." : "Use Client."));
        }
    }

    /// <summary>
    /// Turns the company ids on a request into rows, and refuses anything that is not a company
    /// of this project's own client.
    /// </summary>
    /// <remarks>
    /// This is the check that stops a consultant — or a tampered request — attaching Client B's
    /// company codes to a grant on Client A's project. From Phase 4 those codes go straight into
    /// an embed token, so a code that got in here wrongly would be a code the model honours.
    /// </remarks>
    private async Task<List<Company>> ResolveCompaniesAsync(
        Project project,
        ProjectRole role,
        IReadOnlyList<Guid> companyIds,
        CancellationToken cancellationToken)
    {
        if (role != ProjectRole.Client)
        {
            if (companyIds.Count > 0)
            {
                throw new PortalRuleException(
                    "ACP roles see every company in the project, so do not name any.");
            }

            return [];
        }

        var wanted = companyIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            throw new PortalRuleException("A client contact must be given at least one company.");
        }

        var companies = await db.Companies
            .Where(c => c.ClientId == project.ClientId && wanted.Contains(c.Id))
            .ToListAsync(cancellationToken);

        if (companies.Count != wanted.Count)
        {
            throw new PortalRuleException(
                "One of those companies does not belong to this project's client.");
        }

        return companies;
    }

    private async Task<GrantView?> ViewAsync(Guid grantId, CancellationToken cancellationToken) =>
        await db.ProjectGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId)
            .Select(g => new GrantView(
                g.Id,
                g.UserId,
                g.User!.DisplayName,
                g.User.Email,
                g.User.Kind.ToString(),
                g.Role.ToString(),
                g.EndDate,
                g.Companies.Select(c => c.Company!.Code).OrderBy(c => c).ToList()))
            .FirstOrDefaultAsync(cancellationToken);
}

public sealed record GrantView(
    Guid Id,
    Guid UserId,
    string DisplayName,
    string Email,
    string Kind,
    string Role,
    DateOnly? EndDate,
    IReadOnlyList<string> Companies);
