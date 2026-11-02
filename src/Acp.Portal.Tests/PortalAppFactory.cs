using Acp.Portal.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
internal sealed class PortalAppFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // AddPortalInfrastructure refuses to start without one; the value is never dialled.
        builder.UseSetting("ConnectionStrings:Portal", "Server=unused;Database=unused;");

        builder.ConfigureServices(services =>
        {
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
        });
    }
}
