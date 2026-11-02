namespace Acp.Portal.Domain.Entities;

/// <summary>
/// One engagement for one client. Adds a semantic model and a report to the client's existing
/// workspace rather than a workspace of its own, which is what keeps the workspace count at
/// roughly one per client instead of one per project.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public Client? Client { get; set; }

    /// <summary>URL-safe key, unique across all clients.</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    /// <summary>Access ends on this date. A date, not an instant: end dates are agreed in contracts.</summary>
    public DateOnly EndDate { get; set; }

    public ProjectStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<ProjectGrant> Grants { get; } = [];

    public List<ReportVersion> Versions { get; } = [];
}
