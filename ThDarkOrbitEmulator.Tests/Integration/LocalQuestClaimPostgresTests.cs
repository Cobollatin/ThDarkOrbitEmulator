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

// Menu-84: drives the Quests claim POST handler against migration-seeded Postgres quest offers
// (011/018). Progress comes from real Postgres Shop purchases and Assembly crafts; the reward is
// keyed quest:{id} in the append-only ledger, so it applies once per account even across
// replicas, cache loss, and concurrent submits.
[TestFixture]
[Category("Integration")]
public sealed class LocalQuestClaimPostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 84_000;

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

    [TestCase("first_purchase", 1, 250)]
    [TestCase("field_supplier", 3, 500)]
    [TestCase("bulk_buyer", 10, 1000)]
    public async Task PurchaseQuest_AtTarget_CreditsRewardOnceThenReturns409(string questId, int purchases, long reward)
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, purchases);
        var before = await CreditsAsync(harness);

        var first = await ClaimAsync(harness, harness.Equipment, questId);
        var second = await ClaimAsync(harness, harness.Equipment, questId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalQuests&claimed=" + questId));
            Assert.That(first.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(second.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(second));
            Assert.That(Body(second), Does.Contain("already claimed"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + reward));
            Assert.That(await LedgerDeltasAsync(harness, $"quest:{questId}"), Is.EqualTo(new[] { reward }));
        });
    }

    [Test]
    public async Task PurchaseQuest_OneShort_Returns400WithoutCredit()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 2);
        var before = await CreditsAsync(harness);

        var context = await ClaimAsync(harness, harness.Equipment, "field_supplier");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(context));
            Assert.That(Body(context), Does.Contain("not complete"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await LedgerDeltasAsync(harness, "quest:field_supplier"), Is.Empty);
        });
    }

    [Test]
    public async Task NewPilot_EveryQuestIncomplete_NoGrant()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var results = new List<DefaultHttpContext>();
        foreach (var quest in new[] { "first_purchase", "field_supplier", "first_craft", "bulk_buyer", "master_engineer" })
            results.Add(await ClaimAsync(harness, harness.Equipment, quest));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Select(r => r.Response.StatusCode), Is.All.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await QuestLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task CraftQuests_TrackPostgresCrafts()
    {
        var harness = await HarnessAsync();
        await CraftAsync(harness, 1);
        var afterOne = await ClaimAsync(harness, harness.Equipment, "first_craft");
        var engineerEarly = await ClaimAsync(harness, harness.Equipment, "master_engineer");
        await CraftAsync(harness, 4);
        var before = await CreditsAsync(harness);

        var engineer = await ClaimAsync(harness, harness.Equipment, "master_engineer");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(afterOne.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(afterOne));
            Assert.That(engineerEarly.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(engineerEarly));
            Assert.That(engineer.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(engineer));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 1500));
            Assert.That(await LedgerDeltasAsync(harness, "quest:first_craft"), Is.EqualTo(new long[] { 400 }));
            Assert.That(await LedgerDeltasAsync(harness, "quest:master_engineer"), Is.EqualTo(new long[] { 1500 }));
        });
    }

    [Test]
    public async Task Claim_FromFreshReplicaAfterClaim_Returns409WithoutSecondGrant()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);
        var first = await ClaimAsync(harness, harness.Equipment, "first_purchase");
        var afterFirst = await CreditsAsync(harness);

        // The fresh store has no cached claim list; the ledger key must block the replay.
        var replay = await ClaimAsync(harness, NewStore(harness), "first_purchase");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(afterFirst));
            Assert.That(await LedgerDeltasAsync(harness, "quest:first_purchase"), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public async Task Claim_ConcurrentFromSeparateInstances_GrantsOnce()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 3);
        var before = await CreditsAsync(harness);
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore(harness)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => ClaimAsync(harness, store, "field_supplier")));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 500));
            Assert.That(await LedgerDeltasAsync(harness, "quest:field_supplier"), Is.EqualTo(new long[] { 500 }));
        });
    }

    [Test]
    public async Task Claim_DisabledOffer_Returns409EvenWhenComplete()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);
        var before = await CreditsAsync(harness);
        await SetQuestEnabledAsync("first_purchase", false);
        DefaultHttpContext disabled;
        try { disabled = await ClaimAsync(harness, harness.Equipment, "first_purchase"); }
        finally { await SetQuestEnabledAsync("first_purchase", true); }

        var enabled = await ClaimAsync(harness, harness.Equipment, "first_purchase");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(disabled.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(disabled));
            Assert.That(Body(disabled), Does.Contain("unavailable"));
            Assert.That(enabled.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(enabled));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 250));
        });
    }

    [TestCase("unknown_quest")]
    [TestCase("supplier")] // a battle-pass offer id, not a quest
    public async Task Claim_UnknownQuest_Returns404(string questId)
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);

        var context = await ClaimAsync(harness, harness.Equipment, questId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(await QuestLedgerRowsAsync(harness), Is.Zero);
            Assert.That(await LedgerDeltasAsync(harness, $"battlepass:{questId}"), Is.Empty);
        });
    }

    [Test]
    public async Task Claim_FormSignedForOtherQuest_Returns400WithoutCredit()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 3);
        var key = LocalQuestAction.CreateFormKey(harness.Session.Dosid, "first_purchase", Now);

        var context = await PostAsync(harness, harness.Equipment, $"quest=field_supplier&key={Uri.EscapeDataString(key)}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(context));
            Assert.That(await QuestLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task Claim_ExpiredForm_Returns409WithoutCredit()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);
        var key = LocalQuestAction.CreateFormKey(harness.Session.Dosid, "first_purchase",
            Now - LocalQuestAction.FormLifetime - TimeSpan.FromSeconds(1));

        var context = await PostAsync(harness, harness.Equipment, $"quest=first_purchase&key={Uri.EscapeDataString(key)}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("expired"));
            Assert.That(await QuestLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task Claim_DoesNotChangePurchaseOrCraftProgress()
    {
        var harness = await HarnessAsync();
        await BuyAsync(harness, 1);

        var context = await ClaimAsync(harness, harness.Equipment, "first_purchase");
        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);

        await Assert.MultipleAsync(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(state.PurchasedItems, Is.EqualTo(1));
            Assert.That(state.CraftedItems, Is.Zero);
            Assert.That(state.ClaimedQuestIds, Is.EquivalentTo(new[] { "first_purchase" }));
            return Task.CompletedTask;
        });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalHangarEquipmentStore Equipment);

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
            insert.Parameters.AddWithValue("u", $"quest-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"quest-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var equipment = new LocalHangarEquipmentStore(NewCache(), economy);
        // The first menu read imports the starter wallet, as the Quests page does before a claim form is shown.
        await equipment.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, equipment);
    }

    // Real Postgres Shop purchases of the 500-Credit Flax drone, funded by a test grant.
    private static async Task BuyAsync(Harness harness, int count)
    {
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
        var ct = TestContext.CurrentContext.CancellationToken;
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "credits", 400L * count,
            "test_grant", "test", $"test:craft-credits:{Guid.NewGuid():N}", ct), Is.True);
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "assembly_components", 100L * count,
            "test_grant", "test", $"test:craft-components:{Guid.NewGuid():N}", ct), Is.True);
        for (var i = 0; i < count; i++)
            Assert.That(await harness.Economy.CraftAsync(harness.Session.AccountId, "shield",
                $"test:craft:{Guid.NewGuid():N}", ct), Is.EqualTo(EconomyCraftResult.Applied));
    }

    private static Task<DefaultHttpContext> ClaimAsync(Harness harness, LocalHangarEquipmentStore store, string questId)
    {
        var key = LocalQuestAction.CreateFormKey(harness.Session.Dosid, questId, Now);
        return PostAsync(harness, store, $"quest={questId}&key={Uri.EscapeDataString(key)}");
    }

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalHangarEquipmentStore store, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalQuestClaim");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalQuestAction.HandleAsync(context, harness.Sessions, store, new FixedClock(Now),
            TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static async Task<long> CreditsAsync(Harness harness) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == "credits").Amount;

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

    private async Task<long> QuestLedgerRowsAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM economy.ledger WHERE account_id=@a AND idempotency_key LIKE 'quest:%'", db);
        count.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task SetQuestEnabledAsync(string questId, bool enabled)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE economy.reward_offers SET enabled=@e WHERE source='quest' AND id=@i", db);
        update.Parameters.AddWithValue("e", enabled);
        update.Parameters.AddWithValue("i", questId);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
