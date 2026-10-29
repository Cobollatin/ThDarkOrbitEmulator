using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public interface IWorldPulseInventory
{
    Task<PulseChargeResult> ConsumeAsync(Guid accountId, Guid actionId, CancellationToken ct);
    Task<bool> StartAsync(Guid accountId, Guid actionId, CancellationToken ct);
    Task CompleteAsync(Guid accountId, Guid actionId, CancellationToken ct);
    Task<bool> RefundUnstartedAsync(Guid accountId, Guid actionId, CancellationToken ct);
}

public sealed class WorldPulseInventory(EconomyStore economy) : IWorldPulseInventory
{
    public Task<PulseChargeResult> ConsumeAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
        economy.ConsumeItemOnceAsync(accountId, "pulse_charge", actionId, ct);

    public Task<bool> StartAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
        economy.StartPulseActionAsync(accountId, actionId, ct);

    public Task CompleteAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
        economy.CompletePulseActionAsync(accountId, actionId, ct);

    public Task<bool> RefundUnstartedAsync(Guid accountId, Guid actionId, CancellationToken ct) =>
        economy.RefundUnstartedPulseActionAsync(accountId, actionId, ct);
}
