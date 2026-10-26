using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using NpgsqlTypes;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Admin;

public sealed record RuntimeVariable(string Key, string Category, string ValueType,
    string DefaultValue, string CurrentValue);
public sealed record RuntimeVariableChangeEntry(long ChangeId, string VariableKey, string Category,
    string PreviousValue, string NewValue, string DefaultValue, string ValueType,
    string Surface, string ChangedBy, string NodeId, string Reason, string? RawCommand,
    DateTimeOffset ChangedAtUtc);
public sealed record RuntimeVariableActor(string Surface, string ChangedBy, string NodeId,
    string Reason, string? RawCommand = null);

public interface IRuntimeVariableRegistry
{
    IReadOnlyList<RuntimeVariable> Catalog { get; }
    Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default);
    Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default);
    Task<double> NumberAsync(string key, CancellationToken ct = default);
    Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
        CancellationToken ct = default);
    Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
        CancellationToken ct = default);
    Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor,
        CancellationToken ct = default);
    Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100,
        long? beforeChangeId = null, CancellationToken ct = default);
}

public sealed class RuntimeVariableStore(IDistributedCache cache, IConfiguration configuration,
    IServerMeshMutex? mutex = null) : IRuntimeVariableRegistry
{
    private static RuntimeVariable Define(string key, string category, string type, string value) =>
        new(key, category, type, value, value);
    public IReadOnlyList<RuntimeVariable> Catalog { get; } =
    [
        Define("world.tick_rate_ms", "World", "int", "1000"),
        Define("world.movement.speed_multiplier", "Movement", "double", "1"),
        Define("world.radiation.damage_per_attempt", "World", "int", "0"),
        Define("world.config.switch_cooldown_ms", "World", "int", "5000"),
        Define("world.portal.jump_radius", "World", "int", "500"),
        Define("world.station.peace_radius", "World", "int", "1000"),
        Define("world.station.repair_hp_per_tick", "World", "int", "0"),
        Define("world.spawn.streuner_count", "Spawns", "int", "3"),
        Define("world.spawn.lordakia_count", "Spawns", "int", "2"),
        Define("world.spawn.saimon_count", "Spawns", "int", "1"),
        Define("world.spawn.bonus_box_count", "Spawns", "int", "6"),
        Define("world.collectibles.respawn_seconds", "Collectibles", "int", "300"),
        Define("world.collectibles.pickup_radius", "Collectibles", "int", "180"),
        Define("world.combat.laser_damage_multiplier", "Combat", "double", "1"),
        Define("world.combat.fire_range", "Combat", "int", "1000"),
        Define("world.combat.shield_absorption_ratio", "Combat", "double", "1"),
        Define("world.ship.pusat_plus_base_speed", "Movement", "int", "370"),
        Define("world.ship.phoenix_base_speed", "Movement", "int", "320"),
        Define("economy.credits_reward_multiplier", "Economy", "double", "1"),
        Define("economy.uridium_reward_multiplier", "Economy", "double", "1"),
        Define("economy.xp_reward_multiplier", "Economy", "double", "1"),
        Define("economy.honor_reward_multiplier", "Economy", "double", "1"),
        Define("economy.shop_discount_percent", "Economy", "double", "0"),
        Define("mesh.desired_replicas", "ServerMesh", "int", configuration.GetValue("Cluster:DesiredWorkers", 1).ToString(CultureInfo.InvariantCulture)),
        Define("mesh.max_maps_per_node", "ServerMesh", "int", configuration.GetValue("Cluster:MaxMapsPerNode", 4).ToString(CultureInfo.InvariantCulture)),
        Define("mesh.heartbeat_ttl_seconds", "ServerMesh", "int", configuration.GetValue("Cluster:LeaseTtlSeconds", 30).ToString(CultureInfo.InvariantCulture))
    ];

    private NpgsqlConnection Connection() => new(configuration.GetConnectionString("identity-write")
        ?? throw new InvalidOperationException("identity-write connection is required."));
    private static string CacheKey(string key) => $"runtime-variable:v1:{key}";
    private static readonly DistributedCacheEntryOptions CacheLifetime = new()
        { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30) };

    public async Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT variable_key,category,value_type,default_value,current_value FROM identity.runtime_variables ORDER BY variable_key", db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = Catalog.ToDictionary(v => v.Key, StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            var item = new RuntimeVariable(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4));
            result[item.Key] = item;
        }
        return result.Values.OrderBy(v => v.Category).ThenBy(v => v.Key).ToArray();
    }

    public async Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default)
    {
        var cached = await cache.GetStringAsync(CacheKey(key), ct);
        if (cached is not null) return JsonSerializer.Deserialize<RuntimeVariable>(cached)!;
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT category,value_type,default_value,current_value FROM identity.runtime_variables WHERE variable_key=@key", db);
        cmd.Parameters.AddWithValue("key", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        RuntimeVariable value;
        if (await reader.ReadAsync(ct))
            value = new(key, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        else value = Catalog.FirstOrDefault(v => v.Key == key)
            ?? throw new KeyNotFoundException($"Unknown runtime variable: {key}");
        await cache.SetStringAsync(CacheKey(key), JsonSerializer.Serialize(value), CacheLifetime, ct);
        return value;
    }

    public async Task<double> NumberAsync(string key, CancellationToken ct = default) =>
        double.Parse((await GetAsync(key, ct)).CurrentValue, CultureInfo.InvariantCulture);

    public Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
        CancellationToken ct = default) => ChangeAsync(key, value, actor, ct);

    public async Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
        CancellationToken ct = default)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT variable_key,previous_value,new_value FROM identity.runtime_variable_history WHERE change_id=@id", db);
        cmd.Parameters.AddWithValue("id", changeId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Unknown change: {changeId}");
        var key = reader.GetString(0);
        var previous = reader.GetString(1);
        var expected = reader.GetString(2);
        // A rollback restores the value that change replaced only while that change is still the live value.
        return await ChangeAsync(key, previous, actor with { Reason = $"Rollback of {changeId}: {actor.Reason}" }, ct,
            (changeId, expected));
    }

    public async Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor,
        CancellationToken ct = default) => await ChangeAsync(key, (await GetAsync(key, ct)).DefaultValue,
            actor with { Reason = $"Reset to default: {actor.Reason}" }, ct);

    public async Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null,
        int limit = 100, long? beforeChangeId = null, CancellationToken ct = default)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT change_id,variable_key,category,previous_value,new_value,default_value,value_type,surface,changed_by,node_id,reason,raw_command,changed_at_utc FROM identity.runtime_variable_history WHERE (@key IS NULL OR variable_key=@key) AND (@before IS NULL OR change_id<@before) ORDER BY change_id DESC LIMIT @limit", db);
        cmd.Parameters.Add("key", NpgsqlDbType.Text).Value = (object?)key ?? DBNull.Value;
        cmd.Parameters.Add("before", NpgsqlDbType.Bigint).Value = (object?)beforeChangeId ?? DBNull.Value;
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<RuntimeVariableChangeEntry>();
        while (await reader.ReadAsync(ct)) result.Add(ReadChange(reader));
        return result;
    }

    private async Task<RuntimeVariableChangeEntry> ChangeAsync(string key, string input,
        RuntimeVariableActor actor, CancellationToken ct, (long ChangeId, string Value)? expected = null)
    {
        if (!Regex.IsMatch(key, "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)
            || key.Length > 128) throw new ArgumentException("Invalid variable key.", nameof(key));
        if (actor.Surface is not ("ServerManagement" or "AdminCms" or "InGameAdmin")
            || string.IsNullOrWhiteSpace(actor.ChangedBy) || string.IsNullOrWhiteSpace(actor.NodeId)
            || string.IsNullOrWhiteSpace(actor.Reason)) throw new ArgumentException("Invalid change attribution.", nameof(actor));
        await using var held = mutex is null ? null : await mutex.AcquireAsync(ct);
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var order = new NpgsqlCommand("SELECT pg_advisory_xact_lock(42420028)", db, tx))
            await order.ExecuteNonQueryAsync(ct);
        // Global advisory ordering plus the row lock preserve history order and exact previous values.
        await using var seed = new NpgsqlCommand("INSERT INTO identity.runtime_variables(variable_key,category,value_type,default_value,current_value) VALUES(@key,@category,@type,@default,@default) ON CONFLICT DO NOTHING", db, tx);
        var known = Catalog.FirstOrDefault(v => v.Key == key);
        var type = known?.ValueType ?? (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? input.Contains('.') ? "double" : "int" : "string");
        seed.Parameters.AddWithValue("key", key);
        seed.Parameters.AddWithValue("category", known?.Category ?? "Dynamic");
        seed.Parameters.AddWithValue("type", type);
        seed.Parameters.AddWithValue("default", known?.DefaultValue ?? Normalize(input, type));
        await seed.ExecuteNonQueryAsync(ct);
        await using var select = new NpgsqlCommand("SELECT category,value_type,default_value,current_value FROM identity.runtime_variables WHERE variable_key=@key FOR UPDATE", db, tx);
        select.Parameters.AddWithValue("key", key);
        RuntimeVariable current;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            current = new(key, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        }
        if (expected is { } stale && current.CurrentValue != stale.Value)
            throw new InvalidOperationException(
                $"Rollback conflict: {key} is now {current.CurrentValue}, not {stale.Value} set by change {stale.ChangeId}. Roll back the later change first.");
        var value = Normalize(input, current.ValueType);
        ValidateRange(key, value);
        await using var update = new NpgsqlCommand("UPDATE identity.runtime_variables SET current_value=@value,updated_at_utc=now() WHERE variable_key=@key", db, tx);
        update.Parameters.AddWithValue("key", key);
        update.Parameters.AddWithValue("value", value);
        await update.ExecuteNonQueryAsync(ct);
        await using var history = new NpgsqlCommand("INSERT INTO identity.runtime_variable_history(variable_key,category,previous_value,new_value,default_value,value_type,surface,changed_by,node_id,reason,raw_command) VALUES(@key,@category,@previous,@value,@default,@type,@surface,@by,@node,@reason,@raw) RETURNING change_id,changed_at_utc", db, tx);
        history.Parameters.AddWithValue("key", key);
        history.Parameters.AddWithValue("category", current.Category);
        history.Parameters.AddWithValue("previous", current.CurrentValue);
        history.Parameters.AddWithValue("value", value);
        history.Parameters.AddWithValue("default", current.DefaultValue);
        history.Parameters.AddWithValue("type", current.ValueType);
        history.Parameters.AddWithValue("surface", actor.Surface);
        history.Parameters.AddWithValue("by", actor.ChangedBy);
        history.Parameters.AddWithValue("node", actor.NodeId);
        history.Parameters.AddWithValue("reason", actor.Reason);
        history.Parameters.AddWithValue("raw", (object?)actor.RawCommand ?? DBNull.Value);
        long id; DateTimeOffset at;
        await using (var reader = await history.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            id = reader.GetInt64(0);
            at = reader.GetFieldValue<DateTimeOffset>(1);
        }
        await tx.CommitAsync(ct);
        var changed = current with { CurrentValue = value };
        await cache.SetStringAsync(CacheKey(key), JsonSerializer.Serialize(changed), CacheLifetime, ct);
        var entry = new RuntimeVariableChangeEntry(id, key, current.Category, current.CurrentValue,
            value, current.DefaultValue, current.ValueType, actor.Surface, actor.ChangedBy, actor.NodeId,
            actor.Reason, actor.RawCommand, at);
        await cache.SetStringAsync($"runtime-variable-history:v1:{id}", JsonSerializer.Serialize(entry), CacheLifetime, ct);
        return entry;
    }

    private static string Normalize(string value, string type) => type switch
    {
        "int" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && number is >= -1000000 and <= 1000000 => number.ToString(CultureInfo.InvariantCulture),
        "double" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number is >= -1000000 and <= 1000000 => number.ToString("R", CultureInfo.InvariantCulture),
        "string" when value.Length <= 4096 => value,
        _ => throw new ArgumentException("Invalid runtime variable value.", nameof(value))
    };

    private void ValidateRange(string key, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return;
        if (number < 0 && Catalog.Any(item => item.Key == key))
            throw new ArgumentOutOfRangeException(nameof(value), $"Out-of-range runtime variable: {key}");
        var valid = key switch
        {
            "mesh.desired_replicas" => number is >= 1 and <= 16,
            "mesh.max_maps_per_node" => number is >= 1 and <= 256,
            "mesh.heartbeat_ttl_seconds" => number > 2 * configuration.GetValue("Cluster:HeartbeatIntervalSeconds", 10)
                && number <= 3600,
            "world.collectibles.respawn_seconds" => number is >= 1 and <= 86400,
            "world.tick_rate_ms" => number is >= 20 and <= 60000,
            "world.spawn.streuner_count" or "world.spawn.lordakia_count" or "world.spawn.saimon_count"
                => number is >= 0 and <= 30,
            "world.spawn.bonus_box_count" => number is >= 0 and <= 1000,
            "world.combat.shield_absorption_ratio" => number is >= 0 and <= 1,
            "economy.shop_discount_percent" => number is >= 0 and <= 100,
            "world.movement.speed_multiplier" => number is > 0 and <= 100,
            _ => true
        };
        if (!valid) throw new ArgumentOutOfRangeException(nameof(value), $"Out-of-range runtime variable: {key}");
    }

    private static RuntimeVariableChangeEntry ReadChange(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
            r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetFieldValue<DateTimeOffset>(12));
}
