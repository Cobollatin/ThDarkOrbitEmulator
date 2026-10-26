namespace ThDarkOrbitEmulator.Identity.Application;

public interface IIdentityCommandService
{
    Task<bool> ExecuteAsync(RegisterAccountCommand command, CancellationToken cancellationToken);

    Task<bool> SetCompanyAsync(Guid accountId, string company, CancellationToken cancellationToken);
}
