using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdentityDatabaseOptions>(configuration.GetSection(IdentityDatabaseOptions.SectionName));

        services.AddDbContext<IdentityWriteDbContext>((sp, options) =>
        {
            var connectionString = configuration.GetConnectionString("identity-write")
                ?? throw new InvalidOperationException("Missing connection string 'identity-write'.");
            options.UseNpgsql(connectionString);
        });

        services.AddDbContext<IdentityReadDbContext>((sp, options) =>
        {
            var connectionString = configuration.GetConnectionString("identity-read")
                ?? throw new InvalidOperationException("Missing connection string 'identity-read'.");
            options.UseNpgsql(connectionString);
        });

        services.AddHttpClient();
        services.AddScoped<IKeycloakService, KeycloakService>();
        services.AddScoped<IIdentityCommandService, IdentityCommandService>();
        services.AddScoped<IIdentityQueryService, IdentityQueryService>();
        services.AddHostedService<SqlMigrationHostedService>();

        return services;
    }
}
