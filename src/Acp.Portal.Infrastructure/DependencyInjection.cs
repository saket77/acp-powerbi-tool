using Acp.Portal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acp.Portal.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPortalInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Portal")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Portal is not configured. For local development run "
                + "`docker compose up -d` and use the value in appsettings.Development.json.");

        services.AddDbContext<PortalDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(PortalDbContext).Assembly.FullName);

                // Local SQL Server runs under emulation on Apple Silicon and is slow to wake.
                // Retry rather than fail the first request after `docker compose up`.
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), null);
            }));

        return services;
    }
}
