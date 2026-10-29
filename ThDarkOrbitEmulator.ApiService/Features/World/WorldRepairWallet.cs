using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public enum WorldRepairCharge { Applied, AlreadyCharged, InsufficientFunds }
public sealed record WorldRepairPayment(WorldRepairCharge Charge, long Balance);

public interface IWorldRepairWallet
{
    Task<WorldRepairPayment> ChargeAsync(Guid accountId, WorldRespawnLocation location,
        string deathId, CancellationToken ct);
}

public sealed class WorldRepairWallet(EconomyStore economy) : IWorldRepairWallet
{
    public static int Price(WorldRespawnLocation location) => location switch
    {
        WorldRespawnLocation.DeathLocation => 300,
        WorldRespawnLocation.NearestPortal => 200,
        _ => 0
    };

    public Task<WorldRepairPayment> ChargeAsync(Guid accountId, WorldRespawnLocation location,
        string deathId, CancellationToken ct) => economy.ChargeRepairAsync(accountId,
            Price(location), $"world-repair:{deathId}", ct);
}
