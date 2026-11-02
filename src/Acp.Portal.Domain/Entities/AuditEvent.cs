namespace Acp.Portal.Domain.Entities;

/// <summary>
/// Append-only record of anything that changed access or published data. Survives project
/// clean-up: the model and report are deleted after retention, the audit log is kept.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Null for actions taken by a timer rather than a person.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Stable verb, e.g. <c>grant.added</c>, <c>version.released</c>, <c>token.issued</c>.</summary>
    public required string Action { get; set; }

    public Guid? ProjectId { get; set; }

    /// <summary>
    /// JSON. Must never contain financial values, secrets or embed tokens — the wireframe's
    /// logging rule, enforced by review rather than by the type system.
    /// </summary>
    public string? Detail { get; set; }
}
