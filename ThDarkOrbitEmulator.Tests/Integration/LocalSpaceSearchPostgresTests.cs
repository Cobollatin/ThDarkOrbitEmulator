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

// Menu-90: drives the Space Search explore POST handler (EconomyStore-backed LocalHangarEquipmentStore)
// against migrated Postgres. Each of the five planets debits 100 Credits and grants one catalog item once;
// the durable `space-search:{planet}` item-grant key survives cache loss and serializes replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalSpaceSearchPostgresTests
{
    private const long StarterCredits = 10_000;
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 90_000;

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
    public async Task Explore_DebitsCreditsOnceAndGrantsRewardItem()
    {
        var harness = await HarnessAsync();
        var before = await QuantityAsync(harness, "laser_lf3");

        var context = await ExploreAsync(harness, harness.Equipment, "planet=mars");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalSpaceSearch&explored=mars"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(StarterCredits - 100));
            Assert.That(await LedgerDeltasAsync(harness, "space-search:mars"), Is.EqualTo(new long[] { -100 }));
            Assert.That(await QuantityAsync(harness, "laser_lf3"), Is.EqualTo(before + 1));
            Assert.That(await GrantKeysAsync(harness), Is.EqualTo(new[] { "space-search:mars" }));
        });
    }

    [Test]
    public async Task Explore_ReplayOnFreshReplica_IsRefusedWithoutSecondCharge()
    {
        var harness = await HarnessAsync();
        var before = await QuantityAsync(harness, "shield_sg3n_b02");

        var first = await ExploreAsync(harness, harness.Equipment, "planet=venus");
        var replay = await ExploreAsync(harness, NewStore(harness), "planet=venus");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(Body(replay), Does.Contain("already searched"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(StarterCredits - 100));
            Assert.That(await LedgerDeltasAsync(harness, "space-search:venus"), Is.EqualTo(new long[] { -100 }));
            Assert.That(await QuantityAsync(harness, "shield_sg3n_b02"), Is.EqualTo(before + 1));
        });
    }

    [Test]
    public async Task ConcurrentExploresOnSeparateReplicas_ApplyExactlyOnce()
    {
        var harness = await HarnessAsync();
        var before = await QuantityAsync(harness, "drone_flax");

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => ExploreAsync(harness, NewStore(harness), "planet=earth")));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(StarterCredits - 100));
            Assert.That(await LedgerDeltasAsync(harness, "space-search:earth"), Is.EqualTo(new long[] { -100 }));
            Assert.That(await QuantityAsync(harness, "drone_flax"), Is.EqualTo(before + 1));
        });
    }

    [Test]
    public async Task Explore_WithTooFewCredits_IsRefusedAndExactBalanceSucceeds()
    {
        var harness = await HarnessAsync();
        await SetCreditsAsync(harness, 99);
        var before = await QuantityAsync(harness, "laser_lf2");

        var refused = await ExploreAsync(harness, harness.Equipment, "planet=jupiter");
        await SetCreditsAsync(harness, 100);
        var exact = await ExploreAsync(harness, NewStore(harness), "planet=jupiter");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(refused.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(refused));
            Assert.That(Body(refused), Does.Contain("not have enough local credits"));
            Assert.That(exact.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(exact));
            Assert.That(await CreditsAsync(harness), Is.Zero);
            Assert.That(await LedgerDeltasAsync(harness, "space-search:jupiter"), Is.EqualTo(new long[] { -100 }));
            Assert.That(await QuantityAsync(harness, "laser_lf2"), Is.EqualTo(before + 1));
        });
    }

    [Test]
    public async Task Explore_DisabledRewardItem_IsRefusedWithNothingChargedAndWorksOnceReenabled()
    {
        var harness = await HarnessAsync();
        var before = await QuantityAsync(harness, "shield_sg3n_a03");
        DefaultHttpContext refused;
        await SetItemEnabledAsync("shield_sg3n_a03", false);
        try { refused = await ExploreAsync(harness, harness.Equipment, "planet=saturn"); }
        finally { await SetItemEnabledAsync("shield_sg3n_a03", true); }

        var balanceAfterRefusal = await CreditsAsync(harness);
        var grantsAfterRefusal = await GrantKeysAsync(harness);
        var retry = await ExploreAsync(harness, NewStore(harness), "planet=saturn");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(refused.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(refused));
            Assert.That(Body(refused), Does.Contain("not available"));
            Assert.That(balanceAfterRefusal, Is.EqualTo(StarterCredits));
            Assert.That(grantsAfterRefusal, Is.Empty);
            Assert.That(retry.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(retry));
            Assert.That(await LedgerDeltasAsync(harness, "space-search:saturn"), Is.EqualTo(new long[] { -100 }));
            Assert.That(await QuantityAsync(harness, "shield_sg3n_a03"), Is.EqualTo(before + 1));
        });
    }

    [TestCase("planet=pluto", StatusCodes.Status404NotFound)]
    [TestCase("planet=Mars", StatusCodes.Status400BadRequest)]
    [TestCase("planet=mars&planet=venus", StatusCodes.Status400BadRequest)]
    [TestCase("planet=mars&key=extra", StatusCodes.Status400BadRequest)]
    public async Task Explore_InvalidOrUnknownPlanet_WritesNothing(string form, int status)
    {
        var harness = await HarnessAsync();

        var context = await ExploreAsync(harness, harness.Equipment, form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), Body(context));
            Assert.That(Body(context), Does.Not.Contain("pluto").And.Not.Contain("Mars").And.Not.Contain("extra"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(StarterCredits));
            Assert.That(await GrantKeysAsync(harness), Is.Empty);
        });
    }

    [Test]
    public async Task AllPlanetsSearched_IsVisibleToFreshReplicaAndPage()
    {
        var harness = await HarnessAsync();
        foreach (var planet in LocalSpaceSearch.Catalog)
        {
            var context = await ExploreAsync(harness, harness.Equipment, $"planet={planet.Id}");
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        }

        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);
        var page = LocalSpaceSearch.Page(state);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(state.SearchedPlanetIds, Is.EquivalentTo(LocalSpaceSearch.Catalog.Select(p => p.Id)));
            Assert.That(state.Credits, Is.EqualTo(StarterCredits - 500));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(StarterCredits - 500));
            Assert.That(await GrantKeysAsync(harness), Is.EquivalentTo(LocalSpaceSearch.Catalog.Select(p => $"space-search:{p.Id}")));
            Assert.That(page, Does.Contain("Searched 5 of 5 planets"));
            Assert.That(page, Does.Not.Contain("action=internalSpaceSearchExplore"));
        });
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
            insert.Parameters.AddWithValue("u", $"search-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"search-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var equipment = new LocalHangarEquipmentStore(NewCache(), economy);
        // The first menu read imports the starter wallet and inventory, as the Space Search page does.
        await equipment.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, equipment);
    }

    private static async Task<DefaultHttpContext> ExploreAsync(Harness harness, LocalHangarEquipmentStore store, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalSpaceSearchExplore");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalSpaceSearchAction.HandleAsync(context, harness.Sessions, store, TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static async Task<long> CreditsAsync(Harness harness) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == "credits").Amount;

    private async Task<long> QuantityAsync(Harness harness, string itemId)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT quantity FROM economy.inventory WHERE account_id=@a AND item_id=@i", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("i", itemId);
        return await cmd.ExecuteScalarAsync() is long quantity ? quantity : 0;
    }

    private async Task<long[]> LedgerDeltasAsync(Harness harness, string key)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT delta FROM economy.ledger WHERE account_id=@a AND asset_id='credits' AND idempotency_key=@k ORDER BY id", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("k", key);
        var deltas = new List<long>();
        await using var row = await cmd.ExecuteReaderAsync();
        while (await row.ReadAsync()) deltas.Add(row.GetInt64(0));
        return deltas.ToArray();
    }

    private async Task<string[]> GrantKeysAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT idempotency_key FROM economy.item_grants WHERE account_id=@a ORDER BY idempotency_key", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        var keys = new List<string>();
        await using var row = await cmd.ExecuteReaderAsync();
        while (await row.ReadAsync()) keys.Add(row.GetString(0));
        return keys.ToArray();
    }

    private async Task SetCreditsAsync(Harness harness, long amount)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE economy.balances SET amount=@n WHERE account_id=@a AND asset_id='credits'", db);
        update.Parameters.AddWithValue("n", amount);
        update.Parameters.AddWithValue("a", harness.Session.AccountId);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private async Task SetItemEnabledAsync(string itemId, bool enabled)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand("UPDATE economy.items SET enabled=@e WHERE id=@i", db);
        update.Parameters.AddWithValue("e", enabled);
        update.Parameters.AddWithValue("i", itemId);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
