using Npgsql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Progression;

public sealed record TimerOverride(DateTimeOffset ReadyAt);
public sealed record GateOverride(int Wave, int Kills, int NpcHp, int ShipHp, int Lives);
public sealed record StationOverride(int Level, DateTimeOffset? ReadyAt);
public sealed record RelationOverride(string Status, string Proposer);

public static class ProgressionBackofficeEndpoints
{
    public static void MapProgressionBackoffice(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/backoffice/progression").RequireAuthorization(EmulatorRoles.Admin).WithTags("Player progression");
        admin.MapGet("/accounts/{accountId:guid}/hangar", async (Guid accountId, CockpitCatalogStore catalog, LocalHangarStore hangars, CancellationToken ct) =>
            Results.Ok(new { activeSlot = await hangars.GetActiveSlotAsync(accountId, ct), assignments = await catalog.AssignmentsAsync(accountId, ct) }));
        admin.MapPut("/accounts/{accountId:guid}/hangar", async (Guid accountId, ActiveHangarOverride input, CockpitCatalogStore catalog, LocalHangarStore hangars, CancellationToken ct) =>
        {
            if (input.Slot is < 1 or > 2 || !(await catalog.AssignmentsAsync(accountId, ct)).Any(h => h.SlotNumber == input.Slot)) return Results.BadRequest();
            var current = await hangars.GetActiveSlotAsync(accountId, ct);
            if (current == input.Slot) return Results.NoContent();
            return await hangars.ChangeAsync(accountId, input.Slot, current, ct) == LocalHangarChange.Applied ? Results.NoContent() : Results.Conflict();
        });
        admin.MapGet("/accounts/{accountId:guid}/skylab", async (Guid accountId, EconomyStore economy, CancellationToken ct) =>
            Results.Ok(new { collectors = await economy.CollectorUpgradeJobsAsync(accountId, ct), refinery = await economy.RefineryJobAsync(accountId, ct) }));
        admin.MapPut("/accounts/{accountId:guid}/skylab/collectors/{collectorId}", async (Guid accountId, string collectorId, TimerOverride input, ProgressionBackofficeStore store, CancellationToken ct) =>
            LocalSkylab.IsCollector(collectorId) && await store.SetCollectorReadyAtAsync(accountId, collectorId, input.ReadyAt, ct) ? Results.NoContent() : Results.NotFound());
        admin.MapPut("/accounts/{accountId:guid}/skylab/refinery", async (Guid accountId, TimerOverride input, ProgressionBackofficeStore store, CancellationToken ct) =>
            await store.SetRefineryReadyAtAsync(accountId, input.ReadyAt, ct) ? Results.NoContent() : Results.NotFound());
        admin.MapGet("/accounts/{accountId:guid}/gates", (Guid accountId, ProgressionBackofficeStore store, CancellationToken ct) => store.GatesAsync(accountId, ct));
        admin.MapPut("/accounts/{accountId:guid}/gates/{gateId}", async (Guid accountId, string gateId, GateOverride input, ProgressionBackofficeStore store, CancellationToken ct) =>
        {
            var wave = EconomyStore.GateWaveAt(gateId, input.Wave);
            if (wave is null || input.Kills < 0 || input.Kills >= wave.Count || input.NpcHp is < 1 or > 1000 || input.ShipHp is < 0 or > 1000 || input.Lives is < 0 or > 5) return Results.BadRequest();
            return await store.SetGateAsync(accountId, gateId, input, ct) ? Results.NoContent() : Results.NotFound();
        });
        admin.MapGet("/clans/{clanId}/station", (string clanId, ProgressionBackofficeStore store, CancellationToken ct) => store.StationAsync(clanId, ct));
        admin.MapPut("/clans/{clanId}/station/{moduleId}", async (string clanId, string moduleId, StationOverride input, ProgressionBackofficeStore store, CancellationToken ct) =>
            moduleId is not ("hull" or "defense" or "relay") || input.Level is < 0 or > 3 ? Results.BadRequest() :
                await store.SetStationAsync(clanId, moduleId, input, ct) ? Results.NoContent() : Results.NotFound());
        admin.MapGet("/clans/{clanId}/relations", (string clanId, EconomyStore economy, CancellationToken ct) => economy.ClanRelationsAsync(clanId, ct));
        admin.MapPut("/clans/{clanId}/relations/{otherClanId}", async (string clanId, string otherClanId, RelationOverride input, ProgressionBackofficeStore store, CancellationToken ct) =>
            clanId == otherClanId || LocalClanStore.Find(clanId) is not { } own || LocalClanStore.Find(otherClanId)?.Company != own.Company ||
            input.Status is not ("pending" or "allied" or "war") || input.Proposer is null || input.Proposer != clanId && input.Proposer != otherClanId
                ? Results.BadRequest() : await store.SetRelationAsync(clanId, otherClanId, input, ct) ? Results.NoContent() : Results.NotFound());
    }
}

public sealed record ActiveHangarOverride(int Slot);

public sealed record GateBackofficeRow(string GateId, int Wave, int TotalWaves, int Kills, int NpcHp, int ShipHp, int Lives, DateTimeOffset? CompletedAt);
public sealed class ProgressionBackofficeStore(IConfiguration configuration)
{
    private NpgsqlConnection Connection() => new(configuration.GetConnectionString("identity-write") ?? throw new InvalidOperationException("identity-write connection is missing."));
    public async Task<IReadOnlyList<GateBackofficeRow>> GatesAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT gate_id,wave,total_waves,kills,npc_hp,ship_hp,lives,completed_at FROM economy.gate_runs WHERE account_id=@a ORDER BY gate_id", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<GateBackofficeRow>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetInt32(1), rows.GetInt32(2), rows.GetInt32(3), rows.GetInt32(4), rows.GetInt32(5), rows.GetInt32(6), rows.IsDBNull(7) ? null : rows.GetFieldValue<DateTimeOffset>(7)));
        return result;
    }
    public async Task<IReadOnlyList<ClanStationModule>> StationAsync(string clanId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT module_id,level,ready_at FROM economy.clan_station_modules WHERE clan_id=@c ORDER BY module_id", db);
        cmd.Parameters.AddWithValue("c", clanId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ClanStationModule>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetInt32(1), rows.IsDBNull(2) ? null : rows.GetFieldValue<DateTimeOffset>(2)));
        return result;
    }
    public Task<bool> SetCollectorReadyAtAsync(Guid accountId, string collectorId, DateTimeOffset readyAt, CancellationToken ct) =>
        UpdateAsync("UPDATE economy.skylab_upgrade_jobs SET ready_at=@ready WHERE account_id=@a AND collector_id=@id", ct, ("a", accountId), ("id", collectorId), ("ready", readyAt));
    public Task<bool> SetRefineryReadyAtAsync(Guid accountId, DateTimeOffset readyAt, CancellationToken ct) =>
        UpdateAsync("UPDATE economy.skylab_refinery_jobs SET ready_at=@ready WHERE account_id=@a", ct, ("a", accountId), ("ready", readyAt));
    // Override only an existing run. Completion rewards remain owned by the combat flow.
    public Task<bool> SetGateAsync(Guid accountId, string gateId, GateOverride value, CancellationToken ct) =>
        UpdateAsync("UPDATE economy.gate_runs SET wave=@wave,kills=@kills,npc_hp=@npc,ship_hp=@ship,lives=@lives WHERE account_id=@a AND gate_id=@g AND completed_at IS NULL", ct,
            ("a", accountId), ("g", gateId), ("wave", value.Wave), ("kills", value.Kills), ("npc", value.NpcHp), ("ship", value.ShipHp), ("lives", value.Lives));
    public Task<bool> SetStationAsync(string clanId, string moduleId, StationOverride value, CancellationToken ct) =>
        UpdateAsync("UPDATE economy.clan_station_modules SET level=@level,ready_at=@ready WHERE clan_id=@c AND module_id=@m", ct,
            ("c", clanId), ("m", moduleId), ("level", value.Level), ("ready", value.ReadyAt));
    public Task<bool> SetRelationAsync(string clanId, string otherClanId, RelationOverride value, CancellationToken ct) =>
        UpdateAsync("UPDATE economy.clan_relations SET status=@status,proposer=@proposer WHERE clan_low=@low AND clan_high=@high", ct,
            ("low", string.CompareOrdinal(clanId, otherClanId) < 0 ? clanId : otherClanId), ("high", string.CompareOrdinal(clanId, otherClanId) < 0 ? otherClanId : clanId),
            ("status", value.Status), ("proposer", value.Proposer));
    private async Task<bool> UpdateAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
