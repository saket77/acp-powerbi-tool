using Acp.Portal.Domain.Abstractions;
using Acp.Portal.Infrastructure.Data;
using Acp.Portal.Infrastructure.PowerBi;
using Acp.Portal.Infrastructure.Projects;
using Acp.Portal.Infrastructure.Staging;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Acp.Portal.Infrastructure.Identity;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Fail closed. Anything that has not registered a real accessor — the design-time
        // tooling, a stray root-scope resolve — is nobody, and row-level security shows nobody
        // nothing. The API registers the HTTP-backed one before calling this, so TryAdd leaves
        // it alone.
        services.TryAddScoped<ICurrentUserAccessor, NobodyCurrentUserAccessor>();
        services.AddScoped<SessionContextInterceptor>();

        services.AddDbContext<PortalDbContext>((serviceProvider, options) =>
            options
                .UseSqlServer(connectionString, sql =>
                {
                    sql.MigrationsAssembly(typeof(PortalDbContext).Assembly.FullName);

                    // Local SQL Server runs under emulation on Apple Silicon and is slow to wake.
                    // Retry rather than fail the first request after `docker compose up`.
                    sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), null);
                })
                // Stamps every connection with who is asking. Without it the row-level security
                // predicates see no user and the database correctly shows nothing at all.
                .AddInterceptors(serviceProvider.GetRequiredService<SessionContextInterceptor>()));

        services.TryAddSingletonTimeProvider();

        services.AddScoped<ClientDirectory>();
        services.AddScoped<ProjectDirectory>();
        services.AddScoped<GrantService>();

        return services;
    }

    /// <summary>The caller when there is no caller. Sees nothing, which is the safe direction.</summary>
    private sealed class NobodyCurrentUserAccessor : ICurrentUserAccessor
    {
        public PortalCaller Current => PortalCaller.Anonymous;
    }

    /// <summary>
    /// Everything about who someone is. Call it after <see cref="AddPortalInfrastructure"/>.
    /// </summary>
    /// <param name="isDevelopmentEnvironment">
    /// Passed in rather than read from configuration, because it decides whether the
    /// development sign-in is allowed to exist and that decision must not be reachable by
    /// anything inside a configuration file.
    /// </param>
    public static IServiceCollection AddPortalIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopmentEnvironment)
    {
        services.AddOptions<PortalAuthOptions>()
            .Bind(configuration.GetSection(PortalAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<EntraOptions>()
            .Bind(configuration.GetSection(EntraOptions.SectionName))
            .ValidateOnStart();

        var auth = configuration.GetSection(PortalAuthOptions.SectionName).Get<PortalAuthOptions>() ?? new();

        if (auth.Mode == AuthMode.Development && !isDevelopmentEnvironment)
        {
            // The one failure this project must never ship. Refusing to start is the only
            // response that cannot be missed in a log.
            throw new InvalidOperationException(
                "Auth:Mode is Development but ASPNETCORE_ENVIRONMENT is not Development. "
                + "The development sign-in accepts anyone named in configuration and must never "
                + "run outside a developer's machine. Set Auth:Mode to Entra.");
        }

        services.AddScoped<IIdentityResolver, IdentityResolver>();
        services.AddScoped<ClientInvitations>();

        if (auth.Mode == AuthMode.Entra)
        {
            services.TryAddSingleton(_ => CreateCredential(configuration));
            services.AddHttpClient<IGuestInviter, GraphGuestInviter>(http =>
            {
                http.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
                http.Timeout = TimeSpan.FromSeconds(30);
            });
        }
        else
        {
            services.AddScoped<IGuestInviter, DevelopmentGuestInviter>();
        }

        return services;
    }

    /// <summary>
    /// Everything to do with Power BI. Call it after <see cref="AddPortalInfrastructure"/>.
    /// </summary>
    /// <param name="isDevelopmentEnvironment">
    /// Passed in rather than read from configuration, for the same reason as the development
    /// sign-in: whether Power BI may be simulated must not be reachable from a config file.
    /// </param>
    public static IServiceCollection AddPortalPowerBi(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopmentEnvironment)
    {
        services.AddOptions<PowerBiOptions>()
            .Bind(configuration.GetSection(PowerBiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var powerBi = configuration.GetSection(PowerBiOptions.SectionName).Get<PowerBiOptions>() ?? new();

        if (powerBi.Mode == PowerBiMode.Fake && !isDevelopmentEnvironment)
        {
            throw new InvalidOperationException(
                "PowerBi:Mode is Fake but ASPNETCORE_ENVIRONMENT is not Development. A simulated "
                + "Power BI issues tokens nothing honours, so a deployed environment running this "
                + "way would show every client an empty report and report success. "
                + "Set PowerBi:Mode to Live.");
        }

        if (powerBi.Mode == PowerBiMode.Live)
        {
            services.TryAddSingleton(_ => CreateCredential(configuration));
            services.AddScoped<IModelInspector, XmlaModelInspector>();

            services.AddHttpClient<IPowerBiClient, PowerBiRestClient>(http =>
            {
                http.BaseAddress = new Uri(powerBi.ApiBaseUrl);
                // Importing a .pbix is a large upload followed by a poll; the poll has its own
                // deadline, so this only has to be generous enough for the upload itself.
                http.Timeout = TimeSpan.FromMinutes(5);
            });
        }
        else
        {
            services.AddScoped<IPowerBiClient, FakePowerBiClient>();
            services.AddScoped<IModelInspector, FakeModelInspector>();
        }

        services.AddScoped<ClientProvisioning>();
        services.AddScoped<ReportVersions>();
        services.AddScoped<EmbedTokens>();

        return services;
    }

    /// <summary>
    /// Blob staging and the import queue. Call it after <see cref="AddPortalInfrastructure"/>.
    /// </summary>
    /// <remarks>
    /// A connection string means Azurite and nothing else: the deployed storage account sets
    /// <c>allowSharedKeyAccess: false</c>, so there is no key there to put in one. In Azure the
    /// account is addressed by URL and reached with the portal's managed identity.
    /// </remarks>
    public static IServiceCollection AddPortalStaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new();

        if (storage.UsesSharedKey)
        {
            services.AddSingleton(_ => new BlobServiceClient(storage.ConnectionString));
            services.AddSingleton(_ => new QueueServiceClient(storage.ConnectionString));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(storage.BlobAccountUrl) || string.IsNullOrWhiteSpace(storage.QueueAccountUrl))
            {
                throw new InvalidOperationException(
                    "Storage:BlobAccountUrl and Storage:QueueAccountUrl must be set when there is no "
                    + "connection string. Locally, set Storage:ConnectionString to "
                    + "'UseDevelopmentStorage=true' and run scripts/dev-up.sh.");
            }

            services.TryAddSingleton(_ => CreateCredential(configuration));
            services.AddSingleton(sp => new BlobServiceClient(
                new Uri(storage.BlobAccountUrl), sp.GetRequiredService<TokenCredential>()));
            services.AddSingleton(sp => new QueueServiceClient(
                new Uri(storage.QueueAccountUrl), sp.GetRequiredService<TokenCredential>()));
        }

        services.AddScoped<PbixStaging>();
        services.AddScoped<ImportQueue>();
        services.AddScoped<UploadPipeline>();

        return services;
    }

    /// <summary>
    /// A client secret locally, the platform's own identity in Azure. Choosing on the presence
    /// of a secret rather than on an explicit setting means a deployed environment cannot
    /// accidentally fall back to one: there is nothing there to fall back to.
    /// </summary>
    private static TokenCredential CreateCredential(IConfiguration configuration)
    {
        var entra = configuration.GetSection(EntraOptions.SectionName).Get<EntraOptions>() ?? new();

        if (!entra.IsConfigured)
        {
            throw new InvalidOperationException(
                "Auth:Mode is Entra but Entra:TenantId / Entra:ClientId are empty. "
                + "Work through scripts/tenant-setup.md, then fill in config/entra.local.json.");
        }

        return string.IsNullOrWhiteSpace(entra.ClientSecret)
            ? new DefaultAzureCredential()
            : new ClientSecretCredential(entra.TenantId, entra.ClientId, entra.ClientSecret);
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TimeProvider))) return;
        services.AddSingleton(TimeProvider.System);
    }
}
