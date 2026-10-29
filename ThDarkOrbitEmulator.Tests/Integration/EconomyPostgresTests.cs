using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Http;
using Npgsql;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

public class EconomyPostgresTests
{
    [Test]
    [Category("Integration")]
    public async Task PulseCharge_ConsumesDurableInventoryOnceAcrossConcurrentStores()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var create = new NpgsqlCommand(
            "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'pulse-pilot',4022)", db))
        { create.Parameters.AddWithValue("a", account); await create.ExecuteNonQueryAsync(ct); }
        var first = new EconomyStore(config);
        var second = new EconomyStore(config);
        Assert.That(await first.GrantItemAsync(account, "pulse_charge", 1, "pulse-test", 0,
            "test", ct), Is.True);
        var consumed = await Task.WhenAll(first.ConsumeItemAsync(account, "pulse_charge", ct),
            second.ConsumeItemAsync(account, "pulse_charge", ct));
        Assert.That(consumed.Count(value => value), Is.EqualTo(1));
        Assert.That((await second.InventoryAsync(account, ct))["pulse_charge"], Is.Zero);
    }

    [Test]
    [Category("Integration")]
    public async Task PulseCharge_ActionRetryConsumesOnlyOnceAcrossStores()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var create = new NpgsqlCommand(
            "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'pulse-retry',4023)", db))
        { create.Parameters.AddWithValue("a", account); await create.ExecuteNonQueryAsync(ct); }
        var first = new EconomyStore(config);
        var second = new EconomyStore(config);
        Assert.That(await first.GrantItemAsync(account, "pulse_charge", 2, "pulse-retry", 0,
            "test", ct), Is.True);
        var action = Guid.NewGuid();
        var retries = await Task.WhenAll(first.ConsumeItemOnceAsync(account, "pulse_charge", action, ct),
            second.ConsumeItemOnceAsync(account, "pulse_charge", action, ct));
        Assert.That(retries, Is.EquivalentTo(new[]
            { PulseChargeResult.Consumed, PulseChargeResult.Duplicate }));
        Assert.That((await first.InventoryAsync(account, ct))["pulse_charge"], Is.EqualTo(1));
        Assert.That(await second.ConsumeItemOnceAsync(account, "pulse_charge", action, ct),
            Is.EqualTo(PulseChargeResult.Duplicate));
        Assert.That(await second.ConsumeItemOnceAsync(account, "pulse_charge", Guid.NewGuid(), ct),
            Is.EqualTo(PulseChargeResult.Consumed));
        Assert.That(await first.ConsumeItemOnceAsync(account, "pulse_charge", Guid.NewGuid(), ct),
            Is.EqualTo(PulseChargeResult.Unavailable));
        Assert.That((await first.InventoryAsync(account, ct))["pulse_charge"], Is.Zero);
    }

    [Test]
    [Category("Integration")]
    public async Task PulseCharge_RefundsOnlyBeforeWorldEffectStarts()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var create = new NpgsqlCommand(
            "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'pulse-recovery',4024)", db))
        { create.Parameters.AddWithValue("a", account); await create.ExecuteNonQueryAsync(ct); }
        var first = new EconomyStore(config);
        var second = new EconomyStore(config);
        Assert.That(await first.GrantItemAsync(account, "pulse_charge", 2, "pulse-recovery", 0,
            "test", ct), Is.True);

        var unstarted = Guid.NewGuid();
        Assert.That(await first.ConsumeItemOnceAsync(account, "pulse_charge", unstarted, ct),
            Is.EqualTo(PulseChargeResult.Consumed));
        Assert.That(await second.RefundUnstartedPulseActionAsync(account, unstarted, ct), Is.True);
        Assert.That(await first.StartPulseActionAsync(account, unstarted, ct), Is.False);
        Assert.That(await first.RefundUnstartedPulseActionAsync(account, unstarted, ct), Is.False);
        Assert.That((await second.InventoryAsync(account, ct))["pulse_charge"], Is.EqualTo(2));

        var started = Guid.NewGuid();
        Assert.That(await first.ConsumeItemOnceAsync(account, "pulse_charge", started, ct),
            Is.EqualTo(PulseChargeResult.Consumed));
        Assert.That(await second.StartPulseActionAsync(account, started, 3,
            [new PulseTarget(101, 4), new PulseTarget(102, 7)], ct), Is.True);
        var storedPlan = await first.PulseActionPlanAsync(account, started, ct);
        Assert.That(storedPlan?.Status, Is.EqualTo("started"));
        Assert.That(storedPlan?.MapId, Is.EqualTo(3));
        Assert.That(storedPlan?.Targets, Is.EqualTo(new[]
        { new PulseTarget(101, 4), new PulseTarget(102, 7) }));
        Assert.That(await first.RefundUnstartedPulseActionAsync(account, started, ct), Is.False);
        await first.CompletePulseActionAsync(account, started, ct);
        Assert.That(await second.RefundUnstartedPulseActionAsync(account, started, ct), Is.False);
        Assert.That((await first.InventoryAsync(account, ct))["pulse_charge"], Is.EqualTo(1));

        var racing = Guid.NewGuid();
        Assert.That(await first.ConsumeItemOnceAsync(account, "pulse_charge", racing, ct),
            Is.EqualTo(PulseChargeResult.Consumed));
        var start = first.StartPulseActionAsync(account, racing, ct);
        var refund = second.RefundUnstartedPulseActionAsync(account, racing, ct);
        await Task.WhenAll(start, refund);
        Assert.That(start.Result != refund.Result, Is.True);
        Assert.That((await first.InventoryAsync(account, ct))["pulse_charge"],
            Is.EqualTo(refund.Result ? 1 : 0));
    }

    [Test]
    [Category("Integration")]
    public async Task LegacyEquipmentImport_CapsShipSlotsAndKeepsRemainderTransferable()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var create = new NpgsqlCommand(
            "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'transfer-pilot',4003)", db))
        { create.Parameters.AddWithValue("a", account); await create.ExecuteNonQueryAsync(ct); }

        var first = new EconomyStore(config);
        var second = new EconomyStore(config);
        await first.ImportAsync(account, new LocalEquipmentState(
            [new("laser_lf3", "LF-3 laser", "Equipment", 4, "laser"),
             new("laser_lf2", "LF-2 laser", "Equipment", 2, "laser")], []), ct);
        await first.ImportHangarEquipmentAsync(account,
            [new(1, 1, "laser_lf3", 4), new(1, 1, "laser_lf2", 2)], ct);

        var imported = await second.HangarEquipmentAsync(account, ct);
        Assert.That(imported, Is.EquivalentTo(new[]
        {
            new LocalEquipmentAssignment(1, 1, "laser_lf2", 2),
            new LocalEquipmentAssignment(1, 1, "laser_lf3", 3)
        }));
        Assert.That(await second.ChangeHangarEquipmentAsync(account, 2, 1, "laser_lf3", 1, ct),
            Is.EqualTo(LocalHangarChange.Applied));
        Assert.That(await first.ChangeHangarEquipmentAsync(account, 2, 2, "laser_lf3", 2, ct),
            Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(await first.HangarEquipmentAsync(account, ct), Has.Count.EqualTo(3));

        Assert.That(await first.ChangeHangarEquipmentAsync(account, 2, 1, "laser_lf3", 0, ct),
            Is.EqualTo(LocalHangarChange.Applied));

        Assert.That(await second.TransferHangarEquipmentAsync(account, 1, 2, 1, "laser_lf3", 1, ct),
            Is.EqualTo(LocalHangarChange.Applied));
        var moved = await first.HangarEquipmentAsync(account, ct);
        Assert.That(moved.Single(a => a.HangarSlot == 1 && a.ItemId == "laser_lf3").Quantity, Is.EqualTo(2));
        Assert.That(moved.Single(a => a.HangarSlot == 2 && a.ItemId == "laser_lf3").Quantity, Is.EqualTo(1));
        Assert.That(await first.TransferHangarEquipmentAsync(account, 1, 2, 1, "laser_lf3", 3, ct),
            Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(await second.HangarEquipmentAsync(account, ct), Is.EquivalentTo(moved));
    }

    [Test]
    [Category("Integration")]
    public async Task ShopRestock_CapsAtBigintLimitAndAuditsActualAddition()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var insert = new NpgsqlCommand("INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'restock-pilot',4002)", db))
        { insert.Parameters.AddWithValue("a", account); await insert.ExecuteNonQueryAsync(ct); }
        var store = new EconomyStore(config);
        await store.ImportAsync(account,
            new LocalEquipmentState([new LocalEquipmentItem("laser_lf3", "LF-3 laser", "Equipment", 4)], []), ct);

        async Task MakeRestockDueAsync()
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE economy.listings SET stock_available=9223372036854775806,
                    restock_amount=5, restock_interval_seconds=60,
                    last_restock_at=now()-interval '2 minutes'
                WHERE id='laser_lf3'
                """, db);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await MakeRestockDueAsync();
        Assert.That((await store.ListingsAsync(ct)).Single(item => item.Id == "laser_lf3").StockAvailable,
            Is.EqualTo(long.MaxValue));
        await MakeRestockDueAsync();
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 1, "restock-cap", ct),
            Is.EqualTo(EconomyPurchaseResult.Applied));
        await using var audit = new NpgsqlCommand("""
            SELECT delta,stock_after FROM economy.stock_audit
            WHERE listing_id='laser_lf3' AND reason='scheduled_restock' ORDER BY id
            """, db);
        await using var rows = await audit.ExecuteReaderAsync(ct);
        var entries = new List<(long Delta, long Stock)>();
        while (await rows.ReadAsync(ct)) entries.Add((rows.GetInt64(0), rows.GetInt64(1)));
        Assert.That(entries, Is.EqualTo(new[] { (1L, long.MaxValue), (1L, long.MaxValue) }));
    }

    [Test]
    [Category("Integration")]
    public async Task PurchaseAtQuantityCapacity_DoesNotDebitOrRecordOrder()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var insert = new NpgsqlCommand("INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'capacity-pilot',4001)", db))
        { insert.Parameters.AddWithValue("a", account); await insert.ExecuteNonQueryAsync(ct); }
        var store = new EconomyStore(config);
        await store.ImportAsync(account,
            new LocalEquipmentState([new LocalEquipmentItem("laser_lf3", "LF-3 laser", "Equipment", 4)], []), ct);
        await using (var cap = new NpgsqlCommand("UPDATE economy.inventory SET quantity=9223372036854775807 WHERE account_id=@a AND item_id='laser_lf3'", db))
        { cap.Parameters.AddWithValue("a", account); await cap.ExecuteNonQueryAsync(ct); }
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 1, "inventory-full", ct),
            Is.EqualTo(EconomyPurchaseResult.Unavailable));

        await using (var cap = new NpgsqlCommand("""
            UPDATE economy.inventory SET quantity=4 WHERE account_id=@a AND item_id='laser_lf3';
            INSERT INTO economy.purchases(account_id,listing_id,quantity) VALUES (@a,'laser_lf3',9223372036854775807)
            """, db))
        { cap.Parameters.AddWithValue("a", account); await cap.ExecuteNonQueryAsync(ct); }
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 1, "counter-full", ct),
            Is.EqualTo(EconomyPurchaseResult.Unavailable));
        Assert.That((await store.BalancesAsync(account, ct)).Single(b => b.AssetId == "credits").Amount,
            Is.EqualTo(10000));
        await using var count = new NpgsqlCommand("SELECT count(*) FROM economy.purchase_requests WHERE account_id=@a", db);
        count.Parameters.AddWithValue("a", account);
        Assert.That(await count.ExecuteScalarAsync(ct), Is.EqualTo(0L));
    }

    [Test]
    [Category("Integration")]
    public async Task TechFactoryJob_SurvivesCacheLossAndSharesNonnegativeWallet()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var insert = new NpgsqlCommand("INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'tech-pilot',3001)", db);
            insert.Parameters.AddWithValue("a", account); await insert.ExecuteNonQueryAsync(ct);
        }
        var economy = new EconomyStore(config);
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var factory = new LocalTechFactoryStore(cache, TimeProvider.System, economy);
        await factory.GetAsync(account, ct);
        var now = DateTimeOffset.UtcNow;
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            economy.ChangeTechFactoryAsync(account, "tech_targeting", Guid.Empty, $"tech:test:{i}", now, ct)));
        Assert.That(starts.Count(x => x == TechFactoryChange.Applied), Is.EqualTo(1));
        Assert.That((await economy.BalancesAsync(account, ct)).Single(b => b.AssetId == "tech_components").Amount, Is.EqualTo(900));
        await cache.RemoveAsync($"localtechfactory:{account:N}", ct);
        var persisted = await new LocalTechFactoryStore(cache, TimeProvider.System, economy).GetAsync(account, ct);
        Assert.That(persisted.Job, Is.Not.Null);
        Assert.That(persisted.Components, Is.EqualTo(900));
        Assert.That(await economy.AdjustAsync(account, "tech_components", -900, "test_spend", "test", "tech:test:other", ct), Is.True);
        Assert.That(await economy.ChangeTechFactoryAsync(account, "tech_shield", Guid.Empty, "tech:test:blocked", now, ct), Is.EqualTo(TechFactoryChange.Conflict));
        Assert.That(await economy.ChangeTechFactoryAsync(account, null, persisted.Job!.Id, "tech:test:claim", now, ct), Is.EqualTo(TechFactoryChange.Conflict));
        Assert.That(await economy.ChangeTechFactoryAsync(account, null, persisted.Job.Id, "tech:test:claim", now.AddMinutes(2), ct), Is.EqualTo(TechFactoryChange.Applied));
        Assert.That(await economy.ChangeTechFactoryAsync(account, null, persisted.Job.Id, "tech:test:claim", now.AddMinutes(2), ct), Is.EqualTo(TechFactoryChange.Conflict));
        Assert.That((await economy.BalancesAsync(account, ct)).Single(b => b.AssetId == "targeting_boost").Amount, Is.EqualTo(1));
        Assert.That((await economy.BalancesAsync(account, ct)).Single(b => b.AssetId == "tech_components").Amount, Is.Zero);
        Assert.That(await economy.ChangeTechFactoryAsync(account, "tech_shield", Guid.Empty, "tech:test:poor", now, ct), Is.EqualTo(TechFactoryChange.InsufficientComponents));

        var legacyAccount = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var insert = new NpgsqlCommand("INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'legacy-tech-pilot',3002)", db);
            insert.Parameters.AddWithValue("a", legacyAccount); await insert.ExecuteNonQueryAsync(ct);
        }
        var legacy = new LocalTechFactoryState(900, new Dictionary<string, int>(),
            new LocalTechJob("targeting", now.AddMinutes(-1)));
        await cache.SetStringAsync($"localtechfactory:{legacyAccount:N}", JsonSerializer.Serialize(legacy), ct);
        var imported = await factory.GetAsync(legacyAccount, ct);
        Assert.That(imported.Job?.RecipeId, Is.EqualTo("tech_targeting"));
        Assert.That(imported.Components, Is.EqualTo(900));
        await cache.RemoveAsync($"localtechfactory:{legacyAccount:N}", ct);
        Assert.That((await factory.GetAsync(legacyAccount, ct)).Job?.Id, Is.EqualTo(imported.Job?.Id));
        Assert.That(await economy.ChangeTechFactoryAsync(legacyAccount, null, imported.Job!.Id,
            "tech:test:legacy-claim", now, ct), Is.EqualTo(TechFactoryChange.Applied));
        await cache.SetStringAsync($"localtechfactory:{legacyAccount:N}", JsonSerializer.Serialize(legacy), ct);
        Assert.That((await factory.GetAsync(legacyAccount, ct)).Job, Is.Null);
        Assert.That((await economy.BalancesAsync(legacyAccount, ct)).Single(b => b.AssetId == "targeting_boost").Amount, Is.EqualTo(1));
    }

    [Test]
    [Category("Integration")]
    public async Task ConcurrentPurchases_DoNotOversell_AndRetryDoesNotDebitAgain()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'first-pilot',1001),(@b,'second-pilot',1002); UPDATE economy.listings SET stock_available=1 WHERE id='laser_lf3'", db);
            cmd.Parameters.AddWithValue("a", first); cmd.Parameters.AddWithValue("b", second);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var store = new EconomyStore(config);
        var starter = new LocalEquipmentState([new LocalEquipmentItem("laser_lf3", "LF-3 laser", "Equipment", 4)], []);
        await store.ImportAsync(first, starter, ct);
        await store.ImportAsync(second, starter, ct);
        var results = await Task.WhenAll(store.PurchaseAsync(first, "laser_lf3", 1, "first-order", ct),
            store.PurchaseAsync(second, "laser_lf3", 1, "second-order", ct));
        Assert.That(results.Count(result => result == EconomyPurchaseResult.Applied), Is.EqualTo(1));
        Assert.That(results.Count(result => result == EconomyPurchaseResult.Unavailable), Is.EqualTo(1));
        var winner = results[0] == EconomyPurchaseResult.Applied ? first : second;
        var key = winner == first ? "first-order" : "second-order";
        Assert.That(await store.PurchaseAsync(winner, "laser_lf3", 1, key, ct), Is.EqualTo(EconomyPurchaseResult.Duplicate));
        Assert.That((await store.BalancesAsync(winner, ct)).Single(b => b.AssetId == "credits").Amount, Is.EqualTo(8500));
        await using var check = new NpgsqlConnection(postgres.GetConnectionString()); await check.OpenAsync(ct);
        await using var query = new NpgsqlCommand("SELECT stock_available FROM economy.listings WHERE id='laser_lf3'", check);
        Assert.That(await query.ExecuteScalarAsync(ct), Is.EqualTo(0L));
        Assert.That(await store.PurchaseAsync(winner, "laser_lf3", 1, "sold-out", ct), Is.EqualTo(EconomyPurchaseResult.Unavailable));

        await using (var update = new NpgsqlCommand("UPDATE economy.listings SET stock_available=2,prices='{\"credits\":1500,\"uridium\":5}'::jsonb,per_account_limit=1 WHERE id='laser_lf3'", check))
            await update.ExecuteNonQueryAsync(ct);
        Assert.That(await store.PurchaseAsync(winner, "laser_lf3", 1, "uridium", "over-limit", ct), Is.EqualTo(EconomyPurchaseResult.Unavailable));
        Assert.That(await store.PurchaseAsync(second == winner ? first : second, "laser_lf3", 1, "uridium", "no-uridium", ct), Is.EqualTo(EconomyPurchaseResult.InsufficientFunds));
        await using (var disable = new NpgsqlCommand("UPDATE economy.items SET enabled=false WHERE id='laser_lf3'", check))
            await disable.ExecuteNonQueryAsync(ct);
        Assert.That(await store.PurchaseAsync(second == winner ? first : second, "laser_lf3", 1, "disabled", ct), Is.EqualTo(EconomyPurchaseResult.Unavailable));
        Assert.That((await store.BalancesAsync(winner, ct)).Single(b => b.AssetId == "credits").Amount, Is.EqualTo(8500));
        await using (var enable = new NpgsqlCommand("UPDATE economy.items SET enabled=true WHERE id='laser_lf3'", check))
            await enable.ExecuteNonQueryAsync(ct);
        var other = second == winner ? first : second;
        Assert.That(await store.AdjustAsync(other, "uridium", 10, "test_grant", "test", "grant-uridium", ct), Is.True);
        Assert.That(await store.PurchaseAsync(other, "laser_lf3", 1, "uridium", "uridium-buy", ct), Is.EqualTo(EconomyPurchaseResult.Applied));
        var otherBalances = await store.BalancesAsync(other, ct);
        Assert.That(otherBalances.Single(b => b.AssetId == "uridium").Amount, Is.EqualTo(5));
        Assert.That(otherBalances.Single(b => b.AssetId == "credits").Amount, Is.EqualTo(10000));
        Assert.That(await store.CraftAsync(other, "lf3", ct), Is.True);
        Assert.That((await store.BalancesAsync(other, ct)).Single(b => b.AssetId == "assembly_components").Amount, Is.EqualTo(850));
        await using (var disableRecipe = new NpgsqlCommand("UPDATE economy.recipes SET enabled=false WHERE id='lf3'", check))
            await disableRecipe.ExecuteNonQueryAsync(ct);
        Assert.That(await store.CraftAsync(other, "lf3", ct), Is.False);

        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(new AccountSummary(other, "second-pilot", null, 1002), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        var equipment = new LocalHangarEquipmentStore(cache, store);
        var disabledCraft = CraftRequest(session.Dosid, $"recipe=lf3&key={LocalAssemblyAction.CreateFormKey(session.Dosid, "lf3")}");
        await LocalAssemblyAction.HandleAsync(disabledCraft, sessions, equipment, store, ct);
        Assert.That(disabledCraft.Response.StatusCode, Is.EqualTo(409));
        var unsignedCraft = CraftRequest(session.Dosid, "recipe=flax");
        await LocalAssemblyAction.HandleAsync(unsignedCraft, sessions, equipment, store, ct);
        Assert.That(unsignedCraft.Response.StatusCode, Is.EqualTo(400));
        var craftBody = $"recipe=flax&key={LocalAssemblyAction.CreateFormKey(session.Dosid, "flax")}";
        var validCraft = CraftRequest(session.Dosid, craftBody);
        await LocalAssemblyAction.HandleAsync(validCraft, sessions, equipment, store, ct);
        Assert.That(validCraft.Response.StatusCode, Is.EqualTo(303));
        Assert.That(validCraft.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAssembly&crafted=flax"));
        Assert.That(await store.RecipeCraftCountAsync(session.AccountId, "flax", ct), Is.EqualTo(1));
        Assert.That(await store.RecipeCraftCountAsync(session.AccountId, "lf3", ct), Is.EqualTo(1));
        Assert.That(await store.RecipeCraftCountAsync(session.AccountId, "shield", ct), Is.Zero);
        Assert.That(await store.RecipeCraftCountAsync(winner, "lf3", ct), Is.Zero);
        var replayedCraft = CraftRequest(session.Dosid, craftBody);
        await LocalAssemblyAction.HandleAsync(replayedCraft, sessions, equipment, store, ct);
        Assert.That(replayedCraft.Response.StatusCode, Is.EqualTo(409));
        await using (var soldOut = new NpgsqlCommand("UPDATE economy.listings SET stock_available=0 WHERE id='laser_lf3'", check))
            await soldOut.ExecuteNonQueryAsync(ct);
        var soldOutRequest = ShopRequest(session.Dosid, $"item=laser_lf3&quantity=1&currency=credits&idempotencyKey={LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3")}");
        await LocalShopPurchase.HandleAsync(soldOutRequest, sessions, equipment, store, ct);
        Assert.That(soldOutRequest.Response.StatusCode, Is.EqualTo(409));
        await using (var restock = new NpgsqlCommand("UPDATE economy.listings SET stock_available=1,per_account_limit=NULL,prices='{\"credits\":1500,\"uridium\":6}'::jsonb WHERE id='laser_lf3'", check))
            await restock.ExecuteNonQueryAsync(ct);
        var insufficientRequest = ShopRequest(session.Dosid, $"item=laser_lf3&quantity=1&currency=uridium&idempotencyKey={LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3", currency: "uridium")}");
        await LocalShopPurchase.HandleAsync(insufficientRequest, sessions, equipment, store, ct);
        Assert.That(insufficientRequest.Response.StatusCode, Is.EqualTo(400));
        await using (var disableAgain = new NpgsqlCommand("UPDATE economy.items SET enabled=false WHERE id='laser_lf3'", check))
            await disableAgain.ExecuteNonQueryAsync(ct);
        var disabledRequest = ShopRequest(session.Dosid, $"item=laser_lf3&quantity=1&currency=credits&idempotencyKey={LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3")}");
        await LocalShopPurchase.HandleAsync(disabledRequest, sessions, equipment, store, ct);
        Assert.That(disabledRequest.Response.StatusCode, Is.EqualTo(409));
        await using (var enableAgain = new NpgsqlCommand("UPDATE economy.items SET enabled=true WHERE id='laser_lf3'", check))
            await enableAgain.ExecuteNonQueryAsync(ct);
        var auctionNow = DateTimeOffset.UtcNow;
        Assert.That(await store.BidAsync(other, "laser_lf3", 700, auctionNow, ct), Is.True);
        Assert.That(await store.BidAsync(other, "laser_lf3", 700, auctionNow, ct), Is.False);
        Assert.That(await store.ClaimAuctionAsync(other, "laser_lf3", auctionNow.AddMinutes(6), ct), Is.True);
        Assert.That(await store.ClaimAuctionAsync(other, "laser_lf3", auctionNow.AddMinutes(6), ct), Is.False);
        Assert.That((await store.InventoryAsync(other, ct))["laser_lf3"], Is.EqualTo(7));
        Assert.That(await store.GrantItemAsync(other, "drone_flax", 1, "test-reward", 0, "events", ct), Is.True);
        Assert.That(await store.GrantItemAsync(other, "drone_flax", 1, "test-reward", 0, "events", ct), Is.False);
        // One signed Assembly craft (its replay is refused by economy.craft_requests) plus one grant.
        Assert.That((await store.InventoryAsync(other, ct))["drone_flax"], Is.EqualTo(2));
        await using (var crafts = new NpgsqlCommand("SELECT count(*) FROM economy.craft_requests WHERE account_id=@a AND recipe_id='flax'", check))
        {
            crafts.Parameters.AddWithValue("a", other);
            Assert.That(await crafts.ExecuteScalarAsync(ct), Is.EqualTo(1L));
        }
    }

    private static DefaultHttpContext CraftRequest(string dosid, string form)
    {
        var context = ShopRequest(dosid, form);
        context.Request.QueryString = new QueryString("?action=internalAssemblyCraft");
        return context;
    }

    private static DefaultHttpContext ShopRequest(string dosid, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalShopBuy");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    [Test]
    [Category("Integration")]
    public async Task GalaxyEnergy_ImportsOnce_AndDebitsThroughLedger()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var accountId = Guid.NewGuid();
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using (var create = new NpgsqlCommand("INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'gate-pilot',1003)", db))
        {
            create.Parameters.AddWithValue("a", accountId);
            await create.ExecuteNonQueryAsync(ct);
        }
        var store = new EconomyStore(config);
        await store.ImportResourceAsync(accountId, "galaxy_energy", 250, "galaxy_gates", ct);
        await store.ImportResourceAsync(accountId, "galaxy_energy", 250, "galaxy_gates", ct);
        await store.ReconcileResourcesAsync(accountId, new Dictionary<string, long> { ["galaxy_energy"] = -10 }, "galaxy_gates", ct);
        Assert.That((await store.BalancesAsync(accountId, ct)).Single(b => b.AssetId == "galaxy_energy").Amount, Is.EqualTo(240));
        await using var ledger = new NpgsqlCommand("SELECT delta,balance_after FROM economy.ledger WHERE account_id=@a AND asset_id='galaxy_energy' ORDER BY id", db);
        ledger.Parameters.AddWithValue("a", accountId);
        await using var rows = await ledger.ExecuteReaderAsync(ct);
        Assert.That(await rows.ReadAsync(ct), Is.True);
        Assert.That((rows.GetInt64(0), rows.GetInt64(1)), Is.EqualTo((250L, 250L)));
        Assert.That(await rows.ReadAsync(ct), Is.True);
        Assert.That((rows.GetInt64(0), rows.GetInt64(1)), Is.EqualTo((-10L, 240L)));
        Assert.That(await rows.ReadAsync(ct), Is.False);
    }

    [Test]
    [Category("Integration")]
    public async Task GalaxyGateSpin_UsesDurableProgressAndAtomicEnergyAcrossInstances()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await postgres.StartAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        var account = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var create = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,'gate-pilot-2',1004)", db);
            create.Parameters.AddWithValue("a", account);
            await create.ExecuteNonQueryAsync(ct);
        }
        var first = new EconomyStore(config);
        var second = new EconomyStore(config);
        await first.ImportResourceAsync(account, "galaxy_energy", 250, "galaxy_gates", ct);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? first : second).ChangeGalaxyGateAsync(account, "alpha", 10, "same-form", ct)));
        Assert.That(results.Count(r => r == GalaxyGateChange.Applied), Is.EqualTo(1));
        Assert.That(results.Count(r => r == GalaxyGateChange.Conflict), Is.EqualTo(11));
        Assert.That(await first.ChangeGalaxyGateAsync(account, "alpha", 10, "second-form", ct), Is.EqualTo(GalaxyGateChange.Applied));
        Assert.That(await second.ChangeGalaxyGateAsync(account, "alpha", 10, "third-form", ct), Is.EqualTo(GalaxyGateChange.Applied));
        Assert.That(await first.ChangeGalaxyGateAsync(account, "alpha", 4, "fourth-form", ct), Is.EqualTo(GalaxyGateChange.Applied));
        Assert.That(await second.ChangeGalaxyGateAsync(account, "alpha", 1, "over-complete", ct), Is.EqualTo(GalaxyGateChange.Conflict));
        Assert.That(await second.ChangeGalaxyGateAsync(account, "alpha", 0, "activate", ct), Is.EqualTo(GalaxyGateChange.Applied));
        Assert.That(await first.ChangeGalaxyGateAsync(account, "alpha", 0, "replay-activate", ct), Is.EqualTo(GalaxyGateChange.Conflict));
        var progress = await second.GalaxyGateProgressAsync(account, ct);
        Assert.That(progress["alpha"], Is.EqualTo((34, true)));
        var balances = await first.BalancesAsync(account, ct);
        Assert.That(balances.Single(b => b.AssetId == "galaxy_energy").Amount, Is.EqualTo(216));
        Assert.That(balances.Single(b => b.AssetId == "credits").Amount, Is.Zero);
        using var cacheServices = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var freshMenu = new LocalGalaxyGateStore(cacheServices.GetRequiredService<IDistributedCache>(), second);
        var afterCacheExpiry = await freshMenu.GetAsync(account, ct);
        Assert.That(afterCacheExpiry.Gates.Single(g => g.Id == "alpha").Activated, Is.True);
        Assert.That(afterCacheExpiry.Energy, Is.EqualTo(216));
        Assert.That(await second.ChangeGalaxyGateAsync(account, "beta", 10, "beta-1", ct), Is.EqualTo(GalaxyGateChange.Applied));
        Assert.That((await first.BalancesAsync(account, ct)).Single(b => b.AssetId == "galaxy_energy").Amount, Is.EqualTo(206));
        Assert.That(await first.AdjustAsync(account, "galaxy_energy", -206, "test_debit", "test", "drain-energy", ct), Is.True);
        Assert.That(await second.ChangeGalaxyGateAsync(account, "beta", 1, "beta-no-energy", ct), Is.EqualTo(GalaxyGateChange.InsufficientEnergy));
        Assert.That((await first.GalaxyGateProgressAsync(account, ct))["beta"].Parts, Is.EqualTo(10));
        Assert.That((await first.BalancesAsync(account, ct)).Single(b => b.AssetId == "galaxy_energy").Amount, Is.Zero);
    }
}
