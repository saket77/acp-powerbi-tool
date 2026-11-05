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

    public bool IsConfigured => Mode == PowerBiMode.Fake || !string.IsNullOrWhiteSpace(CapacityId);
}
