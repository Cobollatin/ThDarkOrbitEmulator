using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Authorization;

public class PermissionAuthorizationHandlerTests
{
    [Test]
    public async Task AdminRole_SatisfiesAnyPermission()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, EmulatorRoles.Admin)
        ],
        "Bearer"));

        var handler = new PermissionAuthorizationHandler();
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(EmulatorPermissions.IdentityDelete)],
            user,
            null);

        await handler.HandleAsync(context);
        Assert.That(context.HasSucceeded, Is.True);
    }

    [Test]
    public async Task PermissionClaim_SatisfiesMatchingRequirement()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(EmulatorClaimTypes.Permission, EmulatorPermissions.KeycloakRead)
        ],
        "Bearer"));

        var handler = new PermissionAuthorizationHandler();
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(EmulatorPermissions.KeycloakRead)],
            user,
            null);

        await handler.HandleAsync(context);
        Assert.That(context.HasSucceeded, Is.True);
    }

    [Test]
    public async Task MissingPermission_DoesNotSucceed()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));
        var handler = new PermissionAuthorizationHandler();
        var context = new AuthorizationHandlerContext(
            [new PermissionRequirement(EmulatorPermissions.CacheWrite)],
            user,
            null);

        await handler.HandleAsync(context);
        Assert.That(context.HasSucceeded, Is.False);
    }
}
