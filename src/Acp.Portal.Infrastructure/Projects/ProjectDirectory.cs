using System.Text.Json;
using Acp.Portal.Domain;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.Projects;

/// <summary>
/// Projects, scoped to whoever is asking.
/// </summary>
/// <remarks>
/// Nothing in here filters by user. It does not have to: row-level security has already removed
/// every project the caller holds no grant on, so a query that forgets a filter returns nothing
/// rather than everything. That is the point of wall 2, and it is why <see cref="GetAsync"/>
/// can return null for both "no such project" and "not yours" without ever having to tell them
/// apart — the database already did.
/// </remarks>
public sealed class ProjectDirectory(
    PortalDbContext db,
    TimeProvider clock,
    ILogger<ProjectDirectory> logger)
{
    public async Task<Project> CreateAsync(
        string clientSlug,
        string key,
        string name,
        DateOnly endDate,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Slug == clientSlug, cancellationToken)
            ?? throw new PortalRuleException($"No client with the key '{clientSlug}'.");

        var trimmedName = name.Trim();
        if (trimmedName.Length == 0) throw new PortalRuleException("A project needs a name.");

        var projectKey = ClientDirectory.Slugify(string.IsNullOrWhiteSpace(key) ? trimmedName : key);
        if (projectKey.Length == 0) throw new PortalRuleException("That name does not make a usable URL key. Give one explicitly.");

        var now = clock.GetUtcNow();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Key = projectKey,
            Name = trimmedName,
            EndDate = endDate,
            Status = ProjectStatus.Draft,
            CreatedAt = now,
        };

        db.Projects.Add(project);

        // The creator's own grant, written in the same transaction. Without it they would not
        // be able to read back the project they just made — row-level security applies to them
        // exactly as it does to everybody else, which is the property worth keeping.
        db.ProjectGrants.Add(new ProjectGrant
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            UserId = actingUser,
            Role = ProjectRole.Lead,
            CreatedAt = now,
        });

        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "project.created",
            ProjectId = project.Id,
            Detail = JsonSerializer.Serialize(new { key = projectKey, client = client.Slug }),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (IsDuplicateKey(e))
        {
            // Project keys are unique across every client, so the clashing project may well be
            // one this consultant cannot see — and no query here can check that first. Row-level
            // security is a database filter, invisible to EF, so IgnoreQueryFilters() would not
            // reveal it either; it only turns off filters EF itself added. The unique index is
            // the only thing that actually knows, so let it decide and translate what it says.
            //
            // This does tell the caller that some project holds the key. That is unavoidable —
            // the insert fails either way — and it says nothing about whose it is.
            throw new PortalRuleException($"The key '{projectKey}' is already taken.");
        }

        logger.LogInformation("Project {Key} created under {Client}.", projectKey, client.Slug);

        return project;
    }

    private static bool IsDuplicateKey(DbUpdateException e) =>
        e.InnerException is SqlException { Number: 2601 or 2627 };

    /// <summary>Everything the caller may see. For a client user that is their Databooks.</summary>
    public async Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.Client!.Name).ThenBy(p => p.Name)
            .Select(p => new ProjectListItem(
                p.Key,
                p.Name,
                p.Client!.Name,
                p.Client.Slug,
                p.Status.ToString(),
                p.EndDate,
                p.Versions.Any(v => v.State == ReportVersionState.Released)))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Null means "no such project, or not yours". The caller turns that into a 404 without
    /// knowing which, because the two are the same answer.
    /// </summary>
    public async Task<ProjectDetail?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        await db.Projects
            .AsNoTracking()
            .Where(p => p.Key == key)
            .Select(p => new ProjectDetail(
                p.Key,
                p.Name,
                p.Client!.Name,
                p.Client.Slug,
                p.Status.ToString(),
                p.EndDate,
                p.Versions.Any(v => v.State == ReportVersionState.Released),
                p.Versions.Count))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <param name="HasReleasedVersion">
/// False everywhere until Phase 5. A client sees the project but nothing to open, which is the
/// correct state rather than a placeholder.
/// </param>
public sealed record ProjectListItem(
    string Key,
    string Name,
    string ClientName,
    string ClientSlug,
    string Status,
    DateOnly EndDate,
    bool HasReleasedVersion);

public sealed record ProjectDetail(
    string Key,
    string Name,
    string ClientName,
    string ClientSlug,
    string Status,
    DateOnly EndDate,
    bool HasReleasedVersion,
    int VersionCount);
