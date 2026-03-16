namespace ThDarkOrbitEmulator.Identity.Application;

public interface IIdentityCommandService
{
    Task<bool> ExecuteAsync(RegisterAccountCommand command, CancellationToken cancellationToken);
}
