using Npgsql;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.Economy;

public sealed record ClanRelation(string OtherClanId, string Status, string Proposer);
public sealed record ClanStationModule(string ModuleId, int Level, DateTimeOffset? ReadyAt);
public enum ClanOperationChange { Applied, Invalid, Forbidden, Conflict }

public sealed partial class EconomyStore
{
    public async Task<bool> IsClanLeaderAsync(Guid accountId, string clanId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM economy.clan_leaders l JOIN economy.clan_memberships m ON m.account_id=l.account_id AND m.clan_id=l.clan_id WHERE l.account_id=@a AND l.clan_id=@c", db);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("c", clanId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<ClanRelation>> ClanRelationsAsync(string clanId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT CASE WHEN clan_low=@c THEN clan_high ELSE clan_low END,status,proposer FROM economy.clan_relations WHERE clan_low=@c OR clan_high=@c ORDER BY 1", db);
        cmd.Parameters.AddWithValue("c", clanId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ClanRelation>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<ClanStationModule>> ClanStationAsync(string clanId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT module_id,level,ready_at FROM economy.clan_station_modules WHERE clan_id=@c ORDER BY module_id", db);
        cmd.Parameters.AddWithValue("c", clanId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ClanStationModule>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetInt32(1),
            rows.IsDBNull(2) ? null : rows.GetFieldValue<DateTimeOffset>(2)));
        return result;
    }

    public async Task<ClanOperationChange> ChangeClanRelationAsync(Guid accountId, string clanId,
        string otherClanId, string action, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || clanId == otherClanId ||
            LocalClanStore.Find(clanId) is not { } own ||
            LocalClanStore.Find(otherClanId)?.Company != own.Company ||
            action is not ("propose" or "accept" or "reject" or "war" or "end") ||
            string.IsNullOrWhiteSpace(key) || key.Length > 160)
            return ClanOperationChange.Invalid;
        var low = string.CompareOrdinal(clanId, otherClanId) < 0 ? clanId : otherClanId;
        var high = low == clanId ? otherClanId : clanId;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@p))", db, tx))
        { gate.Parameters.AddWithValue("p", $"clan-relation:{low}:{high}"); await gate.ExecuteNonQueryAsync(ct); }
        if (!await LeaderInTransactionAsync(db, tx, accountId, clanId, ct)) return ClanOperationChange.Forbidden;
        if (await OperationUsedAsync(db, tx, accountId, key, ct)) return ClanOperationChange.Conflict;
        string? status = null, proposer = null;
        await using (var read = new NpgsqlCommand(
            "SELECT status,proposer FROM economy.clan_relations WHERE clan_low=@l AND clan_high=@h", db, tx))
        {
            read.Parameters.AddWithValue("l", low); read.Parameters.AddWithValue("h", high);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct)) { status = rows.GetString(0); proposer = rows.GetString(1); }
        }
        var allowed = action switch
        {
            "propose" => status is null,
            "accept" or "reject" => status == "pending" && proposer == otherClanId,
            "war" => status is null or "allied",
            "end" => status is "allied" or "war",
            _ => false
        };
        if (!allowed) return ClanOperationChange.Conflict;
        if (action is "reject" or "end")
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM economy.clan_relations WHERE clan_low=@l AND clan_high=@h", db, tx);
            delete.Parameters.AddWithValue("l", low); delete.Parameters.AddWithValue("h", high);
            await delete.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var write = new NpgsqlCommand(
                "INSERT INTO economy.clan_relations(clan_low,clan_high,proposer,status) VALUES (@l,@h,@p,@s) ON CONFLICT (clan_low,clan_high) DO UPDATE SET proposer=EXCLUDED.proposer,status=EXCLUDED.status", db, tx);
            write.Parameters.AddWithValue("l", low); write.Parameters.AddWithValue("h", high);
            write.Parameters.AddWithValue("p", action == "accept" ? otherClanId : clanId);
            write.Parameters.AddWithValue("s", action switch { "propose" => "pending", "accept" => "allied", _ => "war" });
            await write.ExecuteNonQueryAsync(ct);
        }
        await RecordOperationAsync(db, tx, accountId, key, ct);
        await tx.CommitAsync(ct);
        return ClanOperationChange.Applied;
    }

    public async Task<ClanOperationChange> UpgradeClanModuleAsync(Guid accountId, string clanId,
        string moduleId, string key, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty || LocalClanStore.Find(clanId) is null ||
            moduleId is not ("hull" or "defense" or "relay") ||
            string.IsNullOrWhiteSpace(key) || key.Length > 160) return ClanOperationChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@p))", db, tx))
        { gate.Parameters.AddWithValue("p", $"clan-station:{clanId}:{moduleId}"); await gate.ExecuteNonQueryAsync(ct); }
        if (!await LeaderInTransactionAsync(db, tx, accountId, clanId, ct)) return ClanOperationChange.Forbidden;
        if (await OperationUsedAsync(db, tx, accountId, key, ct)) return ClanOperationChange.Conflict;
        var level = 0;
        DateTimeOffset? readyAt = null;
        await using (var read = new NpgsqlCommand(
            "SELECT level,ready_at FROM economy.clan_station_modules WHERE clan_id=@c AND module_id=@m", db, tx))
        {
            read.Parameters.AddWithValue("c", clanId); read.Parameters.AddWithValue("m", moduleId);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
            { level = rows.GetInt32(0); readyAt = rows.IsDBNull(1) ? null : rows.GetFieldValue<DateTimeOffset>(1); }
        }
        if (level >= 3 || readyAt > now) return ClanOperationChange.Conflict;
        await using (var write = new NpgsqlCommand(
            "INSERT INTO economy.clan_station_modules(clan_id,module_id,level,ready_at) VALUES (@c,@m,@l,@r) ON CONFLICT (clan_id,module_id) DO UPDATE SET level=EXCLUDED.level,ready_at=EXCLUDED.ready_at", db, tx))
        {
            write.Parameters.AddWithValue("c", clanId); write.Parameters.AddWithValue("m", moduleId);
            write.Parameters.AddWithValue("l", level + 1); write.Parameters.AddWithValue("r", now.AddMinutes(5));
            await write.ExecuteNonQueryAsync(ct);
        }
        await RecordOperationAsync(db, tx, accountId, key, ct);
        await tx.CommitAsync(ct);
        return ClanOperationChange.Applied;
    }

    private static async Task<bool> LeaderInTransactionAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, string clanId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM economy.clan_leaders l JOIN economy.clan_memberships m ON m.account_id=l.account_id AND m.clan_id=l.clan_id WHERE l.account_id=@a AND l.clan_id=@c FOR SHARE OF l,m", db, tx);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("c", clanId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> OperationUsedAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM economy.clan_operations WHERE account_id=@a AND action_key=@k", db, tx);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task RecordOperationAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.clan_operations(account_id,action_key) VALUES (@a,@k)", db, tx);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
