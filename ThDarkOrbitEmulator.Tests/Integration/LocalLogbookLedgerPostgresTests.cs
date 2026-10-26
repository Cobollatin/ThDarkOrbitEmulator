using FastEndpoints;
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

// Menu-96: the Logbook ledger query (EconomyStore.LedgerAsync) and its rendering through the real
// GET /indexInternal.es?action=internalLogbook endpoint against migrated Postgres. Ledger entries are
// numbered GeneratedEntries + 2 + the per-account ledger sequence.
[TestFixture]
[Category("Integration")]
public sealed class LocalLogbookLedgerPostgresTests
{
    private const long FirstLedgerNumber = LocalLogbook.GeneratedEntries + 3;
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 96_000;

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
    public async Task Ledger_RowsGetContiguousPerAccountSequenceOldestFirst()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var harness = await HarnessAsync();
        var other = await HarnessAsync();
        var account = harness.Session.AccountId;
        Assert.That(await harness.Economy.AdjustAsync(account, "credits", 5000, "admin_grant", "admin", "a1", ct), Is.True);
        Assert.That(await other.Economy.AdjustAsync(other.Session.AccountId, "credits", 7, "admin_grant", "admin", "o1", ct), Is.True);
        Assert.That(await harness.Economy.AdjustAsync(account, "credits", -2500, "shop_purchase", "shop", "a2", ct), Is.True);
        Assert.That(await other.Economy.AdjustAsync(other.Session.AccountId, "credits", 8, "admin_grant", "admin", "o2", ct), Is.True);
        Assert.That(await harness.Economy.AdjustAsync(account, "uridium", 40, "quest_reward", "quests", "a3", ct), Is.True);

        var ledger = await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct);
        var otherLedger = await other.Economy.LedgerAsync(other.Session.AccountId, LocalLogbook.LedgerLimit, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ledger.Select(r => r.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(ledger.Select(r => r.Reason), Is.EqualTo(new[] { "admin_grant", "shop_purchase", "quest_reward" }));
            Assert.That(ledger.Select(r => r.Id), Is.Ordered.Ascending);
            Assert.That(ledger.Select(r => r.BalanceAfter), Is.EqualTo(new long[] { 5000, 2500, 40 }));
            Assert.That(otherLedger.Select(r => r.Sequence), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(otherLedger.Select(r => r.Delta), Is.EqualTo(new long[] { 7, 8 }));
        });
    }

    [Test]
    public async Task Ledger_LimitKeepsNewestRowsAndTheirOriginalSequence()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var harness = await HarnessAsync();
        var account = harness.Session.AccountId;
        const int total = LocalLogbook.LedgerLimit + 5;
        // The first visit writes the starter import rows; the bulk rows follow them.
        Assert.That((await SendAsync("?action=internalLogbook", harness)).Response.StatusCode, Is.EqualTo(200));
        var imported = (await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct)).Count;
        for (var n = 1; n <= total; n++)
            Assert.That(await harness.Economy.AdjustAsync(account, "credits", n, "admin_grant", "admin", $"bulk-{n}", ct), Is.True);

        var ledger = await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct);
        var oldest = await SendAsync($"?action=internalLogbook&entry={harness.Session.GameUserId}-{FirstLedgerNumber}", harness);
        var newestNumber = FirstLedgerNumber + imported + total - 1;
        var newest = await SendAsync($"?action=internalLogbook&entry={harness.Session.GameUserId}-{newestNumber}", harness);
        var firstKept = await SendAsync($"?action=internalLogbook&entry={harness.Session.GameUserId}-{newestNumber - LocalLogbook.LedgerLimit + 1}", harness);
        var firstDropped = await SendAsync($"?action=internalLogbook&entry={harness.Session.GameUserId}-{newestNumber - LocalLogbook.LedgerLimit}", harness);

        Assert.Multiple(() =>
        {
            Assert.That(ledger, Has.Count.EqualTo(LocalLogbook.LedgerLimit));
            Assert.That(ledger[0].Sequence, Is.EqualTo(imported + total - LocalLogbook.LedgerLimit + 1));
            Assert.That(ledger[^1].Sequence, Is.EqualTo(imported + total));
            Assert.That(ledger[^1].Delta, Is.EqualTo(total));
            // Rows beyond the cap are not shown, but entry numbers never shift.
            Assert.That(oldest.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(newest.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), Body(newest));
            Assert.That(Body(newest), Does.Contain($"<dt>Change</dt><dd>+{total}</dd>"));
            Assert.That(firstKept.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), Body(firstKept));
            Assert.That(firstDropped.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
        });
    }

    [Test]
    public async Task Page_ShowsLedgerRowsWithCategoriesAndDetail()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var harness = await HarnessAsync();
        var account = harness.Session.AccountId;
        var before = (await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct)).Count;
        Assert.That(await harness.Economy.AdjustAsync(account, "credits", 5000, "admin_grant", "admin", "p1", ct), Is.True);
        Assert.That(await harness.Economy.AdjustAsync(account, "credits", -2500, "shop_purchase", "shop", "p2", ct), Is.True);
        Assert.That(await harness.Economy.AdjustAsync(account, "uridium", 40, "quest_reward", "quests", "p3", ct), Is.True);
        var ledger = await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct);
        var shop = ledger.Single(r => r.Reason == "shop_purchase");
        var shopNumber = LocalLogbook.GeneratedEntries + 2 + shop.Sequence;

        var all = Body(await SendAsync("?action=internalLogbook", harness));
        var economy = Body(await SendAsync("?action=internalLogbook&category=economy", harness));
        var missions = Body(await SendAsync("?action=internalLogbook&category=missions", harness));
        var accountPage = Body(await SendAsync("?action=internalLogbook&category=account", harness));
        var detail = await SendAsync($"?action=internalLogbook&entry={harness.Session.GameUserId}-{shopNumber}", harness);
        var detailBody = Body(detail);

        Assert.Multiple(() =>
        {
            Assert.That(ledger, Has.Count.EqualTo(before + 3));
            Assert.That(all, Does.Contain("Shop purchase (shop): -2,500 credits, balance"));
            Assert.That(economy, Does.Contain("Shop purchase (shop)"));
            Assert.That(economy, Does.Not.Contain("Admin grant (admin)").And.Not.Contain("Quest reward (quests)"));
            Assert.That(missions, Does.Contain("Quest reward (quests)"));
            Assert.That(missions, Does.Not.Contain("Shop purchase (shop)"));
            Assert.That(accountPage, Does.Contain("Admin grant (admin)"));
            Assert.That(detail.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), detailBody);
            Assert.That(detailBody, Does.Contain($"<dt>Source</dt><dd>Local ledger row {shop.Id}</dd>"));
            Assert.That(detailBody, Does.Contain("<dt>Change</dt><dd>-2500</dd>"));
            Assert.That(detailBody, Does.Contain($"<dt>Balance after</dt><dd>{shop.BalanceAfter}</dd>"));
            Assert.That(detailBody, Does.Contain("<dt>Menu</dt><dd>shop</dd>"));
            Assert.That(detailBody, Does.Contain("<dt>Type</dt><dd>economy</dd>"));
        });
    }

    // Regression: the page used to read the ledger before its own starter import, so the first visit
    // omitted the import rows it had just written and the next visit listed more entries.
    [Test]
    public async Task Page_FirstVisitAlreadyListsStarterImportRows()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var harness = await HarnessAsync();

        var first = Body(await SendAsync("?action=internalLogbook&category=account", harness));
        var second = Body(await SendAsync("?action=internalLogbook&category=account", harness));
        var ledger = await harness.Economy.LedgerAsync(harness.Session.AccountId, LocalLogbook.LedgerLimit, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ledger, Is.Not.Empty);
            Assert.That(first, Does.Contain("Imported starting balance"));
            Assert.That(second, Is.EqualTo(first));
        });
    }

    [Test]
    public async Task Page_DoesNotShowAnotherAccountsLedgerRows()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var harness = await HarnessAsync();
        var other = await HarnessAsync();
        Assert.That(await other.Economy.AdjustAsync(other.Session.AccountId, "credits", 123456, "admin_grant", "admin",
            "foreign", ct), Is.True);

        var page = Body(await SendAsync("?action=internalLogbook&category=account", harness));
        var foreign = await SendAsync($"?action=internalLogbook&entry={other.Session.GameUserId}-{FirstLedgerNumber}", harness);

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Not.Contain("123,456"));
            Assert.That(foreign.Response.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        });
    }

    [Test]
    public async Task Page_IsReadOnlyAcrossRepeatedAndConcurrentLoads()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var harness = await HarnessAsync();
        var account = harness.Session.AccountId;
        Assert.That(await harness.Economy.AdjustAsync(account, "credits", 900, "admin_grant", "admin", "r1", ct), Is.True);
        await SendAsync("?action=internalLogbook", harness);
        var ledger = await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct);
        var balances = await harness.Economy.BalancesAsync(account, ct);

        var pages = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => SendAsync("?action=internalLogbook&category=account", harness)));
        var bodies = pages.Select(Body).ToArray();
        var ledgerAfter = await harness.Economy.LedgerAsync(account, LocalLogbook.LedgerLimit, ct);
        var balancesAfter = await harness.Economy.BalancesAsync(account, ct);

        Assert.Multiple(() =>
        {
            Assert.That(pages.Select(p => p.Response.StatusCode), Is.All.EqualTo(StatusCodes.Status200OK));
            Assert.That(bodies.Distinct().Count(), Is.EqualTo(1));
            Assert.That(ledgerAfter.Select(r => r.Id), Is.EqualTo(ledger.Select(r => r.Id)));
            Assert.That(balancesAfter, Is.EqualTo(balances));
        });
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalHangarEquipmentStore Equipment);

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
            insert.Parameters.AddWithValue("u", $"logbook-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"logbook-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        return new Harness(sessions, session, economy, new LocalHangarEquipmentStore(cache, economy));
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, Harness harness)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        context.Response.Body = new MemoryStream();

        // Only the economy and equipment stores are reached by the Logbook page.
        FastEndpointsTestResolver.Reset();
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, harness.Sessions, null!, null!, harness.Equipment,
            harness.Economy, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
