using System.ComponentModel.DataAnnotations;

namespace Acp.Portal.Infrastructure.Identity;

public enum AuthMode
{
    /// <summary>
    /// Local sign-in against a roster in configuration. No identity provider, no network, no
    /// tenant. Refuses to start outside the Development environment.
    /// </summary>
    Development = 0,

    /// <summary>Real Microsoft sign-in. Everything a deployed environment ever uses.</summary>
    Entra = 1,
}

/// <summary>Bound from the <c>Auth</c> configuration section.</summary>
public sealed class PortalAuthOptions
{
    public const string SectionName = "Auth";

    public AuthMode Mode { get; set; } = AuthMode.Development;

    /// <summary>
    /// How long a session lasts without activity. Sliding. Short enough that an abandoned
    /// laptop is not a standing grant, long enough to survive a working day of reading reports.
    /// </summary>
    [Range(5, 60 * 24)]
    public int SessionLifetimeMinutes { get; set; } = 480;

    /// <summary>
    /// Email domains whose members are ACP staff. Everyone else — including anyone in the
    /// directory whose domain is not listed — is a client user.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than a deny-list, and matched against the token's verified email
    /// rather than anything a request carries. Leaving it empty means "every member of the home
    /// tenant is staff", which is the right default for a tenant that exists only for ACP, and
    /// the wrong one for a shared tenant — so a deployed environment should always set it.
    /// </remarks>
    public string[] StaffEmailDomains { get; set; } = [];

    /// <summary>
    /// The directory ACP's own people live in. A token from any other directory is a client
    /// user whatever its email says. Empty disables the check, which is only sane in development.
    /// </summary>
    public string? HomeTenantId { get; set; }

    /// <summary>Where an invited guest lands after accepting. Must be the portal's own origin.</summary>
    [Required]
    public string InviteRedirectUrl { get; set; } = "http://localhost:5173/";

    public DevelopmentAuthOptions Development { get; set; } = new();

    /// <summary>
    /// True when this address belongs to ACP by its domain alone — no directory lookup, no
    /// existing row.
    /// </summary>
    /// <remarks>
    /// Used both to classify someone signing in and to refuse inviting a colleague as a client
    /// contact. The second caller is why this cannot be a question about an existing user: a
    /// consultant who has never signed in has no row, and inviting their address as a guest
    /// would be wrong before they ever appeared.
    ///
    /// With no domains configured this is always false, which is the honest answer — "every
    /// member of the home tenant is staff" is a statement about the directory, not about
    /// addresses, and nothing here can check it.
    /// </remarks>
    public bool IsStaffEmailDomain(string email)
    {
        if (StaffEmailDomains.Length == 0) return false;

        var at = email.LastIndexOf('@');
        if (at < 0) return false;

        var domain = email[(at + 1)..];

        return StaffEmailDomains.Any(
            d => string.Equals(d.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DevelopmentAuthOptions
{
    /// <summary>The people you can sign in as when <see cref="AuthMode.Development"/> is on.</summary>
    public DevelopmentAccount[] Accounts { get; set; } = [];
}

/// <summary>
/// One entry in the development sign-in picker.
/// </summary>
/// <remarks>
/// Note what is *not* here: the user kind. These accounts are classified by exactly the same
/// code that classifies a real Entra token, so the development loop tests the classification
/// rather than bypassing it. Set <see cref="Guest"/> and <see cref="Email"/> to say who someone
/// is; the portal works out what that makes them.
/// </remarks>
public sealed class DevelopmentAccount
{
    /// <summary>Short stable key. What the sign-in request names.</summary>
    [Required]
    public string Key { get; set; } = "";

    /// <summary>Stands in for the Entra object id. Any stable string.</summary>
    [Required]
    public string ObjectId { get; set; } = "";

    [Required, EmailAddress]
    public string Email { get; set; } = "";

    [Required]
    public string DisplayName { get; set; } = "";

    /// <summary>True to stand in for a B2B guest — an external person with their own Microsoft account.</summary>
    public bool Guest { get; set; }

    /// <summary>Stands in for the <c>tid</c> claim. Defaults to the configured home tenant.</summary>
    public string? TenantId { get; set; }
}
