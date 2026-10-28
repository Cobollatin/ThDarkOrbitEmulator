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
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

// Menu-88: drives the Galaxy Gates spin/activate POST handlers (EconomyStore-backed LocalGalaxyGateStore)
// against migrated Postgres. Accounts start with 250 galaxy_energy; each spin part costs 1 energy, gates need
// 34/48/82/96/120 parts, and activation is free once a gate is complete. Form keys are single-use and the
// account lock serializes replicas.
[TestFixture]
[Category("Integration")]
public sealed class LocalGalaxyGatePostgresTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedClock(Start);
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 88_000;

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
    public void GateCatalog_HasTenOrderedNpcWavesForEachRequestedGate()
    {
        var kinds = new[] { "Streuner", "Lordakia", "Mordon", "Saimon", "Devolarium",
            "Kristallin", "Sibelon", "Sibelonit", "Kristallon", "Small Kubikon" };
        foreach (var gate in new[] { "alpha", "beta", "gamma" })
        {
            Assert.That(Enumerable.Range(0, 10).Select(w => EconomyStore.GateWaveAt(gate, w)!.Npc), Is.EqualTo(kinds));
            Assert.That(EconomyStore.GateWaveAt(gate, 10), Is.Null);
        }
        Assert.That(EconomyStore.GateWaveAt("alpha", 7)!.Count, Is.EqualTo(80));
        Assert.That(EconomyStore.GateWaveAt("beta", 2)!.Count, Is.EqualTo(40));
        Assert.That(EconomyStore.GateWaveAt("gamma", 3)!.Count, Is.EqualTo(40));
        Assert.That(Enumerable.Range(0, 10).Select(w => EconomyStore.GateWaveAt("delta", w)!.Count),
            Is.EqualTo(new[] { 30, 12, 30, 13, 24, 16, 20, 11, 18, 3 }));
        Assert.That(EconomyStore.GateWaveAt("delta", 0)!.NpcAt(5), Is.EqualTo("Mordon"));
        Assert.That(EconomyStore.GateWaveAt("delta", 9)!.NpcAt(0), Is.EqualTo("demaNeR"));
    }

    [Test]
    public async Task FirstRead_ImportsStarterEnergyOnceWithoutGateRows()
    {
        var harness = await HarnessAsync();
        var state = await harness.Gates.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(state.Energy, Is.EqualTo(250));
            Assert.That(state.Gates.Select(g => (g.Id, g.PartsRequired, g.PartsOwned, g.Activated)), Is.EqualTo(
                LocalGalaxyGates.Catalog.Select(g => (g.Id, g.PartsRequired, 0, false))));
            Assert.That(await EnergyAsync(harness), Is.EqualTo(250));
            Assert.That(await LedgerDeltasAsync(harness, "legacy_import"), Is.EqualTo(new long[] { 250 }));
            Assert.That(await GateRowCountAsync(harness), Is.Zero);
        });
    }

    [Test]
    public async Task ActivatedGate_RequiresNpcKillsAndDeductsLifeOnShipDestruction()
    {
        var h = await HarnessAsync();
        var ct = TestContext.CurrentContext.CancellationToken;
        await SpinUntilAsync(h, "alpha", 34);
        var activation = await ActivateAsync(h, h.Gates, "alpha");
        Assert.That(activation.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(activation));
        var id = h.Session.AccountId;
        var other = new EconomyStore(_config);
        Assert.That(await other.GateRunAsync(id, "alpha", ct), Is.EqualTo(new GateRun(0, 10, null)));
        Assert.That(await h.Economy.AdvanceGateWaveAsync(id, "alpha", 0, Start, "forged", ct), Is.EqualTo(ProgressionChange.Conflict));
        Assert.That(await h.Economy.AttackGateNpcAsync(id, "alpha", 0, Start, "shot-1", ct), Is.EqualTo(ProgressionChange.Applied));
        Assert.That(await other.AttackGateNpcAsync(id, "alpha", 0, Start, "shot-1", ct), Is.EqualTo(ProgressionChange.Conflict));
        Assert.That((await other.GateCombatAsync(id, "alpha", ct))!.Kills, Is.EqualTo(1));
        Assert.That(await h.Economy.AdvanceGateWaveAsync(id, "alpha", 0, Start, "forged-2", ct), Is.EqualTo(ProgressionChange.Conflict));
        await SetCombatAsync(id, "alpha", kills: 1, shipHp: 20);
        Assert.That(await other.AttackGateNpcAsync(id, "alpha", 0, Start, "fatal-shot", ct), Is.EqualTo(ProgressionChange.Applied));
        var afterDeath = await h.Economy.GateCombatAsync(id, "alpha", ct);
        Assert.That((afterDeath!.Lives, afterDeath.ShipHp, afterDeath.Kills), Is.EqualTo((4, 1000, 2)));
        Assert.That(await other.AttackGateNpcAsync(id, "alpha", 1, Start, "skip", ct), Is.EqualTo(ProgressionChange.Conflict));
        Assert.That(await EnergyAsync(h), Is.EqualTo(216));
    }

    [Test]
    public async Task LastLifeLost_BlocksFurtherAttacksAndCompletionRewards()
    {
        var h = await HarnessAsync();
        var ct = TestContext.CurrentContext.CancellationToken;
        await SpinUntilAsync(h, "alpha", 34);
        await ActivateAsync(h, h.Gates, "alpha");
        await SetCombatAsync(h.Session.AccountId, "alpha", 0, 20, lives: 1);
        Assert.That(await h.Economy.AttackGateNpcAsync(h.Session.AccountId, "alpha", 0, Start, "last-life", ct), Is.EqualTo(ProgressionChange.Applied));
        Assert.That((await h.Economy.GateCombatAsync(h.Session.AccountId, "alpha", ct))!.Lives, Is.Zero);
        Assert.That(await h.Economy.AttackGateNpcAsync(h.Session.AccountId, "alpha", 0, Start, "after-death", ct), Is.EqualTo(ProgressionChange.Conflict));
        Assert.That(await RewardDeltaAsync(h.Session.AccountId, "alpha", "uridium"), Is.Zero);
        Assert.That(await AchievementCountAsync(h.Session.AccountId, "alpha"), Is.Zero);
    }

    [Test]
    public async Task SignedCombatForm_AppliesOnceAndLegacyWaveFormIsRejected()
    {
        var h = await HarnessAsync();
        await SpinUntilAsync(h, "alpha", 34);
        await ActivateAsync(h, h.Gates, "alpha");
        var key = LocalProgressionAction.FormKey(h.Session.Dosid, "internalGalaxyGateAttack", "alpha", "0", Start);
        var form = $"gate=alpha&wave=0&key={Uri.EscapeDataString(key)}";
        Assert.That((await ProgressionPostAsync(h, "internalGalaxyGateAttack", form)).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await ProgressionPostAsync(h, "internalGalaxyGateAttack", form)).Response.StatusCode, Is.EqualTo(409));
        Assert.That((await ProgressionPostAsync(h, "internalGalaxyGateWave", form)).Response.StatusCode, Is.EqualTo(400));
        Assert.That((await h.Economy.GateCombatAsync(h.Session.AccountId, "alpha", TestContext.CurrentContext.CancellationToken))!.Kills, Is.EqualTo(1));
    }

    [Test]
    public async Task WorldPortal_EntersActiveGateAndWorldShotsAdvanceWavesAndGrantRewards()
    {
        var h = await HarnessAsync();
        var ct = TestContext.CurrentContext.CancellationToken;
        var cache = NewCache();
        var world = new WorldSessionStore(cache, economy: h.Economy);
        var combat = new WorldGateCombat(h.Economy, cache);
        var pilot = new WorldPilot(h.Session.GameUserId, "mmo", 1, 1, "ship_phoenix", 0, 0)
        {
            AccountId = h.Session.AccountId, FactionId = 1, X = 17500, Y = 6500
        };
        pilot = await world.EnterAsync(pilot, ct);
        var portal = WorldMapInstance.Starter(1).Portals.Single(p => p.DestinationMapId == 101);
        Assert.That(await world.JumpAsync(pilot, portal.Id, null, ct), Is.Null);
        await SpinUntilAsync(h, "alpha", 34);
        await ActivateAsync(h, h.Gates, "alpha");
        var jump = await world.JumpAsync(pilot, portal.Id, null, ct);
        Assert.That(jump, Is.Not.Null);
        pilot = jump!.Pilot;
        Assert.That(pilot.MapId, Is.EqualTo(101));
        Assert.That((await world.RestoreAsync(pilot, ct)).MapId, Is.EqualTo(101));
        for (var wave = 0; wave < 10; wave++)
        {
            var required = EconomyStore.GateWaveAt("alpha", wave)!.Count;
            await SetCombatAsync(h.Session.AccountId, "alpha", required - 1);
            var npc = (await combat.VisibleAsync(pilot, ct)).Single();
            Assert.That(npc.Kind, Is.EqualTo(EconomyStore.GateWaveAt("alpha", wave)!.Npc));
            Assert.That(await combat.SelectAsync(pilot, npc.Id, ct), Is.Not.Null);
            Assert.That((await combat.AttackAsync(pilot, npc.Id, world, ct)).Destroyed, Is.True);
            Assert.That((await h.Economy.GateRunAsync(h.Session.AccountId, "alpha", ct))!.Wave,
                Is.EqualTo(wave + 1));
        }
        Assert.That(await combat.VisibleAsync(pilot, ct), Is.Empty);
        Assert.That(await RewardDeltaAsync(h.Session.AccountId, "alpha", "uridium"), Is.EqualTo(20000));
        Assert.That(await AchievementCountAsync(h.Session.AccountId, "alpha"), Is.EqualTo(1));
        var exit = await world.JumpAsync(pilot, WorldMapInstance.Starter(101).Portals.Single().Id, null, ct);
        Assert.That(exit!.Pilot.MapId, Is.EqualTo(1));
    }

    private static async Task<DefaultHttpContext> ProgressionPostAsync(Harness h, string action, string form)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={h.Session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(form);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalProgressionAction.HandleAsync(context, h.Sessions,
            new LocalSkylabStore(NewCache(), Clock, h.Economy), h.Gates, h.Economy, Clock,
            TestContext.CurrentContext.CancellationToken);
        return context;
    }

    [TestCase("alpha", 34, 20000, 100000, 4000000)]
    [TestCase("beta", 48, 40000, 200000, 8000000)]
    [TestCase("gamma", 82, 60000, 300000, 12000000)]
    [TestCase("delta", 96, 45000, 225000, 9000000)]
    public async Task ClearingEachWave_GrantsCompletionRewardsAndAchievementOnce(
        string gate, int parts, long uridium, long honor, long experience)
    {
        var h = await HarnessAsync();
        var ct = TestContext.CurrentContext.CancellationToken;
        await SpinUntilAsync(h, gate, parts);
        Assert.That((await ActivateAsync(h, h.Gates, gate)).Response.StatusCode, Is.EqualTo(303));
        var id = h.Session.AccountId;
        for (var wave = 0; wave < 10; wave++)
        {
            var composition = EconomyStore.GateWaveAt(gate, wave)!;
            Assert.That(composition.Count, Is.GreaterThan(1));
            await SetCombatAsync(id, gate, kills: composition.Count - 1);
            Assert.That(await h.Economy.AttackGateNpcAsync(id, gate, wave, Start,
                $"{gate}-wave-{wave}", ct), Is.EqualTo(ProgressionChange.Applied));
            Assert.That((await h.Economy.GateRunAsync(id, gate, ct))!.Wave, Is.EqualTo(wave + 1));
        }
        Assert.That((await h.Economy.GateRunAsync(id, gate, ct))!.CompletedAt, Is.EqualTo(Start));
        Assert.That(await h.Economy.AttackGateNpcAsync(id, gate, 9, Start, "replay", ct), Is.EqualTo(ProgressionChange.Conflict));
        Assert.That(await RewardDeltaAsync(id, gate, "uridium"), Is.EqualTo(uridium));
        Assert.That(await RewardDeltaAsync(id, gate, "ucb_100"), Is.EqualTo(uridium));
        Assert.That(await RewardDeltaAsync(id, gate, "honor"), Is.EqualTo(honor));
        Assert.That(await RewardDeltaAsync(id, gate, "experience"), Is.EqualTo(experience));
        Assert.That(await AchievementCountAsync(id, gate), Is.EqualTo(1));
    }

    private async Task SetCombatAsync(Guid accountId, string gate, int kills, int? shipHp = null, int? lives = null)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE economy.gate_runs SET kills=@k,ship_hp=COALESCE(@h,ship_hp),lives=COALESCE(@l,lives) WHERE account_id=@a AND gate_id=@g", db);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("g", gate);
        cmd.Parameters.AddWithValue("k", kills); cmd.Parameters.AddWithValue("h", (object?)shipHp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("l", (object?)lives ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> RewardDeltaAsync(Guid accountId, string gate, string asset)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COALESCE(sum(delta),0) FROM economy.ledger WHERE account_id=@a AND menu_source=@g AND asset_id=@s AND reason='gate_complete'", db);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("g", gate); cmd.Parameters.AddWithValue("s", asset);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<long> AchievementCountAsync(Guid accountId, string gate)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM economy.gate_achievements WHERE account_id=@a AND gate_id=@g", db);
        cmd.Parameters.AddWithValue("a", accountId); cmd.Parameters.AddWithValue("g", gate);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Test]
    public async Task Spin_DebitsEnergyOnceAndSavesParts()
    {
        var harness = await HarnessAsync();

        var context = await SpinAsync(harness, harness.Gates, "alpha", 7);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&spun=alpha"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(await EnergyAsync(harness), Is.EqualTo(243));
            Assert.That(await LedgerDeltasAsync(harness, "gate_spin"), Is.EqualTo(new long[] { -7 }));
            Assert.That(await PartsAsync(harness, "alpha"), Is.EqualTo((7, false)));
        });
    }

    [Test]
    public async Task Spin_ReplayedFormOnAnotherReplica_IsRefusedWithoutSecondCharge()
    {
        var harness = await HarnessAsync();
        var form = SpinForm(harness, "beta", 5);

        var first = await PostAsync(harness, harness.Gates, "internalGalaxyGateSpin", form);
        var replay = await PostAsync(harness, NewStore(harness), "internalGalaxyGateSpin", form);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(first));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(replay));
            Assert.That(Body(replay), Does.Contain("already submitted"));
            Assert.That(await EnergyAsync(harness), Is.EqualTo(245));
            Assert.That(await LedgerDeltasAsync(harness, "gate_spin"), Is.EqualTo(new long[] { -5 }));
            Assert.That(await PartsAsync(harness, "beta"), Is.EqualTo((5, false)));
        });
    }

    [Test]
    public async Task Spin_MoreThanMissingParts_IsRefusedWithNothingCharged()
    {
        var harness = await HarnessAsync();
        await SpinUntilAsync(harness, "alpha", 30);

        var context = await SpinAsync(harness, harness.Gates, "alpha", 5);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(context));
            Assert.That(Body(context), Does.Contain("Only 4 part(s) are missing"));
            Assert.That(await EnergyAsync(harness), Is.EqualTo(220));
            Assert.That(await PartsAsync(harness, "alpha"), Is.EqualTo((30, false)));
        });
    }

    [Test]
    public async Task Activate_RequiresCompleteGateAndIsFreeAndSingleUse()
    {
        var harness = await HarnessAsync();
        var early = await ActivateAsync(harness, harness.Gates, "alpha");
        await SpinUntilAsync(harness, "alpha", 34);
        var spinComplete = await SpinAsync(harness, harness.Gates, "alpha", 1);

        var activate = await ActivateAsync(harness, harness.Gates, "alpha");
        var again = await ActivateAsync(harness, NewStore(harness), "alpha");
        var spinActivated = await SpinAsync(harness, harness.Gates, "alpha", 1);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(early.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(early));
            Assert.That(Body(early), Does.Contain("still needs 34 part(s)"));
            Assert.That(spinComplete.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(spinComplete));
            Assert.That(Body(spinComplete), Does.Contain("Activate it instead"));
            Assert.That(activate.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(activate));
            Assert.That(again.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(again));
            Assert.That(Body(again), Does.Contain("already activated"));
            Assert.That(spinActivated.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict), Body(spinActivated));
            Assert.That(await PartsAsync(harness, "alpha"), Is.EqualTo((34, true)));
            Assert.That(await EnergyAsync(harness), Is.EqualTo(250 - 34));
            Assert.That((await LedgerDeltasAsync(harness, "gate_spin")).Sum(), Is.EqualTo(-34));
        });
    }

    [Test]
    public async Task Spin_WhenEnergyRunsOut_IsRefusedWithNothingCharged()
    {
        var harness = await HarnessAsync();
        await SpinUntilAsync(harness, "alpha", 34);
        await SpinUntilAsync(harness, "beta", 48);
        await SpinUntilAsync(harness, "gamma", 82);
        await SpinUntilAsync(harness, "delta", 86);
        Assert.That(await EnergyAsync(harness), Is.Zero);

        var context = await SpinAsync(harness, harness.Gates, "delta", 1);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), Body(context));
            Assert.That(Body(context), Does.Contain("1 needed, 0 available"));
            Assert.That(await EnergyAsync(harness), Is.Zero);
            Assert.That(await PartsAsync(harness, "delta"), Is.EqualTo((86, false)));
            Assert.That((await LedgerDeltasAsync(harness, "gate_spin")).Sum(), Is.EqualTo(-250));
        });
    }

    [Test]
    public async Task SavedProgress_IsVisibleToAFreshReplica()
    {
        var harness = await HarnessAsync();
        await SpinAsync(harness, harness.Gates, "gamma", 9);

        var state = await NewStore(harness).GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(state.Energy, Is.EqualTo(241));
            Assert.That(state.Gates.Single(g => g.Id == "gamma").PartsOwned, Is.EqualTo(9));
            Assert.That(state.Gates.Where(g => g.Id != "gamma").Sum(g => g.PartsOwned), Is.Zero);
        });
    }

    [Test]
    public async Task ConcurrentSpinsForLastParts_ExactlyOneWins()
    {
        var harness = await HarnessAsync();
        await SpinUntilAsync(harness, "alpha", 30);

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => SpinAsync(harness, NewStore(harness), "alpha", 4)));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status303SeeOther), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Response.StatusCode == StatusCodes.Status409Conflict), Is.EqualTo(3));
            Assert.That(await PartsAsync(harness, "alpha"), Is.EqualTo((34, false)));
            Assert.That(await EnergyAsync(harness), Is.EqualTo(250 - 34));
        });
    }

    [Test]
    public async Task LegacyCacheProgress_IsImportedOnceAndClamped()
    {
        var harness = await HarnessAsync(readFirst: false);
        var cache = NewCache();
        var legacy = new LocalGalaxyGateState(40, [.. LocalGalaxyGates.Catalog.Select(g => g.Id switch
        {
            "alpha" => g with { PartsOwned = 34, Activated = true },
            "beta" => g with { PartsOwned = 500 },
            _ => g
        })]);
        await cache.SetStringAsync($"localgalaxygates:{harness.Session.AccountId:N}", JsonSerializer.Serialize(legacy));
        var store = new LocalGalaxyGateStore(cache, harness.Economy);

        await store.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);
        var context = await SpinAsync(harness, store, "gamma", 3);
        var state = await store.GetAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            Assert.That(state.Energy, Is.EqualTo(37));
            Assert.That(await LedgerDeltasAsync(harness, "legacy_import"), Is.EqualTo(new long[] { 40 }));
            Assert.That(await PartsAsync(harness, "alpha"), Is.EqualTo((34, true)));
            Assert.That(await PartsAsync(harness, "beta"), Is.EqualTo((48, false)));
            Assert.That(await PartsAsync(harness, "gamma"), Is.EqualTo((3, false)));
        });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalGalaxyGateStore Gates);

    private static IDistributedCache NewCache() =>
        new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();

    private static LocalGalaxyGateStore NewStore(Harness harness) => new(NewCache(), harness.Economy);

    private async Task<Harness> HarnessAsync(bool readFirst = true)
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
            insert.Parameters.AddWithValue("u", $"gate-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var sessions = new GameAuthSessionStore(NewCache());
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"gate-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        var gates = new LocalGalaxyGateStore(NewCache(), economy);
        // The first Galaxy Gates page read imports the starter energy.
        if (readFirst) await gates.GetAsync(accountId, ct);
        return new Harness(sessions, session, economy, gates);
    }

    private static string SpinForm(Harness harness, string gate, int amount)
    {
        var key = LocalGalaxyGateAction.CreateFormKey(harness.Session.Dosid, gate, issued: Start);
        return $"gate={gate}&amount={amount}&key={Uri.EscapeDataString(key)}";
    }

    private static Task<DefaultHttpContext> SpinAsync(Harness harness, LocalGalaxyGateStore store, string gate, int amount) =>
        PostAsync(harness, store, "internalGalaxyGateSpin", SpinForm(harness, gate, amount));

    private static Task<DefaultHttpContext> ActivateAsync(Harness harness, LocalGalaxyGateStore store, string gate)
    {
        var key = LocalGalaxyGateAction.CreateFormKey(harness.Session.Dosid, gate, activate: true, issued: Start);
        return PostAsync(harness, store, "internalGalaxyGateActivate", $"gate={gate}&key={Uri.EscapeDataString(key)}");
    }

    // Spins in maximum-size forms until the gate owns `target` parts.
    private async Task SpinUntilAsync(Harness harness, string gate, int target)
    {
        for (var owned = (await PartsAsync(harness, gate)).Parts; owned < target;)
        {
            var amount = Math.Min(LocalGalaxyGateAction.MaxSpinsPerForm, target - owned);
            var context = await SpinAsync(harness, harness.Gates, gate, amount);
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther), Body(context));
            owned += amount;
        }
    }

    private static async Task<DefaultHttpContext> PostAsync(Harness harness, LocalGalaxyGateStore store,
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
        await LocalGalaxyGateAction.HandleAsync(context, harness.Sessions, store,
            TestContext.CurrentContext.CancellationToken, Clock);
        return context;
    }

    private static async Task<long> EnergyAsync(Harness harness) =>
        (await harness.Economy.BalancesAsync(harness.Session.AccountId, TestContext.CurrentContext.CancellationToken))
            .Single(b => b.AssetId == "galaxy_energy").Amount;

    private async Task<(int Parts, bool Activated)> PartsAsync(Harness harness, string gate)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT parts_owned,activated FROM economy.galaxy_gates WHERE account_id=@a AND gate_id=@g", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        cmd.Parameters.AddWithValue("g", gate);
        await using var row = await cmd.ExecuteReaderAsync();
        return await row.ReadAsync() ? (row.GetInt32(0), row.GetBoolean(1)) : (0, false);
    }

    private async Task<long> GateRowCountAsync(Harness harness)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM economy.galaxy_gates WHERE account_id=@a", db);
        cmd.Parameters.AddWithValue("a", harness.Session.AccountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long[]> LedgerDeltasAsync(Harness harness, string reason)
    {
        await using var db = new NpgsqlConnection(_postgres.GetConnectionString());
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT delta FROM economy.ledger WHERE account_id=@a AND asset_id='galaxy_energy' AND reason=@r ORDER BY id", db);
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
