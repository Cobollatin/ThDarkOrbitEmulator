namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public interface IGameUserIdGenerator
{
    Task<long> NextAsync(CancellationToken cancellationToken = default);
}
