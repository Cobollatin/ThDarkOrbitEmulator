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

// Menu-80: drives the Voucher and Payment POST handlers against migration-seeded Postgres reward
// offers. The grant is keyed in the append-only ledger, so it must apply once per account even when
// the menu cache is lost, the code is typed in another case, or two requests race.
[TestFixture]
[Category("Integration")]
public sealed class LocalRewardRedeemPostgresTests
{
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 80_000;

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

    [TestCase("TRAINING-500", 500)]
    [TestCase("HANGAR-750", 750)]
    public async Task Voucher_SeededCode_CreditsOnceThenReturns409(string code, long amount)
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var first = await VoucherAsync(harness, harness.Equipment, $"code={code}");
        var second = await VoucherAsync(harness, harness.Equipment, $"code={code}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo($"/indexInternal.es?action=internalVouchers&redeemed={code}"));
            Assert.That(second.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(second));
            Assert.That(second.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + amount));
            Assert.That(await LedgerRowsAsync(harness, $"voucher:{code}"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Voucher_LowercaseCode_UsesCanonicalLedgerKey()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var lower = await VoucherAsync(harness, harness.Equipment, "code=training-500");
        var upper = await VoucherAsync(harness, harness.Equipment, "code=TRAINING-500");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(lower.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(lower));
            Assert.That(upper.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(upper));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 500));
            Assert.That(await LedgerRowsAsync(harness, "voucher:TRAINING-500"), Is.EqualTo(1));
            Assert.That(await LedgerRowsAsync(harness, "voucher:training-500"), Is.Zero);
        });
    }

    [Test]
    public async Task Voucher_AfterMenuCacheLoss_StaysRedeemedFromLedger()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);
        Assert.That((await VoucherAsync(harness, harness.Equipment, "code=HANGAR-750")).Response.StatusCode,
            Is.EqualTo(StatusCodes.Status303SeeOther));

        // A fresh cache stands in for an expired or evicted menu state.
        var fresh = new LocalHangarEquipmentStore(NewCache(), harness.Economy);
        var state = await fresh.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);
        var retry = await VoucherAsync(harness, fresh, "code=HANGAR-750");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(state.RedeemedVoucherCodes, Does.Contain("HANGAR-750"));
            Assert.That(retry.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(retry));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 750));
        });
    }

    [Test]
    public async Task Voucher_ConcurrentRedeemsFromSeparateInstances_CreditOnce()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);
        // Separate caches model two ApiService replicas; only the ledger key serializes them.
        var stores = Enumerable.Range(0, 4).Select(_ => new LocalHangarEquipmentStore(NewCache(), harness.Economy)).ToArray();

        var results = await Task.WhenAll(stores.Select(store => VoucherAsync(harness, store, "code=TRAINING-500")));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + 500));
            Assert.That(await LedgerRowsAsync(harness, "voucher:TRAINING-500"), Is.EqualTo(1));
        });
    }

    [TestCase("code=UNKNOWN-1")]
    [TestCase("code=starter")]
    public async Task Voucher_CodeOutsideVoucherOffers_Returns404WithoutLedger(string form)
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var context = await VoucherAsync(harness, harness.Equipment, form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
            Assert.That(await LedgerRowsAsync(harness, null), Is.Zero);
        });
    }

    [Test]
    public async Task Voucher_DisabledOffer_Returns410WithoutLedger()
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);
        await SetVoucherEnabledAsync("HANGAR-750", false);
        try
        {
            var context = await VoucherAsync(harness, harness.Equipment, "code=HANGAR-750");

            await Assert.MultipleAsync(async () =>
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status410Gone), Body(context));
                Assert.That(await CreditsAsync(harness), Is.EqualTo(before));
                Assert.That(await LedgerRowsAsync(harness, null), Is.Zero);
            });
        }
        finally { await SetVoucherEnabledAsync("HANGAR-750", true); }
    }

    [TestCase("starter", 1000)]
    [TestCase("fleet", 5000)]
    public async Task Payment_SeededPackage_CreditsOnceThenReturns409(string package, long amount)
    {
        var harness = await HarnessAsync();
        var before = await CreditsAsync(harness);

        var first = await PaymentAsync(harness, harness.Equipment, $"package={package}");
        var fresh = new LocalHangarEquipmentStore(NewCache(), harness.Economy);
        var second = await PaymentAsync(harness, fresh, $"package={package}");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo($"/indexInternal.es?action=internalPayment&claimed={package}"));
            Assert.That(second.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(second));
            Assert.That(await CreditsAsync(harness), Is.EqualTo(before + amount));
            Assert.That(await LedgerRowsAsync(harness, $"payment:{package}"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Payment_VoucherCodeAsPackage_Returns404WithoutLedger()
    {
        var harness = await HarnessAsync();

        var context = await PaymentAsync(harness, harness.Equipment, "package=TRAINING-500");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(await LedgerRowsAsync(harness, null), Is.Zero);
        });
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalHangarEquipmentStore Equipment);

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
            insert.Parameters.AddWithValue("u", $"reward-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"reward-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var equipment = new LocalHangarEquipmentStore(NewCache(), economy);
        // The first menu read imports the starter wallet, as the shell does before any reward form is shown.
        await equipment.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, equipment);
    }

    private static async Task<DefaultHttpContext> VoucherAsync(Harness harness, LocalHangarEquipmentStore store, string form)
    {
        var context = Request(harness, "internalVoucherRedeem", form);
        await LocalVoucherAction.HandleAsync(context, harness.Sessions, store, TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static async Task<DefaultHttpContext> PaymentAsync(Harness harness, LocalHangarEquipmentStore store, string form)
    {
        var context = Request(harness, "internalPaymentClaim", form);
        await LocalPaymentAction.HandleAsync(context, harness.Sessions, store, TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static DefaultHttpContext Request(Harness harness, string action, string form)
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
        return context;
    }

    private static async Task<long> CreditsAsync(Harness harness) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == "credits").Amount;

    // Counts reward grants (voucher/payment keys); a null key counts every reward grant for the account.
    private async Task<long> LedgerRowsAsync(Harness harness, string? key)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var count = new NpgsqlCommand(key is null
            ? "SELECT count(*) FROM economy.ledger WHERE account_id=@a AND (idempotency_key LIKE 'voucher:%' OR idempotency_key LIKE 'payment:%')"
            : "SELECT count(*) FROM economy.ledger WHERE account_id=@a AND idempotency_key=@k", db);
        count.Parameters.AddWithValue("a", harness.Session.AccountId);
        if (key is not null) count.Parameters.AddWithValue("k", key);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private async Task SetVoucherEnabledAsync(string code, bool enabled)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE economy.reward_offers SET enabled=@e WHERE source='voucher' AND id=@i", db);
        update.Parameters.AddWithValue("e", enabled);
        update.Parameters.AddWithValue("i", code);
        Assert.That(await update.ExecuteNonQueryAsync(), Is.EqualTo(1));
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
