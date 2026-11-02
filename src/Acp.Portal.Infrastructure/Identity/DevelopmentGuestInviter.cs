using System.Security.Cryptography;
using System.Text;
using Acp.Portal.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.Identity;

/// <summary>
/// Stands in for Graph when there is no tenant to invite anyone into.
/// </summary>
/// <remarks>
/// It does the one thing that matters for the rest of the system: it returns a stable object id
/// for an email address. That id is what the development sign-in picker uses, so a consultant
/// can invite a fictional client in the UI and then sign in as them a moment later, exercising
/// the whole invite → first sign-in → classify path with no identity provider in sight.
/// </remarks>
public sealed class DevelopmentGuestInviter(ILogger<DevelopmentGuestInviter> logger) : IGuestInviter
{
    public Task<GuestInvitationResult> InviteAsync(
        GuestInvitation invitation,
        CancellationToken cancellationToken = default)
    {
        var objectId = StableObjectId(invitation.Email);

        logger.LogWarning(
            "Development sign-in is on, so no invitation was sent to {Email}. "
            + "Sign in as them from the account picker instead.",
            invitation.Email);

        return Task.FromResult(new GuestInvitationResult(
            objectId,
            $"{invitation.RedirectUrl.TrimEnd('/')}/?devInvite={Uri.EscapeDataString(invitation.Email)}"));
    }

    /// <summary>
    /// Derived from the address so that inviting the same person twice yields the same id, the
    /// way a directory would. Hashing rather than storing keeps this stateless.
    /// </summary>
    internal static string StableObjectId(string email)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(EntraClaims.NormalizeEmail(email)));
        return "dev-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
