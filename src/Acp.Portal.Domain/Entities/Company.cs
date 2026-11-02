namespace Acp.Portal.Domain.Entities;

/// <summary>
/// A legal entity inside a client group — "Muster Logistik GmbH" under "Muster group".
/// </summary>
public sealed class Company
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public Client? Client { get; set; }

    /// <summary>
    /// The FIN Tool <c>Ges.</c> code. This is what row-level security filters on inside the
    /// semantic model, and what the embed token carries. Codes rather than display names,
    /// deliberately: a delimiter or an umlaut in a company name must not be able to break
    /// the filter.
    /// </summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
}
