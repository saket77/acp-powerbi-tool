namespace Acp.Portal.Domain.Entities;

/// <summary>
/// Join row: this grant may see this company. Kept as its own entity rather than a string list
/// so that a company rename cannot silently widen or narrow somebody's access.
/// </summary>
public sealed class GrantCompany
{
    public Guid GrantId { get; set; }

    public ProjectGrant? Grant { get; set; }

    public Guid CompanyId { get; set; }

    public Company? Company { get; set; }
}
