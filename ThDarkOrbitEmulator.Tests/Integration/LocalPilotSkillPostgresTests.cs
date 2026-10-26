using System.Text;
using System.Text.Json;
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

// Menu-89: drives the Pilot Sheet allocate/reset POST handlers (EconomyStore-backed LocalPilotSkillStore)
// against migrated Postgres. Ranks and revision live in economy.pilot_skill_states, form keys are
// single-use across replicas (pilot_skill_actions), and the account advisory lock serializes submissions.
[TestFixture]
[Category("Integration")]
public sealed class LocalPilotSkillPostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 89_000;

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
    public async Task NewPilot_HasNoRowAndAllPointsAvailable()
    {
        var harness = await HarnessAsync();

        var state = await harness.Skills.GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(state.Ranks, Is.Empty);
            Assert.That(state.Revision, Is.Zero);
            Assert.That(state.Available, Is.EqualTo(LocalPilotSkillState.TotalPoints));
        });
        Assert.That(await RowAsync(harness), Is.Null);
    }

    [Test]
    public async Task Allocate_PersistsRankRevisionAndKey_VisibleToFreshReplica()
    {
        var harness = await HarnessAsync();

        var context = await AllocateAsync(harness, harness.Skills, "hull", 0);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalPilotSheet&trained=hull"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
        var row = await RowAsync(harness);
        var fresh = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);
        Assert.Multiple(async () =>
        {
            Assert.That(row?.Ranks, Is.EqualTo(new Dictionary<string, int> { ["hull"] = 1 }));
            Assert.That(row?.Revision, Is.EqualTo(1));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
            Assert.That(fresh.Ranks["hull"], Is.EqualTo(1));
            Assert.That(fresh.Available, Is.EqualTo(LocalPilotSkillState.TotalPoints - 1));
        });
    }

    [Test]
    public async Task PrerequisiteChain_UnlocksAfterThreeRanks()
    {
        var harness = await HarnessAsync();

        var early = await AllocateAsync(harness, harness.Skills, "evasion", 0);
        for (var rev = 0; rev < 3; rev++)
            Assert.That((await AllocateAsync(harness, NewStore(harness), "hull", rev)).Response.StatusCode,
                Is.EqualTo(StatusCodes.Status303SeeOther));
        var unlocked = await AllocateAsync(harness, NewStore(harness), "evasion", 3);

        Assert.Multiple(async () =>
        {
            // A locked skill with a current revision is refused as a stale/conflicting form.
            Assert.That(early.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(early));
            Assert.That(unlocked.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(unlocked));
            Assert.That((await RowAsync(harness))?.Ranks,
                Is.EqualTo(new Dictionary<string, int> { ["hull"] = 3, ["evasion"] = 1 }));
            Assert.That((await RowAsync(harness))?.Revision, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task MaxRankAndPointBudget_AreEnforced()
    {
        var harness = await HarnessAsync();
        var rev = 0;
        foreach (var skill in new[] { "hull", "hull", "hull", "hull", "hull", "shields", "shields", "shields", "shields", "shields" })
            Assert.That((await AllocateAsync(harness, harness.Skills, skill, rev++)).Response.StatusCode,
                Is.EqualTo(StatusCodes.Status303SeeOther));

        var overRank = await AllocateAsync(harness, harness.Skills, "hull", rev);
        var noPoints = await AllocateAsync(harness, harness.Skills, "lasers", rev);

        Assert.Multiple(async () =>
        {
            Assert.That(overRank.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(overRank));
            Assert.That(noPoints.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(noPoints));
            var row = await RowAsync(harness);
            Assert.That(row?.Ranks, Is.EqualTo(new Dictionary<string, int> { ["hull"] = 5, ["shields"] = 5 }));
            Assert.That(row?.Revision, Is.EqualTo(10));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(10));
        });
    }

    [Test]
    public async Task StaleRevision_IsConflictAndChangesNothing()
    {
        var harness = await HarnessAsync();
        await AllocateAsync(harness, harness.Skills, "lasers", 0);

        var stale = await AllocateAsync(harness, NewStore(harness), "shields", 0);

        Assert.That(stale.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(stale));
        Assert.That((await RowAsync(harness))?.Ranks, Is.EqualTo(new Dictionary<string, int> { ["lasers"] = 1 }));
    }

    [Test]
    public async Task ReplayedKey_OnAnotherReplicaAfterReset_IsConflict()
    {
        var harness = await HarnessAsync();
        var allocateForm = AllocateForm(harness, "hull", 0);
        var first = await PostAsync(harness, harness.Skills, "internalPilotSkillAllocate", allocateForm);
        var reset = await ResetAsync(harness, harness.Skills, 1);
        // The resubmitted form carries a used key and a stale revision; both refuse it on a fresh replica.
        var replay = await PostAsync(harness, NewStore(harness), "internalPilotSkillAllocate", allocateForm);

        Assert.Multiple(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(reset.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(reset));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            var row = await RowAsync(harness);
            Assert.That(row?.Ranks, Is.Empty);
            Assert.That(row?.Revision, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ReplayedKey_WithMatchingRevision_IsRejectedByActionTable()
    {
        var harness = await HarnessAsync();
        var key = LocalPilotSkillAction.CreateFormKey(harness.Session.Dosid, "allocate", "hull", 0, Start);
        await InsertActionAsync(harness, key.Split('.')[0]);

        var context = await PostAsync(harness, NewStore(harness), "internalPilotSkillAllocate",
            $"skill=hull&rev=0&key={Uri.EscapeDataString(key)}");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
        Assert.That(await RowAsync(harness), Is.Null);
    }

    [Test]
    public async Task Reset_ClearsRanks_AndEmptyResetIsConflict()
    {
        var harness = await HarnessAsync();
        await AllocateAsync(harness, harness.Skills, "hull", 0);
        await AllocateAsync(harness, harness.Skills, "shields", 1);

        var reset = await ResetAsync(harness, NewStore(harness), 2);
        var again = await ResetAsync(harness, NewStore(harness), 3);

        Assert.Multiple(async () =>
        {
            Assert.That(reset.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(reset));
            Assert.That(again.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(again));
            var row = await RowAsync(harness);
            Assert.That(row?.Ranks, Is.Empty);
            Assert.That(row?.Revision, Is.EqualTo(3));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task ExpiredForm_IsConflictWithoutWrite()
    {
        var harness = await HarnessAsync();
        var key = LocalPilotSkillAction.CreateFormKey(harness.Session.Dosid, "allocate", "hull", 0,
            Start - LocalPilotSkillAction.FormLifetime - TimeSpan.FromSeconds(1));

        var context = await PostAsync(harness, harness.Skills, "internalPilotSkillAllocate",
            $"skill=hull&rev=0&key={Uri.EscapeDataString(key)}");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
        Assert.That(await RowAsync(harness), Is.Null);
        Assert.That(await ActionCountAsync(harness), Is.Zero);
    }

    [Test]
    public async Task ConcurrentAllocations_FromSeparateReplicas_ApplyExactlyOnce()
    {
        var harness = await HarnessAsync();
        var forms = Enumerable.Range(0, 4).Select(_ => AllocateForm(harness, "lasers", 0)).ToList();

        var results = await Task.WhenAll(forms.Select(form =>
            PostAsync(harness, NewStore(harness), "internalPilotSkillAllocate", form)));

        Assert.Multiple(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            var row = await RowAsync(harness);
            Assert.That(row?.Ranks, Is.EqualTo(new Dictionary<string, int> { ["lasers"] = 1 }));
            Assert.That(row?.Revision, Is.EqualTo(1));
            Assert.That(await ActionCountAsync(harness), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LegacyCacheState_IsImportedOnceAndRemovedFromCache()
    {
        var harness = await HarnessAsync();
        await harness.Cache.SetStringAsync($"localpilotskills:{harness.Session.AccountId:N}",
            JsonSerializer.Serialize(new LocalPilotSkillState(new Dictionary<string, int> { ["hull"] = 3, ["evasion"] = 1 }, 4)), Ct);

        var imported = await harness.Skills.GetAsync(harness.Session.AccountId, Ct);
        var next = await AllocateAsync(harness, harness.Skills, "evasion", 4);

        Assert.Multiple(async () =>
        {
            Assert.That(imported.Ranks, Is.EqualTo(new Dictionary<string, int> { ["hull"] = 3, ["evasion"] = 1 }));
            Assert.That(imported.Revision, Is.EqualTo(4));
            Assert.That(await harness.Cache.GetStringAsync($"localpilotskills:{harness.Session.AccountId:N}", Ct), Is.Null);
            Assert.That(next.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(next));
            Assert.That((await RowAsync(harness))?.Ranks["evasion"], Is.EqualTo(2));
        });
    }

    [Test]
    public async Task LegacyCacheState_NeverOverwritesExistingRow_AndIsSanitized()
    {
        var harness = await HarnessAsync();
        await AllocateAsync(harness, harness.Skills, "shields", 0);
        var cacheKey = $"localpilotskills:{harness.Session.AccountId:N}";
        await harness.Cache.SetStringAsync(cacheKey,
            JsonSerializer.Serialize(new LocalPilotSkillState(new Dictionary<string, int> { ["hull"] = 5 }, 9)), Ct);

        var state = await harness.Skills.GetAsync(harness.Session.AccountId, Ct);

        // An oversized legacy entry for a pilot without a row imports as an empty state.
        var other = await HarnessAsync();
        await other.Cache.SetStringAsync($"localpilotskills:{other.Session.AccountId:N}",
            JsonSerializer.Serialize(new LocalPilotSkillState(new Dictionary<string, int> { ["hull"] = 9, ["shields"] = 9, ["bogus"] = 4 }, 2)), Ct);
        var sanitized = await other.Skills.GetAsync(other.Session.AccountId, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(state.Ranks, Is.EqualTo(new Dictionary<string, int> { ["shields"] = 1 }));
            Assert.That(state.Revision, Is.EqualTo(1));
            Assert.That(sanitized.Ranks, Is.EqualTo(new Dictionary<string, int> { ["hull"] = 5, ["shields"] = 5 }));
            Assert.That(sanitized.Revision, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task TamperedRow_IsSanitizedOnRead()
    {
        var harness = await HarnessAsync();
        await using (var db = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await db.OpenAsync(Ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO economy.pilot_skill_states(account_id,ranks,revision) VALUES (@a,'{\"hull\":7,\"lasers\":-2,\"bogus\":3,\"shields\":2}'::jsonb,5)", db);
            insert.Parameters.AddWithValue("a", harness.Session.AccountId);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, Ct);

        Assert.Multiple(() =>
        {
            Assert.That(state.Ranks, Is.EqualTo(new Dictionary<string, int> { ["hull"] = 5, ["shields"] = 2 }));
            Assert.That(state.Revision, Is.EqualTo(5));
        });
    }

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        IDistributedCache Cache, LocalPilotSkillStore Skills);

    private sealed record SkillRow(Dictionary<string, int> Ranks, int Revision);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalPilotSkillStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

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
            insert.Parameters.AddWithValue("u", $"skill-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"skill-pilot-{gameUserId}", null, gameUserId), null, Ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", Ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, Ct))!;
        var economy = new EconomyStore(_config);
        var cache = NewCache();
        return new Harness(sessions, session, economy, cache, new LocalPilotSkillStore(cache, economy));
    }

    private static string AllocateForm(Harness harness, string skill, int rev) =>
        $"skill={skill}&rev={rev}&key={Uri.EscapeDataString(LocalPilotSkillAction.CreateFormKey(harness.Session.Dosid, "allocate", skill, rev, Start))}";

    private static Task<DefaultHttpContext> AllocateAsync(Harness harness, LocalPilotSkillStore store, string skill, int rev) =>
        PostAsync(harness, store, "internalPilotSkillAllocate", AllocateForm(harness, skill, rev));

    private static Task<DefaultHttpContext> ResetAsync(Harness harness, LocalPilotSkillStore store, int rev) =>
        PostAsync(harness, store, "internalPilotSkillReset",
            $"rev={rev}&key={Uri.EscapeDataString(LocalPilotSkillAction.CreateFormKey(harness.Session.Dosid, "reset", "", rev, Start))}");

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalPilotSkillStore store,
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
        await LocalPilotSkillAction.HandleAsync(context, harness.Sessions, store, new FixedClock(Start), Ct);
        return context;
    }

    private async Task<SkillRow?> RowAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT ranks::text, revision FROM economy.pilot_skill_states WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new SkillRow(JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(0))!, reader.GetInt32(1));
    }

    private async Task InsertActionAsync(Harness harness, string key)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO economy.pilot_skill_actions(account_id,action_key) VALUES (@a,@k)", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ActionCountAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM economy.pilot_skill_actions WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
