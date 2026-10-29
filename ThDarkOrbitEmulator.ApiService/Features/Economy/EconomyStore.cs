using System.Text.Json;
using Npgsql;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.ApiService.Features.Economy;

public sealed record EconomyBalance(string AssetId, string Kind, long Amount);
public sealed record EconomyOwnedItem(string Id, string Name, string Type, long Quantity);
public sealed record EconomyListing(string Id, string ItemId, string Name, string Type, string Rarity,
    JsonElement Stats, string? AssetKey, JsonElement Prices, long? StockAvailable,
    long? PerAccountLimit, bool Enabled, int? RestockIntervalSeconds = null, long? RestockAmount = null);
public sealed record EconomyRecipe(string Id, string Name, JsonElement Inputs, JsonElement Outputs, int CraftSeconds, bool Enabled);
public sealed record EconomyAuctionLot(string Id, string ItemId, string Name, long OpeningBid,
    long? Bid, DateTimeOffset? EndsAt, bool Claimed, bool Enabled);
public sealed record EconomyRewardOffer(string Source, string Id, string Name, string AssetId,
    long Amount, string ProgressKind, long Target, bool Enabled);
public sealed record EconomyClaimState(IReadOnlySet<string> Keys, DateTimeOffset? ExpeditionStartedAt);
public sealed record EconomyLedgerRow(long Id, long Sequence, string AssetId, long Delta, long BalanceAfter,
    string Reason, string MenuSource, DateTimeOffset OccurredAt);
public enum EconomyPurchaseResult { Applied, Duplicate, Invalid, Unavailable, InsufficientFunds }
public enum EconomyCraftResult { Applied, Duplicate, Invalid, NotFound, Unavailable, InsufficientInputs }
public enum GalaxyGateChange { Applied, Invalid, Conflict, InsufficientEnergy }
public enum SkylabChange { Applied, Invalid, Conflict, InsufficientResources, MaxLevel, NothingToCollect }
public enum TechFactoryChange { Applied, Invalid, Conflict, InsufficientComponents }
public enum ClanChange { Applied, Invalid, Conflict }

public sealed partial class EconomyStore(IConfiguration configuration, IRuntimeVariableRegistry? variables = null)
{
    public async Task<string?> ClanMembershipAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT clan_id FROM economy.clan_memberships WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task ImportClanMembershipAsync(Guid accountId, string clanId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO economy.clan_memberships(account_id,clan_id) VALUES (@a,@c) ON CONFLICT DO NOTHING", db, tx);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("c", clanId);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var leader = new NpgsqlCommand("INSERT INTO economy.clan_leaders(clan_id,account_id) SELECT clan_id,account_id FROM economy.clan_memberships WHERE account_id=@a AND clan_id=@c ON CONFLICT DO NOTHING", db, tx);
        leader.Parameters.AddWithValue("c", clanId); leader.Parameters.AddWithValue("a", accountId);
        await leader.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ClanChange> ChangeClanAsync(Guid accountId, string? clanId, string? expectedClanId,
        string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 160)
            return ClanChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var replay = new NpgsqlCommand("SELECT 1 FROM economy.clan_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return ClanChange.Conflict;
        }
        string? current;
        await using (var read = new NpgsqlCommand("SELECT clan_id FROM economy.clan_memberships WHERE account_id=@a", db, tx))
        {
            read.Parameters.AddWithValue("a", accountId);
            current = await read.ExecuteScalarAsync(ct) as string;
        }
        if (current != expectedClanId || clanId == current || clanId is not null && current is not null)
            return ClanChange.Conflict;
        if (clanId is null)
        {
            await using var remove = new NpgsqlCommand("DELETE FROM economy.clan_memberships WHERE account_id=@a", db, tx);
            remove.Parameters.AddWithValue("a", accountId);
            await remove.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var insert = new NpgsqlCommand("INSERT INTO economy.clan_memberships(account_id,clan_id) VALUES (@a,@c)", db, tx);
            insert.Parameters.AddWithValue("a", accountId); insert.Parameters.AddWithValue("c", clanId);
            await insert.ExecuteNonQueryAsync(ct);
            await using var leader = new NpgsqlCommand("INSERT INTO economy.clan_leaders(clan_id,account_id) VALUES (@c,@a) ON CONFLICT DO NOTHING", db, tx);
            leader.Parameters.AddWithValue("c", clanId); leader.Parameters.AddWithValue("a", accountId);
            await leader.ExecuteNonQueryAsync(ct);
        }
        if (clanId is null && expectedClanId is not null)
        {
            await using var successor = new NpgsqlCommand("UPDATE economy.clan_leaders SET account_id=COALESCE((SELECT account_id FROM economy.clan_memberships WHERE clan_id=@c ORDER BY account_id LIMIT 1),@a) WHERE clan_id=@c AND account_id=@a", db, tx);
            successor.Parameters.AddWithValue("c", expectedClanId); successor.Parameters.AddWithValue("a", accountId);
            await successor.ExecuteNonQueryAsync(ct);
            await using var removeVacant = new NpgsqlCommand("DELETE FROM economy.clan_leaders WHERE clan_id=@c AND account_id=@a", db, tx);
            removeVacant.Parameters.AddWithValue("c", expectedClanId); removeVacant.Parameters.AddWithValue("a", accountId);
            await removeVacant.ExecuteNonQueryAsync(ct);
        }
        await using (var action = new NpgsqlCommand("INSERT INTO economy.clan_actions(account_id,action_key) VALUES (@a,@k)", db, tx))
        {
            action.Parameters.AddWithValue("a", accountId); action.Parameters.AddWithValue("k", key);
            await action.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ClanChange.Applied;
    }

    public async Task<LocalPilotSkillState?> PilotSkillsAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        return await ReadPilotSkillsAsync(db, null, accountId, ct);
    }

    private static async Task<LocalPilotSkillState?> ReadPilotSkillsAsync(NpgsqlConnection db, NpgsqlTransaction? tx,
        Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT ranks::text, revision FROM economy.pilot_skill_states WHERE account_id=@a", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        Dictionary<string, int>? ranks;
        try { ranks = JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(0)); }
        catch (JsonException) { ranks = null; }
        return LocalPilotSkillStore.Sanitize(new LocalPilotSkillState(ranks!, reader.GetInt32(1)));
    }

    // Copies a cache-era state once; an existing Postgres row always wins.
    public async Task ImportPilotSkillsAsync(Guid accountId, LocalPilotSkillState legacy, CancellationToken ct)
    {
        var state = LocalPilotSkillStore.Sanitize(legacy);
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.pilot_skill_states(account_id,ranks,revision) VALUES (@a,@r::jsonb,@v) ON CONFLICT DO NOTHING", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("r", JsonSerializer.Serialize(state.Ranks));
        cmd.Parameters.AddWithValue("v", state.Revision);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    // The account advisory lock serializes replicas; the form key is single-use per account.
    public async Task<PilotSkillChange> ChangePilotSkillsAsync(Guid accountId, int? expectedRevision, string? key,
        Func<LocalPilotSkillState, Dictionary<string, int>?> change, CancellationToken ct)
    {
        if (accountId == Guid.Empty || key is not null && (key.Length is 0 or > 160)) return PilotSkillChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        if (key is not null)
        {
            await using var replay = new NpgsqlCommand(
                "SELECT 1 FROM economy.pilot_skill_actions WHERE account_id=@a AND action_key=@k", db, tx);
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return PilotSkillChange.Conflict;
        }
        var state = await ReadPilotSkillsAsync(db, tx, accountId, ct) ?? new LocalPilotSkillState(new Dictionary<string, int>());
        if (expectedRevision is not null && expectedRevision != state.Revision) return PilotSkillChange.Conflict;
        if (change(state) is not { } ranks)
            return expectedRevision is null ? PilotSkillChange.Invalid : PilotSkillChange.Conflict;
        await using (var upsert = new NpgsqlCommand(
            "INSERT INTO economy.pilot_skill_states(account_id,ranks,revision) VALUES (@a,@r::jsonb,@v) " +
            "ON CONFLICT (account_id) DO UPDATE SET ranks=EXCLUDED.ranks, revision=EXCLUDED.revision", db, tx))
        {
            upsert.Parameters.AddWithValue("a", accountId);
            upsert.Parameters.AddWithValue("r", JsonSerializer.Serialize(ranks));
            upsert.Parameters.AddWithValue("v", state.Revision + 1);
            await upsert.ExecuteNonQueryAsync(ct);
        }
        if (key is not null)
        {
            await using var action = new NpgsqlCommand(
                "INSERT INTO economy.pilot_skill_actions(account_id,action_key) VALUES (@a,@k)", db, tx);
            action.Parameters.AddWithValue("a", accountId); action.Parameters.AddWithValue("k", key);
            await action.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return PilotSkillChange.Applied;
    }

    public async Task<LocalMessagingState?> MessagingAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        return await ReadMessagingAsync(db, null, accountId, ct);
    }

    private static async Task<LocalMessagingState?> ReadMessagingAsync(NpgsqlConnection db, NpgsqlTransaction? tx,
        Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT state::text, revision FROM economy.messaging_states WHERE account_id=@a", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        LocalMessagingState? stored;
        try { stored = JsonSerializer.Deserialize<LocalMessagingState>(reader.GetString(0)); }
        catch (JsonException) { stored = null; }
        return LocalMessagingStore.Sanitize(stored ?? new LocalMessagingState([], [])) with { Revision = reader.GetInt32(1) };
    }

    // Copies a cache-era mailbox once; an existing Postgres row always wins.
    public async Task ImportMessagingAsync(Guid accountId, LocalMessagingState legacy, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await WriteMessagingAsync(db, tx, accountId, LocalMessagingStore.Sanitize(legacy), " ON CONFLICT DO NOTHING", ct);
        await tx.CommitAsync(ct);
    }

    private static async Task WriteMessagingAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId,
        LocalMessagingState state, string conflict, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.messaging_states(account_id,state,revision) VALUES (@a,@s::jsonb,@v)" + conflict, db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("s", JsonSerializer.Serialize(state with { Revision = 0 }));
        cmd.Parameters.AddWithValue("v", state.Revision);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The account advisory lock serializes replicas; the form key is single-use per account.
    public async Task<MessagingChange> ChangeMessagingAsync(Guid accountId, int? expectedRevision, string? key,
        Func<LocalMessagingState> seed, Func<LocalMessagingState, (MessagingChange Result, LocalMessagingState? State)> change,
        CancellationToken ct, bool bumpRevision = true)
    {
        if (accountId == Guid.Empty || key is not null && (key.Length is 0 or > 160)) return MessagingChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        if (key is not null)
        {
            await using var replay = new NpgsqlCommand(
                "SELECT 1 FROM economy.messaging_actions WHERE account_id=@a AND action_key=@k", db, tx);
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return MessagingChange.Conflict;
        }
        var state = await ReadMessagingAsync(db, tx, accountId, ct) ?? seed();
        if (expectedRevision is not null && expectedRevision != state.Revision) return MessagingChange.Conflict;
        var (result, next) = change(state);
        if (result != MessagingChange.Applied || next is null) return result;
        await WriteMessagingAsync(db, tx, accountId, next with { Revision = state.Revision + (bumpRevision ? 1 : 0) },
            " ON CONFLICT (account_id) DO UPDATE SET state=EXCLUDED.state, revision=EXCLUDED.revision", ct);
        if (key is not null)
        {
            await using var action = new NpgsqlCommand(
                "INSERT INTO economy.messaging_actions(account_id,action_key) VALUES (@a,@k)", db, tx);
            action.Parameters.AddWithValue("a", accountId); action.Parameters.AddWithValue("k", key);
            await action.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return MessagingChange.Applied;
    }

    public async Task<(LocalAccountPreferences Preferences, int Revision)?> AccountPreferencesAsync(Guid accountId,
        CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        return await ReadAccountPreferencesAsync(db, null, accountId, ct);
    }

    private static async Task<(LocalAccountPreferences Preferences, int Revision)?> ReadAccountPreferencesAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT language, sound_enabled, notifications_enabled, revision FROM economy.account_preferences WHERE account_id=@a", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return LocalAccountPreferencesStore.Sanitize(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
            reader.GetInt32(3));
    }

    private static async Task WriteAccountPreferencesAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId,
        LocalAccountPreferences preferences, int revision, string conflict, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.account_preferences(account_id,language,sound_enabled,notifications_enabled,revision) VALUES (@a,@l,@s,@n,@v)" + conflict, db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("l", preferences.Language);
        cmd.Parameters.AddWithValue("s", preferences.SoundEnabled);
        cmd.Parameters.AddWithValue("n", preferences.NotificationsEnabled);
        cmd.Parameters.AddWithValue("v", revision);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Copies cache-era preferences once; an existing Postgres row always wins.
    public async Task ImportAccountPreferencesAsync(Guid accountId, LocalAccountPreferences legacy, int revision,
        CancellationToken ct)
    {
        var (preferences, clamped) = LocalAccountPreferencesStore.Sanitize(legacy.Language, legacy.SoundEnabled,
            legacy.NotificationsEnabled, revision);
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await WriteAccountPreferencesAsync(db, tx, accountId, preferences, clamped, " ON CONFLICT DO NOTHING", ct);
        await tx.CommitAsync(ct);
    }

    // The account advisory lock serializes replicas, so one revision is applied once across instances.
    // Saving the stored values again is Unchanged (no write, no revision bump). The returned revision is the one
    // this call wrote or found, so the save redirect never confirms a concurrent save.
    public async Task<(AccountPreferencesChange Change, int Revision)> ChangeAccountPreferencesAsync(Guid accountId,
        LocalAccountPreferences preferences, int? expectedRevision, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return (AccountPreferencesChange.Invalid, 0);
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        var (current, revision) = await ReadAccountPreferencesAsync(db, tx, accountId, ct)
            ?? (LocalAccountPreferencesStore.Default, 0);
        if (expectedRevision is not null && expectedRevision != revision) return (AccountPreferencesChange.Conflict, revision);
        if (current == preferences) return (AccountPreferencesChange.Unchanged, revision);
        var next = revision >= LocalAccountPreferencesStore.MaxRevision ? 1 : revision + 1;
        await WriteAccountPreferencesAsync(db, tx, accountId, preferences, next,
            " ON CONFLICT (account_id) DO UPDATE SET language=EXCLUDED.language, sound_enabled=EXCLUDED.sound_enabled, notifications_enabled=EXCLUDED.notifications_enabled, revision=EXCLUDED.revision", ct);
        await tx.CommitAsync(ct);
        return (AccountPreferencesChange.Applied, next);
    }

    public async Task<int?> HangarSlotAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        return await ReadHangarSlotAsync(db, null, accountId, ct);
    }

    private static async Task<int?> ReadHangarSlotAsync(NpgsqlConnection db, NpgsqlTransaction? tx, Guid accountId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT active_slot FROM economy.hangar_selections WHERE account_id=@a", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) is int slot && LocalHangars.IsValidSlot(slot) ? slot : null;
    }

    private static async Task WriteHangarSlotAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId, int slot,
        string conflict, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.hangar_selections(account_id,active_slot) VALUES (@a,@s)" + conflict, db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("s", slot);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Copies a cache-era active slot once; an existing Postgres row always wins.
    public async Task ImportHangarSlotAsync(Guid accountId, int slot, CancellationToken ct)
    {
        if (!LocalHangars.IsValidSlot(slot)) return;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await WriteHangarSlotAsync(db, tx, accountId, slot, " ON CONFLICT DO NOTHING", ct);
        await tx.CommitAsync(ct);
    }

    // Compare-and-set under the account advisory lock, so one Dock form applies once across replicas.
    public async Task<LocalHangarChange> ChangeHangarSlotAsync(Guid accountId, int slot, int? expectedCurrent,
        CancellationToken ct)
    {
        if (accountId == Guid.Empty || !LocalHangars.IsValidSlot(slot)
            || expectedCurrent is { } expected && (!LocalHangars.IsValidSlot(expected) || expected == slot))
            return LocalHangarChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        var active = await ReadHangarSlotAsync(db, tx, accountId, ct) ?? LocalHangars.DefaultSlot;
        if (active == slot || expectedCurrent is { } current && active != current) return LocalHangarChange.Conflict;
        await WriteHangarSlotAsync(db, tx, accountId, slot,
            " ON CONFLICT (account_id) DO UPDATE SET active_slot=EXCLUDED.active_slot", ct);
        await tx.CommitAsync(ct);
        return LocalHangarChange.Applied;
    }

    public async Task<IReadOnlyList<LocalEquipmentAssignment>> HangarEquipmentAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        return await ReadHangarEquipmentAsync(db, null, accountId, ct);
    }

    private static async Task<List<LocalEquipmentAssignment>> ReadHangarEquipmentAsync(NpgsqlConnection db,
        NpgsqlTransaction? tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT hangar_slot,configuration,item_id,quantity FROM economy.hangar_equipment_assignments WHERE account_id=@a ORDER BY hangar_slot,configuration,item_id",
            db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<LocalEquipmentAssignment>();
        while (await rows.ReadAsync(ct))
            result.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.GetString(2), rows.GetInt32(3)));
        return result;
    }

    private static JsonElement DiscountPrices(JsonElement prices, double percent)
    {
        if (percent <= 0 || prices.ValueKind != JsonValueKind.Object) return prices;
        var adjusted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var price in prices.EnumerateObject())
            if (price.Value.TryGetInt64(out var amount))
                adjusted[price.Name] = (long)Math.Round(amount * (100 - percent) / 100,
                    MidpointRounding.AwayFromZero);
        return JsonSerializer.SerializeToElement(adjusted);
    }

    // Returns false when the import marker already existed, i.e. the account's assignments live in Postgres.
    private static async Task<bool> MarkHangarEquipmentImportedAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.hangar_equipment_imports(account_id) VALUES (@a) ON CONFLICT DO NOTHING RETURNING account_id", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // Owned items, reconciled assignments and the replacement are read and written under the account lock.
    private static async Task WriteHangarEquipmentAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId,
        IReadOnlyList<LocalEquipmentAssignment> assignments, CancellationToken ct)
    {
        await using (var delete = new NpgsqlCommand("DELETE FROM economy.hangar_equipment_assignments WHERE account_id=@a", db, tx))
        {
            delete.Parameters.AddWithValue("a", accountId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var assignment in assignments)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO economy.hangar_equipment_assignments(account_id,hangar_slot,configuration,item_id,quantity) VALUES (@a,@s,@c,@i,@q)",
                db, tx);
            insert.Parameters.AddWithValue("a", accountId);
            insert.Parameters.AddWithValue("s", assignment.HangarSlot);
            insert.Parameters.AddWithValue("c", assignment.Configuration);
            insert.Parameters.AddWithValue("i", assignment.ItemId);
            insert.Parameters.AddWithValue("q", assignment.Quantity);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<LocalEquipmentItem>> ReadOwnedEquipmentAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT i.id,i.name,i.type,LEAST(v.quantity,2147483647)::integer FROM economy.inventory v JOIN economy.items i ON i.id=v.item_id WHERE v.account_id=@a AND v.quantity>0 ORDER BY i.id",
            db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<LocalEquipmentItem>();
        while (await rows.ReadAsync(ct))
            result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3), rows.GetString(2)));
        return result;
    }

    // Copies cache-era assignments once (capped by owned items); afterwards Postgres always wins.
    public async Task ImportHangarEquipmentAsync(Guid accountId, IReadOnlyList<LocalEquipmentAssignment> legacy,
        CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        if (await MarkHangarEquipmentImportedAsync(db, tx, accountId, ct))
        {
            var owned = await ReadOwnedEquipmentAsync(db, tx, accountId, ct);
            var valid = legacy.Where(a => a.Quantity <= LocalHangarEquipmentSelection.MaxQuantity).ToList();
            await WriteHangarEquipmentAsync(db, tx, accountId, LocalHangarEquipmentStore.ReconcileAssignments(owned, valid), ct);
        }
        await tx.CommitAsync(ct);
    }

    // Applies one Dock equipment form under the account advisory lock, so concurrent replicas cannot overbook an item.
    public async Task<LocalHangarChange> ChangeHangarEquipmentAsync(Guid accountId, int hangarSlot, int configuration,
        string itemId, int quantity, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalHangarChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        // A change without a prior import makes Postgres authoritative; a later cache copy is ignored.
        await MarkHangarEquipmentImportedAsync(db, tx, accountId, ct);
        var owned = await ReadOwnedEquipmentAsync(db, tx, accountId, ct);
        var current = LocalHangarEquipmentStore.ReconcileAssignments(owned,
            await ReadHangarEquipmentAsync(db, tx, accountId, ct));
        var (result, assignments) = LocalHangarEquipmentStore.ApplyAssignment(owned, current,
            hangarSlot, configuration, itemId, quantity);
        if (result != LocalHangarChange.Applied) return result;
        await WriteHangarEquipmentAsync(db, tx, accountId, assignments, ct);
        await tx.CommitAsync(ct);
        return LocalHangarChange.Applied;
    }

    public async Task<LocalHangarChange> TransferHangarEquipmentAsync(Guid accountId, int sourceSlot,
        int destinationSlot, int configuration, string itemId, int quantity, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalHangarChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await MarkHangarEquipmentImportedAsync(db, tx, accountId, ct);
        var owned = await ReadOwnedEquipmentAsync(db, tx, accountId, ct);
        var current = LocalHangarEquipmentStore.ReconcileAssignments(owned,
            await ReadHangarEquipmentAsync(db, tx, accountId, ct));
        var (result, assignments) = LocalHangarEquipmentStore.ApplyTransfer(owned, current,
            sourceSlot, destinationSlot, configuration, itemId, quantity);
        if (result != LocalHangarChange.Applied) return result;
        await WriteHangarEquipmentAsync(db, tx, accountId, assignments, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task ImportTechJobAsync(Guid accountId, LocalTechJob legacy, CancellationToken ct)
    {
        var recipeId = legacy.RecipeId.StartsWith("tech_", StringComparison.Ordinal)
            ? legacy.RecipeId : $"tech_{legacy.RecipeId}";
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var marker = new NpgsqlCommand(
            "INSERT INTO economy.tech_factory_legacy_imports(account_id) VALUES (@a) ON CONFLICT DO NOTHING RETURNING account_id", db, tx))
        {
            marker.Parameters.AddWithValue("a", accountId);
            if (await marker.ExecuteScalarAsync(ct) is null) { await tx.CommitAsync(ct); return; }
        }
        await using (var recipe = new NpgsqlCommand("SELECT outputs::text FROM economy.recipes WHERE id=@r", db, tx))
        {
            recipe.Parameters.AddWithValue("r", recipeId);
            var json = await recipe.ExecuteScalarAsync(ct) as string;
            if (json is not null)
            {
                using var output = JsonDocument.Parse(json);
                if (output.RootElement.GetArrayLength() == 1 &&
                    output.RootElement[0].GetProperty("kind").GetString() == "resource" &&
                    output.RootElement[0].GetProperty("quantity").TryGetInt64(out var quantity) && quantity > 0)
                {
                    await using var insert = new NpgsqlCommand("INSERT INTO economy.tech_factory_jobs(account_id,job_id,recipe_id,output_asset_id,output_quantity,ready_at) VALUES (@a,@j,@r,@o,@q,@t) ON CONFLICT (account_id) DO NOTHING", db, tx);
                    insert.Parameters.AddWithValue("a", accountId);
                    insert.Parameters.AddWithValue("j", legacy.Id == Guid.Empty ? Guid.NewGuid() : legacy.Id);
                    insert.Parameters.AddWithValue("r", recipeId);
                    insert.Parameters.AddWithValue("o", output.RootElement[0].GetProperty("id").GetString()!);
                    insert.Parameters.AddWithValue("q", quantity); insert.Parameters.AddWithValue("t", legacy.ReadyAt);
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
        }
        await tx.CommitAsync(ct);
    }

    public async Task<LocalTechJob?> TechJobAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT job_id,recipe_id,ready_at FROM economy.tech_factory_jobs WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var row = await cmd.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new LocalTechJob(row.GetString(1), row.GetFieldValue<DateTimeOffset>(2), row.GetGuid(0)) : null;
    }

    public async Task<bool> CanClaimTechJobAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT COALESCE(b.amount, 0)::numeric + j.output_quantity <= 9223372036854775807
            FROM economy.tech_factory_jobs j
            LEFT JOIN economy.balances b ON b.account_id=j.account_id AND b.asset_id=j.output_asset_id
            WHERE j.account_id=@a
            """, db);
        cmd.Parameters.AddWithValue("a", accountId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<TechFactoryChange> ChangeTechFactoryAsync(Guid accountId, string? recipeId,
        Guid expectedJobId, string key, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 160)
            return TechFactoryChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var replay = new NpgsqlCommand("SELECT 1 FROM economy.tech_factory_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return TechFactoryChange.Conflict;
        }
        if (recipeId is not null)
        {
            if (expectedJobId != Guid.Empty || !recipeId.StartsWith("tech_", StringComparison.Ordinal)) return TechFactoryChange.Invalid;
            await using (var active = new NpgsqlCommand("SELECT 1 FROM economy.tech_factory_jobs WHERE account_id=@a", db, tx))
            {
                active.Parameters.AddWithValue("a", accountId);
                if (await active.ExecuteScalarAsync(ct) is not null) return TechFactoryChange.Conflict;
            }
            long cost, outputQuantity; string outputId; int seconds;
            await using (var recipe = new NpgsqlCommand("SELECT inputs::text,outputs::text,craft_seconds FROM economy.recipes WHERE id=@id AND enabled FOR SHARE", db, tx))
            {
                recipe.Parameters.AddWithValue("id", recipeId);
                await using var row = await recipe.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct)) return TechFactoryChange.Invalid;
                using var input = JsonDocument.Parse(row.GetString(0));
                using var output = JsonDocument.Parse(row.GetString(1));
                if (input.RootElement.GetArrayLength() != 1 || output.RootElement.GetArrayLength() != 1 ||
                    input.RootElement[0].GetProperty("kind").GetString() != "resource" ||
                    input.RootElement[0].GetProperty("id").GetString() != "tech_components" ||
                    output.RootElement[0].GetProperty("kind").GetString() != "resource" ||
                    !input.RootElement[0].GetProperty("quantity").TryGetInt64(out cost) || cost <= 0 ||
                    !output.RootElement[0].GetProperty("quantity").TryGetInt64(out outputQuantity) || outputQuantity <= 0)
                    return TechFactoryChange.Invalid;
                outputId = output.RootElement[0].GetProperty("id").GetString()!;
                seconds = row.GetInt32(2);
            }
            if (!await ChangeBalanceAsync(db, tx, accountId, "tech_components", -cost,
                "tech_factory_start", "tech_factory", key, ct)) return TechFactoryChange.InsufficientComponents;
            await using var insert = new NpgsqlCommand("INSERT INTO economy.tech_factory_jobs(account_id,job_id,recipe_id,output_asset_id,output_quantity,ready_at) VALUES (@a,@j,@r,@o,@q,@t)", db, tx);
            insert.Parameters.AddWithValue("a", accountId); insert.Parameters.AddWithValue("j", Guid.NewGuid());
            insert.Parameters.AddWithValue("r", recipeId); insert.Parameters.AddWithValue("o", outputId);
            insert.Parameters.AddWithValue("q", outputQuantity); insert.Parameters.AddWithValue("t", now.AddSeconds(seconds));
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            string outputId; long outputQuantity;
            await using (var job = new NpgsqlCommand("SELECT output_asset_id,output_quantity,ready_at FROM economy.tech_factory_jobs WHERE account_id=@a AND job_id=@j", db, tx))
            {
                job.Parameters.AddWithValue("a", accountId); job.Parameters.AddWithValue("j", expectedJobId);
                await using var row = await job.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct) || row.GetFieldValue<DateTimeOffset>(2) > now) return TechFactoryChange.Conflict;
                outputId = row.GetString(0); outputQuantity = row.GetInt64(1);
            }
            if (!await ChangeBalanceAsync(db, tx, accountId, outputId, outputQuantity,
                "tech_factory_claim", "tech_factory", key, ct)) return TechFactoryChange.Conflict;
            await using var delete = new NpgsqlCommand("DELETE FROM economy.tech_factory_jobs WHERE account_id=@a", db, tx);
            delete.Parameters.AddWithValue("a", accountId); await delete.ExecuteNonQueryAsync(ct);
        }
        await using (var action = new NpgsqlCommand("INSERT INTO economy.tech_factory_actions(account_id,action_key) VALUES (@a,@k)", db, tx))
        {
            action.Parameters.AddWithValue("a", accountId); action.Parameters.AddWithValue("k", key);
            await action.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return TechFactoryChange.Applied;
    }
    private NpgsqlConnection Connection() => new(configuration.GetConnectionString("identity-write")
        ?? throw new InvalidOperationException("Missing connection string 'identity-write'."));

    public async Task ImportAsync(Guid accountId, LocalEquipmentState old, CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using var marker = new NpgsqlCommand("INSERT INTO economy.account_imports(account_id) VALUES (@a) ON CONFLICT DO NOTHING RETURNING account_id", db, tx);
        marker.Parameters.AddWithValue("a", accountId);
        if (await marker.ExecuteScalarAsync(ct) is null) { await tx.CommitAsync(ct); return; }
        await ChangeBalanceAsync(db, tx, accountId, "credits", old.Credits, "legacy_import", "hangar", "legacy:credits", ct);
        await ChangeBalanceAsync(db, tx, accountId, "assembly_components", old.AssemblyComponents, "legacy_import", "assembly", "legacy:assembly", ct);
        foreach (var item in old.Inventory.Where(i => i.Quantity > 0))
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO economy.inventory(account_id,item_id,quantity) VALUES (@a,@i,@q) ON CONFLICT (account_id,item_id) DO UPDATE SET quantity=EXCLUDED.quantity", db, tx);
            cmd.Parameters.AddWithValue("a", accountId);
            cmd.Parameters.AddWithValue("i", item.Id);
            cmd.Parameters.AddWithValue("q", (long)item.Quantity);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<EconomyBalance>> BalancesAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT a.id,a.kind,COALESCE(b.amount,0) FROM economy.assets a LEFT JOIN economy.balances b ON b.asset_id=a.id AND b.account_id=@a ORDER BY a.id", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyBalance>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    // Newest ledger rows for one account, returned oldest first, each with its stable 1-based per-account
    // sequence (the ledger is append-only). Logbook reads these; never writes.
    public async Task<IReadOnlyList<EconomyLedgerRow>> LedgerAsync(Guid accountId, int limit, CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id,seq,asset_id,delta,balance_after,reason,menu_source,occurred_at FROM (SELECT *,row_number() OVER (ORDER BY id) AS seq FROM economy.ledger WHERE account_id=@a) l ORDER BY id DESC LIMIT @l", db);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 1000));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyLedgerRow>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        result.Reverse();
        return result;
    }

    public async Task<IReadOnlyDictionary<string, long>> InventoryAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT item_id,quantity FROM economy.inventory WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, long>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0), reader.GetInt64(1));
        return result;
    }

    public async Task<IReadOnlyList<EconomyOwnedItem>> OwnedItemsAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT i.id,i.name,i.type,v.quantity FROM economy.inventory v JOIN economy.items i ON i.id=v.item_id WHERE v.account_id=@a ORDER BY i.id", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyOwnedItem>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetInt64(3)));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, long>> PurchasesAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT listing_id,quantity FROM economy.purchases WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, long>();
        while (await rows.ReadAsync(ct)) result.Add(rows.GetString(0), rows.GetInt64(1));
        return result;
    }

    public async Task<IReadOnlyList<EconomyAuctionLot>> AuctionLotsAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT l.id,l.item_id,i.name,l.opening_bid,b.bid,b.ends_at,COALESCE(b.claimed,false),l.enabled AND i.enabled FROM economy.auction_lots l JOIN economy.items i ON i.id=l.item_id LEFT JOIN economy.auction_bids b ON b.lot_id=l.id AND b.account_id=@a ORDER BY l.id", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyAuctionLot>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2),
            rows.GetInt64(3), rows.IsDBNull(4) ? null : rows.GetInt64(4), rows.IsDBNull(5) ? null : rows.GetFieldValue<DateTimeOffset>(5),
            rows.GetBoolean(6), rows.GetBoolean(7)));
        return result;
    }

    public async Task<IReadOnlyList<EconomyRewardOffer>> RewardOffersAsync(string source, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT source,id,name,asset_id,amount,progress_kind,target,enabled FROM economy.reward_offers WHERE source=@s ORDER BY id", db);
        cmd.Parameters.AddWithValue("s", source);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyRewardOffer>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2),
            rows.GetString(3), rows.GetInt64(4), rows.GetString(5), rows.GetInt64(6), rows.GetBoolean(7)));
        return result;
    }

    public async Task<EconomyClaimState> ClaimStateAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? expeditionStarted = null;
        await using (var ledger = new NpgsqlCommand("SELECT idempotency_key,occurred_at FROM economy.ledger WHERE account_id=@a AND (idempotency_key LIKE 'payment:%' OR idempotency_key LIKE 'voucher:%' OR idempotency_key LIKE 'quest:%' OR idempotency_key LIKE 'battlepass:%' OR idempotency_key='expedition:start')", db))
        {
            ledger.Parameters.AddWithValue("a", accountId);
            await using var rows = await ledger.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                var key = rows.GetString(0);
                keys.Add(key);
                if (key == "expedition:start") expeditionStarted = rows.GetFieldValue<DateTimeOffset>(1);
            }
        }
        await using (var grants = new NpgsqlCommand("SELECT idempotency_key FROM economy.item_grants WHERE account_id=@a", db))
        {
            grants.Parameters.AddWithValue("a", accountId);
            await using var rows = await grants.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) keys.Add(rows.GetString(0));
        }
        return new EconomyClaimState(keys, expeditionStarted);
    }

    public async Task<bool> BidAsync(Guid accountId, string lotId, long amount, DateTimeOffset now, CancellationToken ct) =>
        await BidResultAsync(accountId, lotId, amount, now, ct) == LocalAuctionResult.Applied;

    public async Task<LocalAuctionResult> BidResultAsync(Guid accountId, string lotId, long amount, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty || amount <= 0) return LocalAuctionResult.Rejected;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        long opening; int duration;
        await using (var lot = new NpgsqlCommand("SELECT l.opening_bid,l.duration_seconds,l.enabled AND i.enabled FROM economy.auction_lots l JOIN economy.items i ON i.id=l.item_id WHERE l.id=@l FOR SHARE OF l", db, tx))
        {
            lot.Parameters.AddWithValue("l", lotId);
            await using var row = await lot.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return LocalAuctionResult.UnknownLot;
            if (!row.GetBoolean(2)) return LocalAuctionResult.Unavailable;
            opening = row.GetInt64(0); duration = row.GetInt32(1);
        }
        long prior = 0; DateTimeOffset endsAt = now.AddSeconds(duration);
        await using (var bid = new NpgsqlCommand("SELECT bid,ends_at,claimed FROM economy.auction_bids WHERE account_id=@a AND lot_id=@l FOR UPDATE", db, tx))
        {
            bid.Parameters.AddWithValue("a", accountId); bid.Parameters.AddWithValue("l", lotId);
            await using var row = await bid.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
            {
                prior = row.GetInt64(0); endsAt = row.GetFieldValue<DateTimeOffset>(1);
                if (row.GetBoolean(2)) return LocalAuctionResult.AlreadyClaimed;
                if (now >= endsAt) return LocalAuctionResult.Ended;
            }
        }
        var minimum = prior == 0 ? opening : prior + Math.Max(1, prior / 10);
        if (amount < minimum) return LocalAuctionResult.BidTooLow;
        if (!await ChangeBalanceAsync(db, tx, accountId, "credits", -(amount - prior),
            "auction_bid", "auction", $"auction:{lotId}:{Guid.NewGuid():N}", ct)) return LocalAuctionResult.InsufficientCredits;
        await using (var update = new NpgsqlCommand("INSERT INTO economy.auction_bids(account_id,lot_id,bid,ends_at) VALUES (@a,@l,@b,@e) ON CONFLICT (account_id,lot_id) DO UPDATE SET bid=@b", db, tx))
        {
            update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("l", lotId);
            update.Parameters.AddWithValue("b", amount); update.Parameters.AddWithValue("e", endsAt);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return LocalAuctionResult.Applied;
    }

    public async Task<bool> ClaimAuctionAsync(Guid accountId, string lotId, DateTimeOffset now, CancellationToken ct) =>
        await ClaimAuctionResultAsync(accountId, lotId, now, ct) == LocalAuctionResult.Applied;

    public async Task<LocalAuctionResult> ClaimAuctionResultAsync(Guid accountId, string lotId, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalAuctionResult.Rejected;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        string itemId;
        await using (var lot = new NpgsqlCommand("SELECT 1 FROM economy.auction_lots WHERE id=@l", db, tx))
        {
            lot.Parameters.AddWithValue("l", lotId);
            if (await lot.ExecuteScalarAsync(ct) is null) return LocalAuctionResult.UnknownLot;
        }
        await using (var cmd = new NpgsqlCommand("SELECT l.item_id,b.bid,b.claimed,b.ends_at,l.enabled AND i.enabled FROM economy.auction_bids b JOIN economy.auction_lots l ON l.id=b.lot_id JOIN economy.items i ON i.id=l.item_id WHERE b.account_id=@a AND b.lot_id=@l FOR UPDATE OF b", db, tx))
        {
            cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("l", lotId);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.GetInt64(1) <= 0) return LocalAuctionResult.NoBid;
            if (row.GetBoolean(2)) return LocalAuctionResult.AlreadyClaimed;
            if (now < row.GetFieldValue<DateTimeOffset>(3)) return LocalAuctionResult.StillRunning;
            if (!row.GetBoolean(4)) return LocalAuctionResult.Unavailable;
            itemId = row.GetString(0);
        }
        await using (var grant = new NpgsqlCommand("INSERT INTO economy.inventory(account_id,item_id,quantity) VALUES (@a,@i,1) ON CONFLICT (account_id,item_id) DO UPDATE SET quantity=economy.inventory.quantity+1 WHERE economy.inventory.quantity<9223372036854775807 RETURNING quantity", db, tx))
        {
            grant.Parameters.AddWithValue("a", accountId); grant.Parameters.AddWithValue("i", itemId);
            if (await grant.ExecuteScalarAsync(ct) is null) return LocalAuctionResult.InventoryFull;
        }
        await using (var update = new NpgsqlCommand("UPDATE economy.auction_bids SET claimed=true WHERE account_id=@a AND lot_id=@l", db, tx))
        { update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("l", lotId); await update.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return LocalAuctionResult.Applied;
    }

    public async Task<bool> GrantItemAsync(Guid accountId, string itemId, long quantity,
        string key, long creditCost, string source, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(key)
            || quantity <= 0 || creditCost < 0) return false;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var enabled = new NpgsqlCommand("SELECT 1 FROM economy.items WHERE id=@i AND enabled", db, tx))
        {
            enabled.Parameters.AddWithValue("i", itemId);
            if (await enabled.ExecuteScalarAsync(ct) is null) return false;
        }
        await using (var marker = new NpgsqlCommand("INSERT INTO economy.item_grants(account_id,idempotency_key,item_id,quantity) VALUES (@a,@k,@i,@q) ON CONFLICT DO NOTHING RETURNING item_id", db, tx))
        {
            marker.Parameters.AddWithValue("a", accountId); marker.Parameters.AddWithValue("k", key);
            marker.Parameters.AddWithValue("i", itemId); marker.Parameters.AddWithValue("q", quantity);
            if (await marker.ExecuteScalarAsync(ct) is null) return false;
        }
        if (creditCost > 0 && !await ChangeBalanceAsync(db, tx, accountId, "credits", -creditCost,
            "item_grant_cost", source, key, ct)) return false;
        await using (var grant = new NpgsqlCommand("INSERT INTO economy.inventory(account_id,item_id,quantity) VALUES (@a,@i,@q) ON CONFLICT (account_id,item_id) DO UPDATE SET quantity=economy.inventory.quantity+EXCLUDED.quantity", db, tx))
        {
            grant.Parameters.AddWithValue("a", accountId); grant.Parameters.AddWithValue("i", itemId);
            grant.Parameters.AddWithValue("q", quantity); await grant.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ConsumeItemAsync(Guid accountId, string itemId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(itemId)) return false;
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE economy.inventory SET quantity=quantity-1
            WHERE account_id=@a AND item_id=@i AND quantity>0
            RETURNING quantity
            """, db);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("i", itemId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<EconomyRecipe>> RecipesAsync(CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id,name,inputs::text,outputs::text,craft_seconds,enabled FROM economy.recipes ORDER BY id", db);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyRecipe>();
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetString(1),
            JsonSerializer.Deserialize<JsonElement>(rows.GetString(2)), JsonSerializer.Deserialize<JsonElement>(rows.GetString(3)), rows.GetInt32(4), rows.GetBoolean(5)));
        return result;
    }

    public async Task<long> CraftCountAsync(Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT LEAST(COALESCE(sum(quantity),0), 9223372036854775807)::bigint FROM economy.crafts WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    // Menu-122: how often one account crafted one recipe; feeds the Assembly confirmation notice.
    public async Task<long> RecipeCraftCountAsync(Guid accountId, string recipeId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT COALESCE((SELECT quantity FROM economy.crafts WHERE account_id=@a AND recipe_id=@r),0)::bigint", db);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("r", recipeId);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<bool> CraftAsync(Guid accountId, string recipeId, CancellationToken ct) =>
        await CraftAsync(accountId, recipeId, $"craft:{Guid.NewGuid():N}", ct) == EconomyCraftResult.Applied;

    // The request key is stored in economy.craft_requests, so a replayed or double-submitted
    // form crafts at most once. Failed crafts roll back and leave the key unused.
    public async Task<EconomyCraftResult> CraftAsync(Guid accountId, string recipeId, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(recipeId) || string.IsNullOrWhiteSpace(key))
            return EconomyCraftResult.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        JsonElement inputs, outputs;
        await using (var cmd = new NpgsqlCommand("SELECT inputs::text,outputs::text,enabled FROM economy.recipes WHERE id=@id AND left(id,5)<>'tech_' FOR SHARE", db, tx))
        {
            cmd.Parameters.AddWithValue("id", recipeId);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return EconomyCraftResult.NotFound;
            if (!row.GetBoolean(2)) return EconomyCraftResult.Unavailable;
            inputs = JsonSerializer.Deserialize<JsonElement>(row.GetString(0));
            outputs = JsonSerializer.Deserialize<JsonElement>(row.GetString(1));
        }
        await using (var request = new NpgsqlCommand("INSERT INTO economy.craft_requests(account_id,idempotency_key,recipe_id) VALUES (@a,@k,@r) ON CONFLICT DO NOTHING RETURNING recipe_id", db, tx))
        {
            request.Parameters.AddWithValue("a", accountId); request.Parameters.AddWithValue("k", key); request.Parameters.AddWithValue("r", recipeId);
            if (await request.ExecuteScalarAsync(ct) is null) return EconomyCraftResult.Duplicate;
        }
        if (inputs.ValueKind != JsonValueKind.Array || outputs.ValueKind != JsonValueKind.Array || !outputs.EnumerateArray().Any()) return EconomyCraftResult.Unavailable;
        var ingredients = new List<(string Kind, string Id, long Quantity)>();
        foreach (var element in inputs.EnumerateArray().Concat(outputs.EnumerateArray()))
        {
            if (!element.TryGetProperty("kind", out var kind) || !element.TryGetProperty("id", out var id)
                || !element.TryGetProperty("quantity", out var quantity) || !quantity.TryGetInt64(out var value)
                || value <= 0 || kind.GetString() is not ("currency" or "resource" or "item")
                || string.IsNullOrWhiteSpace(id.GetString())) return EconomyCraftResult.Unavailable;
            ingredients.Add((kind.GetString()!, id.GetString()!, value));
        }
        var inputCount = inputs.GetArrayLength();
        for (var index = 0; index < ingredients.Count; index++)
        {
            var (kind, id, quantity) = ingredients[index];
            var delta = index < inputCount ? -quantity : quantity;
            var failure = delta < 0 ? EconomyCraftResult.InsufficientInputs : EconomyCraftResult.Unavailable;
            if (kind == "item")
            {
                await using var item = new NpgsqlCommand("SELECT 1 FROM economy.items WHERE id=@i AND enabled", db, tx);
                item.Parameters.AddWithValue("i", id);
                if (await item.ExecuteScalarAsync(ct) is null) return EconomyCraftResult.Unavailable;
                await using var change = new NpgsqlCommand(delta < 0
                    ? "UPDATE economy.inventory SET quantity=quantity+@d WHERE account_id=@a AND item_id=@i AND quantity+@d>=0 RETURNING quantity"
                    : "INSERT INTO economy.inventory(account_id,item_id,quantity) VALUES (@a,@i,@d) ON CONFLICT (account_id,item_id) DO UPDATE SET quantity=economy.inventory.quantity+@d WHERE economy.inventory.quantity::numeric+@d<=9223372036854775807 RETURNING quantity", db, tx);
                change.Parameters.AddWithValue("a", accountId); change.Parameters.AddWithValue("i", id); change.Parameters.AddWithValue("d", delta);
                if (await change.ExecuteScalarAsync(ct) is null) return failure;
            }
            else if (!await ChangeBalanceAsync(db, tx, accountId, id, delta, "recipe_craft", "assembly", $"{key}:{index}", ct)) return failure;
        }
        await using (var progress = new NpgsqlCommand("INSERT INTO economy.crafts(account_id,recipe_id,quantity) VALUES (@a,@r,1) ON CONFLICT (account_id,recipe_id) DO UPDATE SET quantity=economy.crafts.quantity+1 WHERE economy.crafts.quantity<9223372036854775807", db, tx))
        {
            progress.Parameters.AddWithValue("a", accountId); progress.Parameters.AddWithValue("r", recipeId);
            await progress.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return EconomyCraftResult.Applied;
    }

    public async Task ReconcileLegacyAsync(Guid accountId, long creditsDelta, long componentsDelta,
        IReadOnlyDictionary<string, long> inventoryDeltas, CancellationToken ct)
    {
        if (creditsDelta == 0 && componentsDelta == 0 && inventoryDeltas.Values.All(delta => delta == 0)) return;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        var key = $"legacy:{Guid.NewGuid():N}";
        if (creditsDelta != 0 && !await ChangeBalanceAsync(db, tx, accountId, "credits", creditsDelta, "legacy_menu_action", "menu", key, ct))
            throw new InvalidOperationException("Insufficient credits for legacy menu action.");
        if (componentsDelta != 0 && !await ChangeBalanceAsync(db, tx, accountId, "assembly_components", componentsDelta, "legacy_menu_action", "assembly", key, ct))
            throw new InvalidOperationException("Insufficient components for legacy menu action.");
        foreach (var (itemId, delta) in inventoryDeltas.Where(pair => pair.Value != 0))
        {
            await using var cmd = new NpgsqlCommand("UPDATE economy.inventory SET quantity=quantity+@d WHERE account_id=@a AND item_id=@i AND quantity+@d>=0 RETURNING quantity", db, tx);
            cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("i", itemId); cmd.Parameters.AddWithValue("d", delta);
            var after = await cmd.ExecuteScalarAsync(ct);
            if (after is null && delta > 0)
            {
                await using var insert = new NpgsqlCommand("INSERT INTO economy.inventory(account_id,item_id,quantity) VALUES (@a,@i,@d) ON CONFLICT DO NOTHING RETURNING quantity", db, tx);
                insert.Parameters.AddWithValue("a", accountId); insert.Parameters.AddWithValue("i", itemId); insert.Parameters.AddWithValue("d", delta);
                after = await insert.ExecuteScalarAsync(ct);
            }
            if (after is null) throw new InvalidOperationException("Insufficient inventory for legacy menu action.");
        }
        await tx.CommitAsync(ct);
    }

    public async Task ImportResourceAsync(Guid accountId, string assetId, long amount, string source, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using var marker = new NpgsqlCommand("INSERT INTO economy.resource_imports(account_id,asset_id) VALUES (@a,@s) ON CONFLICT DO NOTHING RETURNING asset_id", db, tx);
        marker.Parameters.AddWithValue("a", accountId); marker.Parameters.AddWithValue("s", assetId);
        if (await marker.ExecuteScalarAsync(ct) is not null && amount > 0)
            await ChangeBalanceAsync(db, tx, accountId, assetId, amount, "legacy_import", source, $"legacy:{assetId}", ct);
        await tx.CommitAsync(ct);
    }

    public async Task ReconcileResourcesAsync(Guid accountId, IReadOnlyDictionary<string, long> deltas, string source, CancellationToken ct)
    {
        if (deltas.Values.All(delta => delta == 0)) return;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        var key = $"legacy:{Guid.NewGuid():N}";
        foreach (var (assetId, delta) in deltas.Where(pair => pair.Value != 0))
            if (!await ChangeBalanceAsync(db, tx, accountId, assetId, delta, "legacy_menu_action", source, key, ct))
                throw new InvalidOperationException($"Insufficient {assetId} for legacy menu action.");
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<(string Id, int Level, DateTimeOffset LastCollected)>> SkylabProgressAsync(
        Guid accountId, LocalSkylabState legacy, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        foreach (var collector in LocalSkylab.Catalog)
        {
            var old = legacy.Collectors.FirstOrDefault(c => c.Id == collector.Id);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO economy.skylab_collectors(account_id,collector_id,level,last_collected)
                VALUES (@a,@id,@level,@time) ON CONFLICT DO NOTHING
                """, db, tx);
            insert.Parameters.AddWithValue("a", accountId); insert.Parameters.AddWithValue("id", collector.Id);
            insert.Parameters.AddWithValue("level", Math.Clamp(old?.Level ?? 1, 1, 10));
            insert.Parameters.AddWithValue("time", legacy.LastCollected);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var result = new List<(string, int, DateTimeOffset)>();
        await using (var select = new NpgsqlCommand(
            "SELECT collector_id,level,last_collected FROM economy.skylab_collectors WHERE account_id=@a ORDER BY collector_id", db, tx))
        {
            select.Parameters.AddWithValue("a", accountId);
            await using var rows = await select.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) result.Add((rows.GetString(0), rows.GetInt32(1), rows.GetFieldValue<DateTimeOffset>(2)));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<SkylabChange> ChangeSkylabAsync(Guid accountId, string? collectorId,
        int expectedLevel, DateTimeOffset expectedTime, DateTimeOffset now, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(key) ||
            (collectorId is not null && !LocalSkylab.Catalog.Any(c => c.Id == collectorId))) return SkylabChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var replay = new NpgsqlCommand(
            "SELECT 1 FROM economy.skylab_actions WHERE account_id=@a AND action_key=@k", db, tx))
        {
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return SkylabChange.Conflict;
        }
        var progress = new List<(string Id, int Level, DateTimeOffset Time)>();
        await using (var select = new NpgsqlCommand(
            "SELECT collector_id,level,last_collected FROM economy.skylab_collectors WHERE account_id=@a ORDER BY collector_id", db, tx))
        {
            select.Parameters.AddWithValue("a", accountId);
            await using var rows = await select.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct)) progress.Add((rows.GetString(0), rows.GetInt32(1), rows.GetFieldValue<DateTimeOffset>(2)));
        }
        if (collectorId is not null)
        {
            var current = progress.FirstOrDefault(p => p.Id == collectorId);
            if (current.Id is null || current.Level != expectedLevel) return SkylabChange.Conflict;
            if (await PendingCollectorUpgradeAsync(db, tx, accountId, collectorId, ct)) return SkylabChange.Conflict;
            if (current.Level >= 10) return SkylabChange.MaxLevel;
            if (!await ChangeBalanceAsync(db, tx, accountId, collectorId, -100L * current.Level,
                "skylab_upgrade", "skylab", key, ct)) return SkylabChange.InsufficientResources;
            await using var update = new NpgsqlCommand(
                "UPDATE economy.skylab_collectors SET level=level+1 WHERE account_id=@a AND collector_id=@id", db, tx);
            update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("id", collectorId);
            await update.ExecuteNonQueryAsync(ct);
        }
        else
        {
            if (progress.Count != LocalSkylab.Catalog.Count || progress.Any(p => p.Time != expectedTime)) return SkylabChange.Conflict;
            var elapsedMinutes = LocalSkylab.ElapsedMinutes(expectedTime, now);
            var minutes = Math.Min(LocalSkylab.MaxCollectMinutes, elapsedMinutes);
            if (minutes < 1) return SkylabChange.NothingToCollect;
            foreach (var current in progress)
                if (!await ChangeBalanceAsync(db, tx, accountId, current.Id, minutes * current.Level * 10,
                    "skylab_collect", "skylab", $"{key}:{current.Id}", ct)) return SkylabChange.Conflict;
            await using var update = new NpgsqlCommand(
                "UPDATE economy.skylab_collectors SET last_collected=last_collected+@elapsed_minutes * interval '1 minute' WHERE account_id=@a", db, tx);
            update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("elapsed_minutes", elapsedMinutes);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO economy.skylab_actions(account_id,action_key) VALUES (@a,@k)", db, tx))
        {
            insert.Parameters.AddWithValue("a", accountId); insert.Parameters.AddWithValue("k", key);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return SkylabChange.Applied;
    }

    public async Task<IReadOnlyDictionary<string, (int Parts, bool Activated)>> GalaxyGateProgressAsync(
        Guid accountId, CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT gate_id,parts_owned,activated FROM economy.galaxy_gates WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", accountId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        while (await rows.ReadAsync(ct)) result.Add(rows.GetString(0), (rows.GetInt32(1), rows.GetBoolean(2)));
        return result;
    }

    public async Task ImportGalaxyGateProgressAsync(Guid accountId, IEnumerable<LocalGalaxyGate> legacy,
        CancellationToken ct)
    {
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        foreach (var gate in legacy.Where(g => g.PartsOwned > 0 || g.Activated))
        {
            var definition = LocalGalaxyGates.Catalog.SingleOrDefault(g => g.Id == gate.Id);
            if (definition is null) continue;
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO economy.galaxy_gates(account_id,gate_id,parts_owned,activated)
                VALUES (@a,@g,@p,@active) ON CONFLICT DO NOTHING
                """, db, tx);
            cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("g", gate.Id);
            cmd.Parameters.AddWithValue("p", Math.Clamp(gate.PartsOwned, 0, definition.PartsRequired));
            cmd.Parameters.AddWithValue("active", gate.Activated && gate.PartsOwned >= definition.PartsRequired);
            await cmd.ExecuteNonQueryAsync(ct);
            if (gate.Activated && gate.PartsOwned >= definition.PartsRequired)
            {
                await using var run = new NpgsqlCommand("INSERT INTO economy.gate_runs(account_id,gate_id,wave,total_waves) VALUES (@a,@g,0,@total) ON CONFLICT DO NOTHING", db, tx);
                run.Parameters.AddWithValue("a", accountId); run.Parameters.AddWithValue("g", gate.Id);
                run.Parameters.AddWithValue("total", GalaxyGateWaveCount);
                await run.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    public async Task<GalaxyGateChange> ChangeGalaxyGateAsync(Guid accountId, string gateId,
        int amount, string key, CancellationToken ct)
    {
        var definition = LocalGalaxyGates.Catalog.SingleOrDefault(g => g.Id == gateId);
        if (accountId == Guid.Empty || definition is null || amount is < 0 or > 10 || string.IsNullOrWhiteSpace(key))
            return GalaxyGateChange.Invalid;
        await using var db = Connection(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using var select = new NpgsqlCommand(
            "SELECT parts_owned,activated FROM economy.galaxy_gates WHERE account_id=@a AND gate_id=@g", db, tx);
        select.Parameters.AddWithValue("a", accountId); select.Parameters.AddWithValue("g", gateId);
        var parts = 0; var activated = false;
        await using (var rows = await select.ExecuteReaderAsync(ct))
            if (await rows.ReadAsync(ct)) { parts = rows.GetInt32(0); activated = rows.GetBoolean(1); }
        if (activated || (amount == 0 ? parts != definition.PartsRequired :
            amount > definition.PartsRequired - parts)) return GalaxyGateChange.Conflict;
        if (amount > 0)
        {
            await using var replay = new NpgsqlCommand(
                "SELECT 1 FROM economy.ledger WHERE account_id=@a AND asset_id='galaxy_energy' AND idempotency_key=@k", db, tx);
            replay.Parameters.AddWithValue("a", accountId); replay.Parameters.AddWithValue("k", key);
            if (await replay.ExecuteScalarAsync(ct) is not null) return GalaxyGateChange.Conflict;
            if (!await ChangeBalanceAsync(db, tx, accountId, "galaxy_energy", -amount,
                "gate_spin", "galaxy_gates", key, ct)) return GalaxyGateChange.InsufficientEnergy;
        }
        await using var update = new NpgsqlCommand("""
            INSERT INTO economy.galaxy_gates(account_id,gate_id,parts_owned,activated)
            VALUES (@a,@g,@p,@active)
            ON CONFLICT (account_id,gate_id) DO UPDATE SET
              parts_owned=EXCLUDED.parts_owned,activated=EXCLUDED.activated
            """, db, tx);
        update.Parameters.AddWithValue("a", accountId); update.Parameters.AddWithValue("g", gateId);
        update.Parameters.AddWithValue("p", parts + amount); update.Parameters.AddWithValue("active", amount == 0);
        await update.ExecuteNonQueryAsync(ct);
        if (amount == 0)
        {
            await using var wave = new NpgsqlCommand("INSERT INTO economy.gate_runs(account_id,gate_id,wave,total_waves) VALUES (@a,@g,0,@total)", db, tx);
            wave.Parameters.AddWithValue("a", accountId); wave.Parameters.AddWithValue("g", gateId);
            wave.Parameters.AddWithValue("total", GalaxyGateWaveCount);
            await wave.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return GalaxyGateChange.Applied;
    }

    public async Task<IReadOnlyList<EconomyListing>> ListingsAsync(CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await RestockDueAsync(db, ct);
        await using var cmd = new NpgsqlCommand("SELECT l.id,l.item_id,i.name,i.type,i.rarity,i.stats::text,i.asset_key,l.prices::text,l.stock_available,l.per_account_limit,l.restock_interval_seconds,l.restock_amount,l.enabled AND i.enabled FROM economy.listings l JOIN economy.items i ON i.id=l.item_id ORDER BY l.id", db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<EconomyListing>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.GetBoolean(12),
            reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetInt64(11)));
        if (variables is null) return result;
        var discount = await variables.NumberAsync("economy.shop_discount_percent", ct);
        return result.Select(item => item with { Prices = DiscountPrices(item.Prices, discount) }).ToArray();
    }

    private static async Task RestockDueAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            WITH due AS (
              SELECT id, LEAST(restock_amount, 9223372036854775807 - stock_available) AS added
              FROM economy.listings
              WHERE enabled AND stock_available IS NOT NULL AND stock_available < 9223372036854775807
                AND restock_interval_seconds IS NOT NULL
                AND last_restock_at + make_interval(secs => restock_interval_seconds) <= now()
              FOR UPDATE
            ), changed AS (
              UPDATE economy.listings AS listing SET stock_available=listing.stock_available+due.added,
                last_restock_at=now()
              FROM due WHERE listing.id=due.id
              RETURNING listing.id,listing.stock_available,due.added)
            INSERT INTO economy.stock_audit(listing_id,delta,stock_after,reason)
            SELECT id,added,stock_available,'scheduled_restock' FROM changed
            """, db);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> AdjustAsync(Guid accountId, string assetId, long delta, string reason, string menuSource, string key, CancellationToken ct, string? adminActorSubject = null)
    {
        if (accountId == Guid.Empty || delta == 0 || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(menuSource) || string.IsNullOrWhiteSpace(key)) return false;
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var existing = new NpgsqlCommand("SELECT 1 FROM economy.ledger WHERE account_id=@a AND idempotency_key=@k", db, tx))
        {
            existing.Parameters.AddWithValue("a", accountId);
            existing.Parameters.AddWithValue("k", key);
            if (await existing.ExecuteScalarAsync(ct) is not null) return false;
        }
        var applied = await ChangeBalanceAsync(db, tx, accountId, assetId, delta, reason, menuSource, key, ct, adminActorSubject);
        if (applied) await tx.CommitAsync(ct);
        return applied;
    }

    public async Task<WorldRepairPayment> ChargeRepairAsync(Guid accountId, int price, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || price <= 0 || string.IsNullOrWhiteSpace(key))
            return new(WorldRepairCharge.InsufficientFunds, 0);
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var existing = new NpgsqlCommand(
            "SELECT delta,balance_after FROM economy.ledger WHERE account_id=@a AND idempotency_key=@k AND reason='world_repair' AND asset_id='uridium'", db, tx))
        {
            existing.Parameters.AddWithValue("a", accountId);
            existing.Parameters.AddWithValue("k", key);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var matches = reader.GetInt64(0) == -price;
                await reader.DisposeAsync();
                return matches ? new(WorldRepairCharge.AlreadyCharged,
                    await CurrentBalanceAsync(db, tx, accountId, "uridium", ct))
                    : new(WorldRepairCharge.InsufficientFunds, 0);
            }
        }
        if (!await ChangeBalanceAsync(db, tx, accountId, "uridium", -price,
            "world_repair", "world", key, ct)) return new(WorldRepairCharge.InsufficientFunds, 0);
        var balance = await CurrentBalanceAsync(db, tx, accountId, "uridium", ct);
        await tx.CommitAsync(ct);
        return new(WorldRepairCharge.Applied, balance);
    }

    private static async Task<long> CurrentBalanceAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid accountId, string assetId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT amount FROM economy.balances WHERE account_id=@a AND asset_id=@s", db, tx);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("s", assetId);
        return (long?)await cmd.ExecuteScalarAsync(ct) ?? 0;
    }

    public async Task<int> NpcKillsAsync(Guid accountId, CancellationToken ct, string? kind = null)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM economy.ledger WHERE account_id=@a AND reason='npc_destroyed' AND asset_id='credits' AND (@kind IS NULL OR menu_source=@kind)", db);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.Add("kind", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)kind ?? DBNull.Value;
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L));
    }

    public async Task<EconomyPurchaseResult> PurchaseAsync(Guid accountId, string listingId, int quantity, string key, CancellationToken ct)
        => await PurchaseAsync(accountId, listingId, quantity, "credits", key, ct);

    public async Task<EconomyPurchaseResult> PurchaseAsync(Guid accountId, string listingId, int quantity, string currencyId, string key, CancellationToken ct)
    {
        if (accountId == Guid.Empty || quantity is < 1 or > 100 || string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(currencyId)) return EconomyPurchaseResult.Invalid;
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await LockAccountAsync(db, tx, accountId, ct);
        await using (var prior = new NpgsqlCommand("SELECT listing_id,quantity,currency_id FROM economy.purchase_requests WHERE account_id=@a AND idempotency_key=@k", db, tx))
        {
            prior.Parameters.AddWithValue("a", accountId); prior.Parameters.AddWithValue("k", key);
            await using var row = await prior.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct)) return row.GetString(0) == listingId && row.GetInt64(1) == quantity && row.GetString(2) == currencyId ? EconomyPurchaseResult.Duplicate : EconomyPurchaseResult.Invalid;
        }
        await using (var restock = new NpgsqlCommand("""
            WITH due AS (
              SELECT id, LEAST(restock_amount, 9223372036854775807 - stock_available) AS added
              FROM economy.listings
              WHERE id=@id AND stock_available IS NOT NULL AND stock_available < 9223372036854775807
                AND restock_interval_seconds IS NOT NULL
                AND last_restock_at + make_interval(secs => restock_interval_seconds) <= now()
              FOR UPDATE
            ), changed AS (
              UPDATE economy.listings AS listing SET stock_available=listing.stock_available+due.added,
                last_restock_at=now()
              FROM due WHERE listing.id=due.id
              RETURNING listing.id,listing.stock_available,due.added)
            INSERT INTO economy.stock_audit(listing_id,delta,stock_after,reason)
            SELECT id,added,stock_available,'scheduled_restock' FROM changed
            """, db, tx))
        { restock.Parameters.AddWithValue("id", listingId); await restock.ExecuteNonQueryAsync(ct); }
        string itemId;
        long? stock, limit;
        JsonElement prices;
        await using (var listing = new NpgsqlCommand("SELECT l.item_id,l.prices::text,l.stock_available,l.per_account_limit FROM economy.listings l JOIN economy.items i ON i.id=l.item_id WHERE l.id=@id AND l.enabled AND i.enabled FOR UPDATE OF l", db, tx))
        {
            listing.Parameters.AddWithValue("id", listingId);
            await using var row = await listing.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) return EconomyPurchaseResult.Unavailable;
            itemId = row.GetString(0); prices = JsonSerializer.Deserialize<JsonElement>(row.GetString(1));
            stock = row.IsDBNull(2) ? null : row.GetInt64(2); limit = row.IsDBNull(3) ? null : row.GetInt64(3);
        }
        if (stock < quantity) return EconomyPurchaseResult.Unavailable;
        await using (var count = new NpgsqlCommand("SELECT COALESCE(quantity,0) FROM economy.purchases WHERE account_id=@a AND listing_id=@l", db, tx))
        {
            count.Parameters.AddWithValue("a", accountId); count.Parameters.AddWithValue("l", listingId);
            var bought = (long)(await count.ExecuteScalarAsync(ct) ?? 0L);
            if (bought > long.MaxValue - quantity || limit is not null && bought + quantity > limit)
                return EconomyPurchaseResult.Unavailable;
        }
        await using (var ownedQuery = new NpgsqlCommand("SELECT quantity FROM economy.inventory WHERE account_id=@a AND item_id=@i", db, tx))
        {
            ownedQuery.Parameters.AddWithValue("a", accountId); ownedQuery.Parameters.AddWithValue("i", itemId);
            var owned = (long)(await ownedQuery.ExecuteScalarAsync(ct) ?? 0L);
            if (owned > long.MaxValue - quantity) return EconomyPurchaseResult.Unavailable;
        }
        if (prices.ValueKind != JsonValueKind.Object || !prices.TryGetProperty(currencyId, out var selectedPrice)) return EconomyPurchaseResult.Invalid;
        var currencyIds = new[] { currencyId };
        await using (var currencyCheck = new NpgsqlCommand("SELECT count(*) FROM economy.assets WHERE id=ANY(@ids) AND kind='currency' AND enabled", db, tx))
        {
            currencyCheck.Parameters.AddWithValue("ids", currencyIds);
            if ((long)(await currencyCheck.ExecuteScalarAsync(ct) ?? 0L) != currencyIds.Length)
                return EconomyPurchaseResult.Invalid;
        }
        if (!selectedPrice.TryGetInt64(out var unit) || unit < 0) return EconomyPurchaseResult.Invalid;
        if (variables is not null)
        {
            var discount = await variables.NumberAsync("economy.shop_discount_percent", ct);
            unit = (long)Math.Round(unit * (100 - discount) / 100, MidpointRounding.AwayFromZero);
        }
        long cost;
        try { cost = checked(unit * quantity); } catch (OverflowException) { return EconomyPurchaseResult.Invalid; }
        if (cost > 0 && !await ChangeBalanceAsync(db, tx, accountId, currencyId, -cost, "shop_purchase", "shop", key, ct))
            return EconomyPurchaseResult.InsufficientFunds;
        await using (var cmd = new NpgsqlCommand("INSERT INTO economy.inventory(account_id,item_id,quantity) VALUES (@a,@i,@q) ON CONFLICT (account_id,item_id) DO UPDATE SET quantity=economy.inventory.quantity+EXCLUDED.quantity", db, tx))
        { cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("i", itemId); cmd.Parameters.AddWithValue("q", (long)quantity); await cmd.ExecuteNonQueryAsync(ct); }
        await using (var cmd = new NpgsqlCommand("INSERT INTO economy.purchases(account_id,listing_id,quantity) VALUES (@a,@l,@q) ON CONFLICT (account_id,listing_id) DO UPDATE SET quantity=economy.purchases.quantity+EXCLUDED.quantity", db, tx))
        { cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("l", listingId); cmd.Parameters.AddWithValue("q", (long)quantity); await cmd.ExecuteNonQueryAsync(ct); }
        if (stock is not null)
        {
            await using var cmd = new NpgsqlCommand("UPDATE economy.listings SET stock_available=stock_available-@q WHERE id=@l; INSERT INTO economy.stock_audit(listing_id,delta,stock_after,reason,actor_account_id) VALUES (@l,-@q,@after,'purchase',@a)", db, tx);
            cmd.Parameters.AddWithValue("q", (long)quantity); cmd.Parameters.AddWithValue("l", listingId);
            cmd.Parameters.AddWithValue("after", stock.Value - quantity); cmd.Parameters.AddWithValue("a", accountId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("INSERT INTO economy.purchase_requests(account_id,idempotency_key,listing_id,quantity,currency_id) VALUES (@a,@k,@l,@q,@c)", db, tx))
        { cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("k", key); cmd.Parameters.AddWithValue("l", listingId); cmd.Parameters.AddWithValue("q", (long)quantity); cmd.Parameters.AddWithValue("c", currencyId); await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return EconomyPurchaseResult.Applied;
    }

    private static async Task LockAccountAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@a,0))", db, tx);
        cmd.Parameters.AddWithValue("a", accountId.ToString("N"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ChangeBalanceAsync(NpgsqlConnection db, NpgsqlTransaction tx, Guid accountId,
        string assetId, long delta, string reason, string source, string key, CancellationToken ct, string? adminActorSubject = null)
    {
        await using var cmd = new NpgsqlCommand("UPDATE economy.balances SET amount=amount+@d WHERE account_id=@a AND asset_id=@s AND amount::numeric+@d>=0 AND amount::numeric+@d<=9223372036854775807 RETURNING amount", db, tx);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("s", assetId); cmd.Parameters.AddWithValue("d", delta);
        var after = await cmd.ExecuteScalarAsync(ct);
        if (after is null && delta > 0)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO economy.balances(account_id,asset_id,amount) VALUES (@a,@s,@d) ON CONFLICT DO NOTHING RETURNING amount", db, tx);
            insert.Parameters.AddWithValue("a", accountId); insert.Parameters.AddWithValue("s", assetId); insert.Parameters.AddWithValue("d", delta);
            after = await insert.ExecuteScalarAsync(ct);
        }
        if (after is null) return false;
        await using var ledger = new NpgsqlCommand("INSERT INTO economy.ledger(account_id,asset_id,delta,balance_after,reason,menu_source,idempotency_key,admin_actor_subject) VALUES (@a,@s,@d,@b,@r,@m,@k,@actor)", db, tx);
        ledger.Parameters.AddWithValue("a", accountId); ledger.Parameters.AddWithValue("s", assetId); ledger.Parameters.AddWithValue("d", delta);
        ledger.Parameters.AddWithValue("b", (long)after); ledger.Parameters.AddWithValue("r", reason); ledger.Parameters.AddWithValue("m", source); ledger.Parameters.AddWithValue("k", key);
        ledger.Parameters.AddWithValue("actor", (object?)adminActorSubject ?? DBNull.Value);
        await ledger.ExecuteNonQueryAsync(ct);
        return true;
    }
}
