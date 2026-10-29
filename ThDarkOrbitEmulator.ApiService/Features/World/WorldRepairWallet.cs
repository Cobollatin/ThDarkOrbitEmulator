using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public enum WorldRepairCharge { Applied, AlreadyCharged, InsufficientFunds }
public sealed record WorldRepairPayment(WorldRepairCharge Charge, long Balance);
public sealed record WorldRepairOutcome(int MapId, int X, int Y, long HitPoints, long Shield);

public interface IWorldRepairWallet
{
    Task RecordDeathAsync(Guid accountId, string deathId, CancellationToken ct);
    Task<string?> FindDeathAsync(Guid accountId, CancellationToken ct);
    Task<string?> FindPendingDeathAsync(Guid accountId, CancellationToken ct);
    Task<WorldRepairOutcome?> FindPendingRepairAsync(Guid accountId, CancellationToken ct);
    Task StageRepairAsync(Guid accountId, string deathId, WorldRepairOutcome outcome, CancellationToken ct);
    Task CompleteDeathAsync(Guid accountId, string deathId, CancellationToken ct);
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
    public Task<string?> FindPendingDeathAsync(Guid accountId, CancellationToken ct) =>
        economy.FindPendingWorldDeathAsync(accountId, ct);
    public async Task<WorldRepairOutcome?> FindPendingRepairAsync(Guid accountId, CancellationToken ct)
    {
        var json = await economy.FindPendingWorldRepairAsync(accountId, ct);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<WorldRepairOutcome>(json);
    }
    public Task StageRepairAsync(Guid accountId, string deathId, WorldRepairOutcome outcome, CancellationToken ct) =>
        economy.StageWorldRepairAsync(accountId, deathId, System.Text.Json.JsonSerializer.Serialize(outcome), ct);
    public Task CompleteDeathAsync(Guid accountId, string deathId, CancellationToken ct) =>
        economy.CompleteWorldDeathAsync(accountId, deathId, ct);
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
