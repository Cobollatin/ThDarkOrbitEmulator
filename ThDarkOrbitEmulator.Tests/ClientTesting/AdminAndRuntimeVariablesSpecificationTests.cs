using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-08: Docker-free contract and live-consumer checks. PostgreSQL behavior has Integration tests.
public sealed class AdminAndRuntimeVariablesSpecificationTests
{
    private static readonly (string Key, string Category, string Type, string Default)[] Expected =
    [
        ("world.tick_rate_ms", "World", "int", "1000"),
        ("world.movement.speed_multiplier", "Movement", "double", "1"),
        ("world.radiation.damage_per_attempt", "World", "int", "0"),
        ("world.config.switch_cooldown_ms", "World", "int", "5000"),
        ("world.portal.jump_radius", "World", "int", "500"),
        ("world.station.peace_radius", "World", "int", "1000"),
        ("world.station.repair_hp_per_tick", "World", "int", "0"),
        ("world.spawn.streuner_count", "Spawns", "int", "3"),
        ("world.spawn.lordakia_count", "Spawns", "int", "2"),
        ("world.spawn.saimon_count", "Spawns", "int", "1"),
        ("world.spawn.bonus_box_count", "Spawns", "int", "6"),
        ("world.collectibles.respawn_seconds", "Collectibles", "int", "300"),
        ("world.collectibles.pickup_radius", "Collectibles", "int", "180"),
        ("world.combat.laser_damage_multiplier", "Combat", "double", "1"),
        ("world.combat.fire_range", "Combat", "int", "1000"),
        ("world.combat.shield_absorption_ratio", "Combat", "double", "1"),
        ("world.ship.pusat_plus_base_speed", "Movement", "int", "370"),
        ("world.ship.phoenix_base_speed", "Movement", "int", "320"),
        ("economy.credits_reward_multiplier", "Economy", "double", "1"),
        ("economy.uridium_reward_multiplier", "Economy", "double", "1"),
        ("economy.xp_reward_multiplier", "Economy", "double", "1"),
        ("economy.honor_reward_multiplier", "Economy", "double", "1"),
        ("economy.shop_discount_percent", "Economy", "double", "0"),
        ("mesh.desired_replicas", "ServerMesh", "int", "1"),
        ("mesh.max_maps_per_node", "ServerMesh", "int", "4"),
        ("mesh.heartbeat_ttl_seconds", "ServerMesh", "int", "30")
    ];

    private static RuntimeVariableStore CatalogStore() => new(null!, new ConfigurationBuilder().Build());

    [Test]
    public void Catalog_ContainsEveryDocumentedKeyTypeCategoryAndDefault()
    {
        var catalog = CatalogStore().Catalog;
        Assert.That(catalog.Select(v => (v.Key, v.Category, v.ValueType, v.DefaultValue)),
            Is.EquivalentTo(Expected));
        Assert.That(catalog.All(v => v.CurrentValue == v.DefaultValue), Is.True);
        Assert.That(catalog.Select(v => v.Key).Distinct().Count(), Is.EqualTo(catalog.Count));
    }

    [Test]
    public void MeshCatalogDefaults_AreReadFromClusterConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:DesiredWorkers"] = "3", ["Cluster:MaxMapsPerNode"] = "8",
            ["Cluster:LeaseTtlSeconds"] = "45"
        }).Build();
        var catalog = new RuntimeVariableStore(null!, config).Catalog.ToDictionary(v => v.Key);
        Assert.That(catalog["mesh.desired_replicas"].DefaultValue, Is.EqualTo("3"));
        Assert.That(catalog["mesh.max_maps_per_node"].DefaultValue, Is.EqualTo("8"));
        Assert.That(catalog["mesh.heartbeat_ttl_seconds"].DefaultValue, Is.EqualTo("45"));
    }

    [Test]
    public void Dispatcher_AdvertisesEveryImplementedVerb_AndNoUnimplementedAlias()
    {
        Assert.That(AdminCommandDispatcher.Commands.Select(c => string.Join(' ', c.Split(' ').Take(2))),
            Is.EquivalentTo(new[] { "help", "var list", "var get", "var set", "var reset",
                "var history", "var rollback", "mesh status", "mesh scale", "mesh drain",
                "mesh activate", "mesh rebalance", "world status", "world tp", "world heal",
                "world spawn", "world grant", "world announce" }));
        Assert.That(AdminCommandDispatcher.Commands, Has.None.StartsWith("economy grant"));
        Assert.That(AdminCommandDispatcher.Commands, Has.None.StartsWith("mesh assign-map"));
    }

    [Test]
    public void Parser_PreservesQuotedReasonsAndRejectsInvalidInput()
    {
        Assert.That(AdminCommandDispatcher.Parse("VAR set world.tick_rate_ms 750 'operator tuning'"),
            Is.EqualTo(new[] { "VAR", "set", "world.tick_rate_ms", "750", "operator tuning" }));
        Assert.That(AdminCommandDispatcher.Parse("world announce \"two words\""),
            Is.EqualTo(new[] { "world", "announce", "two words" }));
        Assert.Throws<ArgumentException>(() => AdminCommandDispatcher.Parse("var set a.b 1 'unfinished"));
        Assert.Throws<ArgumentException>(() => AdminCommandDispatcher.Parse(new string('x', 2049)));
    }

    [TestCase("", new string[0])]
    [TestCase(" \t\r\n ", new string[0])]
    [TestCase("  var\tset\r\na.b  1  ", new[] { "var", "set", "a.b", "1" })]
    [TestCase("world announce 'hello world'", new[] { "world", "announce", "hello world" })]
    [TestCase("world announce \"hello world\"", new[] { "world", "announce", "hello world" })]
    [TestCase("world announce pre'hello world'post", new[] { "world", "announce", "prehello worldpost" })]
    [TestCase("world announce '' \"\"", new[] { "world", "announce" })]
    [TestCase("world announce a\\b", new[] { "world", "announce", "a\\b" })]
    [TestCase("world announce 'it\\'s here'", new[] { "world", "announce", "it's here" })]
    [TestCase("world announce \"say \\q now\"", new[] { "world", "announce", "say q now" })]
    public void Parser_TokenBoundariesAndQuotedEscapes_AreStable(string text, string[] expected)
    {
        Assert.That(AdminCommandDispatcher.Parse(text), Is.EqualTo(expected));
    }

    [TestCase("'unfinished")]
    [TestCase("\"unfinished")]
    [TestCase("'ends with escape\\")]
    public void Parser_RejectsOpenQuotes(string text)
    {
        Assert.Throws<ArgumentException>(() => AdminCommandDispatcher.Parse(text));
    }

    [Test]
    public void Parser_EnforcesCharacterLimitBeforeTokenizing()
    {
        Assert.That(AdminCommandDispatcher.Parse(new string(' ', 2048)), Is.Empty);
        Assert.That(AdminCommandDispatcher.Parse(new string('x', 2048)),
            Is.EqualTo(new[] { new string('x', 2048) }));
        Assert.Throws<ArgumentException>(() => AdminCommandDispatcher.Parse(new string(' ', 2049)));
    }

    [Test]
    public void InGameShortcuts_LeavePlainTextAloneAndMapOnlyLeadingSlash()
    {
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship", 0, 0);
        Assert.That(InGameAdmin.Expand("/", pilot), Is.EqualTo("help"));
        Assert.That(InGameAdmin.Expand("/   ", pilot), Is.EqualTo("help"));
        Assert.That(InGameAdmin.Expand("world heal 99", pilot), Is.EqualTo("world heal 99"));
        Assert.That(InGameAdmin.Expand(" /heal", pilot), Is.EqualTo(" /heal"));
        Assert.That(InGameAdmin.Expand("/HeAl extra", pilot), Is.EqualTo("world heal 42"));
        Assert.That(InGameAdmin.Expand("/VaR list", pilot), Is.EqualTo("VaR list"));
    }

    [Test]
    public void InGameShortcuts_TargetTheAuthenticatedPilotAndPreserveSurface()
    {
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship", 0, 0) { UserName = "pilot" };
        Assert.That(InGameAdmin.Expand("/tp 2 3000 1500", pilot), Is.EqualTo("world tp 42 2 3000 1500"));
        Assert.That(InGameAdmin.Expand("/heal", pilot), Is.EqualTo("world heal 42"));
        Assert.That(InGameAdmin.Expand("/grant credits 5", pilot), Is.EqualTo("world grant 42 credits 5"));
        Assert.That(InGameAdmin.Expand("/spawn box 1 3000 1500", pilot),
            Is.EqualTo("world spawn box 1 3000 1500"));
        Assert.That(InGameAdmin.Expand("/var list", pilot), Is.EqualTo("var list"));
        var actor = InGameAdmin.Actor(pilot, "node-a");
        Assert.That((actor.Surface, actor.Actor, actor.NodeId), Is.EqualTo(("InGameAdmin", "pilot:42", "node-a")));
    }

    [TestCase("/spawn box 1 3000 1500", "world spawn box 1 3000 1500")]
    [TestCase("/SPAWN Box 1 3000 1500", "world SPAWN Box 1 3000 1500")]
    [TestCase("/spawn  box   1 2 3", "world spawn  box   1 2 3")]
    [TestCase("/tp 'two' 3000 1500", "world tp 42 two 3000 1500")]
    [TestCase("/tp", "world tp 42 ")]
    [TestCase("/grant", "world grant 42 ")]
    [TestCase("/GRANT XP  9", "world grant 42 XP 9")]
    [TestCase("/heal 'other pilot'", "world heal 42")]
    [TestCase("//heal", "/heal")]
    [TestCase("/world   heal 7", "world   heal 7")]
    public void InGameShortcuts_RewriteOrPreserveTheExactRawRemainder(string text, string expected)
    {
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship", 0, 0);
        Assert.That(InGameAdmin.Expand(text, pilot), Is.EqualTo(expected));
    }

    [Test]
    public void InGameShortcuts_RejectUnterminatedQuotesAfterTheSlash()
    {
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship", 0, 0);
        Assert.Throws<ArgumentException>(() => InGameAdmin.Expand("/tp 'open", pilot));
        Assert.That(InGameAdmin.Expand("tp 'open", pilot), Is.EqualTo("tp 'open"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void InGameActor_FallsBackToMachineAndProcessNodeId(string? nodeId)
    {
        var pilot = new WorldPilot(42, "mmo", 1, 1, "ship", 0, 0) { UserName = "pilot" };
        Assert.That(InGameAdmin.Actor(pilot, nodeId).NodeId,
            Is.EqualTo($"{Environment.MachineName}-{Environment.ProcessId}"));
    }

    private static (WorldAdminEvents Events, ServiceProvider Services) AdminEvents()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        return (new WorldAdminEvents(services.GetRequiredService<IDistributedCache>(), new Mutex()), services);
    }

    [TestCase("box", "bonus", "credits", 200)]
    [TestCase("ore", "ore", "prometium", 10)]
    public async Task ManualSpawn_CollectibleKindsHaveFixedShapes(string kind, string boxKind, string asset, int amount)
    {
        var (events, services) = AdminEvents();
        using (services)
        {
            var box = (WorldCollectible)await events.SpawnAsync(kind, 3, 1234, 567, default);
            Assert.That((box.Kind, box.X, box.Y, box.AssetId, box.Amount),
                Is.EqualTo((boxKind, 1234, 567, asset, amount)));
            Assert.That(box.Id, Is.Negative);
            Assert.That(await events.BoxesAsync(3, default), Is.EqualTo(new[] { box }));
            Assert.That(await events.NpcsAsync(3, default), Is.Empty);
        }
    }

    [TestCase("streuner", "Streuner", 800, 200)]
    [TestCase("lordakia", "Lordakia", 2000, 800)]
    [TestCase("saimon", "Saimon", 4000, 1600)]
    public async Task ManualSpawn_NpcKindsHaveFixedStatsAndRewards(string kind, string name, int hp, int shield)
    {
        var (events, services) = AdminEvents();
        using (services)
        {
            var npc = (WorldNpc)await events.SpawnAsync(kind, 2, 4000, 2000, default);
            Assert.That(npc, Is.EqualTo(new WorldNpc(npc.Id, name, $"npc_{kind}", 4000, 2000,
                hp, hp, shield, shield, 400, 1, 400, 2)));
            Assert.That(npc.Id, Is.Negative);
            Assert.That(await events.NpcsAsync(2, default), Is.EqualTo(new[] { npc }));
            Assert.That(await events.BoxesAsync(2, default), Is.Empty);
        }
    }

    [Test]
    public async Task ManualSpawns_AreIsolatedPerMapAndKeepInsertionOrder()
    {
        var (events, services) = AdminEvents();
        using (services)
        {
            var first = (WorldCollectible)await events.SpawnAsync("box", 1, 10, 10, default);
            var second = (WorldCollectible)await events.SpawnAsync("ore", 1, 20, 20, default);
            var npc = (WorldNpc)await events.SpawnAsync("streuner", 1, 30, 30, default);
            Assert.That(await events.BoxesAsync(1, default), Is.EqualTo(new[] { first, second }));
            Assert.That(await events.NpcsAsync(1, default), Is.EqualTo(new[] { npc }));
            Assert.That(await events.BoxesAsync(2, default), Is.Empty);
            Assert.That(await events.NpcsAsync(2, default), Is.Empty);
        }
    }

    [Test]
    public async Task ManualSpawns_CapBoxesAndNpcsTogetherAt200PerMap()
    {
        var (events, services) = AdminEvents();
        using (services)
        {
            for (var i = 0; i < 200; i++)
                await events.SpawnAsync(i % 2 == 0 ? "box" : "lordakia", 4, i, i, default);
            Assert.That((await events.BoxesAsync(4, default)).Count + (await events.NpcsAsync(4, default)).Count,
                Is.EqualTo(200));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await events.SpawnAsync("ore", 4, 1, 1, default));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await events.SpawnAsync("saimon", 4, 1, 1, default));
            Assert.That((await events.BoxesAsync(4, default)).Count, Is.EqualTo(100));
            Assert.That(await events.SpawnAsync("ore", 5, 1, 1, default), Is.InstanceOf<WorldCollectible>());
        }
    }

    [Test]
    public async Task Announcements_StartEmptyAndTheLatestReplacesThePrevious()
    {
        var (events, services) = AdminEvents();
        using (services)
        {
            Assert.That(await events.LatestAsync(default), Is.Null);
            var before = DateTimeOffset.UtcNow;
            await events.AnnounceAsync("first", new AdminCommandActor("AdminCms", "op-a", "node-a"), default);
            var second = await events.AnnounceAsync("second notice",
                new AdminCommandActor("InGameAdmin", "pilot:42", "node-b"), default);
            Assert.That((second.Message, second.Actor, second.Surface),
                Is.EqualTo(("second notice", "pilot:42", "InGameAdmin")));
            Assert.That(second.AtUtc, Is.GreaterThanOrEqualTo(before).And.LessThanOrEqualTo(DateTimeOffset.UtcNow));
            Assert.That(await events.LatestAsync(default), Is.EqualTo(second));
        }
    }

    private sealed class MemoryVariables : IRuntimeVariableRegistry
    {
        private readonly Dictionary<string, RuntimeVariable> values = CatalogStore().Catalog.ToDictionary(v => v.Key);
        private readonly List<RuntimeVariableChangeEntry> changes = [];
        public IReadOnlyList<RuntimeVariable> Catalog => values.Values.ToArray();
        public Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeVariable>>(values.Values.ToArray());
        public Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(values[key]);
        public Task<double> NumberAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(double.Parse(values[key].CurrentValue, CultureInfo.InvariantCulture));
        public Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
            CancellationToken ct = default)
        {
            if (actor.Surface is not ("ServerManagement" or "AdminCms" or "InGameAdmin"))
                throw new ArgumentException("Invalid surface.");
            var prior = values[key];
            var entry = new RuntimeVariableChangeEntry(changes.Count + 1, key, prior.Category,
                prior.CurrentValue, value, prior.DefaultValue, prior.ValueType, actor.Surface,
                actor.ChangedBy, actor.NodeId, actor.Reason, actor.RawCommand, DateTimeOffset.UtcNow);
            values[key] = prior with { CurrentValue = value };
            changes.Add(entry);
            return Task.FromResult(entry);
        }
        public Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor,
            CancellationToken ct = default) => SetAsync(key, values[key].DefaultValue, actor, ct);
        public Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
            CancellationToken ct = default)
        {
            var prior = changes.Single(c => c.ChangeId == changeId);
            if (values[prior.VariableKey].CurrentValue != prior.NewValue)
                throw new InvalidOperationException("Rollback conflict");
            return SetAsync(prior.VariableKey, prior.PreviousValue, actor, ct);
        }
        public Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100,
            long? beforeChangeId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeVariableChangeEntry>>(changes
                .Where(c => (key is null || c.VariableKey == key) &&
                    (beforeChangeId is null || c.ChangeId < beforeChangeId))
                .OrderByDescending(c => c.ChangeId).Take(Math.Clamp(limit, 1, 1000)).ToArray());
    }

    [Test]
    public async Task SurfaceChanges_AreAttributedOrderedPagedAndRolledBack()
    {
        var vars = new MemoryVariables();
        RuntimeVariableActor Actor(string surface) => new(surface, "operator", "node-a", "review");
        var first = await vars.SetAsync("world.movement.speed_multiplier", "2", Actor("ServerManagement"));
        var second = await vars.SetAsync("world.combat.laser_damage_multiplier", "3", Actor("AdminCms"));
        var third = await vars.SetAsync("world.collectibles.pickup_radius", "400", Actor("InGameAdmin"));
        Assert.That((await vars.HistoryAsync()).Select(c => c.ChangeId), Is.EqualTo(new[]
            { third.ChangeId, second.ChangeId, first.ChangeId }));
        Assert.That((await vars.HistoryAsync(beforeChangeId: third.ChangeId)).Select(c => c.ChangeId),
            Is.EqualTo(new[] { second.ChangeId, first.ChangeId }));
        Assert.That(new[] { first.Surface, second.Surface, third.Surface },
            Is.EqualTo(new[] { "ServerManagement", "AdminCms", "InGameAdmin" }));
        var rollback = await vars.RollbackAsync(first.ChangeId, Actor("InGameAdmin"));
        Assert.That((rollback.PreviousValue, rollback.NewValue, rollback.Surface),
            Is.EqualTo(("2", "1", "InGameAdmin")));
        Assert.That((await vars.GetAsync(first.VariableKey)).CurrentValue, Is.EqualTo("1"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await vars.RollbackAsync(first.ChangeId, Actor("AdminCms")));
        var reset = await vars.ResetToDefaultAsync(third.VariableKey, Actor("AdminCms"));
        Assert.That(reset.NewValue, Is.EqualTo("180"));
    }

    private sealed class Mutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Release(gate); }
        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }
    private sealed class Wallet : IWorldRewardWallet
    {
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct) =>
            Task.FromResult(true);
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(0L);
    }
    private sealed class Rewards : IWorldNpcRewards
    {
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task LiveConsumers_UseChangesFromAllThreeSurfaces()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new Mutex();
        var vars = new MemoryVariables();
        var sessions = new WorldSessionStore(cache, mutex, vars);
        var pilot = new WorldPilot(808, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Guid.NewGuid(), X = 2000, Y = 1300,
            Stats = new WorldShipStats(370, 1000, 0, 100, 100, []) };
        pilot = await sessions.EnterAsync(pilot, default);
        Assert.That((await sessions.LiveStatsAsync(pilot, default)).Speed, Is.EqualTo(370));
        await vars.SetAsync("world.movement.speed_multiplier", "2",
            new("ServerManagement", "operator", "node-a", "speed"));
        await vars.SetAsync("world.combat.laser_damage_multiplier", "3",
            new("AdminCms", "operator", "node-a", "combat"));
        var live = await sessions.LiveStatsAsync(pilot, default);
        Assert.That((live.Speed, live.LaserDamage), Is.EqualTo((740, 300)));
        var movement = await sessions.MoveAsync(pilot, 2740, 1300, null, default);
        Assert.That(movement.TimeToTargetMs, Is.EqualTo(1000));

        var boxes = new WorldCollectibles(cache, mutex, new Wallet(), TimeProvider.System, vars);
        var box = (await boxes.VisibleAsync(1, default))[0];
        var near = pilot with { UserId = 809, X = box.X + 250, Y = box.Y };
        Assert.That((await boxes.CollectAsync(near, box.Id, sessions, default)).Status, Is.EqualTo("distant"));
        var pickup = await vars.SetAsync("world.collectibles.pickup_radius", "400",
            new("InGameAdmin", "pilot", "node-b", "pickup"));
        Assert.That((await boxes.CollectAsync(near, box.Id, sessions, default)).Status, Is.EqualTo("collected"));
        await vars.RollbackAsync(pickup.ChangeId, new("ServerManagement", "operator", "node-a", "restore"));
        Assert.That(await vars.NumberAsync("world.collectibles.pickup_radius"), Is.EqualTo(180));

        var npcs = new WorldNpcs(cache, mutex, new Rewards(), Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 1000, 0, 1, 0, 0, 0, 0)] }),
            TimeProvider.System, vars);
        var npc = (await npcs.VisibleAsync(1, default)).First();
        var shooter = pilot with { UserId = 810, X = npc.X, Y = npc.Y };
        var hit = await npcs.AttackAsync(shooter, npc.Id, sessions, default);
        Assert.That(hit.Damage, Is.EqualTo(300));
    }
}
