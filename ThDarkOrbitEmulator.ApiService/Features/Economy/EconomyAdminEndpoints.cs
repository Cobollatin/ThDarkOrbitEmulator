using Npgsql;
using System.Text.Json;
using System.Security.Claims;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Economy;

public sealed record AssetInput(string Id, string Kind, string Name, bool Enabled);
public sealed record ItemInput(string Id, string Name, string Type, string Rarity, string StatsJson, string? AssetKey, bool Enabled);
public sealed record RecipeInput(string Id, string Name, string InputsJson, string OutputsJson, int CraftSeconds, bool Enabled);
public sealed record ListingInput(string Id, string ItemId, string PricesJson, long? StockAvailable, long? PerAccountLimit,
    int? RestockIntervalSeconds, long? RestockAmount, bool Enabled);
public sealed record StockInput(long Delta, string Reason);
public sealed record BalanceInput(long Delta, string Reason, string MenuSource, string IdempotencyKey);
public sealed record AuctionLotInput(string Id, string ItemId, long OpeningBid, int DurationSeconds, bool Enabled);
public sealed record RewardOfferInput(string Source, string Id, string Name, string AssetId,
    long Amount, string ProgressKind, long Target, bool Enabled);

public static class EconomyAdminEndpoints
{
    public static void MapEconomyAdmin(this WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/economy").RequireAuthorization(EmulatorRoles.Admin).WithTags("Economy administration");
        routes.MapGet("/assets", async (IConfiguration config, CancellationToken ct) =>
            await QueryAsync(config, "SELECT id,kind,name,enabled FROM economy.assets ORDER BY id", ct)).WithSummary("List currencies and resources");
        routes.MapGet("/items", async (IConfiguration config, CancellationToken ct) =>
            await QueryAsync(config, "SELECT id,name,type,rarity,stats::text AS stats,asset_key,enabled FROM economy.items ORDER BY id", ct)).WithSummary("List economy items");
        routes.MapGet("/recipes", async (IConfiguration config, CancellationToken ct) =>
            await QueryAsync(config, "SELECT id,name,inputs::text AS inputs,outputs::text AS outputs,craft_seconds,enabled FROM economy.recipes ORDER BY id", ct)).WithSummary("List crafting recipes");
        routes.MapGet("/listings", (EconomyStore store, CancellationToken ct) => store.ListingsAsync(ct)).WithSummary("List shop listings and stock");
        routes.MapGet("/auction-lots", async (IConfiguration config, CancellationToken ct) =>
            await QueryAsync(config, "SELECT id,item_id,opening_bid,duration_seconds,enabled FROM economy.auction_lots ORDER BY id", ct)).WithSummary("List auction lots");
        routes.MapGet("/reward-offers", async (IConfiguration config, CancellationToken ct) =>
            await QueryAsync(config, "SELECT source,id,name,asset_id,amount,progress_kind,target,enabled FROM economy.reward_offers ORDER BY source,id", ct)).WithSummary("List reward offers");
        routes.MapPut("/reward-offers/{source}/{id}", async (string source, string id, RewardOfferInput input, IConfiguration config, CancellationToken ct) =>
        {
            if (source != input.Source || id != input.Id || source is not ("payment" or "voucher" or "quest" or "battle_pass")
                || input.Amount <= 0 || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.AssetId)
                || input.ProgressKind is not ("none" or "purchases" or "crafts") || input.Target < 0
                || (input.ProgressKind == "none" && input.Target != 0)
                || (input.ProgressKind != "none" && input.Target == 0))
                return Results.BadRequest(new { error = "Reward offer requires valid source, asset, amount, and progress target." });
            if (!await ExistsAsync(config, "assets", input.AssetId, ct))
                return Results.BadRequest(new { error = $"Unknown asset: {input.AssetId}." });
            await ExecuteAsync(config, "INSERT INTO economy.reward_offers(source,id,name,asset_id,amount,progress_kind,target,enabled) VALUES (@s,@id,@name,@asset,@amount,@kind,@target,@enabled) ON CONFLICT (source,id) DO UPDATE SET name=EXCLUDED.name,asset_id=EXCLUDED.asset_id,amount=EXCLUDED.amount,progress_kind=EXCLUDED.progress_kind,target=EXCLUDED.target,enabled=EXCLUDED.enabled", ct,
                ("s", source), ("id", id), ("name", input.Name), ("asset", input.AssetId),
                ("amount", input.Amount), ("kind", input.ProgressKind), ("target", input.Target), ("enabled", input.Enabled));
            return Results.NoContent();
        }).WithSummary("Create or update a reward offer");
        routes.MapPut("/auction-lots/{id}", async (string id, AuctionLotInput input, IConfiguration config, CancellationToken ct) =>
        {
            if (id != input.Id || string.IsNullOrWhiteSpace(input.ItemId)
                || input.OpeningBid <= 0 || input.DurationSeconds <= 0) return Results.BadRequest(new { error = "Auction lot requires an item, positive opening bid, and duration." });
            if (!await ExistsAsync(config, "items", input.ItemId, ct))
                return Results.BadRequest(new { error = $"Unknown item: {input.ItemId}." });
            await ExecuteAsync(config, "INSERT INTO economy.auction_lots(id,item_id,opening_bid,duration_seconds,enabled) VALUES (@id,@item,@bid,@duration,@enabled) ON CONFLICT (id) DO UPDATE SET item_id=EXCLUDED.item_id,opening_bid=EXCLUDED.opening_bid,duration_seconds=EXCLUDED.duration_seconds,enabled=EXCLUDED.enabled", ct,
                ("id", id), ("item", input.ItemId), ("bid", input.OpeningBid),
                ("duration", input.DurationSeconds), ("enabled", input.Enabled));
            return Results.NoContent();
        }).WithSummary("Create or update an auction lot");
        routes.MapGet("/listings/{id}/stock-audit", async (string id, string? reason, int? limit, long? beforeId, IConfiguration config, CancellationToken ct) =>
        {
            if (!ValidPage(limit, beforeId)) return Results.BadRequest(new { error = "limit must be 1–200 and beforeId must be positive." });
            return Results.Ok(await QueryByIdAsync(config, "SELECT id,delta,stock_after,reason,actor_account_id,admin_actor_subject,occurred_at FROM economy.stock_audit WHERE listing_id=@id AND (CAST(@reason AS text) IS NULL OR reason=@reason) AND (CAST(@before AS bigint) IS NULL OR id < @before) ORDER BY id DESC LIMIT @limit", id, ct, limit, beforeId, reason));
        }).WithSummary("Page through stock audit entries");
        routes.MapGet("/accounts/{accountId:guid}/balances", (Guid accountId, EconomyStore store, CancellationToken ct) => store.BalancesAsync(accountId, ct)).WithSummary("Read account balances");
        routes.MapGet("/accounts/{accountId:guid}/ledger", async (Guid accountId, string? assetId, string? reason, int? limit, long? beforeId, IConfiguration config, CancellationToken ct) =>
        {
            if (!ValidPage(limit, beforeId)) return Results.BadRequest(new { error = "limit must be 1–200 and beforeId must be positive." });
            return Results.Ok(await QueryLedgerAsync(config, accountId, assetId, reason, limit, beforeId, ct));
        }).WithSummary("Page through account ledger entries");
        routes.MapPost("/accounts/{accountId:guid}/balances/{assetId}", async (Guid accountId, string assetId, BalanceInput input, ClaimsPrincipal user, EconomyStore store, IConfiguration config, CancellationToken ct) =>
        {
            if (input.Delta == 0 || string.IsNullOrWhiteSpace(input.Reason) || string.IsNullOrWhiteSpace(input.IdempotencyKey))
                return Results.BadRequest(new { error = "A nonzero delta, reason, and idempotency key are required." });
            var actor = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(actor)) return Results.Forbid();
            await using (var db = Open(config))
            {
                await db.OpenAsync(ct);
                await using var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM identity.accounts WHERE id=@account) AND EXISTS (SELECT 1 FROM economy.assets WHERE id=@asset)", db);
                check.Parameters.AddWithValue("account", accountId); check.Parameters.AddWithValue("asset", assetId);
                if (!(bool)(await check.ExecuteScalarAsync(ct) ?? false))
                    return Results.NotFound(new { error = "Account or asset not found." });
            }
            return await store.AdjustAsync(accountId, assetId, input.Delta, input.Reason.Trim(), "admin", input.IdempotencyKey, ct, actor)
                ? Results.Ok() : Results.Conflict(new { error = "Duplicate adjustment or insufficient balance." });
        }).WithSummary("Grant or remove account currency or resources");
        routes.MapPut("/assets/{id}", async (string id, AssetInput input, IConfiguration config, CancellationToken ct) =>
        {
            if (id != input.Id || input.Kind is not ("currency" or "resource") || string.IsNullOrWhiteSpace(input.Name))
                return Results.BadRequest(new { error = "Asset requires matching ID, currency/resource kind, and name." });
            await using (var db = Open(config))
            {
                await db.OpenAsync(ct);
                await using var check = new NpgsqlCommand("SELECT kind FROM economy.assets WHERE id=@id", db);
                check.Parameters.AddWithValue("id", id);
                var existing = await check.ExecuteScalarAsync(ct) as string;
                if (existing is not null && existing != input.Kind)
                    return Results.Conflict(new { error = "Asset kind cannot change; disable it instead." });
            }
            await ExecuteAsync(config, "INSERT INTO economy.assets(id,kind,name,enabled) VALUES (@id,@kind,@name,@enabled) ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name,enabled=EXCLUDED.enabled", ct,
                ("id", id), ("kind", input.Kind), ("name", input.Name), ("enabled", input.Enabled));
            return Results.NoContent();
        }).WithSummary("Create or update a currency or resource");
        routes.MapDelete("/assets/{id}", (string id, IConfiguration config, CancellationToken ct) =>
            DeleteDefinitionAsync(config, "assets", id, ct)).WithSummary("Delete an unused currency or resource");
        routes.MapPut("/items/{id}", async (string id, ItemInput input, IConfiguration config, CancellationToken ct) =>
        {
            if (id != input.Id || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Type)
                || string.IsNullOrWhiteSpace(input.Rarity) || !IsJsonKind(input.StatsJson, JsonValueKind.Object))
                return Results.BadRequest(new { error = "Item requires matching ID, name, type, rarity, and object statsJson." });
            await ExecuteAsync(config, "INSERT INTO economy.items(id,name,type,rarity,stats,asset_key,enabled) VALUES (@id,@name,@type,@rarity,CAST(@stats AS jsonb),@asset,@enabled) ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name,type=EXCLUDED.type,rarity=EXCLUDED.rarity,stats=EXCLUDED.stats,asset_key=EXCLUDED.asset_key,enabled=EXCLUDED.enabled", ct,
                ("id", id), ("name", input.Name), ("type", input.Type), ("rarity", input.Rarity), ("stats", input.StatsJson), ("asset", input.AssetKey), ("enabled", input.Enabled));
            return Results.NoContent();
        }).WithSummary("Create or update an item");
        routes.MapDelete("/items/{id}", (string id, IConfiguration config, CancellationToken ct) =>
            DeleteDefinitionAsync(config, "items", id, ct)).WithSummary("Delete an unused item");
        routes.MapPut("/recipes/{id}", async (string id, RecipeInput input, IConfiguration config, CancellationToken ct) =>
        {
            if (id != input.Id || string.IsNullOrWhiteSpace(input.Name) || input.CraftSeconds < 0 || !IsRecipeArray(input.InputsJson)
                || !IsRecipeArray(input.OutputsJson)) return Results.BadRequest(new { error = "Recipe needs a name, nonnegative time, and valid input/output arrays." });
            await using (var db = Open(config))
            {
                await db.OpenAsync(ct);
                foreach (var json in new[] { input.InputsJson, input.OutputsJson })
                using (var parts = JsonDocument.Parse(json))
                foreach (var part in parts.RootElement.EnumerateArray())
                {
                    var kind = part.GetProperty("kind").GetString();
                    var reference = part.GetProperty("id").GetString();
                    var table = kind == "item" ? "economy.items" : "economy.assets";
                    await using var check = new NpgsqlCommand($"SELECT 1 FROM {table} WHERE id=@id" +
                        (kind == "item" ? "" : " AND kind=@kind"), db);
                    check.Parameters.AddWithValue("id", reference!);
                    if (kind != "item") check.Parameters.AddWithValue("kind", kind!);
                    if (await check.ExecuteScalarAsync(ct) is null)
                        return Results.BadRequest(new { error = $"Unknown {kind}: {reference}." });
                }
            }
            await ExecuteAsync(config, "INSERT INTO economy.recipes(id,name,inputs,outputs,craft_seconds,enabled) VALUES (@id,@name,CAST(@inputs AS jsonb),CAST(@outputs AS jsonb),@seconds,@enabled) ON CONFLICT (id) DO UPDATE SET name=EXCLUDED.name,inputs=EXCLUDED.inputs,outputs=EXCLUDED.outputs,craft_seconds=EXCLUDED.craft_seconds,enabled=EXCLUDED.enabled", ct,
                ("id", id), ("name", input.Name), ("inputs", input.InputsJson), ("outputs", input.OutputsJson), ("seconds", input.CraftSeconds), ("enabled", input.Enabled));
            return Results.NoContent();
        }).WithSummary("Create or update a crafting recipe");
        routes.MapDelete("/recipes/{id}", (string id, IConfiguration config, CancellationToken ct) =>
            DeleteDefinitionAsync(config, "recipes", id, ct)).WithSummary("Delete a recipe");
        routes.MapPut("/listings/{id}", async (string id, ListingInput input, ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            if (id != input.Id || string.IsNullOrWhiteSpace(input.ItemId) || input.StockAvailable < 0 || input.PerAccountLimit <= 0
                || input.RestockIntervalSeconds <= 0 || input.RestockAmount <= 0
                || (input.RestockIntervalSeconds is null) != (input.RestockAmount is null)
                || !IsPriceObject(input.PricesJson)) return Results.BadRequest(new { error = "Listing requires an item, valid currency prices, and nonnegative stock." });
            await using var db = Open(config); await db.OpenAsync(ct);
            await using (var item = new NpgsqlCommand("SELECT 1 FROM economy.items WHERE id=@id", db))
            {
                item.Parameters.AddWithValue("id", input.ItemId);
                if (await item.ExecuteScalarAsync(ct) is null) return Results.BadRequest(new { error = $"Unknown item: {input.ItemId}." });
            }
            using (var prices = JsonDocument.Parse(input.PricesJson))
            {
                var currencies = prices.RootElement.EnumerateObject().Select(price => price.Name).ToArray();
                await using var valid = new NpgsqlCommand("SELECT count(*) FROM economy.assets WHERE id=ANY(@ids) AND kind='currency' AND enabled", db);
                valid.Parameters.AddWithValue("ids", currencies);
                if ((long)(await valid.ExecuteScalarAsync(ct) ?? 0L) != currencies.Length) return Results.BadRequest(new { error = "Prices must reference enabled currencies." });
            }
            await using var tx = await db.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand("INSERT INTO economy.listings(id,item_id,prices,stock_available,per_account_limit,restock_interval_seconds,restock_amount,enabled) VALUES (@id,@item,CAST(@prices AS jsonb),@stock,@limit,@interval,@amount,@enabled) ON CONFLICT (id) DO UPDATE SET item_id=EXCLUDED.item_id,prices=EXCLUDED.prices,per_account_limit=EXCLUDED.per_account_limit,restock_interval_seconds=EXCLUDED.restock_interval_seconds,restock_amount=EXCLUDED.restock_amount,enabled=EXCLUDED.enabled RETURNING stock_available", db, tx);
            cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("item", input.ItemId);
            cmd.Parameters.AddWithValue("prices", input.PricesJson); cmd.Parameters.AddWithValue("stock", (object?)input.StockAvailable ?? DBNull.Value);
            cmd.Parameters.AddWithValue("limit", (object?)input.PerAccountLimit ?? DBNull.Value);
            cmd.Parameters.AddWithValue("interval", (object?)input.RestockIntervalSeconds ?? DBNull.Value);
            cmd.Parameters.AddWithValue("amount", (object?)input.RestockAmount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("enabled", input.Enabled);
            await cmd.ExecuteScalarAsync(ct);
            await using var audit = new NpgsqlCommand("INSERT INTO economy.stock_audit(listing_id,delta,stock_after,reason,admin_actor_subject) SELECT @id,@stock,@stock,'initial_stock',@actor WHERE @stock IS NOT NULL AND NOT EXISTS (SELECT 1 FROM economy.stock_audit WHERE listing_id=@id)", db, tx);
            audit.Parameters.AddWithValue("id", id); audit.Parameters.AddWithValue("stock", (object?)input.StockAvailable ?? DBNull.Value);
            audit.Parameters.AddWithValue("actor", (object?)(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")) ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).WithSummary("Create or update a shop listing");
        routes.MapDelete("/listings/{id}", (string id, IConfiguration config, CancellationToken ct) =>
            DeleteDefinitionAsync(config, "listings", id, ct)).WithSummary("Delete an unused shop listing");
        routes.MapPost("/listings/{id}/stock", async (string id, StockInput input, ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            if (input.Delta == 0 || string.IsNullOrWhiteSpace(input.Reason))
                return Results.BadRequest(new { error = "A nonzero delta and reason are required." });
            var actor = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(actor)) return Results.Forbid();
            await using var db = Open(config); await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE economy.listings SET stock_available=stock_available+@d WHERE id=@id AND stock_available IS NOT NULL AND stock_available::numeric+@d>=0 AND stock_available::numeric+@d<=9223372036854775807 RETURNING stock_available", db, tx);
            cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("d", input.Delta);
            var after = await cmd.ExecuteScalarAsync(ct);
            if (after is null) return Results.Conflict(new { error = "Listing is missing, has unlimited stock, or the adjustment exceeds stock bounds." });
            await using var audit = new NpgsqlCommand("INSERT INTO economy.stock_audit(listing_id,delta,stock_after,reason,admin_actor_subject) VALUES (@id,@d,@after,@reason,@actor)", db, tx);
            audit.Parameters.AddWithValue("id", id); audit.Parameters.AddWithValue("d", input.Delta);
            audit.Parameters.AddWithValue("after", (long)after); audit.Parameters.AddWithValue("reason", input.Reason.Trim());
            audit.Parameters.AddWithValue("actor", actor);
            await audit.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok();
        }).WithSummary("Adjust finite shop stock with an audit reason");
    }

    private static NpgsqlConnection Open(IConfiguration config) => new(config.GetConnectionString("identity-write")
        ?? throw new InvalidOperationException("Missing connection string 'identity-write'."));

    private static async Task<bool> ExistsAsync(IConfiguration config, string table, string id, CancellationToken ct)
    {
        await using var db = Open(config); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT 1 FROM economy.{table} WHERE id=@id", db);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<IResult> DeleteDefinitionAsync(IConfiguration config, string table, string id, CancellationToken ct)
    {
        await using var db = Open(config); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        if (table is "items" or "assets")
        {
            var kind = table == "items" ? "item" : "asset";
            var referenceSql = table == "items"
                ? "SELECT EXISTS (SELECT 1 FROM economy.recipes r, LATERAL jsonb_array_elements(r.inputs || r.outputs) part WHERE part->>'kind'='item' AND part->>'id'=@id)"
                : "SELECT EXISTS (SELECT 1 FROM economy.recipes r, LATERAL jsonb_array_elements(r.inputs || r.outputs) part WHERE part->>'kind' IN ('currency','resource') AND part->>'id'=@id)";
            await using var refs = new NpgsqlCommand(referenceSql, db, tx);
            refs.Parameters.AddWithValue("id", id);
            if ((bool)(await refs.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new { error = $"{kind} is used by a recipe; disable it instead." });
            if (table == "assets")
            {
                await using var prices = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM economy.listings WHERE jsonb_exists(prices,@id))", db, tx);
                prices.Parameters.AddWithValue("id", id);
                if ((bool)(await prices.ExecuteScalarAsync(ct) ?? false))
                    return Results.Conflict(new { error = "Currency is used by a listing; disable it instead." });
            }
        }
        try
        {
            await using var cmd = new NpgsqlCommand($"DELETE FROM economy.{table} WHERE id=@id", db, tx);
            cmd.Parameters.AddWithValue("id", id);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted == 0) return Results.NotFound(new { error = "Definition not found." });
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Conflict(new { error = "Definition is in use; disable it instead." });
        }
    }
    private static bool IsJsonKind(string json, JsonValueKind kind)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.ValueKind == kind; }
        catch (JsonException) { return false; }
    }
    private static bool IsRecipeArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.GetArrayLength() > 0
                && document.RootElement.EnumerateArray().All(entry =>
                    entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() is "item" or "currency" or "resource"
                    && entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString())
                    && entry.TryGetProperty("quantity", out var quantity)
                    && quantity.TryGetInt64(out var count) && count > 0);
        }
        catch (JsonException) { return false; }
    }
    private static bool IsPriceObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var prices = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToArray() : [];
            return document.RootElement.ValueKind == JsonValueKind.Object
                && prices.Length > 0
                && prices.Select(price => price.Name).Distinct(StringComparer.Ordinal).Count() == prices.Length
                && prices.All(price =>
                    price.Value.TryGetInt64(out var amount) && amount >= 0);
        }
        catch (JsonException) { return false; }
    }
    private static async Task ExecuteAsync(IConfiguration config, string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        await using var db = Open(config); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<List<Dictionary<string, object?>>> QueryAsync(IConfiguration config, string sql, CancellationToken ct, Guid? account = null)
    {
        await using var db = Open(config); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        if (account is not null) cmd.Parameters.AddWithValue("a", account.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    private static bool ValidPage(int? limit, long? beforeId) => (limit is null or >= 1 and <= 200) && (beforeId is null or > 0);
    private static int PageSize(int? limit) => limit ?? 50;

    private static async Task<List<Dictionary<string, object?>>> QueryLedgerAsync(IConfiguration config, Guid accountId,
        string? assetId, string? reason, int? limit, long? beforeId, CancellationToken ct)
    {
        await using var db = Open(config); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id,asset_id,delta,balance_after,reason,menu_source,idempotency_key,admin_actor_subject,occurred_at FROM economy.ledger WHERE account_id=@a AND (CAST(@asset AS text) IS NULL OR asset_id=@asset) AND (CAST(@reason AS text) IS NULL OR reason=@reason) AND (CAST(@before AS bigint) IS NULL OR id < @before) ORDER BY id DESC LIMIT @limit", db);
        cmd.Parameters.AddWithValue("a", accountId);
        cmd.Parameters.AddWithValue("asset", (object?)assetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("before", (object?)beforeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", PageSize(limit));
        return await ReadRowsAsync(cmd, ct);
    }

    private static async Task<List<Dictionary<string, object?>>> QueryByIdAsync(IConfiguration config, string sql, string id, CancellationToken ct, int? limit = null, long? beforeId = null, string? reason = null)
    {
        await using var db = Open(config); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("before", (object?)beforeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", PageSize(limit));
        return await ReadRowsAsync(cmd, ct);
    }

    private static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
