namespace ThDarkOrbitEmulator.Identity.Application.Backoffice;

public interface IBackofficeKeycloakService
{
    Task<IReadOnlyList<BackofficeKeycloakUserRow>> ListUsersAsync(int limit, CancellationToken cancellationToken);
}
