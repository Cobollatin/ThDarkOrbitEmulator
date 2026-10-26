using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only progression; no post-company Pilot Sheet contract was captured.
// Skill names, ranks, prerequisites, and point totals are emulator-defined.
public sealed record LocalPilotSkill(string Id, string Name, string Description, int MaxRank = LocalPilotSkillState.MaxRank,
    string? RequiresSkill = null, int RequiresRank = 0, int EffectPerRank = 0, string EffectUnit = "")
{
    public int Bonus(int rank) => EffectPerRank * Math.Clamp(rank, 0, MaxRank);
}

public sealed record LocalPilotSkillState(Dictionary<string, int> Ranks, int Revision = 0)
{
    public const int TotalPoints = 10;
    public const int MaxRank = 5;
    public int Spent => Ranks.Values.Sum();
    public int Available => TotalPoints - Spent;
}

public enum PilotSkillChange { Applied, Invalid, Conflict }

// With an EconomyStore, ranks and single-use form keys persist in Postgres; cache-era state is imported once.
public sealed class LocalPilotSkillStore(IDistributedCache cache, EconomyStore? economy = null)
{
    public static readonly IReadOnlyList<LocalPilotSkill> Skills =
    [
        new("hull", "Hull Training", "Improve ship resilience", EffectPerRank: 2, EffectUnit: "hull points"),
        new("shields", "Shield Training", "Improve shield handling", EffectPerRank: 2, EffectUnit: "shield points"),
        new("lasers", "Laser Training", "Improve laser handling", EffectPerRank: 2, EffectUnit: "laser damage"),
        new("evasion", "Evasion", "Improve evasive manoeuvres", 3, "hull", 3, 1, "evasion chance"),
        new("shieldEngineering", "Shield Engineering", "Improve shield regeneration", 3, "shields", 3, 3, "shield regeneration"),
        new("rocketFusion", "Rocket Fusion", "Improve rocket handling", 3, "lasers", 3, 2, "rocket damage")
    ];

    // Shared across instances so the singleton and test-created stores serialize per process.
    private static readonly SemaphoreSlim MutationLock = new(1, 1);
    private static readonly DistributedCacheEntryOptions StateOptions = new() { SlidingExpiration = TimeSpan.FromDays(30) };
    private static string Key(Guid accountId) => $"localpilotskills:{accountId:N}";

    public static LocalPilotSkill? Find(string skillId) => Skills.FirstOrDefault(skill => skill.Id == skillId);

    public static bool PrerequisiteMet(LocalPilotSkill skill, LocalPilotSkillState state) =>
        skill.RequiresSkill is null || state.Ranks.GetValueOrDefault(skill.RequiresSkill) >= skill.RequiresRank;

    public static IEnumerable<LocalPilotSkill> UnlockedBy(LocalPilotSkill skill) =>
        Skills.Where(candidate => candidate.RequiresSkill == skill.Id);

    // Emulator-defined status names, checked in this order: maxed, locked, no-points, available.
    public static string Status(LocalPilotSkill skill, LocalPilotSkillState state) =>
        state.Ranks.GetValueOrDefault(skill.Id) >= skill.MaxRank ? "maxed"
        : !PrerequisiteMet(skill, state) ? "locked"
        : state.Available <= 0 ? "no-points"
        : "available";

    public static bool CanAllocate(LocalPilotSkill skill, LocalPilotSkillState state) =>
        state.Ranks.GetValueOrDefault(skill.Id) < skill.MaxRank && state.Available > 0 && PrerequisiteMet(skill, state);

    public async Task<LocalPilotSkillState> GetAsync(Guid accountId, CancellationToken ct)
    {
        if (economy is null) return await GetCachedAsync(accountId, ct);
        if (await cache.GetStringAsync(Key(accountId), ct) is not null)
        {
            await economy.ImportPilotSkillsAsync(accountId, await GetCachedAsync(accountId, ct), ct);
            await cache.RemoveAsync(Key(accountId), ct);
        }
        return await economy.PilotSkillsAsync(accountId, ct) ?? new LocalPilotSkillState(new Dictionary<string, int>());
    }

    private async Task<LocalPilotSkillState> GetCachedAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        if (json is null) return new LocalPilotSkillState(new Dictionary<string, int>());
        LocalPilotSkillState? stored;
        try { stored = JsonSerializer.Deserialize<LocalPilotSkillState>(json); }
        catch (JsonException) { stored = null; }
        return Sanitize(stored);
    }

    // Corrupt or tampered cache entries never yield negative, oversized, or unknown ranks.
    internal static LocalPilotSkillState Sanitize(LocalPilotSkillState? stored)
    {
        var ranks = new Dictionary<string, int>();
        if (stored?.Ranks is null) return new LocalPilotSkillState(ranks, Math.Max(0, stored?.Revision ?? 0));
        foreach (var skill in Skills)
        {
            var rank = Math.Clamp(stored.Ranks.GetValueOrDefault(skill.Id), 0, skill.MaxRank);
            if (rank > 0) ranks[skill.Id] = rank;
        }
        var revision = Math.Max(0, stored.Revision);
        return ranks.Values.Sum() > LocalPilotSkillState.TotalPoints
            ? new LocalPilotSkillState(new Dictionary<string, int>(), revision)
            : new LocalPilotSkillState(ranks, revision);
    }

    public async Task<bool> AllocateAsync(Guid accountId, string skillId, CancellationToken ct) =>
        await AllocateAsync(accountId, skillId, null, null, ct) == PilotSkillChange.Applied;

    public async Task<bool> ResetAsync(Guid accountId, CancellationToken ct) =>
        await ResetAsync(accountId, null, null, ct) == PilotSkillChange.Applied;

    public async Task<PilotSkillChange> AllocateAsync(Guid accountId, string skillId, int? expectedRevision,
        string? replayKey, CancellationToken ct)
    {
        if (accountId == Guid.Empty || Find(skillId) is not { } skill) return PilotSkillChange.Invalid;
        return await MutateAsync(accountId, expectedRevision, replayKey, state =>
        {
            if (!CanAllocate(skill, state)) return null;
            var ranks = new Dictionary<string, int>(state.Ranks) { [skill.Id] = state.Ranks.GetValueOrDefault(skill.Id) + 1 };
            return ranks;
        }, ct);
    }

    public async Task<PilotSkillChange> ResetAsync(Guid accountId, int? expectedRevision, string? replayKey,
        CancellationToken ct)
    {
        if (accountId == Guid.Empty) return PilotSkillChange.Invalid;
        return await MutateAsync(accountId, expectedRevision, replayKey,
            state => state.Spent == 0 ? null : new Dictionary<string, int>(), ct);
    }

    private async Task<PilotSkillChange> MutateAsync(Guid accountId, int? expectedRevision, string? replayKey,
        Func<LocalPilotSkillState, Dictionary<string, int>?> change, CancellationToken ct)
    {
        if (economy is not null)
        {
            await GetAsync(accountId, ct);
            return await economy.ChangePilotSkillsAsync(accountId, expectedRevision, replayKey, change, ct);
        }
        await MutationLock.WaitAsync(ct);
        try
        {
            var replayCacheKey = replayKey is null ? null : $"localpilotskills-action:{accountId:N}:{replayKey}";
            if (replayCacheKey is not null && await cache.GetStringAsync(replayCacheKey, ct) is not null)
                return PilotSkillChange.Conflict;
            var state = await GetAsync(accountId, ct);
            if (expectedRevision is not null && expectedRevision != state.Revision) return PilotSkillChange.Conflict;
            if (change(state) is not { } ranks) return expectedRevision is null ? PilotSkillChange.Invalid : PilotSkillChange.Conflict;
            await cache.SetStringAsync(Key(accountId),
                JsonSerializer.Serialize(new LocalPilotSkillState(ranks, state.Revision + 1)), StateOptions, ct);
            if (replayCacheKey is not null)
                await cache.SetStringAsync(replayCacheKey, "1", new DistributedCacheEntryOptions
                    { AbsoluteExpirationRelativeToNow = LocalPilotSkillAction.FormLifetime + TimeSpan.FromMinutes(10) }, ct);
            return PilotSkillChange.Applied;
        }
        finally { MutationLock.Release(); }
    }
}

