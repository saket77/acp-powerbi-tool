using System.ComponentModel.DataAnnotations;

namespace Acp.Portal.Infrastructure.PowerBi;

public enum PowerBiMode
{
    /// <summary>
    /// No Power BI at all. Provisioning and tokens are simulated so the portal runs end to end
    /// with no Fabric capacity and nothing billing. Refuses to start outside Development.
    /// </summary>
    Fake = 0,

    /// <summary>The real service. What every deployed environment uses.</summary>
    Live = 1,
}

/// <summary>
/// How the viewer's company codes reach the model's row-level security.
/// </summary>
/// <remarks>
/// Open question A3: the wireframe puts the codes in the effective identity's username, which
/// Power BI caps at 256 ASCII characters with no spaces. Whether that holds for ACP's widest
/// client is a measurement nobody has taken yet, and it cannot be taken without a capacity.
/// <see cref="EffectiveIdentityMode.CustomData"/> is the documented fallback, already wired, so
/// that answering the question later is a configuration change and a template change rather
/// than a rewrite.
/// </remarks>
public enum EffectiveIdentityMode
{
    Username = 0,
    CustomData = 1,
}

/// <summary>
/// Who the portal is when it reads a model over XMLA.
/// </summary>
/// <remarks>
/// <b>An open question, not a preference.</b> Service principals over XMLA are documented;
/// service principal <i>profiles</i> over XMLA are not documented anywhere I could find. If
/// <see cref="Profile"/> turns out not to work, <see cref="Application"/> is the fallback: the
/// portal's own identity created every one of these workspaces, so it is the parent rather than
/// a stranger, and the embed tokens clients actually use are still minted as the profile. The
/// isolation wall that matters is untouched either way — but this has to be settled at Gate 1.
/// </remarks>
public enum XmlaIdentity
{
    /// <summary>The documented path, and the default because of it.</summary>
    Application = 0,

    /// <summary>Undocumented. Try it at Gate 1; if it connects, prefer it.</summary>
    Profile = 1,
}

public sealed class PowerBiOptions
{
    public const string SectionName = "PowerBi";

    public PowerBiMode Mode { get; set; } = PowerBiMode.Fake;

    [Required]
    public string ApiBaseUrl { get; set; } = "https://api.powerbi.com/v1.0/myorg/";

    /// <summary>
    /// The Fabric capacity every client workspace is assigned to. Embedding does not work on a
    /// workspace that is not on a capacity — this is the one resource that costs real money, and
    /// the reason Phase 4 is the first phase you cannot run for free.
    /// </summary>
    public string CapacityId { get; set; } = "";

    [Range(1, 60)]
    public int EmbedTokenLifetimeMinutes { get; set; } = 30;

    /// <summary>Role name in the Databook template that filters by company code.</summary>
    [Required]
    public string ClientRole { get; set; } = "Client";

    /// <summary>Role name in the Databook template that sees everything. ACP preview only.</summary>
    [Required]
    public string AcpRole { get; set; } = "ACP";

    public EffectiveIdentityMode IdentityMode { get; set; } = EffectiveIdentityMode.Username;

    /// <summary>Separator between company codes. Must not be a space; Power BI rejects those.</summary>
    [Required]
    public string CodeSeparator { get; set; } = "|";

    /// <summary>How long to wait for an import before giving up.</summary>
    [Range(10, 900)]
    public int ImportTimeoutSeconds { get; set; } = 300;

    // --- The Databook template contract -------------------------------------------------------
    //
    // All configurable because none of it has been checked against Sejal's real template yet —
    // that is dependency D6, still outstanding. The names below are what the wireframe says;
    // when the real file arrives, expect to change values here rather than code.

    /// <summary>Oldest template the portal will accept. The wireframe says 1.6.</summary>
    public decimal MinimumTemplateVersion { get; set; } = 1.6m;

    [Required]
    public string TemplateVersionMeasure { get; set; } = "Template Version";

    [Required]
    public string CompaniesTable { get; set; } = "Companies";

    /// <summary>The FIN Tool code column. Row-level security inside the model filters on this.</summary>
    [Required]
    public string CompanyCodeColumn { get; set; } = "Ges.";

    /// <summary>
    /// Tables whose name starts with one of these are expected to be invisible to clients, so
    /// the "is it filtered" check leaves them alone.
    /// </summary>
    /// <remarks>
    /// A convention rather than a fact, and the most speculative thing in this file: the real
    /// template may mark ACP-only tables some other way entirely. Confirm against D6.
    /// </remarks>
    public string[] AcpOnlyTablePrefixes { get; set; } = ["ACP"];

    public string XmlaBaseUrl { get; set; } = "powerbi://api.powerbi.com/v1.0/myorg";

    public XmlaIdentity XmlaConnectAs { get; set; } = XmlaIdentity.Application;

    public bool IsConfigured => Mode == PowerBiMode.Fake || !string.IsNullOrWhiteSpace(CapacityId);
}
