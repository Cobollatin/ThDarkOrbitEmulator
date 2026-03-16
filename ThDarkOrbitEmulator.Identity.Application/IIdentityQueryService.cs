namespace ThDarkOrbitEmulator.Identity.Application;

public interface IIdentityQueryService
{
    Task<bool> ExecuteAsync(ValidateCredentialsQuery query, CancellationToken cancellationToken);
}
