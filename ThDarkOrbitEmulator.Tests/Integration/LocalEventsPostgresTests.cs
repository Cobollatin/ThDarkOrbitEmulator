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

// Menu-85: drives the Events battle pass and expedition POST handlers against migration-seeded
// Postgres battle_pass offers (011/020). Progress comes from real Postgres Shop purchases and
// Assembly crafts. Battle pass rewards are keyed battlepass:{tier}, the expedition debit
// expedition:start and its shield grant expedition:reward, so each applies once per account
// even across replicas, cache loss, and concurrent submits.
[TestFixture]
[Category("Integration")]
public sealed class LocalEventsPostgresTests
{
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 85_000;

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
    public async Task SeededOffers_MatchCacheFallbackTiers()
    {
        var offers = await new EconomyStore(_config).RewardOffersAsync("battle_pass", TestContext.CurrentContext.CancellationToken);

        Assert.That(offers.Select(o => (o.Id, o.Name, o.AssetId, o.Amount, o.Target)),
            Is.EquivalentTo(LocalEvents.Tiers.Select(t => (t.Id, t.Name, "credits", (long)t.RewardCredits, (long)t.Target))));
    }

    [TestCase("supplier", 1, 0, 200)]
    [TestCase("engineer", 0, 1, 300)]
    [TestCase("quartermaster", 3, 0, 400)]
    [TestCase("artisan", 0, 3, 600)]
    [TestCase("tycoon", 10, 0, 1000)]
    public async Task BattlePass_AtTarget_CreditsRewardOnceThenReturns409(string tier, int purchases, int crafts, long reward)
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, purchases);
        await CraftAsync(harness, crafts);
        var before = await CreditsAsync(harness);

        var first = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", $"tier={tier}");
        var second = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", $"tier={tier}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo($"/indexInternal.es?action=internalEvents&claimed={tier}"));
            Assert.That(first.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(second.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(second));
            Assert.That(Body(second), Does.Contain("already claimed"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + reward));
            Assert.That(await LedgerDeltasAsync(harness, $"battlepass:{tier}"), Is.EqualTo(new[] { reward }));
        });
    }

    [TestCase("quartermaster", 2, 0)]
    [TestCase("artisan", 0, 2)]
    [TestCase("tycoon", 9, 0)]
    public async Task BattlePass_OneShort_Returns409WithoutCredit(string tier, int purchases, int crafts)
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, purchases);
        await CraftAsync(harness, crafts);
        var before = await CreditsAsync(harness);

        var context = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", $"tier={tier}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("not ready"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await BattlePassLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [TestCase("first_purchase")] // a quest offer id, not a battle pass tier
    [TestCase("unknown")]
    public async Task BattlePass_UnknownTier_Returns404WithoutCredit(string tier)
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);

        var context = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", $"tier={tier}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(await BattlePassLedgerRowsAsync(harness), Is.Zero);
            Assert.That(await LedgerDeltasAsync(harness, $"quest:{tier}"), Is.Empty);
        });
    }

    [Test]
    public async Task BattlePass_DisabledTier_Returns409EvenWhenComplete()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 3);
        var before = await CreditsAsync(harness);
        await SetTierEnabledAsync("quartermaster", false);
        DefaultHttpContext disabled;
        try { disabled = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", "tier=quartermaster"); }
        finally { await SetTierEnabledAsync("quartermaster", true); }

        var enabled = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", "tier=quartermaster");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(disabled.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(disabled));
            Assert.That(Body(disabled), Does.Contain("not available"));
            Assert.That(enabled.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(enabled));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 400));
        });
    }

    [Test]
    public async Task BattlePass_FromFreshReplicaAfterClaim_Returns409WithoutSecondGrant()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);
        var first = await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", "tier=supplier");
        var afterFirst = await CreditsAsync(harness);

        var replay = await PostAsync(harness, NewStore(harness), "internalBattlePassClaim", "tier=supplier");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(afterFirst));
            Assert.That(await LedgerDeltasAsync(harness, "battlepass:supplier"), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public async Task BattlePass_ConcurrentFromSeparateInstances_GrantsOnce()
    {
        var harness = await HarnessAsync();
        await CraftAsync(harness, 3);
        var before = await CreditsAsync(harness);
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore(harness)).ToArray();

        var results = await Task.WhenAll(stores.Select(store =>
            PostAsync(harness, store, "internalBattlePassClaim", "tier=artisan")));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 600));
            Assert.That(await LedgerDeltasAsync(harness, "battlepass:artisan"), Is.EqualTo(new long[] { 600 }));
        });
    }

    [Test]
    public async Task Page_ListsAllFiveSeededTiersAndClaims()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 3);
        Assert.That((await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", "tier=supplier")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        var ct = TestContext.CurrentContext.CancellationToken;
        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, ct);

        var html = LocalEvents.Page(state, harness.Clock.GetUtcNow(), await harness.Economy.RewardOffersAsync("battle_pass", ct));

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("1 of 5 tiers claimed"));
            foreach (var tier in LocalEvents.Tiers) Assert.That(html, Does.Contain(tier.Name));
            Assert.That(html, Does.Contain("action=internalBattlePassClaim"));
        });
    }

    // Menu-211: status tabs use the seeded Postgres offers and the saved claim state.
    [Test]
    public async Task Page_FiltersSeededTiersByStatus()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 3);
        Assert.That((await PostAsync(harness, harness.Equipment, "internalBattlePassClaim", "tier=supplier")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        var ct = TestContext.CurrentContext.CancellationToken;
        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, ct);
        var offers = await harness.Economy.RewardOffersAsync("battle_pass", ct);

        var ready = LocalEvents.Page(state, harness.Clock.GetUtcNow(), offers, filter: "ready");

        Assert.Multiple(() =>
        {
            Assert.That(ready, Does.Contain("<tr data-tier=\"quartermaster\" data-status=\"ready\">"));
            Assert.That(ready, Does.Not.Contain("<tr data-tier=\"supplier\""));
            Assert.That(ready, Does.Contain("data-filter=\"claimed\">Claimed (1)"));
            Assert.That(LocalEvents.TierInFilter(state, "supplier", "claimed", offers), Is.True);
            Assert.That(LocalEvents.TierInFilter(state, "supplier", "ready", offers), Is.False);
        });
    }

    [Test]
    public async Task Expedition_StartWaitClaim_DebitsOnceAndGrantsShieldOnce()
    {
        var harness = await HarnessAsync();
        var credits = await CreditsAsync(harness);
        var shields = await ShieldsAsync(harness);

        var start = await PostAsync(harness, harness.Equipment, "internalExpeditionStart", "start=1");
        var restart = await PostAsync(harness, harness.Equipment, "internalExpeditionStart", "start=1");
        var early = await PostAsync(harness, harness.Equipment, "internalExpeditionClaim", "claim=1");
        harness.Clock.Advance(TimeSpan.FromMinutes(LocalEvents.ExpeditionMinutes));
        var claim = await PostAsync(harness, harness.Equipment, "internalExpeditionClaim", "claim=1");
        var reclaim = await PostAsync(harness, harness.Equipment, "internalExpeditionClaim", "claim=1");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(start.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(start));
            Assert.That(restart.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(restart));
            Assert.That(early.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(early));
            Assert.That(Body(early), Does.Contain("not ready"));
            Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(claim));
            Assert.That(reclaim.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(reclaim));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(credits - LocalEvents.ExpeditionCost));
            Assert.That(await LedgerDeltasAsync(harness, "expedition:start"), Is.EqualTo(new long[] { -LocalEvents.ExpeditionCost }));
            Assert.That(await ShieldsAsync(harness), Is.EqualTo(shields + 1));
            Assert.That(await ItemGrantRowsAsync(harness, "expedition:reward"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Expedition_InsufficientCredits_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var credits = await CreditsAsync(harness);
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "credits",
            -(credits - LocalEvents.ExpeditionCost + 1), "test_drain", "test", $"test:drain:{Guid.NewGuid():N}",
            TestContext.CurrentContext.CancellationToken), Is.True);

        var context = await PostAsync(harness, NewStore(harness), "internalExpeditionStart", "start=1");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("enough local credits"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(LocalEvents.ExpeditionCost - 1));
            Assert.That(await LedgerDeltasAsync(harness, "expedition:start"), Is.Empty);
        });
    }

    [Test]
    public async Task Expedition_FreshReplica_RestoresStartAndClaimsOnce()
    {
        var harness = await HarnessAsync();
        var credits = await CreditsAsync(harness);
        var shields = await ShieldsAsync(harness);
        var start = await PostAsync(harness, harness.Equipment, "internalExpeditionStart", "start=1");

        // Fresh stores have no cached state; the expedition:start ledger row must restore the timer.
        var restart = await PostAsync(harness, NewStore(harness), "internalExpeditionStart", "start=1");
        var early = await PostAsync(harness, NewStore(harness), "internalExpeditionClaim", "claim=1");
        harness.Clock.Advance(TimeSpan.FromMinutes(LocalEvents.ExpeditionMinutes + 1));
        var claim = await PostAsync(harness, NewStore(harness), "internalExpeditionClaim", "claim=1");
        var reclaim = await PostAsync(harness, NewStore(harness), "internalExpeditionClaim", "claim=1");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(start.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(start));
            Assert.That(restart.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(restart));
            Assert.That(early.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(early));
            Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(claim));
            Assert.That(reclaim.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(reclaim));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(credits - LocalEvents.ExpeditionCost));
            Assert.That(await ShieldsAsync(harness), Is.EqualTo(shields + 1));
        });
    }

    [Test]
    public async Task Expedition_ConcurrentStartsFromSeparateInstances_DebitOnce()
    {
        var harness = await HarnessAsync();
        var credits = await CreditsAsync(harness);
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore(harness)).ToArray();

        var results = await Task.WhenAll(stores.Select(store =>
            PostAsync(harness, store, "internalExpeditionStart", "start=1")));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(credits - LocalEvents.ExpeditionCost));
            Assert.That(await LedgerDeltasAsync(harness, "expedition:start"), Has.Length.EqualTo(1));
        });
    }

    // Starts at real time because a fresh replica restores the expedition timer from the
    // ledger's database timestamp, which the production clock tracks.
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalHangarEquipmentStore Equipment, MutableClock Clock);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalHangarEquipmentStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

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
            insert.Parameters.AddWithValue("u", $"events-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"events-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var equipment = new LocalHangarEquipmentStore(NewCache(), economy);
        // The first menu read imports the starter wallet and inventory, as the Events page does.
        await equipment.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, equipment, new MutableClock());
    }

    // Real Postgres Shop purchases of the 500-Credit Flax drone, funded by a test grant.
    private static async Task BuyAsync(Harness harness, int count)
    {
        if (count == 0) return;
        var ct = TestContext.CurrentContext.CancellationToken;
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "credits", 500L * count,
            "test_grant", "test", $"test:buy:{Guid.NewGuid():N}", ct), Is.True);
        for (var i = 0; i < count; i++)
            Assert.That(await harness.Economy.PurchaseAsync(harness.Session.AccountId, "drone_flax", 1,
                $"test:order:{Guid.NewGuid():N}", ct), Is.EqualTo(EconomyPurchaseResult.Applied));
    }

    // Real Postgres Assembly crafts of the seeded SG3N-B02 recipe (100 components + 400 Credits).
    private static async Task CraftAsync(Harness harness, int count)
    {
        if (count == 0) return;
        var ct = TestContext.CurrentContext.CancellationToken;
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "credits", 400L * count,
            "test_grant", "test", $"test:craft-credits:{Guid.NewGuid():N}", ct), Is.True);
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "assembly_components", 100L * count,
            "test_grant", "test", $"test:craft-components:{Guid.NewGuid():N}", ct), Is.True);
        for (var i = 0; i < count; i++)
            Assert.That(await harness.Economy.CraftAsync(harness.Session.AccountId, "shield",
                $"test:craft:{Guid.NewGuid():N}", ct), Is.EqualTo(EconomyCraftResult.Applied));
    }

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalHangarEquipmentStore store,
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
        await LocalEventsAction.HandleAsync(context, harness.Sessions, store, harness.Clock,
            TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static async Task<long> CreditsAsync(Harness harness) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == "credits").Amount;

    private static async Task<long> ShieldsAsync(Harness harness) =>
        (await harness.Economy.OwnedItemsAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Where(item => item.Id == LocalEvents.ExpeditionRewardItemId).Sum(item => item.Quantity);

    private async Task<long[]> LedgerDeltasAsync(Harness harness, string key)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT delta FROM economy.ledger WHERE account_id=@a AND idempotency_key=@k ORDER BY id", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("k", key);
        var deltas = new List<long>();
        await using var row = await cmd.ExecuteReaderAsync();
        while (await row.ReadAsync()) deltas.Add(row.GetInt64(0));
        return deltas.ToArray();
    }

    private async Task<long> BattlePassLedgerRowsAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM economy.ledger WHERE account_id=@a AND idempotency_key LIKE 'battlepass:%'", db);
        count.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task<long> ItemGrantRowsAsync(Harness harness, string key)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM economy.item_grants WHERE account_id=@a AND idempotency_key=@k", db);
        count.Parameters.AddWithValue("a", harness.Session.AccountId);
        count.Parameters.AddWithValue("k", key);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task SetTierEnabledAsync(string tier, bool enabled)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE economy.reward_offers SET enabled=@e WHERE source='battle_pass' AND id=@i", db);
        update.Parameters.AddWithValue("e", enabled);
        update.Parameters.AddWithValue("i", tier);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
