using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Acp.Portal.Domain.Abstractions;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.Identity;

/// <summary>
/// Invites a guest by posting one request to Microsoft Graph.
/// </summary>
/// <remarks>
/// Deliberately not the Graph SDK. This is the only Graph call the portal makes, the request
/// and response are six fields, and the SDK would add a very large dependency and its own
/// authentication stack to save about twenty lines. If the portal ever needs Graph properly,
/// this is the moment to reconsider — but one call is not that moment.
/// </remarks>
public sealed class GraphGuestInviter(
    HttpClient http,
    TokenCredential credential,
    ILogger<GraphGuestInviter> logger) : IGuestInviter
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    public async Task<GuestInvitationResult> InviteAsync(
        GuestInvitation invitation,
        CancellationToken cancellationToken = default)
    {
        // An application token, not the signed-in consultant's. Inviting is something the
        // portal does on ACP's behalf after checking the consultant may ask for it; it is not
        // something we want every consultant to hold a directory permission for.
        var token = await credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "invitations")
        {
            Content = JsonContent.Create(new InvitationRequest(
                invitation.Email,
                invitation.DisplayName,
                invitation.RedirectUrl,
                SendInvitationMessage: true)),
        };
        request.Headers.Authorization = new("Bearer", token.Token);

        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Graph refused the invitation for {Email}: {Status} {Body}",
                invitation.Email, (int)response.StatusCode, body);

            throw new InvalidOperationException(Explain(response.StatusCode, body));
        }

        var result = await response.Content.ReadFromJsonAsync<InvitationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Graph accepted the invitation but returned no body.");

        // Recording the object id now is what lets this person be matched by id rather than by
        // address when they eventually sign in, possibly months later and possibly after
        // changing their display name.
        var objectId = result.InvitedUser?.Id ?? PendingObjectId.Create();

        logger.LogInformation("Invited {Email} as a guest. Directory object id {ObjectId}.",
            invitation.Email, objectId);

        return new GuestInvitationResult(objectId, result.InviteRedeemUrl ?? "");
    }

    /// <summary>
    /// The two failures that actually happen, named with their fix. Both are tenant setup
    /// rather than code, and both otherwise present as an opaque 403.
    /// </summary>
    private static string Explain(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Forbidden =>
            "Microsoft Graph refused the invitation. The app registration most likely lacks the "
            + "User.Invite.All application permission, or nobody has granted admin consent for it. "
            + "See scripts/tenant-setup.md step 4. Graph said: " + body,

        HttpStatusCode.Unauthorized =>
            "Microsoft Graph rejected the portal's credentials. Check Entra:TenantId, Entra:ClientId "
            + "and the client secret in config/entra.local.json or user-secrets. Graph said: " + body,

        _ => $"Microsoft Graph returned {(int)status}. Graph said: {body}",
    };

    private sealed record InvitationRequest(
        [property: JsonPropertyName("invitedUserEmailAddress")] string Email,
        [property: JsonPropertyName("invitedUserDisplayName")] string DisplayName,
        [property: JsonPropertyName("inviteRedirectUrl")] string RedirectUrl,
        [property: JsonPropertyName("sendInvitationMessage")] bool SendInvitationMessage);

    private sealed record InvitationResponse(
        [property: JsonPropertyName("inviteRedeemUrl")] string? InviteRedeemUrl,
        [property: JsonPropertyName("invitedUser")] InvitedUser? InvitedUser);

    private sealed record InvitedUser([property: JsonPropertyName("id")] string? Id);
}
