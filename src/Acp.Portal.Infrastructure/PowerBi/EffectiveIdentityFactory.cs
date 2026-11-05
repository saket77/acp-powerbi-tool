using Acp.Portal.Domain.Abstractions;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// Builds the identity that goes inside an embed token. The last place the portal decides what
/// somebody may see, and the first place Power BI starts enforcing it.
/// </summary>
/// <remarks>
/// It refuses rather than truncates. A username one character too long is not a cosmetic
/// problem: Power BI would silently filter the model to whatever codes survived the cut, and a
/// client would see a report that looked complete and was not. A loud failure on the consultant
/// who is setting the grant up is the only acceptable way for this to go wrong.
/// </remarks>
public static class EffectiveIdentityFactory
{
    /// <summary>
    /// Power BI's documented ceiling for a cloud model's effective identity username: 256 ASCII
    /// characters, no spaces.
    /// </summary>
    public const int MaxUsernameLength = 256;

    /// <summary>
    /// Conservative, and not from the documentation — the published limit for custom data was
    /// not confirmed. Gate 1 measures the real one; until then this errs small.
    /// </summary>
    public const int MaxCustomDataLength = 1024;

    public const int MaxCompanyCodeLength = 50;

    /// <summary>What a client contact sees: the Client role, filtered to their own company codes.</summary>
    /// <param name="subject">
    /// Used as the username only in <see cref="EffectiveIdentityMode.CustomData"/> mode, where the
    /// codes travel elsewhere but Power BI still wants somebody named.
    /// </param>
    public static PowerBiEffectiveIdentity ForClient(
        string subject,
        IReadOnlyList<string> companyCodes,
        PowerBiOptions options)
    {
        var codes = companyCodes
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (codes.Count == 0)
        {
            throw new PortalRuleException(
                "This grant names no companies, so there is nothing the client would be allowed "
                + "to see. Add at least one company to the grant.");
        }

        foreach (var code in codes) ValidateCompanyCode(code, options);

        var joined = string.Join(options.CodeSeparator, codes);

        if (options.IdentityMode == EffectiveIdentityMode.CustomData)
        {
            if (joined.Length > MaxCustomDataLength)
            {
                throw new PortalRuleException(TooManyCodes(codes.Count, joined.Length, MaxCustomDataLength));
            }

            return new PowerBiEffectiveIdentity(
                Sanitize(subject), [options.ClientRole], CustomData: joined);
        }

        if (joined.Length > MaxUsernameLength)
        {
            // Open question A3, answered in the worst possible way. The fallback is a config
            // change here plus a filter change in the Databook template.
            throw new PortalRuleException(
                TooManyCodes(codes.Count, joined.Length, MaxUsernameLength)
                + " Switching PowerBi:IdentityMode to CustomData moves the codes out of the "
                + "username, which also needs the template's filter to read CUSTOMDATA().");
        }

        return new PowerBiEffectiveIdentity(joined, [options.ClientRole]);
    }

    /// <summary>What a consultant sees when they are not previewing as somebody: everything.</summary>
    public static PowerBiEffectiveIdentity ForAcp(string email, PowerBiOptions options) =>
        new(Sanitize(email), [options.AcpRole]);

    /// <summary>
    /// Checked when a company is created, not only when a token is minted.
    /// </summary>
    /// <remarks>
    /// A code with a space in it would be accepted by every screen in the portal and then
    /// rejected by Power BI months later, in front of a client, on the one call that matters.
    /// Far better to refuse it while somebody is typing it.
    /// </remarks>
    public static void ValidateCompanyCode(string code, PowerBiOptions options)
    {
        if (code.Length is 0 or > MaxCompanyCodeLength)
        {
            throw new PortalRuleException(
                $"Company code '{code}' must be between 1 and {MaxCompanyCodeLength} characters.");
        }

        if (code.Any(char.IsWhiteSpace))
        {
            throw new PortalRuleException(
                $"Company code '{code}' contains a space. Power BI rejects spaces in the identity "
                + "that carries these codes, so they cannot be used here.");
        }

        if (code.Any(c => c > 127))
        {
            throw new PortalRuleException(
                $"Company code '{code}' has a non-ASCII character. Power BI accepts ASCII only in "
                + "the identity that carries these codes. Use the code, not the company name.");
        }

        if (code.Contains(options.CodeSeparator, StringComparison.Ordinal))
        {
            throw new PortalRuleException(
                $"Company code '{code}' contains '{options.CodeSeparator}', which separates codes "
                + "from one another. A code containing the separator would split into two.");
        }

        if (code.Contains(',', StringComparison.Ordinal))
        {
            // Commas separate roles in the same structure; keeping them out of codes avoids
            // having to reason about which field is being parsed where.
            throw new PortalRuleException($"Company code '{code}' must not contain a comma.");
        }
    }

    private static string TooManyCodes(int count, int length, int limit) =>
        $"This grant has {count} companies, which make {length} characters of identity — "
        + $"Power BI allows {limit}. Either narrow the grant, or shorten the codes.";

    /// <summary>Spaces are the only thing an email realistically has that Power BI will not take.</summary>
    private static string Sanitize(string value)
    {
        var trimmed = value.Trim().Replace(" ", "_", StringComparison.Ordinal);

        return trimmed.Length <= MaxUsernameLength ? trimmed : trimmed[..MaxUsernameLength];
    }
}
