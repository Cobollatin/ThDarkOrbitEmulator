using System.Security.Claims;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Authorization;

public class KeycloakClaimsTransformationTests
{
    [Test]
    public void Transform_AddsRealmRolesAndClientPermissions()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("realm_access", """{"roles":["emulator-viewer"]}"""));
        identity.AddClaim(new Claim(
            "resource_access",
            """{"thdarkorbit-api":{"roles":["backoffice.identity.read","backoffice.ops.read"]}}"""));

        var principal = new ClaimsPrincipal(identity);
        var transformation = new KeycloakClaimsTransformation();

        var result = transformation.TransformAsync(principal).GetAwaiter().GetResult();
        var transformed = (ClaimsIdentity)result.Identity!;

        Assert.That(transformed.HasClaim(ClaimTypes.Role, "emulator-viewer"), Is.True);
        Assert.That(
            transformed.HasClaim(EmulatorClaimTypes.Permission, EmulatorPermissions.IdentityRead),
            Is.True);
        Assert.That(
            transformed.HasClaim(EmulatorClaimTypes.Permission, EmulatorPermissions.OpsRead),
            Is.True);
    }

    [Test]
    public void ExtractRealmRoles_ReturnsEmpty_WhenClaimMissing()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var roles = KeycloakClaimsTransformation.ExtractRealmRoles(principal);
        Assert.That(roles, Is.Empty);
    }
}
