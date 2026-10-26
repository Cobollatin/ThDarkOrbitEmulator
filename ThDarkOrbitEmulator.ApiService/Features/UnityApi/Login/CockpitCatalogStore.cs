using Npgsql;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public sealed record ClientLanguage(string Code, string Name, bool Enabled, bool IsDefault);
public sealed record ClientServer(string Code, string InstanceName, string Host, int MapId, string Status, bool Enabled, bool IsDefault);
public sealed record CatalogShip(string ShipId, string Name, int ModelId, string IconPath, bool Enabled);
public sealed record HangarAssignment(Guid AccountId, int SlotNumber, string ShipId, string HangarId);
public sealed record LocaleOverride(string LanguageCode, string Category, string Key, string Value);

public sealed class CockpitCatalogStore(IConfiguration configuration)
{
    private NpgsqlConnection Connection() => new(configuration.GetConnectionString("identity-write")
        ?? throw new InvalidOperationException("identity-write connection is missing."));

    public async Task<IReadOnlyList<ClientLanguage>> LanguagesAsync(CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT code,name,enabled,is_default FROM identity.client_languages ORDER BY is_default DESC,code", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ClientLanguage>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        return result;
    }

    public async Task<IReadOnlyList<ClientServer>> ServersAsync(CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT code,instance_name,host,map_id,status,enabled,is_default FROM identity.client_servers ORDER BY is_default DESC,code", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ClientServer>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6)));
        return result;
    }

    public async Task<IReadOnlyList<CatalogShip>> ShipsAsync(CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT ship_id,name,model_id,icon_path,enabled FROM economy.ship_catalog ORDER BY ship_id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<CatalogShip>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetBoolean(4)));
        return result;
    }

    public async Task<IReadOnlyList<HangarAssignment>> AssignmentsAsync(Guid accountId, CancellationToken ct)
    {
        // Existing accounts predate the catalog migration. Materialize their two
        // starter slots when an admin opens the account, even before its next login.
        await using (var lookup = Connection())
        {
            await lookup.OpenAsync(ct);
            await using var account = new NpgsqlCommand("SELECT game_user_id,company FROM identity.accounts WHERE id=@account", lookup);
            account.Parameters.AddWithValue("account", accountId);
            await using var row = await account.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                var session = new GameAuthSession { AccountId = accountId, GameUserId = row.GetInt64(0),
                    Company = row.IsDBNull(1) ? null : row.GetString(1) };
                await row.CloseAsync();
                await HangarsAsync(session, LocalHangars.DefaultSlot, ct);
            }
        }
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT account_id,slot_number,ship_id,hangar_id FROM economy.account_hangar_ships WHERE account_id=@account ORDER BY slot_number", connection);
        command.Parameters.AddWithValue("account", accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<HangarAssignment>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public async Task<IReadOnlyList<UnityHangar>> HangarsAsync(GameAuthSession session, int activeSlot, CancellationToken ct)
    {
        var defaults = LocalHangars.For(session, activeSlot);
        if (session.AccountId == Guid.Empty) return defaults;
        await using var connection = Connection(); await connection.OpenAsync(ct);
        foreach (var item in defaults)
        {
            await using var seed = new NpgsqlCommand("INSERT INTO economy.account_hangar_ships(account_id,slot_number,ship_id,hangar_id) VALUES (@account,@slot,@ship,@hangar) ON CONFLICT DO NOTHING", connection);
            seed.Parameters.AddWithValue("account", session.AccountId);
            seed.Parameters.AddWithValue("slot", item.SlotNumber);
            seed.Parameters.AddWithValue("ship", item.ShipId);
            seed.Parameters.AddWithValue("hangar", item.HangarId);
            await seed.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand("SELECT h.slot_number,h.ship_id,s.name,s.model_id,s.icon_path,h.hangar_id FROM economy.account_hangar_ships h JOIN economy.ship_catalog s ON s.ship_id=h.ship_id WHERE h.account_id=@account ORDER BY h.slot_number", connection);
        command.Parameters.AddWithValue("account", session.AccountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<UnityHangar>();
        var map = defaults[0].MapId;
        while (await reader.ReadAsync(ct)) result.Add(new UnityHangar { SlotNumber = reader.GetInt32(0), ShipId = reader.GetString(1), ShipName = reader.GetString(2), ModelId = reader.GetInt32(3), TooltipIcon = reader.GetString(4), HangarId = reader.GetString(5), MapId = map, HangarActive = activeSlot == reader.GetInt32(0) });
        return result;
    }

    public async Task UpsertLanguageAsync(ClientLanguage value, CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (value.IsDefault) { await using var clear = new NpgsqlCommand("UPDATE identity.client_languages SET is_default=false", connection, transaction); await clear.ExecuteNonQueryAsync(ct); }
        await using var command = new NpgsqlCommand("INSERT INTO identity.client_languages(code,name,enabled,is_default) VALUES (@code,@name,@enabled,@default) ON CONFLICT(code) DO UPDATE SET name=EXCLUDED.name,enabled=EXCLUDED.enabled,is_default=EXCLUDED.is_default", connection, transaction);
        command.Parameters.AddWithValue("code", value.Code); command.Parameters.AddWithValue("name", value.Name); command.Parameters.AddWithValue("enabled", value.Enabled || value.IsDefault); command.Parameters.AddWithValue("default", value.IsDefault);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task UpsertServerAsync(ClientServer value, CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (value.IsDefault) { await using var clear = new NpgsqlCommand("UPDATE identity.client_servers SET is_default=false", connection, transaction); await clear.ExecuteNonQueryAsync(ct); }
        await using var command = new NpgsqlCommand("INSERT INTO identity.client_servers(code,instance_name,host,map_id,status,enabled,is_default) VALUES (@code,@name,@host,@map,@status,@enabled,@default) ON CONFLICT(code) DO UPDATE SET instance_name=EXCLUDED.instance_name,host=EXCLUDED.host,map_id=EXCLUDED.map_id,status=EXCLUDED.status,enabled=EXCLUDED.enabled,is_default=EXCLUDED.is_default", connection, transaction);
        command.Parameters.AddWithValue("code", value.Code); command.Parameters.AddWithValue("name", value.InstanceName); command.Parameters.AddWithValue("host", value.Host); command.Parameters.AddWithValue("map", value.MapId); command.Parameters.AddWithValue("status", value.Status); command.Parameters.AddWithValue("enabled", value.Enabled || value.IsDefault); command.Parameters.AddWithValue("default", value.IsDefault);
        await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }

    public async Task UpsertShipAsync(CatalogShip value, CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO economy.ship_catalog(ship_id,name,model_id,icon_path,enabled) VALUES (@id,@name,@model,@icon,@enabled) ON CONFLICT(ship_id) DO UPDATE SET name=EXCLUDED.name,model_id=EXCLUDED.model_id,icon_path=EXCLUDED.icon_path,enabled=EXCLUDED.enabled", connection);
        command.Parameters.AddWithValue("id", value.ShipId); command.Parameters.AddWithValue("name", value.Name); command.Parameters.AddWithValue("model", value.ModelId); command.Parameters.AddWithValue("icon", value.IconPath); command.Parameters.AddWithValue("enabled", value.Enabled);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAssignmentAsync(HangarAssignment value, CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO economy.account_hangar_ships(account_id,slot_number,ship_id,hangar_id) VALUES (@account,@slot,@ship,@hangar) ON CONFLICT(account_id,slot_number) DO UPDATE SET ship_id=EXCLUDED.ship_id,hangar_id=EXCLUDED.hangar_id", connection);
        command.Parameters.AddWithValue("account", value.AccountId); command.Parameters.AddWithValue("slot", value.SlotNumber); command.Parameters.AddWithValue("ship", value.ShipId); command.Parameters.AddWithValue("hangar", value.HangarId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LocaleOverride>> LocaleOverridesAsync(CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT language_code,category,key,value FROM identity.client_locale_overrides ORDER BY language_code,category,key", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<LocaleOverride>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public async Task UpsertLocaleOverrideAsync(LocaleOverride value, CancellationToken ct)
    {
        await using var connection = Connection(); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO identity.client_locale_overrides(language_code,category,key,value) VALUES (@language,@category,@key,@value) ON CONFLICT(language_code,category,key) DO UPDATE SET value=EXCLUDED.value", connection);
        command.Parameters.AddWithValue("language", value.LanguageCode); command.Parameters.AddWithValue("category", value.Category); command.Parameters.AddWithValue("key", value.Key); command.Parameters.AddWithValue("value", value.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
