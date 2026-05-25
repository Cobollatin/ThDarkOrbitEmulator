namespace ThDarkOrbitEmulator.Identity.Application.Backoffice;

public interface IBackofficeIdentityService
{
    Task<IReadOnlyList<BackofficeAccountRow>> ListAccountsAsync(int limit, CancellationToken cancellationToken);

    Task<long> CountAccountsAsync(CancellationToken cancellationToken);

    Task<int> DeleteAllAccountsAsync(CancellationToken cancellationToken);
}
