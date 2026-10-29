using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public enum WorldRepairCharge { Applied, AlreadyCharged, InsufficientFunds }
public sealed record WorldRepairPayment(WorldRepairCharge Charge, long Balance);

public interface IWorldRepairWallet
{
    Task RecordDeathAsync(Guid accountId, string deathId, CancellationToken ct);
    Task<string?> FindDeathAsync(Guid accountId, CancellationToken ct);
    Task<WorldRepairPayment> ChargeAsync(Guid accountId, WorldRespawnLocation location,
        string deathId, CancellationToken ct);
    Task<WorldRepairPayment?> FindChargeAsync(Guid accountId, WorldRespawnLocation location,
        string deathId, CancellationToken ct);
}

public sealed class WorldRepairWallet(EconomyStore economy) : IWorldRepairWallet
{
    public Task RecordDeathAsync(Guid accountId, string deathId, CancellationToken ct) =>
        economy.RecordWorldDeathAsync(accountId, deathId, ct);

    public Task<string?> FindDeathAsync(Guid accountId, CancellationToken ct) =>
        economy.FindWorldDeathAsync(accountId, ct);
    public static int Price(WorldRespawnLocation location) => location switch
    {
        WorldRespawnLocation.DeathLocation => 300,
        WorldRespawnLocation.NearestPortal => 200,
        _ => 0
    };

    public Task<WorldRepairPayment> ChargeAsync(Guid accountId, WorldRespawnLocation location,
        string deathId, CancellationToken ct) => economy.ChargeRepairAsync(accountId,
            Price(location), $"world-repair:{deathId}", ct);

    public Task<WorldRepairPayment?> FindChargeAsync(Guid accountId, WorldRespawnLocation location,
        string deathId, CancellationToken ct) => economy.FindRepairChargeAsync(accountId,
            Price(location), $"world-repair:{deathId}", ct);
}
