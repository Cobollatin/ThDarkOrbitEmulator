using FastEndpoints;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Keycloak;

public sealed class ListKeycloakUsersEndpoint(IBackofficeKeycloakService backoffice) : EndpointWithoutRequest<ListKeycloakUsersResponse>
{
    public override void Configure()
    {
        Get("backoffice/keycloak/users");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.KeycloakRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var users = await backoffice.ListUsersAsync(limit: 100, ct);
        await Send.OkAsync(new ListKeycloakUsersResponse
        {
            Users = users.Select(u => new KeycloakUserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Enabled = u.Enabled
            }).ToList()
        }, ct);
    }
}

public sealed class ListKeycloakUsersResponse
{
    public List<KeycloakUserDto> Users { get; init; } = [];
}

public sealed class KeycloakUserDto
{
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    public string Email { get; init; } = "";
    public bool Enabled { get; init; }
}
