using System.Text.Json;
using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acp.Portal.Infrastructure.PowerBi;

/// <summary>
/// Gives a client its own service principal profile and its own Power BI workspace.
/// </summary>
/// <remarks>
/// <para>
/// Three calls that each cost something permanent, so each one is saved as soon as it succeeds
/// and the whole thing is resumable. A profile belongs forever to the identity that created it:
/// if the run failed after creating one and the next run created another, the first would be
/// orphaned and unreachable — not a leak, but a thing nobody can clean up from the portal.
/// </para>
/// <para>
/// This is isolation wall 3. Everything the portal later does for this client is done as this
/// profile, which can reach this workspace and nothing else.
/// </para>
/// </remarks>
public sealed class ClientProvisioning(
    PortalDbContext db,
    IPowerBiClient powerBi,
    IOptions<PowerBiOptions> options,
    TimeProvider clock,
    ILogger<ClientProvisioning> logger)
{
    private readonly PowerBiOptions _options = options.Value;

    /// <summary>Null when there is no such client. Safe to call again after a failure.</summary>
    public async Task<Client?> ProvisionAsync(
        string slug, Guid actingUser, CancellationToken cancellationToken = default)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken);
        if (client is null) return null;

        if (client.IsProvisioned)
        {
            throw new PortalRuleException(
                $"{client.Name} already has a workspace. Provisioning twice would strand the first one.");
        }

        if (_options.Mode == PowerBiMode.Live && string.IsNullOrWhiteSpace(_options.CapacityId))
        {
            throw new PortalRuleException(
                "PowerBi:CapacityId is not set, and a workspace that is not on a capacity cannot "
                + "be embedded. Create the F2 capacity (scripts/tenant-setup.md step 6) and put "
                + "its object id in configuration.");
        }

        if (client.PowerBiProfileId is null)
        {
            var profile = await powerBi.CreateProfileAsync($"ACP {client.Name}", cancellationToken);
            client.PowerBiProfileId = profile.Id;

            // Saved before the next call, so a failure after this point leaves a profile the
            // portal still knows about rather than one nobody can find.
            await db.SaveChangesAsync(cancellationToken);
        }

        if (client.PowerBiWorkspaceId is null)
        {
            var workspace = await powerBi.CreateWorkspaceAsync(
                client.PowerBiProfileId, $"ACP {client.Name}", cancellationToken);

            client.PowerBiWorkspaceId = workspace.Id;
            await db.SaveChangesAsync(cancellationToken);
        }

        await powerBi.AssignToCapacityAsync(
            client.PowerBiProfileId, client.PowerBiWorkspaceId, _options.CapacityId, cancellationToken);

        db.AuditEvents.Add(new AuditEvent
        {
            At = clock.GetUtcNow(),
            UserId = actingUser,
            Action = "client.provisioned",
            Detail = JsonSerializer.Serialize(new
            {
                client.Slug,
                profileId = client.PowerBiProfileId,
                workspaceId = client.PowerBiWorkspaceId,
                simulated = _options.Mode == PowerBiMode.Fake,
            }),
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Provisioned {Client}: profile {Profile}, workspace {Workspace}.",
            client.Slug, client.PowerBiProfileId, client.PowerBiWorkspaceId);

        return client;
    }
}
