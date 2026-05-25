using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace ThDarkOrbitEmulator.ServiceDefaults.Authorization;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.IsInRole(EmulatorRoles.Admin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.HasClaim(EmulatorClaimTypes.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.IsInRole(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class EmulatorClaimTypes
{
    public const string Permission = "permission";
    public const string RealmRole = "realm_role";
}
