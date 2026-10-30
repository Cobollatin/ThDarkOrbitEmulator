using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// A gate map is shared by faction for routing, while its combat roster and target lock
// belong to the account. The durable gate run is the authority for each wave.
public sealed class WorldGateCombat(EconomyStore economy, IDistributedCache cache)
{
    private static string TargetKey(long userId) => $"world:gate:target:{userId}";

    public Task<GateCombatState?> StateAsync(WorldPilot pilot, CancellationToken ct) =>
        WorldMapInstance.GateId(pilot.MapId) is { } gate && pilot.AccountId != Guid.Empty
            ? economy.GateCombatAsync(pilot.AccountId, gate, ct)
            : Task.FromResult<GateCombatState?>(null);

    public async Task<IReadOnlyList<WorldNpc>> VisibleAsync(WorldPilot pilot, CancellationToken ct)
    {
        var gate = WorldMapInstance.GateId(pilot.MapId);
        if (gate is null || pilot.AccountId == Guid.Empty) return [];
        var state = await economy.GateCombatAsync(pilot.AccountId, gate, ct);
        if (state is null || state.CompletedAt is not null || state.Lives <= 0 || state.Wave >= state.TotalWaves)
            return [];
        var id = pilot.MapId * 10000 + state.Wave * 1000 + state.Kills + 1;
        var kind = state.Npc;
        return [new WorldNpc(id, kind, kind.ToLowerInvariant().Replace(' ', '_'),
            2500, 1300, state.NpcHp, 1000, 0, 0, 0, 0, 0, 0)];
    }

    public async Task<WorldNpc?> SelectAsync(WorldPilot pilot, int npcId, WorldSessionStore sessions,
        CancellationToken ct, int? expectedMapId = null)
    {
        var admittedMapId = expectedMapId ?? pilot.MapId;
        pilot = await sessions.RestoreAsync(pilot, ct);
        if (pilot.MapId != admittedMapId) throw new WorldMapChangedException();
        var npc = (await VisibleAsync(pilot, ct)).FirstOrDefault(item => item.Id == npcId);
        if (npc is null) return null;
        await cache.SetStringAsync(TargetKey(pilot.UserId), $"{pilot.AccountId:N}:{npcId}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) }, ct);
        return npc;
    }

    public async Task<WorldNpcAttack> AttackAsync(WorldPilot pilot, int npcId, WorldSessionStore sessions,
        CancellationToken ct, int? expectedMapId = null)
    {
        var admittedMapId = expectedMapId ?? pilot.MapId;
        pilot = await sessions.RestoreAsync(pilot, ct);
        if (pilot.MapId != admittedMapId) throw new WorldMapChangedException();
        if (await cache.GetStringAsync(TargetKey(pilot.UserId), ct) != $"{pilot.AccountId:N}:{npcId}")
            return new("not_selected");
        var npc = (await VisibleAsync(pilot, ct)).FirstOrDefault(item => item.Id == npcId);
        if (npc is null) return new("missing");
        var (x, y) = sessions.CurrentPosition(pilot);
        if ((long)(x - npc.X) * (x - npc.X) + (long)(y - npc.Y) * (y - npc.Y) > 1000L * 1000)
            return new("distant", npc);
        var gate = WorldMapInstance.GateId(pilot.MapId)!;
        var wave = (npcId - pilot.MapId * 10000 - 1) / 1000;
        var kills = (npcId - pilot.MapId * 10000 - 1) % 1000;
        var result = await economy.AttackGateNpcAsync(pilot.AccountId, gate, wave,
            DateTimeOffset.UtcNow, $"world:{pilot.MapId}:{npcId}", ct, kills);
        if (result != ProgressionChange.Applied) return new("conflict");
        await cache.RemoveAsync(TargetKey(pilot.UserId), ct);
        await sessions.BroadcastNpcDamageAsync(pilot, npc with { Hp = 0 }, 1000, true,
            new WorldNpcReward(0, 0, 0, 0), ct);
        return new("hit", npc with { Hp = 0 }, 1000, true);
    }
}
