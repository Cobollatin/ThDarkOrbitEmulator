using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace ThDarkOrbitEmulator.ServiceDefaults.Authorization;

/// <summary>
/// Maps Keycloak <c>realm_access</c> and <c>resource_access.thdarkorbit-api.roles</c> into permission and role claims.
/// </summary>
public sealed class KeycloakClaimsTransformation : IClaimsTransformation
{
    public const string ApiClientId = "thdarkorbit-api";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var claims = new List<Claim>();
        AddRealmRoles(principal, identity, claims);
        AddClientPermissions(principal, identity, claims);

        if (claims.Count > 0)
        {
            identity.AddClaims(claims);
        }

        return Task.FromResult(principal);
    }

    private static void AddRealmRoles(ClaimsPrincipal principal, ClaimsIdentity identity, List<Claim> claims)
    {
        foreach (var role in ExtractRealmRoles(principal))
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            if (!identity.HasClaim(EmulatorClaimTypes.RealmRole, role))
            {
                claims.Add(new Claim(EmulatorClaimTypes.RealmRole, role));
            }
        }
    }

    private static void AddClientPermissions(ClaimsPrincipal principal, ClaimsIdentity identity, List<Claim> claims)
    {
        foreach (var permission in ExtractClientRoles(principal, ApiClientId).Distinct(StringComparer.Ordinal))
        {
            if (!identity.HasClaim(EmulatorClaimTypes.Permission, permission))
            {
                claims.Add(new Claim(EmulatorClaimTypes.Permission, permission));
            }

            if (!identity.HasClaim(ClaimTypes.Role, permission))
            {
                claims.Add(new Claim(ClaimTypes.Role, permission));
            }
        }
    }

    public static IEnumerable<string> ExtractRealmRoles(ClaimsPrincipal principal)
    {
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return roles.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IEnumerable<string> ExtractClientRoles(ClaimsPrincipal principal, string clientId)
    {
        var resourceAccess = principal.FindFirst("resource_access")?.Value;
        if (string.IsNullOrWhiteSpace(resourceAccess))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(resourceAccess);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(clientId, out var client)
                || client.ValueKind != JsonValueKind.Object
                || !client.TryGetProperty("roles", out var roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return roles.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
