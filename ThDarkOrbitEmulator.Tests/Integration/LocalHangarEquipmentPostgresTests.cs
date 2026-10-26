using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

// Menu-94: drives the Dock equipment POST (EconomyStore-backed LocalHangarEquipmentStore) against migrated Postgres.
// Assignments live in economy.hangar_equipment_assignments and are checked against owned items under the account
// advisory lock, so one physical item cannot be booked on both ships across replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalHangarEquipmentPostgresTests
{
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 94_000;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await _postgres.StartAsync(ct);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = _postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(_config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _postgres.DisposeAsync();

    [Test]
    public async Task NewPilot_HasNoAssignmentsAndNoRows()
    {
        var harness = await HarnessAsync();

        var state = await harness.Store.GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(state.Assignments, Is.Empty);
            Assert.That(state.Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(4));
            Assert.That(await RowsAsync(harness), Is.Empty);
        });
    }

    [Test]
    public async Task Assign_PersistsRow_VisibleToFreshReplica()
    {
        var harness = await HarnessAsync();

        var context = await AssignAsync(harness, harness.Store, 1, 1, "laser_lf3", 3);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        Assert.That(context.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalDockEquipment&slot=1&configuration=1&saved=laser_lf3"));
        var fresh = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        Assert.Multiple(async () =>
        {
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:laser_lf3:3" }));
            Assert.That(fresh.Assignments, Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "laser_lf3", 3) }));
        });
    }

    [Test]
    public async Task RepeatedForm_IsConflictOnAnotherReplica()
    {
        var harness = await HarnessAsync();
        Assert.That((await AssignAsync(harness, harness.Store, 1, 1, "drone_flax", 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var repeat = await AssignAsync(harness, NewStore(harness), 1, 1, "drone_flax", 1);

        Assert.Multiple(async () =>
        {
            Assert.That(repeat.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(repeat));
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:drone_flax:1" }));
        });
    }

    [Test]
    public async Task ItemBookedOnOtherShip_IsConflict_ButOtherConfigurationMayReuseIt()
    {
        var harness = await HarnessAsync();
        Assert.That((await AssignAsync(harness, harness.Store, 1, 1, "laser_lf3", 4)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var otherShip = await AssignAsync(harness, NewStore(harness), 2, 1, "laser_lf3", 1);
        var otherConfiguration = await AssignAsync(harness, NewStore(harness), 1, 2, "laser_lf3", 4);

        Assert.Multiple(async () =>
        {
            Assert.That(otherShip.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(otherShip));
            Assert.That(otherConfiguration.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:laser_lf3:4", "1:2:laser_lf3:4" }));
        });
    }

    [Test]
    public async Task ConcurrentBookings_ThroughSeparateReplicas_NeverOverbook()
    {
        var harness = await HarnessAsync();

        // Four units of LF-3: each ship asks for three at once, so only one booking fits.
        var contexts = await Task.WhenAll(new[] { 1, 2, 1, 2 }.Select(slot =>
            AssignAsync(harness, NewStore(harness), slot, 1, "laser_lf3", 3)));

        var statuses = contexts.Select(context => context.Response.StatusCode).ToArray();
        var rows = await RowsAsync(harness);
        Assert.Multiple(() =>
        {
            Assert.That(statuses.Count(status => status == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(statuses.Count(status => status == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows.Single(), Does.EndWith(":1:laser_lf3:3"));
        });
    }

    [Test]
    public async Task ZeroQuantity_RemovesRow()
    {
        var harness = await HarnessAsync();
        Assert.That((await AssignAsync(harness, harness.Store, 1, 1, "pet_10", 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var cleared = await AssignAsync(harness, NewStore(harness), 1, 1, "pet_10", 0);

        Assert.Multiple(async () =>
        {
            Assert.That(cleared.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(cleared));
            Assert.That(await RowsAsync(harness), Is.Empty);
        });
    }

    [Test]
    public async Task UnownedItemOrTooMany_IsBadRequestAndSavesNothing()
    {
        var harness = await HarnessAsync();

        var unknown = await AssignAsync(harness, harness.Store, 1, 1, "laser_lf4", 1);
        var tooMany = await AssignAsync(harness, harness.Store, 1, 1, "laser_lf3", 5);

        Assert.Multiple(async () =>
        {
            Assert.That(unknown.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(unknown));
            Assert.That(tooMany.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(tooMany));
            Assert.That(await RowsAsync(harness), Is.Empty);
        });
    }

    [Test]
    public async Task CachedAssignments_AreImportedOnceCappedByOwnedItems()
    {
        var harness = await HarnessAsync();
        await SeedCacheAsync(harness, new LocalEquipmentAssignment(1, 1, "laser_lf3", 9),
            new LocalEquipmentAssignment(2, 1, "drone_iris", 1), new LocalEquipmentAssignment(1, 1, "laser_lf4", 1));

        var state = await harness.Store.GetAsync(harness.Session.AccountId, Ct);
        Assert.That((await AssignAsync(harness, harness.Store, 2, 1, "drone_iris", 0)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        // The cached copy is still there, but the import marker keeps it from coming back.
        await SeedCacheAsync(harness, new LocalEquipmentAssignment(2, 1, "drone_iris", 1));
        var after = await harness.Store.GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(state.Assignments, Is.EquivalentTo(new[]
            {
                new LocalEquipmentAssignment(1, 1, "laser_lf3", 4), new LocalEquipmentAssignment(2, 1, "drone_iris", 1)
            }));
            Assert.That(after.Assignments, Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "laser_lf3", 4) }));
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:laser_lf3:4" }));
        });
    }

    [Test]
    public async Task ChangeBeforeImport_MakesPostgresAuthoritative()
    {
        var harness = await HarnessAsync();
        Assert.That((await AssignAsync(harness, harness.Store, 1, 1, "shield_sg3n_b02", 2)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        // A cache entry written later (for example by an older replica) must not overwrite saved assignments.
        await SeedCacheAsync(harness, new LocalEquipmentAssignment(2, 2, "shield_sg3n_b02", 4));
        var state = await harness.Store.GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(state.Assignments, Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "shield_sg3n_b02", 2) }));
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:shield_sg3n_b02:2" }));
        });
    }

    [Test]
    public async Task LostInventory_CapsAssignmentsOnReadAndNextChange()
    {
        var harness = await HarnessAsync();
        Assert.That((await AssignAsync(harness, harness.Store, 1, 1, "laser_lf3", 4)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        await ExecuteAsync("UPDATE economy.inventory SET quantity=1 WHERE account_id=@a AND item_id='laser_lf3'", harness);

        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        Assert.That((await AssignAsync(harness, NewStore(harness), 1, 1, "laser_lf2", 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        Assert.Multiple(async () =>
        {
            Assert.That(state.Assignments, Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "laser_lf3", 1) }));
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:laser_lf2:1", "1:1:laser_lf3:1" }));
        });
    }

    [Test]
    public async Task OtherSaves_DoNotCopyAssignmentsIntoCache()
    {
        var harness = await HarnessAsync();
        Assert.That((await AssignAsync(harness, harness.Store, 1, 1, "drone_iris", 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var purchased = await harness.Store.PurchaseAsync(harness.Session.AccountId, "drone_flax", 1, Ct);
        var json = await harness.Cache.GetStringAsync(CacheKey(harness), Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(purchased, Is.True);
            Assert.That(json, Is.Not.Null);
            Assert.That(JsonSerializer.Deserialize<LocalEquipmentState>(json!)!.Assignments, Is.Empty);
            Assert.That((await NewStore(harness).GetAsync(harness.Session.AccountId, Ct)).Assignments,
                Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "drone_iris", 1) }));
        });
    }

    [Test]
    public async Task ExtraPurchasedFromShop_UsesShipSlotsAcrossReplicas()
    {
        var harness = await HarnessAsync();
        await harness.Store.GetAsync(harness.Session.AccountId, Ct);
        var purchase = await harness.Economy.PurchaseAsync(harness.Session.AccountId,
            "extra_cpu", 2, "credits", Guid.NewGuid().ToString("N"), Ct);
        Assert.That(purchase, Is.EqualTo(EconomyPurchaseResult.Applied));

        var purchased = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        Assert.Multiple(() =>
        {
            Assert.That(purchased.Credits, Is.EqualTo(8500));
            Assert.That(purchased.Inventory.Single(item => item.Id == "extra_cpu").Quantity, Is.EqualTo(2));
        });

        var equip = await AssignAsync(harness, harness.Store, 1, 1, "extra_cpu", 2);
        var overCapacity = await AssignAsync(harness, NewStore(harness), 2, 1, "extra_cpu", 1);
        var fresh = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        Assert.Multiple(async () =>
        {
            Assert.That(equip.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(equip));
            Assert.That(overCapacity.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(overCapacity));
            Assert.That(fresh.Assignments, Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "extra_cpu", 2) }));
            Assert.That(await RowsAsync(harness), Is.EqualTo(new[] { "1:1:extra_cpu:2" }));
        });
    }

    [Test]
    public void Database_RejectsInvalidRows()
    {
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<PostgresException>(async () => await InsertRowAsync(3, 1, "laser_lf3", 1));
            Assert.ThrowsAsync<PostgresException>(async () => await InsertRowAsync(1, 3, "laser_lf3", 1));
            Assert.ThrowsAsync<PostgresException>(async () => await InsertRowAsync(1, 1, "laser_lf3", 0));
            Assert.ThrowsAsync<PostgresException>(async () => await InsertRowAsync(1, 1, "no_such_item", 1));
        });
    }

    private async Task InsertRowAsync(int slot, int configuration, string item, int quantity)
    {
        var harness = await HarnessAsync();
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.hangar_equipment_assignments(account_id,hangar_slot,configuration,item_id,quantity) VALUES (@a,@s,@c,@i,@q)", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("s", slot);
        cmd.Parameters.AddWithValue("c", configuration);
        cmd.Parameters.AddWithValue("i", item);
        cmd.Parameters.AddWithValue("q", quantity);
        await cmd.ExecuteNonQueryAsync();
    }

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        IDistributedCache Cache, LocalHangarEquipmentStore Store);

    private static string CacheKey(Harness harness) => $"localhangar:equipment:{harness.Session.AccountId:N}";

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalHangarEquipmentStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

    private static async Task SeedCacheAsync(Harness harness, params LocalEquipmentAssignment[] assignments)
    {
        // A cache-era state: starter inventory plus the given loadout.
        var state = new LocalEquipmentState([
            new("laser_lf3", "LF-3 laser", "Equipment", 4), new("laser_lf2", "LF-2 laser", "Equipment", 2),
            new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 4), new("shield_sg3n_a03", "SG3N-A03 shield", "Equipment", 2),
            new("drone_flax", "Flax drone", "Drones", 1), new("drone_iris", "Iris drone", "Drones", 1),
            new("pet_10", "P.E.T. 10", "PET", 1)], [.. assignments]);
        await harness.Cache.SetStringAsync(CacheKey(harness), JsonSerializer.Serialize(state), Ct);
    }

    private async Task<Harness> HarnessAsync()
    {
        var gameUserId = Interlocked.Increment(ref _nextGameUserId);
        var accountId = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await db.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,@u,@g)", db);
            insert.Parameters.AddWithValue("a", accountId);
            insert.Parameters.AddWithValue("u", $"equipment-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"equipment-pilot-{gameUserId}", null, gameUserId), null, Ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", Ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, Ct))!;
        var economy = new EconomyStore(_config);
        var cache = NewCache();
        return new Harness(sessions, session, economy, cache, new LocalHangarEquipmentStore(cache, economy));
    }

    private static async Task<DefaultHttpContext> AssignAsync(Harness harness, LocalHangarEquipmentStore store,
        int slot, int configuration, string item, int quantity)
    {
        var form = $"slot={slot}&configuration={configuration}&item={item}&quantity={quantity}";
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalDockEquipmentSet");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalHangarEquipmentSelection.HandleAsync(context, harness.Sessions, store, Ct);
        return context;
    }

    private async Task<List<string>> RowsAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT hangar_slot,configuration,item_id,quantity FROM economy.hangar_equipment_assignments WHERE account_id=@a ORDER BY hangar_slot,configuration,item_id", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await using var rows = await cmd.ExecuteReaderAsync();
        var result = new List<string>();
        while (await rows.ReadAsync())
            result.Add($"{rows.GetInt32(0)}:{rows.GetInt32(1)}:{rows.GetString(2)}:{rows.GetInt32(3)}");
        return result;
    }

    private async Task ExecuteAsync(string sql, Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
