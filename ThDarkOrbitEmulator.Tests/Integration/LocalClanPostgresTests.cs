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

// Menu-87: drives the Clan join/leave POST handlers (EconomyStore-backed LocalClanStore) against
// migrated Postgres. One membership per account; joining needs a recruiting clan of the pilot's
// company, leaving names the current clan. Form keys are single-use across replicas (clan_actions)
// and the account advisory lock serializes concurrent submissions.
[TestFixture]
[Category("Integration")]
public sealed class LocalClanPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 87_000;

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
    public async Task Join_RecruitingOwnCompanyClan_PersistsMembership()
    {
        var harness = await HarnessAsync();

        var context = await JoinAsync(harness, harness.Clans, "mmo-guard");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalClan&joined=mmo-guard"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await MembershipAsync(harness), Is.EqualTo("mmo-guard"));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
            // A fresh replica reads the same membership from Postgres.
            Assert.That(await NewStore(harness).GetMembershipAsync(harness.Session.AccountId, Ct), Is.EqualTo("mmo-guard"));
        });
    }

    [Test]
    public async Task JoinThenLeave_RemovesMembershipAndAllowsAnotherJoin()
    {
        var harness = await HarnessAsync();

        var join = await JoinAsync(harness, harness.Clans, "mmo-orbit");
        var leave = await LeaveAsync(harness, harness.Clans, "mmo-orbit");
        var afterLeave = await MembershipAsync(harness);
        var rejoin = await JoinAsync(harness, NewStore(harness), "mmo-traders");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(join.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(join));
            Assert.That(leave.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(leave));
            Assert.That(afterLeave, Is.Null);
            Assert.That(rejoin.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(rejoin));
            Assert.That(await MembershipAsync(harness), Is.EqualTo("mmo-traders"));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Join_WhileMemberOfAnotherClan_Returns409WithoutChange()
    {
        var harness = await HarnessAsync();
        await JoinAsync(harness, harness.Clans, "mmo-guard");

        var context = await JoinAsync(harness, NewStore(harness), "mmo-orbit");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("membership has changed"));
            Assert.That(await MembershipAsync(harness), Is.EqualTo("mmo-guard"));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Leave_WithoutMembershipOrOtherClan_Returns409WithoutChange()
    {
        var harness = await HarnessAsync();
        var notMember = await LeaveAsync(harness, harness.Clans, "mmo-guard");
        await JoinAsync(harness, harness.Clans, "mmo-guard");

        var wrongClan = await LeaveAsync(harness, harness.Clans, "mmo-orbit");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(notMember.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(notMember));
            Assert.That(wrongClan.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(wrongClan));
            Assert.That(await MembershipAsync(harness), Is.EqualTo("mmo-guard"));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
        });
    }

    [TestCase("mmo-elite", StatusCodes.Status409Conflict, "not recruiting")]
    [TestCase("eic-orbit", StatusCodes.Status403Forbidden, "another company")]
    [TestCase("mmo-ghost", StatusCodes.Status404NotFound, "does not exist")]
    public async Task Join_UnavailableClan_IsRejectedWithoutWrites(string clan, int status, string message)
    {
        var harness = await HarnessAsync();

        var context = await JoinAsync(harness, harness.Clans, clan);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), Body(context));
            Assert.That(Body(context), Does.Contain(message));
            Assert.That(await MembershipAsync(harness), Is.Null);
            Assert.That(await ActionCountAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task Join_ReplayedKeyOnAnotherReplica_AppliesOnce()
    {
        var harness = await HarnessAsync();
        var key = LocalClanAction.CreateFormKey(harness.Session.Dosid, "join", "mmo-guard", Start);
        var form = $"clan=mmo-guard&key={Uri.EscapeDataString(key)}";

        var first = await PostAsync(harness, harness.Clans, "internalClanJoin", form);
        var leave = await LeaveAsync(harness, harness.Clans, "mmo-guard");
        // The same join form resubmitted to a fresh replica after leaving must not rejoin.
        var replay = await PostAsync(harness, NewStore(harness), "internalClanJoin", form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(leave.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(leave));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(Body(replay), Does.Contain("already submitted"));
            Assert.That(await MembershipAsync(harness), Is.Null);
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ConcurrentJoinsAcrossReplicas_OnlyOneMembershipWins()
    {
        var harness = await HarnessAsync();
        string[] clans = ["mmo-guard", "mmo-orbit", "mmo-traders", "mmo-guard"];

        var results = await Task.WhenAll(clans.Select(clan => JoinAsync(harness, NewStore(harness), clan)));

        var membership = await MembershipAsync(harness);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(membership, Is.AnyOf("mmo-guard", "mmo-orbit", "mmo-traders"));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LegacyCacheMembership_IsImportedOnceThenLeavable()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync($"localclan:{harness.Session.AccountId:N}", "mmo-traders", Ct);

        var imported = await harness.Clans.GetMembershipAsync(harness.Session.AccountId, Ct);
        var leave = await LeaveAsync(harness, harness.Clans, "mmo-traders");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(imported, Is.EqualTo("mmo-traders"));
            Assert.That(leave.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(leave));
            Assert.That(await harness.Cache.GetStringAsync($"localclan:{harness.Session.AccountId:N}", Ct), Is.Null);
            // The legacy entry was removed on import, so a later read cannot resurrect it.
            Assert.That(await harness.Clans.GetMembershipAsync(harness.Session.AccountId, Ct), Is.Null);
        });
    }

    [Test]
    public async Task Leave_OwnClanAfterCompanyChange_IsAllowed()
    {
        var harness = await HarnessAsync();
        await JoinAsync(harness, harness.Clans, "mmo-guard");
        Assert.That(await harness.Sessions.SelectCompanyAsync(harness.Session.Dosid, "eic", Ct), Is.True);
        var eic = harness with { Session = (await harness.Sessions.TryGetByDosidAsync(harness.Session.Dosid, Ct))! };

        var joinWhileStillMember = await JoinAsync(eic, eic.Clans, "eic-orbit");
        var leave = await LeaveAsync(eic, NewStore(eic), "mmo-guard");
        var join = await JoinAsync(eic, eic.Clans, "eic-orbit");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(joinWhileStillMember.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(joinWhileStillMember));
            Assert.That(leave.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(leave));
            Assert.That(join.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(join));
            Assert.That(await MembershipAsync(harness), Is.EqualTo("eic-orbit"));
        });
    }

    [Test]
    public async Task ClanPage_ShowsPersistedMembership()
    {
        var harness = await HarnessAsync();
        await JoinAsync(harness, harness.Clans, "mmo-orbit");

        var membership = await NewStore(harness).GetMembershipAsync(harness.Session.AccountId, Ct);
        var page = LocalClan.Page(harness.Session, membership, Start);

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Mars Orbit"));
            Assert.That(page, Does.Contain("action=internalClanLeave"));
        });
    }

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        IDistributedCache Cache, LocalClanStore Clans);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalClanStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

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
            insert.Parameters.AddWithValue("u", $"clan-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"clan-pilot-{gameUserId}", null, gameUserId), null, Ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", Ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, Ct))!;
        var economy = new EconomyStore(_config);
        var cache = NewCache();
        return new Harness(sessions, session, economy, cache, new LocalClanStore(cache, economy));
    }

    private static Task<DefaultHttpContext> JoinAsync(Harness harness, LocalClanStore store, string clan) =>
        PostAsync(harness, store, "internalClanJoin",
            $"clan={clan}&key={Uri.EscapeDataString(LocalClanAction.CreateFormKey(harness.Session.Dosid, "join", clan, Start))}");

    private static Task<DefaultHttpContext> LeaveAsync(Harness harness, LocalClanStore store, string clan) =>
        PostAsync(harness, store, "internalClanLeave",
            $"clan={clan}&key={Uri.EscapeDataString(LocalClanAction.CreateFormKey(harness.Session.Dosid, "leave", clan, Start))}");

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalClanStore store,
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
        await LocalClanAction.HandleAsync(context, harness.Sessions, store, new FixedClock(Start), Ct);
        return context;
    }

    private async Task<string?> MembershipAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT clan_id FROM economy.clan_memberships WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<long> ActionCountAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM economy.clan_actions WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
