using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.GameAuth;

public interface IGameAuthSessionStore
{
    Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? keycloakAccessToken, CancellationToken cancellationToken);

    Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken);

    Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken);

    Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken);

    Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken);

    Task InvalidateAsync(string dosid, CancellationToken cancellationToken);
}
