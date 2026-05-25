using FastEndpoints;
using System.Security.Claims;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Auth;

public sealed class BackofficePermissionsEndpoint : EndpointWithoutRequest<BackofficePermissionsResponse>
{
    public override void Configure()
    {
        Get("backoffice/auth/permissions");
        Version(1);
        Description(d => d.WithTags("Backoffice"));
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var user = HttpContext.User;
        var permissions = user.FindAll(EmulatorClaimTypes.Permission).Select(c => c.Value).Distinct().ToList();
        var realmRoles = user.FindAll(EmulatorClaimTypes.RealmRole).Select(c => c.Value).Distinct().ToList();
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToList();

        return Send.OkAsync(new BackofficePermissionsResponse
        {
            Username = user.Identity?.Name ?? user.FindFirst("preferred_username")?.Value ?? "",
            Permissions = permissions,
            RealmRoles = realmRoles,
            Roles = roles
        }, ct);
    }
}

public sealed class BackofficePermissionsResponse
{
    public string Username { get; init; } = "";
    public List<string> Permissions { get; init; } = [];
    public List<string> RealmRoles { get; init; } = [];
    public List<string> Roles { get; init; } = [];
}
