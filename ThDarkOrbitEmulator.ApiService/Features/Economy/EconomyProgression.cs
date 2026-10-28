using Npgsql;

namespace ThDarkOrbitEmulator.ApiService.Features.Economy;

// Provisional local rules: a collector upgrade takes its current level in minutes;
// one refinery batch consumes 10 prometium and 10 endurium, then yields 10 prometid after five minutes.
public enum ProgressionChange { Applied, Invalid, Conflict, InsufficientResources, NotReady }
public sealed record CollectorUpgradeJob(string CollectorId, int ExpectedLevel, DateTimeOffset ReadyAt);
public sealed record RefineryJob(DateTimeOffset ReadyAt);
public sealed record GateRun(int Wave, int TotalWaves, DateTimeOffset? CompletedAt);

public sealed partial class EconomyStore
{
    public const int GalaxyGateWaveCount = 10;

    private static async Task<bool> PendingCollectorUpgradeAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, string collectorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM economy.skylab_upgrade_jobs WHERE account_id=@a AND collector_id=@id", db, tx);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("id", collectorId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<CollectorUpgradeJob>> CollectorUpgradeJobsAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT collector_id,expected_level,ready_at FROM economy.skylab_upgrade_jobs WHERE account_id=@a ORDER BY collector_id", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var jobs = new List<CollectorUpgradeJob>();
        while (await rows.ReadAsync(ct)) jobs.Add(new(rows.GetString(0), rows.GetInt32(1), rows.GetFieldValue<DateTimeOffset>(2)));
        return jobs;
    }

    public async Task<ProgressionChange> StartCollectorUpgradeAsync(Guid accountId, string collectorId,
        int expectedLevel, DateTimeOffset now, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !IndexInternal.InternalStart.LocalSkylab.IsCollector(collectorId)
            || expectedLevel is < 1 or >= 10 || string.IsNullOrWhiteSpace(key)) return ProgressionChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var read = new NpgsqlCommand("SELECT level FROM economy.skylab_collectors WHERE account_id=@a AND collector_id=@id", db, tx))
        {
            read.Parameters.AddWithValue("a", accountId); read.Parameters.AddWithValue("id", collectorId);
            if (await read.ExecuteScalarAsync(ct) is not int level || level != expectedLevel
                || await PendingCollectorUpgradeAsync(db, tx, accountId, collectorId, ct)) return ProgressionChange.Conflict;
        }
        await using (var replay = new NpgsqlCommand("SELECT 1 FROM economy.skylab_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return ProgressionChange.Conflict;
        }
        if (!await ChangeBalanceAsync(db, tx, accountId, collectorId, -100L * expectedLevel,
            "skylab_upgrade_start", "skylab", key, ct)) return ProgressionChange.InsufficientResources;
        await using (var job = new NpgsqlCommand("INSERT INTO economy.skylab_upgrade_jobs(account_id,collector_id,expected_level,ready_at,action_key) VALUES (@a,@id,@level,@ready,@k)", db, tx))
        {
            job.Parameters.AddWithValue("a", accountId); job.Parameters.AddWithValue("id", collectorId);
            job.Parameters.AddWithValue("level", expectedLevel); job.Parameters.AddWithValue("ready", now.AddMinutes(expectedLevel));
            job.Parameters.AddWithValue("k", key); await job.ExecuteNonQueryAsync(ct);
        }
        await RecordSkylabActionAsync(db, tx, accountId, key, ct);
        await tx.CommitAsync(ct); return ProgressionChange.Applied;
    }

    public async Task<ProgressionChange> FinishCollectorUpgradeAsync(Guid accountId, string collectorId,
        DateTimeOffset now, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !IndexInternal.InternalStart.LocalSkylab.IsCollector(collectorId)
            || string.IsNullOrWhiteSpace(key)) return ProgressionChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        int level; DateTimeOffset ready;
        await using (var replay = new NpgsqlCommand("SELECT 1 FROM economy.skylab_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return ProgressionChange.Conflict;
        }
        await using (var read = new NpgsqlCommand("SELECT expected_level,ready_at FROM economy.skylab_upgrade_jobs WHERE account_id=@a AND collector_id=@id", db, tx))
        {
            read.Parameters.AddWithValue("a", accountId); read.Parameters.AddWithValue("id", collectorId);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (!await rows.ReadAsync(ct)) return ProgressionChange.Conflict;
            level = rows.GetInt32(0); ready = rows.GetFieldValue<DateTimeOffset>(1);
        }
        if (now < ready) return ProgressionChange.NotReady;
        await using (var update = new NpgsqlCommand("UPDATE economy.skylab_collectors SET level=level+1 WHERE account_id=@a AND collector_id=@id AND level=@level", db, tx))
        {
            update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("id", collectorId);
            update.Parameters.AddWithValue("level", level);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return ProgressionChange.Conflict;
        }
        await using (var remove = new NpgsqlCommand("DELETE FROM economy.skylab_upgrade_jobs WHERE account_id=@a AND collector_id=@id", db, tx))
        {
            remove.Parameters.AddWithValue("a", accountId); remove.Parameters.AddWithValue("id", collectorId);
            await remove.ExecuteNonQueryAsync(ct);
        }
        await RecordSkylabActionAsync(db, tx, accountId, key, ct);
        await tx.CommitAsync(ct); return ProgressionChange.Applied;
    }

    private static async Task RecordSkylabActionAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId, string key, CancellationToken ct)
    {
        await using var action = new NpgsqlCommand("INSERT INTO economy.skylab_actions(account_id,action_key) VALUES (@a,@k)", db, tx);
        action.Parameters.AddWithValue("a", accountId); action.Parameters.AddWithValue("k", key);
        await action.ExecuteNonQueryAsync(ct);
    }

    public async Task<RefineryJob?> RefineryJobAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT ready_at FROM economy.skylab_refinery_jobs WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) is DateTime ready ? new RefineryJob(new DateTimeOffset(ready, TimeSpan.Zero)) : null;
    }

    public async Task<ProgressionChange> StartRefineryAsync(Guid accountId, DateTimeOffset now, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(key)) return ProgressionChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var check = new NpgsqlCommand("SELECT 1 FROM economy.skylab_refinery_jobs WHERE account_id=@a UNION ALL SELECT 1 FROM economy.skylab_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            check.Parameters.AddWithValue("a", accountId); check.Parameters.AddWithValue("k", key);
            if (await check.ExecuteScalarAsync(ct) is not null) return ProgressionChange.Conflict;
        }
        if (!await ChangeBalanceAsync(db, tx, accountId, "prometium", -10, "skylab_refine_input", "skylab", $"{key}:prometium", ct)
            || !await ChangeBalanceAsync(db, tx, accountId, "endurium", -10, "skylab_refine_input", "skylab", $"{key}:endurium", ct))
            return ProgressionChange.InsufficientResources;
        await using (var job = new NpgsqlCommand("INSERT INTO economy.skylab_refinery_jobs(account_id,ready_at,action_key) VALUES (@a,@ready,@k)", db, tx))
        {
            job.Parameters.AddWithValue("a", accountId); job.Parameters.AddWithValue("ready", now.AddMinutes(5));
            job.Parameters.AddWithValue("k", key); await job.ExecuteNonQueryAsync(ct);
        }
        await RecordSkylabActionAsync(db, tx, accountId, key, ct);
        await tx.CommitAsync(ct); return ProgressionChange.Applied;
    }

    public async Task<ProgressionChange> ClaimRefineryAsync(Guid accountId, DateTimeOffset now, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(key)) return ProgressionChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var replay = new NpgsqlCommand("SELECT 1 FROM economy.skylab_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return ProgressionChange.Conflict;
        }
        await using (var check = new NpgsqlCommand("SELECT ready_at FROM economy.skylab_refinery_jobs WHERE account_id=@a", db, tx))
        {
            check.Parameters.AddWithValue("a", accountId);
            if (await check.ExecuteScalarAsync(ct) is not DateTime ready) return ProgressionChange.Conflict;
            if (now < new DateTimeOffset(ready, TimeSpan.Zero)) return ProgressionChange.NotReady;
        }
        if (!await ChangeBalanceAsync(db, tx, accountId, "prometid", 10, "skylab_refine_output", "skylab", key, ct))
            return ProgressionChange.Conflict;
        await using (var remove = new NpgsqlCommand("DELETE FROM economy.skylab_refinery_jobs WHERE account_id=@a", db, tx))
        {
            remove.Parameters.AddWithValue("a", accountId); await remove.ExecuteNonQueryAsync(ct);
        }
        await RecordSkylabActionAsync(db, tx, accountId, key, ct);
        await tx.CommitAsync(ct); return ProgressionChange.Applied;
    }

    public async Task<GateRun?> GateRunAsync(Guid accountId, string gateId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT wave,total_waves,completed_at FROM economy.gate_runs WHERE account_id=@a AND gate_id=@g", db);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("g", gateId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new GateRun(rows.GetInt32(0), rows.GetInt32(1),
            rows.IsDBNull(2) ? null : rows.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    // Legacy menu calls carry no NPC kill evidence. Only AttackGateNpcAsync may progress a run.
    public Task<ProgressionChange> AdvanceGateWaveAsync(Guid accountId, string gateId,
        int expectedWave, DateTimeOffset now, string key, CancellationToken ct) =>
        Task.FromResult(ProgressionChange.Conflict);
}
