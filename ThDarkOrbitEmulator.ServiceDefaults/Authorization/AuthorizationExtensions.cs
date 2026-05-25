using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ThDarkOrbitEmulator.ServiceDefaults.Authorization;

public static class AuthorizationExtensions
{
    public static IServiceCollection AddEmulatorAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IClaimsTransformation, KeycloakClaimsTransformation>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = null;

            foreach (var permission in EmulatorPermissions.All)
            {
                options.AddPolicy(
                    EmulatorPermissions.PolicyName(permission),
                    policy => policy.AddRequirements(new PermissionRequirement(permission)));
            }

            foreach (var role in EmulatorRoles.All)
            {
                options.AddPolicy(role, policy => policy.RequireRole(role));
            }
        });

        return services;
    }
}
