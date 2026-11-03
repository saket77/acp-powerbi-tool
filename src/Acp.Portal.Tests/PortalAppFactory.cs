using Acp.Portal.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acp.Portal.Tests;

/// <summary>
/// Runs the real application with the database swapped for an in-memory one, so the tests that
/// care about wiring do not need a container.
/// </summary>
/// <remarks>
/// From Phase 3 the isolation tests need real SQL, because row-level security is a database
/// feature and proving it on a fake would prove nothing. Those will get their own fixture
/// against the container; this factory stays for the tests that only exercise plumbing.
/// </remarks>
internal sealed class PortalAppFactory(
    IDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null,
    string? connectionString = null)
    : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // With no connection string the database is swapped for an in-memory one below and this
        // value is never dialled. With one, the app runs against real SQL Server — which the
        // isolation suite needs, because row-level security is a database feature and a fake
        // would prove nothing.
        builder.UseSetting(
            "ConnectionStrings:Portal",
            connectionString ?? "Server=unused;Database=unused;");

        // Added last so it beats appsettings.Development.json *and* any config/entra.local.json
        // the developer running the tests happens to have filled in. A test must not change
        // behaviour because of a file that is not in the repository.
        var overrides = settings ?? DevelopmentDefaults();

        foreach (var (key, value) in overrides)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(overrides));

        builder.ConfigureServices(services =>
        {
            if (connectionString is not null)
            {
                // Leave the real registration alone, interceptor and all: stamping the
                // connection with who is asking is the thing under test.
                configureServices?.Invoke(services);
                return;
            }

            var toRemove = services.Where(d =>
                    d.ServiceType == typeof(PortalDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || d.ServiceType == typeof(DbContextOptions<PortalDbContext>)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericTypeDefinition().Name
                            .StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal)))
                .ToList();

            foreach (var descriptor in toRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<PortalDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            configureServices?.Invoke(services);
        });
    }

    /// <summary>Runs something against the application's own database, as the application sees it.</summary>
    public async Task WithDatabaseAsync(Func<PortalDbContext, Task> work)
    {
        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<PortalDbContext>());
    }

    /// <summary>
    /// A fixed roster, so that renaming an account in appsettings.Development.json breaks the
    /// development loop rather than the test suite.
    /// </summary>
    private static Dictionary<string, string?> DevelopmentDefaults() => new()
    {
        ["Auth:Mode"] = "Development",
        ["Auth:HomeTenantId"] = HomeTenant,
        ["Auth:StaffEmailDomains:0"] = "acp.test",
        ["Auth:InviteRedirectUrl"] = "http://localhost/",

        ["Auth:Development:Accounts:0:Key"] = StaffKey,
        ["Auth:Development:Accounts:0:ObjectId"] = "oid-staff",
        ["Auth:Development:Accounts:0:Email"] = "staff@acp.test",
        ["Auth:Development:Accounts:0:DisplayName"] = "A Consultant",
        ["Auth:Development:Accounts:0:Guest"] = "false",

        ["Auth:Development:Accounts:1:Key"] = ClientKey,
        ["Auth:Development:Accounts:1:ObjectId"] = "oid-client",
        ["Auth:Development:Accounts:1:Email"] = "anna@client.test",
        ["Auth:Development:Accounts:1:DisplayName"] = "Anna Keller",
        ["Auth:Development:Accounts:1:Guest"] = "true",

        // Right email domain, wrong directory. Must come out as a client user.
        ["Auth:Development:Accounts:2:Key"] = OutsiderKey,
        ["Auth:Development:Accounts:2:ObjectId"] = "oid-outsider",
        ["Auth:Development:Accounts:2:Email"] = "impostor@acp.test",
        ["Auth:Development:Accounts:2:DisplayName"] = "Another Directory",
        ["Auth:Development:Accounts:2:Guest"] = "false",
        ["Auth:Development:Accounts:2:TenantId"] = "99999999-9999-9999-9999-999999999999",
    };

    public const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    public const string StaffKey = "staff";
    public const string ClientKey = "client";
    public const string OutsiderKey = "outsider";
}
