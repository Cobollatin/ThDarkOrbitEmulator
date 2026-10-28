using Npgsql;

namespace ThDarkOrbitEmulator.ApiService.Features.Economy;

public sealed record GateNpcGroup(string Npc, int Count);
public sealed record GateWave(string Npc, int Count, IReadOnlyList<GateNpcGroup>? Groups = null)
{
    public string NpcAt(int kills)
    {
        if (Groups is null) return Npc;
        foreach (var group in Groups)
        {
            if (kills < group.Count) return group.Npc;
            kills -= group.Count;
        }
        return "";
    }
}
public sealed record GateCombatState(int Wave, int TotalWaves, string Npc, int Kills, int Required,
    int NpcHp, int ShipHp, int Lives, DateTimeOffset? CompletedAt);

public sealed partial class EconomyStore
{
    // The 2021 official FAQ lists ten waves. Counts are explicit because Beta and Gamma
    // differ from Alpha in selected waves; the local combat damage model is provisional.
    private static readonly string[] GateNpcKinds =
        ["Streuner", "Lordakia", "Mordon", "Saimon", "Devolarium", "Kristallin",
         "Sibelon", "Sibelonit", "Kristallon", "Small Kubikon"];
    private static readonly int[] AlphaCounts = [20, 20, 20, 20, 20, 40, 20, 80, 16, 20];
    private static readonly int[] BetaCounts = [20, 20, 40, 20, 20, 40, 20, 40, 16, 20];
    private static readonly int[] GammaCounts = [20, 20, 20, 40, 20, 40, 20, 40, 16, 20];
    private static readonly GateNpcGroup[][] DeltaGroups =
    [
        [new("Lordakia", 5), new("Mordon", 10), new("Saimon", 15)],
        [new("Streuner", 11), new("StreuneR", 1)],
        [new("Mordon", 5), new("Saimon", 10), new("Kristallin", 15)],
        [new("Lordakia", 12), new("Lordakium", 1)],
        [new("Boss Lordakia", 10), new("Boss Saimon", 6), new("Boss Mordon", 8)],
        [new("Sibelonit", 15), new("Sibelon", 1)],
        [new("Sibelonit", 5), new("Kristallin", 10), new("Boss StreuneR", 5)],
        [new("Kristallin", 10), new("Kristallon", 1)],
        [new("Protegit", 15), new("Boss Lordakium", 3)],
        [new("demaNeR", 3)]
    ];

    public static GateWave? GateWaveAt(string gateId, int wave)
    {
        if (wave is < 0 or >= 10) return null;
        if (gateId == "delta")
        {
            var groups = DeltaGroups[wave];
            return new GateWave(groups[0].Npc, groups.Sum(g => g.Count), groups);
        }
        var counts = gateId switch
        {
            "alpha" => AlphaCounts, "beta" => BetaCounts, "gamma" => GammaCounts,
            "epsilon" => AlphaCounts, _ => null
        };
        return counts is null ? null : new GateWave(GateNpcKinds[wave], counts[wave]);
    }

    public async Task<GateCombatState?> GateCombatAsync(Guid accountId, string gateId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT wave,total_waves,kills,npc_hp,ship_hp,lives,completed_at FROM economy.gate_runs WHERE account_id=@a AND gate_id=@g", db);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("g", gateId);
        await using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        var wave = row.GetInt32(0);
        var composition = GateWaveAt(gateId, wave);
        return new(wave, row.GetInt32(1), composition?.NpcAt(row.GetInt32(2)) ?? "", row.GetInt32(2),
            composition?.Count ?? 0, row.GetInt32(3), row.GetInt32(4), row.GetInt32(5),
            row.IsDBNull(6) ? null : row.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<ProgressionChange> AttackGateNpcAsync(Guid accountId, string gateId,
        int expectedWave, DateTimeOffset now, string key, CancellationToken ct, int? expectedKills = null)
    {
        if (accountId == Guid.Empty || GateWaveAt(gateId, expectedWave) is null ||
            string.IsNullOrWhiteSpace(key)) return ProgressionChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var replay = new NpgsqlCommand("SELECT 1 FROM economy.gate_wave_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return ProgressionChange.Conflict;
        }
        int wave, kills, npcHp, shipHp, lives;
        await using (var read = new NpgsqlCommand("SELECT wave,kills,npc_hp,ship_hp,lives FROM economy.gate_runs WHERE account_id=@a AND gate_id=@g AND completed_at IS NULL", db, tx))
        {
            read.Parameters.AddWithValue("a", accountId); read.Parameters.AddWithValue("g", gateId);
            await using var row = await read.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return ProgressionChange.Conflict;
            wave = row.GetInt32(0); kills = row.GetInt32(1); npcHp = row.GetInt32(2);
            shipHp = row.GetInt32(3); lives = row.GetInt32(4);
        }
        var composition = GateWaveAt(gateId, wave);
        if (wave != expectedWave || expectedKills is not null && kills != expectedKills ||
            composition is null || lives == 0 || kills >= composition.Count)
            return ProgressionChange.Conflict;
        // A server-owned hit removes health from exactly one NPC. Retaliation can destroy
        // the ship; a retry retains all previously cleared NPCs and consumes one life.
        npcHp -= 1000;
        if (npcHp <= 0) { kills++; npcHp = 1000; }
        shipHp = Math.Max(0, shipHp - 20);
        if (shipHp == 0) { lives--; if (lives > 0) shipHp = 1000; }
        var cleared = kills == composition.Count && lives > 0;
        var completed = cleared && wave + 1 == 10;
        if (cleared) { wave++; kills = 0; npcHp = 1000; if (lives > 0) shipHp = 1000; }
        await using (var update = new NpgsqlCommand("UPDATE economy.gate_runs SET wave=@w,kills=@k,npc_hp=@n,ship_hp=@h,lives=@l,completed_at=CASE WHEN @done THEN @now ELSE completed_at END WHERE account_id=@a AND gate_id=@g", db, tx))
        {
            update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("g", gateId);
            update.Parameters.AddWithValue("w", wave); update.Parameters.AddWithValue("k", kills);
            update.Parameters.AddWithValue("n", npcHp); update.Parameters.AddWithValue("h", shipHp);
            update.Parameters.AddWithValue("l", lives); update.Parameters.AddWithValue("done", completed);
            update.Parameters.AddWithValue("now", now); await update.ExecuteNonQueryAsync(ct);
        }
        if (completed)
        {
            var reward = gateId switch
            {
                "alpha" => (20000L, 100000L, 4000000L),
                "beta" => (40000L, 200000L, 8000000L),
                "gamma" => (60000L, 300000L, 12000000L),
                "delta" => (45000L, 225000L, 9000000L),
                _ => (25000L, 150000L, 5000000L)
            };
            foreach (var (asset, amount) in new[] { ("uridium", reward.Item1), ("ucb_100", gateId == "epsilon" ? 20000L : reward.Item1),
                         ("honor", reward.Item2), ("experience", reward.Item3) })
                if (!await ChangeBalanceAsync(db, tx, accountId, asset, amount, "gate_complete", gateId,
                    $"gate:{gateId}:{asset}", ct)) return ProgressionChange.Conflict;
            await using var achievement = new NpgsqlCommand("INSERT INTO economy.gate_achievements(account_id,gate_id,completed_at) VALUES (@a,@g,@now) ON CONFLICT DO NOTHING", db, tx);
            achievement.Parameters.AddWithValue("a", accountId); achievement.Parameters.AddWithValue("g", gateId);
            achievement.Parameters.AddWithValue("now", now); await achievement.ExecuteNonQueryAsync(ct);
        }
        await using (var action = new NpgsqlCommand("INSERT INTO economy.gate_wave_actions(account_id,action_key) VALUES (@a,@k)", db, tx))
        {
            action.Parameters.AddWithValue("a", accountId); action.Parameters.AddWithValue("k", key);
            await action.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ProgressionChange.Applied;
    }
}
