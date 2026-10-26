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

// Menu-81: drives the Auction bid/claim POST handlers (EconomyStore overload) against the
// migration-seeded Postgres lots (laser_lf3 700, shield_sg3n_b02 450, drone_flax 200; 300 s each).
// A raise debits only the difference, a claim grants one item after the lot ends, and the
// account lock keeps racing replicas from double-charging.
[TestFixture]
[Category("Integration")]
public sealed class LocalAuctionPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 81_000;

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
    public async Task Bid_AtOpening_DebitsOpeningBidAndStoresBid()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var context = await BidAsync(harness, harness.Equipment, "laser_lf3", 700);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAuction&placed=laser_lf3"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before - 700));
            Assert.That(await StoredBidAsync(harness, "laser_lf3"), Is.EqualTo(700));
            Assert.That(await BidLedgerRowsAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Bid_BelowOpening_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var context = await BidAsync(harness, harness.Equipment, "laser_lf3", 699);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("10%"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await StoredBidAsync(harness, "laser_lf3"), Is.Null);
            Assert.That(await BidLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task Bid_Raise_RequiresTenPercentAndDebitsOnlyDifference()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var opening = await BidAsync(harness, harness.Equipment, "laser_lf3", 700);
        var tooLow = await BidAsync(harness, harness.Equipment, "laser_lf3", 769);
        var raise = await BidAsync(harness, harness.Equipment, "laser_lf3", 770);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(opening.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(opening));
            Assert.That(tooLow.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(tooLow));
            Assert.That(raise.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(raise));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before - 770));
            Assert.That(await StoredBidAsync(harness, "laser_lf3"), Is.EqualTo(770));
            Assert.That(await BidLedgerDeltasAsync(harness), Is.EqualTo(new long[] { -700, -70 }));
        });
    }

    [Test]
    public async Task Bid_MoreThanWallet_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var context = await BidAsync(harness, harness.Equipment, "drone_flax", 999_999_999);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("enough local credits"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await StoredBidAsync(harness, "drone_flax"), Is.Null);
            Assert.That(await BidLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task BidAndClaim_UnknownLot_Return404()
    {
        var harness = await HarnessAsync();

        var bid = await BidAsync(harness, harness.Equipment, "no_such_lot", 500);
        var claim = await ClaimAsync(harness, harness.Equipment, "no_such_lot");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(bid.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(bid));
            Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(claim));
            Assert.That(await BidLedgerRowsAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task Bid_DisabledLot_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);
        await SetLotEnabledAsync("shield_sg3n_b02", false);
        try
        {
            var context = await BidAsync(harness, harness.Equipment, "shield_sg3n_b02", 450);

            await Assert.MultipleAsync(async () =>
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
                Assert.That(Body(context), Does.Contain("not available"));
                Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
                Assert.That(await BidLedgerRowsAsync(harness), Is.Zero);
            });
        }
        finally { await SetLotEnabledAsync("shield_sg3n_b02", true); }
    }

    [Test]
    public async Task Claim_WithoutBid_Returns409()
    {
        var harness = await HarnessAsync();

        var context = await ClaimAsync(harness, harness.Equipment, "drone_flax");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
        Assert.That(Body(context), Does.Contain("not bid"));
    }

    [Test]
    public async Task Claim_Lifecycle_RunningThenGrantsOnceAfterEnd()
    {
        var harness = await HarnessAsync();
        var items = await InventoryAsync(harness, "drone_flax");
        Assert.That((await BidAsync(harness, harness.Equipment, "drone_flax", 200)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var running = await ClaimAsync(harness, harness.Equipment, "drone_flax");
        harness.Clock.Advance(TimeSpan.FromSeconds(300));
        var lateBid = await BidAsync(harness, harness.Equipment, "drone_flax", 300);
        var claim = await ClaimAsync(harness, harness.Equipment, "drone_flax");
        var again = await ClaimAsync(harness, harness.Equipment, "drone_flax");
        var bidAfterClaim = await BidAsync(harness, harness.Equipment, "drone_flax", 300);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(running.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(running));
            Assert.That(Body(running), Does.Contain("still running"));
            Assert.That(lateBid.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(lateBid));
            Assert.That(Body(lateBid), Does.Contain("has ended"));
            Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(claim));
            Assert.That(again.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(again));
            Assert.That(Body(again), Does.Contain("already claimed"));
            Assert.That(bidAfterClaim.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(bidAfterClaim));
            Assert.That(await InventoryAsync(harness, "drone_flax"), Is.EqualTo(items + 1));
            Assert.That(await BidLedgerRowsAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Claim_AfterMenuCacheLoss_UsesPersistedBid()
    {
        var harness = await HarnessAsync();
        var items = await InventoryAsync(harness, "shield_sg3n_b02");
        Assert.That((await BidAsync(harness, harness.Equipment, "shield_sg3n_b02", 450)).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(6));

        // A fresh cache stands in for an expired or evicted menu state on another replica.
        var fresh = new LocalHangarEquipmentStore(NewCache(), harness.Economy);
        var claim = await ClaimAsync(harness, fresh, "shield_sg3n_b02");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(claim));
            Assert.That(await InventoryAsync(harness, "shield_sg3n_b02"), Is.EqualTo(items + 1));
        });
    }

    [Test]
    public async Task Bid_ConcurrentOpeningBidsFromSeparateInstances_DebitOnce()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);
        // Separate caches model ApiService replicas; only the Postgres account lock serializes them.
        var stores = Enumerable.Range(0, 4).Select(_ => new LocalHangarEquipmentStore(NewCache(), harness.Economy)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => BidAsync(harness, store, "laser_lf3", 700)));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before - 700));
            Assert.That(await BidLedgerRowsAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Bid_ExpiredFormKey_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);
        var key = LocalAuctionAction.CreateFormKey(harness.Session.Dosid, "bid", "drone_flax",
            harness.Clock.GetUtcNow() - LocalAuctionAction.FormLifetime - TimeSpan.FromSeconds(1));

        var context = await PostAsync(harness, harness.Equipment, "internalAuctionBid",
            $"item=drone_flax&amount=200&key={Uri.EscapeDataString(key)}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("expired"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await BidLedgerRowsAsync(harness), Is.Zero);
        });
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalHangarEquipmentStore Equipment, MutableClock Clock);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

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
            insert.Parameters.AddWithValue("u", $"auction-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"auction-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var equipment = new LocalHangarEquipmentStore(NewCache(), economy);
        // The first menu read imports the starter wallet, as the shell does before the Auction page renders.
        await equipment.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, equipment, new MutableClock(Start));
    }

    private static Task<DefaultHttpContext> BidAsync(Harness harness, LocalHangarEquipmentStore store, string lot, long amount)
    {
        var key = LocalAuctionAction.CreateFormKey(harness.Session.Dosid, "bid", lot, harness.Clock.GetUtcNow());
        return PostAsync(harness, store, "internalAuctionBid", $"item={lot}&amount={amount}&key={Uri.EscapeDataString(key)}");
    }

    private static Task<DefaultHttpContext> ClaimAsync(Harness harness, LocalHangarEquipmentStore store, string lot)
    {
        var key = LocalAuctionAction.CreateFormKey(harness.Session.Dosid, "claim", lot, harness.Clock.GetUtcNow());
        return PostAsync(harness, store, "internalAuctionClaim", $"item={lot}&key={Uri.EscapeDataString(key)}");
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
        await LocalAuctionAction.HandleAsync(context, harness.Sessions, store, harness.Economy, harness.Clock,
            TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static async Task<long> CreditsAsync(Harness harness) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == "credits").Amount;

    private async Task<long?> StoredBidAsync(Harness harness, string lot) =>
        await ScalarAsync<long?>("SELECT bid FROM economy.auction_bids WHERE account_id=@a AND lot_id=@l",
            harness, ("l", lot));

    private async Task<long> InventoryAsync(Harness harness, string item) =>
        await ScalarAsync<long?>("SELECT quantity FROM economy.inventory WHERE account_id=@a AND item_id=@l",
            harness, ("l", item)) ?? 0;

    private async Task<long> BidLedgerRowsAsync(Harness harness) => (await BidLedgerDeltasAsync(harness)).Length;

    private async Task<long[]> BidLedgerDeltasAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT delta FROM economy.ledger WHERE account_id=@a AND reason='auction_bid' AND asset_id='credits' ORDER BY id", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        var deltas = new List<long>();
        await using var row = await cmd.ExecuteReaderAsync();
        while (await row.ReadAsync()) deltas.Add(row.GetInt64(0));
        return deltas.ToArray();
    }

    private async Task<T?> ScalarAsync<T>(string sql, Harness harness, (string Name, object Value) parameter)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task SetLotEnabledAsync(string lot, bool enabled)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand("UPDATE economy.auction_lots SET enabled=@e WHERE id=@i", db);
        update.Parameters.AddWithValue("e", enabled);
        update.Parameters.AddWithValue("i", lot);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
