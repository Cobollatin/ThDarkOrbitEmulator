using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public interface IWorldPulseInventory
{
    Task<bool> ConsumeAsync(Guid accountId, CancellationToken ct);
}

public sealed class WorldPulseInventory(EconomyStore economy) : IWorldPulseInventory
{
    public Task<bool> ConsumeAsync(Guid accountId, CancellationToken ct) =>
        economy.ConsumeItemAsync(accountId, "pulse_charge", ct);
}
