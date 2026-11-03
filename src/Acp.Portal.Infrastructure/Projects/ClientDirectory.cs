using System.Text.Json;
using System.Text.RegularExpressions;
using Acp.Portal.Domain.Entities;
using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Acp.Portal.Infrastructure.Projects;

/// <summary>
/// ACP's clients and the legal entities inside them.
/// </summary>
/// <remarks>
/// A client is the unit of isolation: one Power BI workspace and one service principal profile
/// per client, from Phase 4. The companies created here are the <c>Ges.</c> codes that end up
/// in an embed token, which is why they are rows rather than a string on a grant — a company
/// rename must not be able to widen or narrow anyone's access.
/// </remarks>
public sealed partial class ClientDirectory(
    PortalDbContext db,
    TimeProvider clock,
    ILogger<ClientDirectory> logger)
{
    public async Task<Client> CreateAsync(
        string name,
        string? slug,
        IReadOnlyList<NewCompany> companies,
        Guid actingUser,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new PortalRuleException("A client needs a name.");

        var key = Slugify(string.IsNullOrWhiteSpace(slug) ? trimmed : slug);
        if (key.Length == 0) throw new PortalRuleException("That name does not make a usable URL key. Give one explicitly.");

        if (await db.Clients.AnyAsync(c => c.Slug == key, cancellationToken))
        {
            throw new PortalRuleException($"The key '{key}' is already taken by another client.");
        }

        var codes = companies
            .Select(c => (Code: c.Code.Trim(), Name: c.Name.Trim()))
            .Where(c => c.Code.Length > 0)
            .ToList();

        if (codes.Count == 0)
        {
            // A client with no companies can never have a working client grant, because a grant
            // has to name at least one. Refusing now beats a confusing failure two screens later.
            throw new PortalRuleException("Add at least one company. Grants are made of company codes.");
        }

        var duplicate = codes.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new PortalRuleException($"The company code '{duplicate.Key}' appears twice.");
        }

        var now = clock.GetUtcNow();
        var client = new Client { Id = Guid.NewGuid(), Name = trimmed, Slug = key, CreatedAt = now };

        foreach (var (code, companyName) in codes)
        {
            client.Companies.Add(new Company
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                Code = code,
                Name = companyName.Length > 0 ? companyName : code,
            });
        }

        db.Clients.Add(client);
        db.AuditEvents.Add(new AuditEvent
        {
            At = now,
            UserId = actingUser,
            Action = "client.created",
            Detail = JsonSerializer.Serialize(new { client.Slug, companies = codes.Select(c => c.Code) }),
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Client {Slug} created with {Count} companies.", key, codes.Count);

        return client;
    }

    public async Task<IReadOnlyList<ClientSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Clients
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new ClientSummary(
                c.Slug,
                c.Name,
                c.Companies.Count,
                // Projects is row-level filtered, so this counts the ones the caller may see,
                // which is the number they would get if they clicked through.
                c.Projects.Count,
                c.PowerBiWorkspaceId != null))
            .ToListAsync(cancellationToken);

    public async Task<ClientDetail?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
        await db.Clients
            .AsNoTracking()
            .Where(c => c.Slug == slug)
            .Select(c => new ClientDetail(
                c.Slug,
                c.Name,
                c.Companies
                    .OrderBy(x => x.Code)
                    .Select(x => new CompanyView(x.Id, x.Code, x.Name))
                    .ToList(),
                c.Projects
                    .OrderBy(p => p.Name)
                    .Select(p => new ClientProject(p.Key, p.Name, p.Status.ToString(), p.EndDate))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Turns a name into something safe to put in a URL people bookmark. Umlauts become their
    /// two-letter forms rather than disappearing, because "Mueller" is findable and "Mller" is not.
    /// </summary>
    internal static string Slugify(string value)
    {
        var expanded = value.Trim().ToLowerInvariant()
            .Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ö", "oe", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);

        return NonSlugCharacters().Replace(expanded, "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}

public sealed record NewCompany(string Code, string Name);

public sealed record ClientSummary(string Slug, string Name, int CompanyCount, int ProjectCount, bool Provisioned);

public sealed record CompanyView(Guid Id, string Code, string Name);

public sealed record ClientProject(string Key, string Name, string Status, DateOnly EndDate);

public sealed record ClientDetail(
    string Slug,
    string Name,
    IReadOnlyList<CompanyView> Companies,
    IReadOnlyList<ClientProject> Projects);
