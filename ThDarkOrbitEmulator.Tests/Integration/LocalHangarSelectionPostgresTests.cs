using System.Text;
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

// Menu-93: drives the Dock ship-selection POST (EconomyStore-backed LocalHangarStore) against migrated Postgres.
// The active slot lives in economy.hangar_selections, and the account advisory lock makes one signed Dock form
// apply once across replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalHangarSelectionPostgresTests
{
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 93_000;

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
    public async Task NewPilot_DefaultsToSlotOneWithoutRow()
    {
        var harness = await HarnessAsync();

        var slot = await harness.Store.GetActiveSlotAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(slot, Is.EqualTo(LocalHangars.DefaultSlot));
            Assert.That(await RowAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task Select_PersistsRow_VisibleToFreshReplicaAndDockPage()
    {
        var harness = await HarnessAsync();

        var context = await SelectAsync(harness, harness.Store, slot: 2, current: 1);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalDock&activated=2"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
        var fresh = await NewStore(harness).GetActiveSlotAsync(harness.Session.AccountId, Ct);
        var hangars = LocalHangars.For(harness.Session, fresh);
        Assert.Multiple(async () =>
        {
            Assert.That(await RowAsync(harness), Is.EqualTo(2));
            Assert.That(fresh, Is.EqualTo(2));
            Assert.That(hangars.Single(h => h.HangarActive).SlotNumber, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ReplayedForm_IsConflictOnAnotherReplica()
    {
        var harness = await HarnessAsync();
        var key = LocalHangars.CreateFormKey(harness.Session.Dosid, 2, 1);
        Assert.That((await SelectAsync(harness, harness.Store, 2, 1, key)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var replay = await SelectAsync(harness, NewStore(harness), 2, 1, key);

        Assert.Multiple(async () =>
        {
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(await RowAsync(harness), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task StaleCurrentSlot_IsConflictAndKeepsRow()
    {
        var harness = await HarnessAsync();
        Assert.That((await SelectAsync(harness, harness.Store, 2, 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        Assert.That((await SelectAsync(harness, NewStore(harness), 1, 2)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        // A Dock page loaded while slot 2 was active (before the switch back) is now stale.
        var stale = await SelectAsync(harness, NewStore(harness), 1, 2);

        Assert.Multiple(async () =>
        {
            Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
            Assert.That(await RowAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ConcurrentSelects_ThroughSeparateReplicas_ApplyOnce()
    {
        var harness = await HarnessAsync();

        var contexts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            SelectAsync(harness, NewStore(harness), 2, 1)));

        var statuses = contexts.Select(context => context.Response.StatusCode).ToArray();
        Assert.Multiple(async () =>
        {
            Assert.That(statuses.Count(status => status == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(statuses.Count(status => status == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await RowAsync(harness), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task InvalidSlotOrTamperedKey_IsBadRequestAndSavesNothing()
    {
        var harness = await HarnessAsync();

        var badSlot = await SelectAsync(harness, harness.Store, "slot=3&current=1&key=x");
        var tampered = await SelectAsync(harness, harness.Store,
            $"slot=2&current=1&key={Uri.EscapeDataString(LocalHangars.CreateFormKey(harness.Session.Dosid, 1, 2))}");

        Assert.Multiple(async () =>
        {
            Assert.That(badSlot.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(badSlot));
            Assert.That(tampered.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(tampered));
            Assert.That(await RowAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task ExpiredForm_IsConflictAndSavesNothing()
    {
        var harness = await HarnessAsync();
        var issued = DateTimeOffset.UtcNow - LocalHangars.FormLifetime - TimeSpan.FromMinutes(1);
        var key = LocalHangars.CreateFormKey(harness.Session.Dosid, 2, 1, issued);

        var expired = await SelectAsync(harness, harness.Store, 2, 1, key);

        Assert.Multiple(async () =>
        {
            Assert.That(expired.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(expired));
            Assert.That(await RowAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task CachedSlot_IsImportedOnce_AndNeverOverwritesRow()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync(CacheKey(harness), "2", Ct);

        var imported = await harness.Store.GetActiveSlotAsync(harness.Session.AccountId, Ct);
        Assert.That(imported, Is.EqualTo(2));
        Assert.That(await harness.Cache.GetStringAsync(CacheKey(harness), Ct), Is.Null);
        Assert.That(await RowAsync(harness), Is.EqualTo(2));

        // A second, stale cache copy on another replica must not replace the stored slot.
        var otherCache = NewCache();
        await otherCache.SetStringAsync(CacheKey(harness), "1", Ct);
        var other = new LocalHangarStore(otherCache, harness.Economy);

        Assert.Multiple(async () =>
        {
            Assert.That(await other.GetActiveSlotAsync(harness.Session.AccountId, Ct), Is.EqualTo(2));
            Assert.That(await otherCache.GetStringAsync(CacheKey(harness), Ct), Is.Null);
            Assert.That(await RowAsync(harness), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task CachedSlot_IsImportedBeforeCompareAndSet()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync(CacheKey(harness), "2", Ct);

        // The Dock page rendered from the cache showed slot 2 as active, so its form switches 2 -> 1.
        var context = await SelectAsync(harness, harness.Store, 1, 2);

        Assert.Multiple(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(await RowAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task UnreadableCachedSlot_FallsBackToDefaultRow()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync(CacheKey(harness), "07", Ct);

        var slot = await harness.Store.GetActiveSlotAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(async () =>
        {
            Assert.That(slot, Is.EqualTo(LocalHangars.DefaultSlot));
            Assert.That(await harness.Cache.GetStringAsync(CacheKey(harness), Ct), Is.Null);
        });
    }

    [Test]
    public void OutOfRangeRow_IsRejectedByDatabase()
    {
        Assert.ThrowsAsync<PostgresException>(async () =>
        {
            var harness = await HarnessAsync();
            await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO economy.hangar_selections(account_id,active_slot) VALUES (@a,3)", db);
            cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        IDistributedCache Cache, LocalHangarStore Store);

    private static string CacheKey(Harness harness) => $"localhangar:active:{harness.Session.AccountId:N}";

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalHangarStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

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
            insert.Parameters.AddWithValue("u", $"hangar-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"hangar-pilot-{gameUserId}", null, gameUserId), null, Ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", Ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, Ct))!;
        var economy = new EconomyStore(_config);
        var cache = NewCache();
        return new Harness(sessions, session, economy, cache, new LocalHangarStore(cache, economy));
    }

    private static Task<DefaultHttpContext> SelectAsync(Harness harness, LocalHangarStore store, int slot, int current,
        string? key = null) =>
        SelectAsync(harness, store, $"slot={slot}&current={current}&key=" +
            Uri.EscapeDataString(key ?? LocalHangars.CreateFormKey(harness.Session.Dosid, slot, current)));

    private static async Task<DefaultHttpContext> SelectAsync(Harness harness, LocalHangarStore store, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalDockSelect");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalHangarSelection.HandleAsync(context, harness.Sessions, store, Ct);
        return context;
    }

    private async Task<int?> RowAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT active_slot FROM economy.hangar_selections WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return await cmd.ExecuteScalarAsync() as int?;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
