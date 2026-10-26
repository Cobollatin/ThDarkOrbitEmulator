namespace ThDarkOrbitEmulator.Identity.Application;

public interface IIdentityQueryService
{
    Task<bool> ExecuteAsync(ValidateCredentialsQuery query, CancellationToken cancellationToken);

    Task<AccountSummary?> ExecuteAsync(GetAccountByUsernameQuery query, CancellationToken cancellationToken);
}
