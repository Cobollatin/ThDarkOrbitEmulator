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

// Menu-82: drives the Skylab upgrade/collect POST handlers (EconomyStore-backed LocalSkylabStore)
// against migrated Postgres. Collectors start at level 1 with 500 units; an upgrade costs
// 100 x level of the collector's own resource, production is 10 x level per minute, capped at
// 60 minutes per collect. Form keys are single-use and the account lock serializes replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalSkylabPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 82_000;

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
    public async Task FirstRead_ImportsStarterCollectorsOnce()
    {
        var harness = await HarnessAsync();
        await harness.Skylab.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);

        await Assert.MultipleAsync(async () =>
        {
            foreach (var (id, _) in LocalSkylab.Catalog)
            {
                Assert.That(await BalanceAsync(harness, id), Is.EqualTo(500), id);
                Assert.That(await LevelAsync(harness, id), Is.EqualTo(1), id);
            }
            Assert.That(await LastCollectedAsync(harness), Is.EqualTo(Start));
        });
    }

    [Test]
    public async Task Upgrade_AtCurrentLevel_DebitsOwnResourceAndRaisesLevel()
    {
        var harness = await HarnessAsync();

        var context = await UpgradeAsync(harness, harness.Skylab, "prometium", 1);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalSkylab&upgraded=prometium"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await LevelAsync(harness, "prometium"), Is.EqualTo(2));
            Assert.That(await BalanceAsync(harness, "prometium"), Is.EqualTo(400));
            Assert.That(await BalanceAsync(harness, "endurium"), Is.EqualTo(500));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_upgrade"), Is.EqualTo(new long[] { -100 }));
        });
    }

    [Test]
    public async Task Upgrade_UntilShort_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();

        var first = await UpgradeAsync(harness, harness.Skylab, "endurium", 1);
        var second = await UpgradeAsync(harness, harness.Skylab, "endurium", 2);
        // 500 - 100 - 200 leaves 200, short of the 300 needed at level 3.
        var third = await UpgradeAsync(harness, harness.Skylab, "endurium", 3);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(second.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(second));
            Assert.That(third.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(third));
            Assert.That(Body(third), Does.Contain("not enough resources"));
            Assert.That(await LevelAsync(harness, "endurium"), Is.EqualTo(3));
            Assert.That(await BalanceAsync(harness, "endurium"), Is.EqualTo(200));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_upgrade"), Is.EqualTo(new long[] { -100, -200 }));
        });
    }

    [Test]
    public async Task Upgrade_StaleLevel_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();

        var context = await UpgradeAsync(harness, harness.Skylab, "terbium", 2);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("state has changed"));
            Assert.That(await LevelAsync(harness, "terbium"), Is.EqualTo(1));
            Assert.That(await BalanceAsync(harness, "terbium"), Is.EqualTo(500));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_upgrade"), Is.Empty);
        });
    }

    [Test]
    public async Task Upgrade_AtMaximumLevel_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        await SetLevelAsync(harness, "prometium", LocalSkylab.MaxLevel);

        var context = await UpgradeAsync(harness, harness.Skylab, "prometium", LocalSkylab.MaxLevel);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("maximum level"));
            Assert.That(await LevelAsync(harness, "prometium"), Is.EqualTo(LocalSkylab.MaxLevel));
            Assert.That(await BalanceAsync(harness, "prometium"), Is.EqualTo(500));
        });
    }

    [Test]
    public async Task Upgrade_ReplayedKey_AppliesOnce()
    {
        var harness = await HarnessAsync();
        var key = LocalSkylabAction.CreateFormKey(harness.Session.Dosid, "prometium", "1", harness.Clock.GetUtcNow());
        var form = $"collector=prometium&level=1&key={Uri.EscapeDataString(key)}";

        var first = await PostAsync(harness, harness.Skylab, "internalSkylabUpgrade", form);
        // A fresh cache models the same form resubmitted to another replica.
        var replay = await PostAsync(harness, NewStore(harness), "internalSkylabUpgrade", form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(await LevelAsync(harness, "prometium"), Is.EqualTo(2));
            Assert.That(await BalanceAsync(harness, "prometium"), Is.EqualTo(400));
        });
    }

    [Test]
    public async Task Upgrade_ConcurrentFormsFromSeparateInstances_DebitOnce()
    {
        var harness = await HarnessAsync();
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore(harness)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => UpgradeAsync(harness, store, "terbium", 1)));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await LevelAsync(harness, "terbium"), Is.EqualTo(2));
            Assert.That(await BalanceAsync(harness, "terbium"), Is.EqualTo(400));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_upgrade"), Is.EqualTo(new long[] { -100 }));
        });
    }

    [Test]
    public async Task Upgrade_UnknownCollector_Returns404()
    {
        var harness = await HarnessAsync();

        var context = await UpgradeAsync(harness, harness.Skylab, "xenomit", 1);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_upgrade"), Is.Empty);
        });
    }

    [Test]
    public async Task Upgrade_ExpiredFormKey_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var key = LocalSkylabAction.CreateFormKey(harness.Session.Dosid, "prometium", "1",
            harness.Clock.GetUtcNow() - LocalSkylabAction.FormLifetime - TimeSpan.FromSeconds(1));

        var context = await PostAsync(harness, harness.Skylab, "internalSkylabUpgrade",
            $"collector=prometium&level=1&key={Uri.EscapeDataString(key)}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("expired"));
            Assert.That(await LevelAsync(harness, "prometium"), Is.EqualTo(1));
            Assert.That(await BalanceAsync(harness, "prometium"), Is.EqualTo(500));
        });
    }

    [Test]
    public async Task Collect_BeforeOneMinute_Returns409()
    {
        var harness = await HarnessAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(59));

        var context = await CollectAsync(harness, harness.Skylab, Start);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("Nothing to collect"));
            Assert.That(await BalanceAsync(harness, "prometium"), Is.EqualTo(500));
            Assert.That(await LastCollectedAsync(harness), Is.EqualTo(Start));
        });
    }

    [Test]
    public async Task Collect_WholeMinutes_CreditsEachCollectorByLevel()
    {
        var harness = await HarnessAsync();
        Assert.That((await UpgradeAsync(harness, harness.Skylab, "prometium", 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromSeconds(5 * 60 + 30));

        var context = await CollectAsync(harness, harness.Skylab, Start);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            // Level 2 yields 20/min, level 1 yields 10/min; the partial half minute stays pending.
            Assert.That(await BalanceAsync(harness, "prometium"), Is.EqualTo(400 + 100));
            Assert.That(await BalanceAsync(harness, "endurium"), Is.EqualTo(500 + 50));
            Assert.That(await BalanceAsync(harness, "terbium"), Is.EqualTo(500 + 50));
            Assert.That(await LastCollectedAsync(harness), Is.EqualTo(Start.AddMinutes(5)));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_collect"), Is.EquivalentTo(new long[] { 100, 50, 50 }));
        });
    }

    [Test]
    public async Task Collect_AfterLongAbsence_IsCappedAtSixtyMinutes()
    {
        var harness = await HarnessAsync();
        harness.Clock.Advance(TimeSpan.FromHours(3));

        var context = await CollectAsync(harness, harness.Skylab, Start);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(await BalanceAsync(harness, "terbium"), Is.EqualTo(500 + LocalSkylab.MaxCollectMinutes * 10));
            Assert.That(await LastCollectedAsync(harness), Is.EqualTo(Start.AddHours(3)));
        });
        var second = await CollectAsync(harness, harness.Skylab, Start.AddHours(3));
        Assert.That(second.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(second));
        Assert.That(await BalanceAsync(harness, "terbium"), Is.EqualTo(500 + LocalSkylab.MaxCollectMinutes * 10));
    }

    [Test]
    public async Task Collect_StaleTimeAfterCollect_Returns409WithoutCredit()
    {
        var harness = await HarnessAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        var first = await CollectAsync(harness, harness.Skylab, Start);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        // A second tab still holds the original collect form with the old timestamp.
        var stale = await CollectAsync(harness, NewStore(harness), Start);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
            Assert.That(await BalanceAsync(harness, "endurium"), Is.EqualTo(520));
            Assert.That(await LedgerDeltasAsync(harness, "skylab_collect"), Has.Length.EqualTo(3));
        });
    }

    [Test]
    public async Task Collect_AfterMenuCacheLoss_UsesPersistedProgress()
    {
        var harness = await HarnessAsync();
        Assert.That((await UpgradeAsync(harness, harness.Skylab, "endurium", 1)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(3));

        // The fresh store has no cached state; levels and timestamp must come from Postgres.
        var fresh = NewStore(harness);
        var state = await fresh.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);
        var context = await CollectAsync(harness, fresh, state.LastCollected);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(state.Collectors.Single(c => c.Id == "endurium").Level, Is.EqualTo(2));
            Assert.That(state.Collectors.Single(c => c.Id == "endurium").Stock, Is.EqualTo(400));
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(await BalanceAsync(harness, "endurium"), Is.EqualTo(400 + 60));
        });
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalSkylabStore Skylab, MutableClock Clock);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalSkylabStore NewStore(Harness harness) => new(NewCache(), harness.Clock, harness.Economy);

    private async Task<Harness> HarnessAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var gameUserId = Interlocked.Increment(ref _nextGameUserId);
        var accountId = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,@u,@g)", db);
            insert.Parameters.AddWithValue("a", accountId);
            insert.Parameters.AddWithValue("u", $"skylab-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"skylab-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var clock = new MutableClock(Start);
        var skylab = new LocalSkylabStore(NewCache(), clock, economy);
        // The first Skylab page read imports the starter stock and collector rows.
        await skylab.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, skylab, clock);
    }

    private static Task<DefaultHttpContext> UpgradeAsync(Harness harness, LocalSkylabStore store, string collector, int level)
    {
        var key = LocalSkylabAction.CreateFormKey(harness.Session.Dosid, collector, level.ToString(), harness.Clock.GetUtcNow());
        return PostAsync(harness, store, "internalSkylabUpgrade",
            $"collector={collector}&level={level}&key={Uri.EscapeDataString(key)}");
    }

    private static Task<DefaultHttpContext> CollectAsync(Harness harness, LocalSkylabStore store, DateTimeOffset lastCollected)
    {
        var time = lastCollected.Ticks.ToString();
        var key = LocalSkylabAction.CreateFormKey(harness.Session.Dosid, "collect", time, harness.Clock.GetUtcNow());
        return PostAsync(harness, store, "internalSkylabCollect", $"time={time}&key={Uri.EscapeDataString(key)}");
    }

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalSkylabStore store,
        string action, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalSkylabAction.HandleAsync(context, harness.Sessions, store,
            TestContext.CurrentContext.CancellationToken, harness.Clock);
        return context;
    }

    private static async Task<long> BalanceAsync(Harness harness, string assetId) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == assetId).Amount;

    private async Task<int> LevelAsync(Harness harness, string collector)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT level FROM economy.skylab_collectors WHERE account_id=@a AND collector_id=@c", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("c", collector);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<DateTimeOffset> LastCollectedAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT last_collected FROM economy.skylab_collectors WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await using var rows = await cmd.ExecuteReaderAsync();
        var values = new List<DateTimeOffset>();
        while (await rows.ReadAsync()) values.Add(rows.GetFieldValue<DateTimeOffset>(0));
        Assert.That(values, Has.Count.EqualTo(1), "collectors share one collection timestamp");
        return values[0];
    }

    private async Task<long[]> LedgerDeltasAsync(Harness harness, string reason)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT delta FROM economy.ledger WHERE account_id=@a AND reason=@r ORDER BY id", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("r", reason);
        var deltas = new List<long>();
        await using var row = await cmd.ExecuteReaderAsync();
        while (await row.ReadAsync()) deltas.Add(row.GetInt64(0));
        return deltas.ToArray();
    }

    private async Task SetLevelAsync(Harness harness, string collector, int level)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE economy.skylab_collectors SET level=@l WHERE account_id=@a AND collector_id=@c", db);
        update.Parameters.AddWithValue("l", level);
        update.Parameters.AddWithValue("a", harness.Session.AccountId);
        update.Parameters.AddWithValue("c", collector);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
