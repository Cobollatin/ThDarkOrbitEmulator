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

// Menu-83: drives the Tech Factory start/claim POST handlers (EconomyStore-backed LocalTechFactoryStore)
// against migrated Postgres. Pilots start with 1000 tech_components; seeded recipes cost 100-300
// components and take 2-6 minutes. One job slot per account; form keys are single-use and the
// account lock serializes replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalTechFactoryPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 83_000;

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
    public async Task FirstRead_ImportsStarterComponentsAndSeededRecipes()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.CurrentContext.CancellationToken;

        var state = await harness.Factory.GetAsync(harness.Session.AccountId, ct);
        var recipes = await harness.Factory.GetRecipesAsync(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(state.Components, Is.EqualTo(1000));
            Assert.That(state.Job, Is.Null);
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(1000));
            Assert.That(recipes.Select(r => (r.Id, r.ComponentCost, r.Minutes)), Is.SupersetOf(new[]
            {
                ("tech_targeting", 100, 2), ("tech_shield", 150, 3), ("tech_repair", 200, 4),
                ("tech_speed", 250, 5), ("tech_energy", 300, 6)
            }));
        });
    }

    [Test]
    public async Task Start_DebitsComponentsAndPersistsTimedJob()
    {
        var harness = await HarnessAsync();

        var context = await StartJobAsync(harness, harness.Factory, "tech_shield");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalTechFactory&started=tech_shield"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(850));
            Assert.That(await LedgerDeltasAsync(harness, "tech_factory_start"), Is.EqualTo(new long[] { -150 }));
            var job = await JobAsync(harness);
            Assert.That(job?.RecipeId, Is.EqualTo("tech_shield"));
            Assert.That(job?.ReadyAt, Is.EqualTo(Start.AddMinutes(3)));
        });
    }

    [Test]
    public async Task Start_WhileSlotBusy_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_targeting")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        var context = await StartJobAsync(harness, harness.Factory, "tech_repair");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("slot is busy"));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(900));
            Assert.That((await JobAsync(harness))?.RecipeId, Is.EqualTo("tech_targeting"));
        });
    }

    [Test]
    public async Task Start_WithTooFewComponents_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "tech_components", -800,
            "test_spend", "test", $"tech:test:drain:{harness.Session.AccountId:N}",
            TestContext.CurrentContext.CancellationToken), Is.True);

        var context = await StartJobAsync(harness, harness.Factory, "tech_energy");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("Not enough components: 300 needed, 200 available."));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(200));
            Assert.That(await JobAsync(harness), Is.Null);
            Assert.That(await LedgerDeltasAsync(harness, "tech_factory_start"), Is.Empty);
        });
    }

    [TestCase("tech_xenomit")]
    [TestCase("targeting")]
    public async Task Start_UnknownRecipe_Returns404(string recipe)
    {
        var harness = await HarnessAsync();

        var context = await StartJobAsync(harness, harness.Factory, recipe);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(1000));
            Assert.That(await JobAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task Start_KeySignedForOtherRecipe_Returns400()
    {
        var harness = await HarnessAsync();
        var key = LocalTechFactoryAction.CreateFormKey(harness.Session.Dosid, "start", "tech_targeting", harness.Clock.GetUtcNow());

        var context = await PostAsync(harness, harness.Factory, "internalTechFactoryStart",
            $"recipe=tech_energy&key={Uri.EscapeDataString(key)}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(context));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(1000));
            Assert.That(await JobAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task Start_ExpiredFormKey_Returns409WithoutDebit()
    {
        var harness = await HarnessAsync();
        var key = LocalTechFactoryAction.CreateFormKey(harness.Session.Dosid, "start", "tech_targeting",
            harness.Clock.GetUtcNow() - LocalTechFactoryAction.FormLifetime - TimeSpan.FromSeconds(1));

        var context = await PostAsync(harness, harness.Factory, "internalTechFactoryStart",
            $"recipe=tech_targeting&key={Uri.EscapeDataString(key)}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("expired"));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(1000));
        });
    }

    [Test]
    public async Task Start_ReplayedKeyAfterClaim_IsRefused()
    {
        var harness = await HarnessAsync();
        var key = LocalTechFactoryAction.CreateFormKey(harness.Session.Dosid, "start", "tech_targeting", harness.Clock.GetUtcNow());
        var form = $"recipe=tech_targeting&key={Uri.EscapeDataString(key)}";

        var first = await PostAsync(harness, harness.Factory, "internalTechFactoryStart", form);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var claim = await ClaimJobAsync(harness, harness.Factory, (await JobAsync(harness))!.Value.JobId);
        // The slot is free again, so only the persisted action key stops the old form; use another replica.
        var replay = await PostAsync(harness, NewStore(harness), "internalTechFactoryStart", form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(claim));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(Body(replay), Does.Contain("already submitted"));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(900));
            Assert.That(await JobAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task Start_ConcurrentFormsFromSeparateInstances_DebitOnce()
    {
        var harness = await HarnessAsync();
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore(harness)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => StartJobAsync(harness, store, "tech_speed")));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(750));
            Assert.That(await LedgerDeltasAsync(harness, "tech_factory_start"), Is.EqualTo(new long[] { -250 }));
            Assert.That(await JobCountAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Claim_BeforeReady_Returns409WithoutCredit()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_targeting")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromSeconds(119));

        var context = await ClaimJobAsync(harness, harness.Factory, (await JobAsync(harness))!.Value.JobId);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("not ready until 2026-10-24 12:02 UTC"));
            Assert.That(await BalanceAsync(harness, "targeting_boost"), Is.Zero);
            Assert.That(await JobAsync(harness), Is.Not.Null);
        });
    }

    [Test]
    public async Task Claim_WhenReady_CreditsOutputAndFreesSlot()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_repair")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(4));

        var context = await ClaimJobAsync(harness, harness.Factory, (await JobAsync(harness))!.Value.JobId);
        var next = await StartJobAsync(harness, harness.Factory, "tech_targeting");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(await BalanceAsync(harness, "repair_charge"), Is.EqualTo(1));
            Assert.That(await LedgerDeltasAsync(harness, "tech_factory_claim"), Is.EqualTo(new long[] { 1 }));
            Assert.That(next.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(next));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(700));
            Assert.That((await JobAsync(harness))?.RecipeId, Is.EqualTo("tech_targeting"));
        });
    }

    [Test]
    public async Task Claim_WhenOutputWalletFull_StaysReadyUntilSpaceIsAvailable()
    {
        var harness = await HarnessAsync();
        var ct = TestContext.CurrentContext.CancellationToken;
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_targeting")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "targeting_boost",
            long.MaxValue, "test_fill", "test", $"tech:test:fill:{harness.Session.AccountId:N}", ct), Is.True);

        var state = await harness.Factory.GetAsync(harness.Session.AccountId, ct);
        var page = LocalTechFactory.Page(harness.Session, state, harness.Clock.GetUtcNow(),
            await harness.Factory.GetRecipesAsync(ct));
        var blocked = await ClaimJobAsync(harness, harness.Factory, state.Job!.Id);
        Assert.Multiple(() =>
        {
            Assert.That(state.ClaimOutputFull, Is.True);
            Assert.That(page, Does.Contain("techFactoryClaimBlocked"));
            Assert.That(page, Does.Not.Contain("action=internalTechFactoryClaim"));
            Assert.That(blocked.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(blocked));
            Assert.That(Body(blocked), Does.Contain("wallet is full"));
        });
        Assert.That(await BalanceAsync(harness, "targeting_boost"), Is.EqualTo(long.MaxValue));
        Assert.That(await JobAsync(harness), Is.Not.Null);

        Assert.That(await harness.Economy.AdjustAsync(harness.Session.AccountId, "targeting_boost",
            -1, "test_spend", "test", $"tech:test:spend:{harness.Session.AccountId:N}", ct), Is.True);
        var claimed = await ClaimJobAsync(harness, harness.Factory, state.Job.Id);
        Assert.That(claimed.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(claimed));
        Assert.That(await BalanceAsync(harness, "targeting_boost"), Is.EqualTo(long.MaxValue));
        Assert.That(await JobAsync(harness), Is.Null);
    }

    [Test]
    public async Task Claim_WrongJobId_Returns409WithoutCredit()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_targeting")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var context = await ClaimJobAsync(harness, harness.Factory, Guid.NewGuid());

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("no matching production job"));
            Assert.That(await BalanceAsync(harness, "targeting_boost"), Is.Zero);
            Assert.That(await JobAsync(harness), Is.Not.Null);
        });
    }

    [Test]
    public async Task Claim_ReplayedKey_CreditsOnce()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_targeting")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var job = (await JobAsync(harness))!.Value.JobId.ToString("N");
        var key = LocalTechFactoryAction.CreateFormKey(harness.Session.Dosid, "claim", job, harness.Clock.GetUtcNow());
        var form = $"job={job}&key={Uri.EscapeDataString(key)}";

        var first = await PostAsync(harness, harness.Factory, "internalTechFactoryClaim", form);
        var replay = await PostAsync(harness, NewStore(harness), "internalTechFactoryClaim", form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(await BalanceAsync(harness, "targeting_boost"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Claim_ConcurrentFormsFromSeparateInstances_CreditOnce()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_shield")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(3));
        var jobId = (await JobAsync(harness))!.Value.JobId;
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore(harness)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => ClaimJobAsync(harness, store, jobId)));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await BalanceAsync(harness, "shield_boost"), Is.EqualTo(1));
            Assert.That(await LedgerDeltasAsync(harness, "tech_factory_claim"), Is.EqualTo(new long[] { 1 }));
            Assert.That(await JobAsync(harness), Is.Null);
        });
    }

    [Test]
    public async Task Claim_AfterMenuCacheLoss_UsesPersistedJob()
    {
        var harness = await HarnessAsync();
        Assert.That((await StartJobAsync(harness, harness.Factory, "tech_energy")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));
        harness.Clock.Advance(TimeSpan.FromMinutes(6));

        // The fresh store has no cached state; the job and component balance must come from Postgres.
        var fresh = NewStore(harness);
        var state = await fresh.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);
        var context = await ClaimJobAsync(harness, fresh, state.Job!.Id);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(state.Components, Is.EqualTo(700));
            Assert.That(state.Job.RecipeId, Is.EqualTo("tech_energy"));
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(await BalanceAsync(harness, "energy_charge"), Is.EqualTo(1));
            Assert.That(await BalanceAsync(harness, "tech_components"), Is.EqualTo(700));
        });
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalTechFactoryStore Factory, MutableClock Clock);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalTechFactoryStore NewStore(Harness harness) => new(NewCache(), harness.Clock, harness.Economy);

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
            insert.Parameters.AddWithValue("u", $"tech-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"tech-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var clock = new MutableClock(Start);
        var factory = new LocalTechFactoryStore(NewCache(), clock, economy);
        // The first Tech Factory page read imports the starter component stock.
        await factory.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, factory, clock);
    }

    private static Task<DefaultHttpContext> StartJobAsync(Harness harness, LocalTechFactoryStore store, string recipe)
    {
        var key = LocalTechFactoryAction.CreateFormKey(harness.Session.Dosid, "start", recipe, harness.Clock.GetUtcNow());
        return PostAsync(harness, store, "internalTechFactoryStart", $"recipe={recipe}&key={Uri.EscapeDataString(key)}");
    }

    private static Task<DefaultHttpContext> ClaimJobAsync(Harness harness, LocalTechFactoryStore store, Guid jobId)
    {
        var job = jobId.ToString("N");
        var key = LocalTechFactoryAction.CreateFormKey(harness.Session.Dosid, "claim", job, harness.Clock.GetUtcNow());
        return PostAsync(harness, store, "internalTechFactoryClaim", $"job={job}&key={Uri.EscapeDataString(key)}");
    }

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalTechFactoryStore store,
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
        await LocalTechFactoryAction.HandleAsync(context, harness.Sessions, store,
            TestContext.CurrentContext.CancellationToken, harness.Clock);
        return context;
    }

    private static async Task<long> BalanceAsync(Harness harness, string assetId) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .SingleOrDefault(b => b.AssetId == assetId)?.Amount ?? 0;

    private async Task<(Guid JobId, string RecipeId, DateTimeOffset ReadyAt)?> JobAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT job_id,recipe_id,ready_at FROM economy.tech_factory_jobs WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await using var row = await cmd.ExecuteReaderAsync();
        return await row.ReadAsync()
            ? (row.GetGuid(0), row.GetString(1), row.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    private async Task<long> JobCountAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM economy.tech_factory_jobs WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
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

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
