namespace Acp.Portal.Domain.Entities;

/// <summary>
/// One ACP client. Owns exactly one Power BI workspace and one service principal profile;
/// every project for this client puts its semantic model and report inside that workspace.
/// This is isolation wall 3 — a token minted as this client's profile cannot reach another
/// client's workspace at all.
/// </summary>
public sealed class Client
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>URL-safe key. Stable once created; it appears in links people bookmark.</summary>
    public required string Slug { get; set; }

    /// <summary>
    /// Null until the client is provisioned in Phase 4. Profiles belong permanently to the
    /// identity that created them, so this value is only meaningful for the environment that
    /// wrote it — see docs/implementation-plan.md §1.4.
    /// </summary>
    public string? PowerBiProfileId { get; set; }

    public string? PowerBiWorkspaceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Company> Companies { get; } = [];

    public List<Project> Projects { get; } = [];

    public bool IsProvisioned => PowerBiProfileId is not null && PowerBiWorkspaceId is not null;
}
