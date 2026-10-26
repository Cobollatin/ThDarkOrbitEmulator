namespace ThDarkOrbitEmulator.Identity.Application;

public interface IKeycloakService
{
    Task<string?> RequestAccessTokenAsync(string username, string password, CancellationToken cancellationToken);

    Task<bool> CreateUserAsync(
        string username,
        string password,
        string? email,
        CancellationToken cancellationToken,
        string? realmRole = null);

    Task<bool> EnsureRealmRoleAsync(string username, string realmRole, CancellationToken cancellationToken);
}
