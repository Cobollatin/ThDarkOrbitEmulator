using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public interface IWorldPulseInventory
{
    Task<PulseChargeResult> ConsumeAsync(Guid accountId, Guid actionId, CancellationToken ct);
}

public sealed class WorldPulseInventory(EconomyStore economy) : IWorldPulseInventory
{
    public Task<PulseChargeResult> ConsumeAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
        economy.ConsumeItemOnceAsync(accountId, "pulse_charge", actionId, ct);
}
