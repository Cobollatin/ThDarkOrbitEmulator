namespace ThDarkOrbitEmulator.Identity.Application;

public interface IKeycloakService
{
    Task<string?> RequestAccessTokenAsync(string username, string password, CancellationToken cancellationToken);
}
