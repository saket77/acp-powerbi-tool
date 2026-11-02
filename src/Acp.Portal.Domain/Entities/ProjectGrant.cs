namespace Acp.Portal.Domain.Entities;

/// <summary>
/// One person's access to one project. This is isolation wall 1: every request resolves the
/// project through the caller's grant, and a missing grant is indistinguishable from a missing
/// project.
/// </summary>
public sealed class ProjectGrant
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public ProjectRole Role { get; set; }

    /// <summary>Null means "until the project ends".</summary>
    public DateOnly? EndDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Which companies this person may see. Only meaningful for <see cref="ProjectRole.Client"/>;
    /// ACP roles see every company in the project. These codes go into the embed token and are
    /// never read from a request.
    /// </summary>
    public List<GrantCompany> Companies { get; } = [];
}
