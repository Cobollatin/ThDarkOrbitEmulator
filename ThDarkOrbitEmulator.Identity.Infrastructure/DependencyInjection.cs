using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.Identity.Infrastructure.Backoffice;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityDatabaseOptions>(configuration.GetSection(IdentityDatabaseOptions.SectionName));
        services.Configure<KeycloakAdminOptions>(configuration.GetSection(KeycloakAdminOptions.SectionName));
        services.Configure<PlatformOwnerSeedOptions>(configuration.GetSection(PlatformOwnerSeedOptions.SectionName));

        services.AddDbContext<IdentityWriteDbContext>((serviceProvider, optionsBuilder) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var connectionString = config.GetConnectionString("identity-write")
                ?? throw new InvalidOperationException("Missing connection string 'identity-write'.");

            optionsBuilder.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            });
        });

        services.AddDbContext<IdentityReadDbContext>((serviceProvider, optionsBuilder) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var connectionString = config.GetConnectionString("identity-read")
                ?? config.GetConnectionString("identity-write")
                ?? throw new InvalidOperationException("Missing connection string 'identity-read' or 'identity-write'.");

            optionsBuilder.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            });
        });

        services.AddHttpClient(KeycloakHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            .ConfigureAdditionalHttpMessageHandlers(static (handlers, _) =>
            {
                for (var i = handlers.Count - 1; i >= 0; i--)
                {
                    var name = handlers[i].GetType().Name;
                    if (name.Contains("ResolvingHttpDelegatingHandler", StringComparison.Ordinal)
                        || name.Contains("ResilienceHandler", StringComparison.Ordinal))
                    {
                        handlers.RemoveAt(i);
                    }
                }
            });

        services.AddScoped<IKeycloakService, KeycloakService>();
        services.AddScoped<IGameUserIdGenerator, GameUserIdGenerator>();
        services.AddScoped<IIdentityCommandService, IdentityCommandService>();
        services.AddScoped<IIdentityQueryService, IdentityQueryService>();
        services.AddScoped<IBackofficeIdentityService, BackofficeIdentityService>();
        services.AddScoped<IBackofficeKeycloakService, BackofficeKeycloakService>();
        services.AddHostedService<SqlMigrationHostedService>();
        services.AddHostedService<KeycloakBootstrapHostedService>();
        services.AddHostedService<PlatformOwnerSeedHostedService>();

        return services;
    }

    public static async Task ValidateIdentityDatabaseAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        using var scope = host.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityDatabase");
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var writeConnectionString = configuration.GetConnectionString("identity-write");
        if (string.IsNullOrWhiteSpace(writeConnectionString))
        {
            throw new InvalidOperationException("Missing connection string 'identity-write'.");
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityWriteDbContext>();
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException("Cannot connect to identity-write database.");
        }

        logger.LogInformation("Identity database connectivity verified.");
    }
}
