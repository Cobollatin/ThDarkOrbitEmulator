using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Application;
using DotNet.Testcontainers.Builders;
using StackExchange.Redis;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class WorldHttpBridgeTests
{
    [TestCase("application/json; charset=utf-8")]
    [TestCase("APPLICATION/JSON")]
    public async Task Move_AcceptsExactJsonMediaTypeWithCaseAndParameters(string contentType)
    {
        var response = await SendAsync("POST", "move", "{\"x\":5000,\"y\":1300}",
            new Auth(), new Resolver(), Store(), contentType: contentType);
        Assert.That(response.Response.StatusCode, Is.EqualTo(200));
        using var body = JsonDocument.Parse(Body(response));
        Assert.That(body.RootElement.GetProperty("pilot").GetProperty("targetX").GetInt32(), Is.EqualTo(5000));
    }

    private sealed class SpeedVariables : IRuntimeVariableRegistry
    {
        public double Multiplier = 1;
        public double PeaceRadius = 1000;
        public double CreditsReward = 1;
        public double CollectionRadius = 180;
        public double StreunerCount = 1;
        public double ConfigurationCooldown = 1;
        public double PortalJumpRadius = 1;
        public IReadOnlyList<RuntimeVariable> Catalog => [];
        public Task<double> NumberAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(key == "world.movement.speed_multiplier" ? Multiplier :
                key == "world.ship.pusat_plus_base_speed" ? 300d :
                key == "world.station.peace_radius" ? PeaceRadius :
                key == "world.collectibles.pickup_radius" ? CollectionRadius :
                key == "world.collectibles.respawn_seconds" ? 300d :
                key == "world.spawn.bonus_box_count" ? 6d :
                key == "world.combat.fire_range" ? 1000d :
                key == "world.spawn.streuner_count" ? StreunerCount :
                key == "world.config.switch_cooldown_ms" ? ConfigurationCooldown :
                key == "world.portal.jump_radius" ? PortalJumpRadius :
                key == "world.spawn.lordakia_count" || key == "world.spawn.saimon_count" ? 0d :
                key == "economy.credits_reward_multiplier" ? CreditsReward : 1d);
        public Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100,
            long? beforeChangeId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Test]
    public async Task BrowserState_ReflectsLivePeaceRadiusForSpacemap()
    {
        var variables = new SpeedVariables();
        using var services = new ServiceCollection()
            .AddSingleton<IRuntimeVariableRegistry>(variables).BuildServiceProvider();
        var store = Store();
        var auth = new Auth();
        var resolver = new Resolver();
        foreach (var radius in new[] { 1000d, 500d, 1000d })
        {
            variables.PeaceRadius = radius;
            var response = await SendAsync("GET", "state", null, auth, resolver, store,
                services: services);
            Assert.That(response.Response.StatusCode, Is.EqualTo(200));
            using var json = JsonDocument.Parse(Body(response));
            Assert.That(json.RootElement.GetProperty("map").GetProperty("station")
                .GetProperty("peaceRadius").GetInt32(), Is.EqualTo((int)radius));
        }
    }

    [Test]
    public async Task BrowserState_RetimesSavedTravelAfterSpeedChange()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var variables = new SpeedVariables();
        var store = new WorldSessionStore(cache, variables: variables);
        var auth = new Auth();
        var resolver = new Resolver();
        var first = await SendAsync("GET", "enter", null, auth, resolver, store);
        Assert.That(first.Response.StatusCode, Is.EqualTo(200));
        var moved = await SendAsync("POST", "move", "{\"x\":5000,\"y\":1300}", auth, resolver, store);
        Assert.That(moved.Response.StatusCode, Is.EqualTo(200));
        var before = JsonSerializer.Deserialize<WorldTravel>((await cache.GetStringAsync("world:position:73"))!);
        Assert.That(before, Is.Not.Null);

        variables.Multiplier = 2;
        var state = await SendAsync("GET", "state", null, auth, resolver, store);
        Assert.That(state.Response.StatusCode, Is.EqualTo(200));
        var after = JsonSerializer.Deserialize<WorldTravel>((await cache.GetStringAsync("world:position:73"))!);
        Assert.That(after!.Speed, Is.EqualTo(before!.Speed * 2));
        Assert.That(after.StartedAtUnixMilliseconds, Is.GreaterThanOrEqualTo(before.StartedAtUnixMilliseconds));
        using var json = JsonDocument.Parse(Body(state));
        Assert.That(json.RootElement.GetProperty("pilot").GetProperty("speed").GetInt32(), Is.EqualTo(after.Speed));
    }

    [Test]
    public async Task BrowserMove_FromPreviousMap_IsRejectedWithoutChangingSavedTravel()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache, new NpcMutex());
        await cache.SetStringAsync("world:position:73", JsonSerializer.Serialize(new WorldPosition(2, 2500, 1300)));
        var response = await SendAsync("POST", "move", "{\"x\":8000,\"y\":5000,\"mapId\":1}",
            new Auth(), new Resolver(), store);
        Assert.That(response.Response.StatusCode, Is.EqualTo(409));
        using var json = JsonDocument.Parse(Body(response));
        Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("map_changed"));
        var saved = await cache.GetStringAsync("world:position:73");
        Assert.That(saved, Does.Contain("\"MapId\":2"));
    }

    [Test]
    public async Task BrowserJump_FromPreviousMap_IsRejectedWithoutChangingSavedMap()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache, new NpcMutex());
        await cache.SetStringAsync("world:position:73", JsonSerializer.Serialize(new WorldPosition(2, 2500, 1300)));
        var response = await SendAsync("POST", "jump", "{\"portalId\":101,\"mapId\":1}",
            new Auth(), new Resolver(), store);
        Assert.That(response.Response.StatusCode, Is.EqualTo(409));
        using var json = JsonDocument.Parse(Body(response));
        Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("map_changed"));
        Assert.That(await cache.GetStringAsync("world:position:73"), Does.Contain("\"MapId\":2"));
    }

    [Test]
    public async Task BrowserCollect_FromPreviousMap_IsRejectedBeforeReward()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var store = new WorldSessionStore(cache, mutex);
        var wallet = new Wallet();
        var collectibles = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        using var appServices = new ServiceCollection().AddSingleton(collectibles).BuildServiceProvider();
        await cache.SetStringAsync("world:position:73", JsonSerializer.Serialize(new WorldPosition(2, 2500, 1300)));
        var response = await SendAsync("POST", "collect", "{\"boxId\":1001,\"mapId\":1}",
            new Auth(), new Resolver(), store, services: appServices);
        Assert.That(response.Response.StatusCode, Is.EqualTo(409));
        using var json = JsonDocument.Parse(Body(response));
        Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("map_changed"));
        Assert.That(wallet.Calls, Is.Zero);
        Assert.That(await cache.GetStringAsync("world:position:73"), Does.Contain("\"MapId\":2"));
    }

    [Test]
    public async Task BrowserCollect_RefreshUnderMutexRejectsMapChangedByOtherReplica()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var wallet = new Wallet();
        var collectibles = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var stale = await first.EnterAsync(new WorldPilot(73, "mmo", 1, 1, "ship", 0, 0)
            { X = 19500, Y = 1300, AccountId = Guid.NewGuid() }, default);
        await second.PersistAsync(stale with { MapId = 2, X = 2500, Y = 1300 }, default);
        Assert.ThrowsAsync<WorldMapChangedException>(async () =>
            await collectibles.CollectAsync(stale, 1001, first, default, expectedMapId: 1));
        Assert.That(wallet.Calls, Is.Zero);
    }

    [Test]
    public async Task BrowserJump_RefreshUnderMutexRejectsMapChangedByOtherReplica()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var stale = await first.EnterAsync(new WorldPilot(73, "mmo", 1, 1, "ship", 0, 0)
            { X = 19500, Y = 1300 }, default);
        await second.PersistAsync(stale with { MapId = 2, X = 2500, Y = 1300 }, default);

        Assert.ThrowsAsync<WorldMapChangedException>(async () =>
            await first.JumpAsync(stale, 101, null, default, expectedMapId: 1));
        Assert.That(await cache.GetStringAsync("world:position:73"), Does.Contain("\"MapId\":2"));
    }

    [Test]
    public async Task BrowserMove_StaleRevisionOnAnotherReplica_IsRejectedWithoutChangingDestination()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var first = new WorldSessionStore(cache, mutex);
        var second = new WorldSessionStore(cache, mutex);
        var auth = new Auth();
        var resolver = new Resolver();
        var entry = await SendAsync("GET", "enter", null, auth, resolver, first);
        using var initial = JsonDocument.Parse(Body(entry));
        var revision = initial.RootElement.GetProperty("pilot").GetProperty("moveId").GetGuid();
        var command = JsonSerializer.Serialize(new { x = 8000, y = 5000, mapId = 1, expectedMoveId = revision });
        var accepted = await SendAsync("POST", "move", command, auth, resolver, first);
        Assert.That(accepted.Response.StatusCode, Is.EqualTo(200));
        var rejected = await SendAsync("POST", "move",
            JsonSerializer.Serialize(new { x = 9000, y = 6000, mapId = 1, expectedMoveId = revision }),
            auth, resolver, second);
        Assert.That(rejected.Response.StatusCode, Is.EqualTo(409));
        using var conflict = JsonDocument.Parse(Body(rejected));
        Assert.That(conflict.RootElement.GetProperty("status").GetString(), Is.EqualTo("move_changed"));
        var staleSwitch = await SendAsync("POST", "move",
            JsonSerializer.Serialize(new { x = 9000, y = 6000, mapId = 1,
                configuration = 2, expectedMoveId = revision }), auth, resolver, second);
        Assert.That(staleSwitch.Response.StatusCode, Is.EqualTo(409));
        Assert.That(second.Configuration(73), Is.EqualTo(1));
        var saved = JsonSerializer.Deserialize<WorldTravel>((await cache.GetStringAsync("world:position:73"))!);
        Assert.That((saved!.TargetX, saved.TargetY), Is.EqualTo((8000, 5000)));
    }

    private sealed class RecordingAdmin : IAdminCommandDispatcher
    {
        public AdminCommand? Command;
        public AdminCommandActor? Actor;
        public Task<AdminCommandResult> ExecuteAsync(AdminCommand command, AdminCommandActor actor,
            CancellationToken ct = default)
        { Command = command; Actor = actor; return Task.FromResult(new AdminCommandResult("var set", new { ok = true })); }
    }

    [TestCase(false, 403)] [TestCase(true, 200)]
    public async Task BrowserAdminCommand_RequiresAdminAndAttributesSurface(bool isAdmin, int status)
    {
        var dispatcher = new RecordingAdmin();
        var context = await SendAsync("POST", "admin/command",
            "{\"text\":\"/heal\"}", new Auth(isAdmin), new Resolver(isAdmin: isAdmin), Store(),
            services: new ServiceCollection().BuildServiceProvider(), dispatcher: dispatcher);
        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
        if (isAdmin)
        {
            Assert.That(dispatcher.Command?.Text, Is.EqualTo("world heal 73"));
            Assert.That(dispatcher.Actor?.Surface, Is.EqualTo("InGameAdmin"));
        }
        else Assert.That(dispatcher.Command, Is.Null);
    }

    [TestCase("{", 400)]
    [TestCase("{}", 400)]
    [TestCase("{\"text\":null}", 400)]
    [TestCase("{\"text\":\"   \"}", 400)]
    [TestCase("{\"text\":42}", 400)]
    [TestCase("{\"text\":\"/tp 'open\"}", 400)]
    [TestCase("{\"text\":\"/grant credits 5\"}", 200)]
    public async Task BrowserAdminCommand_ValidatesInputBeforeDispatch(string json, int expectedStatus)
    {
        var dispatcher = new RecordingAdmin();
        var context = await SendAsync("POST", "admin/command", json, new Auth(true),
            new Resolver(isAdmin: true), Store(),
            services: new ServiceCollection().BuildServiceProvider(), dispatcher: dispatcher);

        Assert.That(context.Response.StatusCode, Is.EqualTo(expectedStatus));
        if (expectedStatus == 200)
        {
            Assert.That(dispatcher.Command?.Text, Is.EqualTo("world grant 73 credits 5"));
            Assert.That(dispatcher.Actor?.Actor, Does.EndWith(":73"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        }
        else
        {
            Assert.That(dispatcher.Command, Is.Null);
            using var error = JsonDocument.Parse(Body(context));
            Assert.That(error.RootElement.TryGetProperty("error", out _), Is.True);
        }
    }

    [Test]
    public async Task BrowserAdminCommand_RejectsUnauthorizedBeforeParsingBody()
    {
        var dispatcher = new RecordingAdmin();
        var context = await SendAsync("POST", "admin/command", "{", new Auth(false),
            new Resolver(isAdmin: false), Store(),
            services: new ServiceCollection().BuildServiceProvider(), dispatcher: dispatcher);

        Assert.That(context.Response.StatusCode, Is.EqualTo(403));
        Assert.That(dispatcher.Command, Is.Null);
    }

    private sealed class Wallet : IWorldRewardWallet
    {
        public int Calls;
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct)
        { Calls++; return Task.FromResult(true); }
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(0L);
    }

    [Test]
    public async Task BrowserState_SeesAdminEventsFromAnotherReplica()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var eventsA = new WorldAdminEvents(cache, mutex);
        var eventsB = new WorldAdminEvents(cache, mutex);
        await eventsA.SpawnAsync("box", 1, 3000, 2500, default);
        await eventsA.SpawnAsync("saimon", 1, 3600, 2700, default);
        await eventsA.AnnounceAsync("Shared notice",
            new AdminCommandActor("ServerManagement", "operator", "node-a"), default);
        var boxes = new WorldCollectibles(cache, mutex, new Wallet(), TimeProvider.System,
            adminEvents: eventsB);
        var npcs = new WorldNpcs(cache, mutex, new NpcRewards(), Options.Create(new WorldNpcOptions
            { Templates = [] }), TimeProvider.System, adminEvents: eventsB);
        using var appServices = new ServiceCollection().AddSingleton(eventsB).AddSingleton(boxes)
            .AddSingleton(npcs).BuildServiceProvider();
        var response = await SendAsync("GET", "state", null, new Auth(), new Resolver(),
            new WorldSessionStore(cache), services: appServices);
        using var json = JsonDocument.Parse(Body(response));
        Assert.That(json.RootElement.GetProperty("announcement").GetProperty("message").GetString(),
            Is.EqualTo("Shared notice"));
        Assert.That(json.RootElement.GetProperty("boxes").EnumerateArray()
            .Any(item => item.GetProperty("x").GetInt32() == 3000), Is.True);
        Assert.That(json.RootElement.GetProperty("npcs").EnumerateArray()
            .Any(item => item.GetProperty("kind").GetString() == "Saimon"), Is.True);
        Assert.That(LocalSpacemapPage.Html, Does.Contain("id=\"announcement\""));
    }
    private sealed class NpcMutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await gate.WaitAsync(ct); return new Handle(gate); }
        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
    }
    private sealed class NpcRewards : IWorldNpcRewards
    {
        public int Calls;
        public WorldNpc? LastCredited;
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct)
        { LastCredited = npc; Interlocked.Increment(ref Calls); return Task.CompletedTask; }
    }

    [Test]
    public async Task BrowserState_AfterRemoteAdminTeleport_DoesNotShowGhostOnOldMap()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var oldNode = new WorldSessionStore(cache, mutex);
        var adminNode = new WorldSessionStore(cache, mutex);
        await oldNode.EnterAsync(new WorldPilot(73, "mmo", 1, 1, "ship", 0, 0)
            { AccountId = Session.AccountId, UserName = "Test pilot", X = 2000, Y = 1300 }, default);

        Assert.That(await adminNode.TeleportAsync(73, 5, 3400, 2100, false, default), Is.Not.Null);
        await oldNode.RefreshLocalPresenceAsync(default);

        Assert.That(await oldNode.ConnectedAsync(1, default), Is.Empty);
        var response = await SendAsync("GET", "state", null, new Auth(), new Resolver(), oldNode);
        Assert.That(response.Response.StatusCode, Is.EqualTo(200));
        using var body = JsonDocument.Parse(Body(response));
        Assert.That(body.RootElement.GetProperty("map").GetProperty("id").GetInt32(), Is.EqualTo(5));
        Assert.That(body.RootElement.GetProperty("pilot").GetProperty("x").GetInt32(), Is.EqualTo(3400));
        Assert.That(body.RootElement.GetProperty("pilots").EnumerateArray()
            .Count(item => item.GetProperty("id").GetInt64() == 73), Is.EqualTo(1));
    }

    [Test]
    public async Task BrowserState_AfterSavedMapChange_RemovesOldMapPresenceImmediately()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new WorldSessionStore(shared.GetRequiredService<IDistributedCache>(), new NpcMutex());
        var pilot = await store.EnterAsync(new WorldPilot(73, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Session.AccountId, UserName = "Test pilot", X = 2000, Y = 1300 }, default);
        await store.PersistAsync(pilot with { MapId = 5, X = 3400, Y = 2100 }, default);

        var response = await SendAsync("GET", "state", null, new Auth(), new Resolver(), store);
        using var body = JsonDocument.Parse(Body(response));
        var oldMap = await store.ConnectedAsync(1, default);
        Assert.Multiple(() =>
        {
            Assert.That(body.RootElement.GetProperty("map").GetProperty("id").GetInt32(), Is.EqualTo(5));
            Assert.That(oldMap, Is.Empty);
        });
    }

    [Test]
    public async Task BrowserStateTargetAndFire_ShareNpcStateAcrossApiReplicas()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var rewards = new NpcRewards();
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 0)] }), TimeProvider.System);
        using var appServices = new ServiceCollection().AddSingleton(npcs).BuildServiceProvider();
        var a = new WorldSessionStore(cache, mutex);
        var b = new WorldSessionStore(cache, mutex);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        await a.EnterAsync(new WorldPilot(73, "mmo", 1, 1, "ship", 0, 0)
        { AccountId = Session.AccountId, UserName = "Test pilot", X = npc.X, Y = npc.Y,
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, []) }, default);
        var auth = new Auth(); var resolver = new Resolver(100);
        var state = await SendAsync("GET", "state", null, auth, resolver, a, services: appServices);
        using (var json = JsonDocument.Parse(Body(state)))
            Assert.That(json.RootElement.GetProperty("npcs").GetArrayLength(), Is.EqualTo(1));
        var target = await SendAsync("POST", "target", $"{{\"npcId\":{npc.Id}}}", auth, resolver, b, services: appServices);
        using (var json = JsonDocument.Parse(Body(target)))
            Assert.That(json.RootElement.GetProperty("maxHp").GetInt32(), Is.EqualTo(50));
        var fire = await SendAsync("POST", "fire", $"{{\"npcId\":{npc.Id}}}", auth, resolver, b, services: appServices);
        using (var json = JsonDocument.Parse(Body(fire)))
            Assert.That(json.RootElement.GetProperty("destroyed").GetBoolean(), Is.True);
        Assert.That(rewards.Calls, Is.EqualTo(1));
        var after = await SendAsync("GET", "state", null, auth, resolver, a, services: appServices);
        using var result = JsonDocument.Parse(Body(after));
        Assert.That(result.RootElement.GetProperty("npcs").GetArrayLength(), Is.Zero);
    }

    [Test]
    [Explicit("Runs the shipped Spacemap page against live Kestrel pickup endpoints.")]
    public async Task SpacemapClient_UsesLivePickupRadius_BeforeReachingDefaultRange()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        var variables = new SpeedVariables { CollectionRadius = 400 };
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        var wallet = new Wallet();
        builder.Services.AddSingleton(sp => new WorldCollectibles(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), wallet, TimeProvider.System, variables));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        var box = (await app.Services.GetRequiredService<WorldCollectibles>().VisibleAsync(1, ct)).First();
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, box.X - 300, box.Y)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-pickup-radius-live.mjs"), app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("pickupDistance").GetDouble(), Is.GreaterThan(180));
        }
        finally { await app.StopAsync(ct); }
    }

    [Test]
    [Explicit("Runs the shipped Spacemap page against live Kestrel while pickup radius shrinks mid-request.")]
    public async Task SpacemapClient_RetriesPickupAfterLiveRadiusShrinks()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        var variables = new SpeedVariables { CollectionRadius = 400 };
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        var wallet = new Wallet();
        builder.Services.AddSingleton(sp => new WorldCollectibles(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), wallet, TimeProvider.System, variables));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        app.MapPost("/test/pickup-radius", () => { variables.CollectionRadius = 180; return Results.Ok(); });
        var box = (await app.Services.GetRequiredService<WorldCollectibles>().VisibleAsync(1, ct)).First();
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, box.X - 300, box.Y)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-pickup-radius-change-live.mjs"), app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("firstCollectStatus").GetInt32(), Is.EqualTo(409));
            Assert.That(result.RootElement.GetProperty("finalCollectDistance").GetDouble(), Is.LessThanOrEqualTo(180));
            Assert.That(wallet.Calls, Is.EqualTo(1));
        }
        finally { await app.StopAsync(ct); }
    }

    [Test]
    [Explicit("Runs two authenticated Spacemap clients against live Kestrel while a selected box is claimed.")]
    public async Task SpacemapClient_ShowsUnavailableWhenAnotherTabClaimsSelectedBox()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        var variables = new SpeedVariables();
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        var wallet = new Wallet();
        builder.Services.AddSingleton(sp => new WorldCollectibles(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), wallet, TimeProvider.System));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        var box = (await app.Services.GetRequiredService<WorldCollectibles>().VisibleAsync(1, ct)).First();
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, box.X - 300, box.Y)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-pickup-claimed-live.mjs"), app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("claimStatus").GetInt32(), Is.EqualTo(200));
            Assert.That(wallet.Calls, Is.EqualTo(1));
        }
        finally { await app.StopAsync(ct); }
    }

    [Test]
    [Explicit("Runs the shipped Spacemap page against live Kestrel pickup and NPC target endpoints.")]
    public async Task SpacemapClient_TargetSelectionCancelsPendingBoxPickup()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        var variables = new SpeedVariables();
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        var wallet = new Wallet();
        builder.Services.AddSingleton(sp => new WorldCollectibles(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), wallet, TimeProvider.System));
        builder.Services.AddSingleton<NpcRewards>();
        builder.Services.AddSingleton<WorldNpcs>(sp => new WorldNpcs(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>(),
            sp.GetRequiredService<NpcRewards>(), Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] }),
            TimeProvider.System, variables));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        var npc = (await app.Services.GetRequiredService<WorldNpcs>().VisibleAsync(1, ct)).Single();
        var box = (await app.Services.GetRequiredService<WorldCollectibles>().VisibleAsync(1, ct))
            .First(item => Math.Sqrt(Math.Pow(item.X - npc.X, 2) + Math.Pow(item.Y - npc.Y, 2)) > 600);
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, box.X - 300, box.Y)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-target-cancels-pickup-live.mjs"), app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            Assert.That(wallet.Calls, Is.Zero);
        }
        finally { await app.StopAsync(ct); }
    }

    [TestCase("verify-spacemap-jump-cancels-pickup-live.mjs")]
    [TestCase("verify-spacemap-late-move-after-jump-live.mjs")]
    [TestCase("verify-spacemap-move-delivered-after-jump-live.mjs")]
    [TestCase("verify-spacemap-external-jump-during-move-live.mjs")]
    [TestCase("verify-spacemap-late-same-map-move-live.mjs")]
    [TestCase("verify-spacemap-stale-tab-move-live.mjs")]
    [TestCase("verify-spacemap-late-state-after-move-live.mjs")]
    [TestCase("verify-spacemap-pending-move-poll-live.mjs")]
    [TestCase("verify-spacemap-single-jump-live.mjs")]
    [TestCase("verify-spacemap-external-jump-during-jump-live.mjs")]
    [TestCase("verify-spacemap-external-jump-during-target-live.mjs")]
    [TestCase("verify-spacemap-external-jump-during-fire-live.mjs")]
    [TestCase("verify-spacemap-external-jump-during-collect-live.mjs")]
    [Explicit("Runs the shipped Spacemap page against live Kestrel with delayed responses and repeated jump input.")]
    public async Task SpacemapClient_JumpCancelsPendingBoxPickup(string script)
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables
        { CollectionRadius = script.Contains("during-collect", StringComparison.Ordinal) ? 30000 : 180 });
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        var wallet = new Wallet();
        builder.Services.AddSingleton(sp => new WorldCollectibles(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), wallet, TimeProvider.System));
        builder.Services.AddSingleton(sp => new WorldNpcs(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), new NpcRewards(),
            Options.Create(new WorldNpcOptions { Templates =
                [new("Streuner", "npc_streuner", 2, 50, 20, 400, 1, 400, 2, 0)] }), TimeProvider.System));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, 19500, 1300)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing", script),
                app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            Assert.That(wallet.Calls, Is.Zero);
        }
        finally { await app.StopAsync(ct); }
    }

    [TestCase("target", "local")]
    [TestCase("fire", "local")]
    [TestCase("target", "external")]
    [TestCase("fire", "external")]
    [Explicit("Runs the shipped Spacemap page against live Kestrel with delayed combat responses after a jump.")]
    public async Task SpacemapClient_JumpDiscardsLateCombatResponse(string action, string jumpMode)
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables());
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        builder.Services.AddSingleton(sp => new WorldNpcs(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), new NpcRewards(),
            Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] }),
            TimeProvider.System));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, 19500, 1300)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-late-combat-after-jump-live.mjs"), app.Urls.Single(), Session.Dosid, action,
                jumpMode })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
        }
        finally { await app.StopAsync(ct); }
    }

    [Test]
    [Explicit("Runs the shipped Spacemap page against two live Kestrel replicas sharing world state.")]
    public async Task SpacemapClient_ShowsUnavailableWhenOtherReplicaClaimsSelectedBox()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var wallet = new Wallet();

        WebApplication BuildReplica()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(cache);
            builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
            builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
            builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables());
            builder.Services.AddSingleton<IServerMeshMutex>(mutex);
            builder.Services.AddSingleton(sp => new WorldSessionStore(cache, mutex));
            builder.Services.AddSingleton(sp => new WorldCollectibles(cache, mutex, wallet, TimeProvider.System));
            var app = builder.Build();
            WorldHttpBridge.Map(app);
            return app;
        }

        await using var replicaA = BuildReplica();
        await using var replicaB = BuildReplica();
        var box = (await replicaA.Services.GetRequiredService<WorldCollectibles>().VisibleAsync(1, ct)).First();
        await cache.SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, box.X - 300, box.Y)), ct);
        await replicaA.StartAsync(ct);
        await replicaB.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-pickup-claimed-live.mjs"), replicaA.Urls.Single(), Session.Dosid,
                replicaB.Urls.Single() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("replicaClaim").GetBoolean(), Is.True);
            Assert.That(result.RootElement.GetProperty("claimStatus").GetInt32(), Is.EqualTo(200));
            Assert.That(wallet.Calls, Is.EqualTo(1));
        }
        finally
        {
            await replicaB.StopAsync(ct);
            await replicaA.StopAsync(ct);
        }
    }

    [TestCase("verify-spacemap-ordered-config-live.mjs")]
    [TestCase("verify-spacemap-config-stale-poll-live.mjs")]
    [TestCase("verify-spacemap-config-delivered-after-jump-live.mjs")]
    [Explicit("Runs shipped configuration controls against live Kestrel HTTP.")]
    public async Task SpacemapClient_OrdersConfigurationClicksAndShowsCooldown(string script)
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver());
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables { ConfigurationCooldown = 5000,
            PortalJumpRadius = script == "verify-spacemap-config-delivered-after-jump-live.mjs" ? 20000 : 1 });
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>(),
            variables: sp.GetRequiredService<IRuntimeVariableRegistry>()));
        builder.Services.AddSingleton(sp => new WorldNpcs(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), new NpcRewards(),
            Options.Create(new WorldNpcOptions { Templates = [] }), TimeProvider.System));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                script), app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
        }
        finally { await app.StopAsync(ct); }
    }

    [TestCase("verify-spacemap-jump-owner-header-live.mjs")]
    [TestCase("verify-spacemap-config-delivered-after-jump-live.mjs")]
    [Explicit("Runs the shipped Spacemap jump against two live Kestrel replicas with per-map lease owners.")]
    public async Task SpacemapClient_MapConflictAdvertisesCurrentMapOwner(string script)
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var now = DateTimeOffset.UtcNow;
        static ServerMeshNode Node(string id, DateTimeOffset at) => new(id, "api-game", $"http://{id}",
            "127.0.0.1", 61001, ServerMeshNodeStatus.Active, at, at, 0, [1, 2], 0, 0);
        var registry = new MutableRegistry(new ServerMeshSnapshot([Node("a", now), Node("b", now)],
            [new ServerMapLease(1, "a", 1, now.AddMinutes(5)), new ServerMapLease(2, "b", 1, now.AddMinutes(5))]));

        WebApplication BuildReplica()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(cache);
            builder.Services.AddSingleton<IServerMeshRegistry>(registry);
            builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
            builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver());
            builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables {
                PortalJumpRadius = 20000, ConfigurationCooldown = 5000 });
            builder.Services.AddSingleton<IServerMeshMutex>(mutex);
            builder.Services.AddSingleton(sp => new WorldSessionStore(cache, mutex,
                variables: sp.GetRequiredService<IRuntimeVariableRegistry>()));
            var app = builder.Build();
            WorldHttpBridge.Map(app);
            return app;
        }

        await using var replicaA = BuildReplica();
        await using var replicaB = BuildReplica();
        await replicaA.StartAsync(ct);
        await replicaB.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                script), replicaA.Urls.Single(), Session.Dosid,
                replicaB.Urls.Single() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("conflictOwner").GetString(), Is.EqualTo("b"));
        }
        finally
        {
            await replicaB.StopAsync(ct);
            await replicaA.StopAsync(ct);
        }
    }

    [TestCase(50, 20, false)]
    [TestCase(5, 0, true)]
    [TestCase(50, 20, false, "verify-spacemap-ordered-targets-live.mjs")]
    [Explicit("Runs the shipped Spacemap page against live Kestrel while an earlier laser result is delayed.")]
    public async Task SpacemapClient_TargetSwitchDiscardsLateFireResult(int hp, int shield, bool destroyed,
        string script = "verify-spacemap-target-switch-during-fire-live.mjs")
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables());
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        builder.Services.AddSingleton(sp => new WorldNpcs(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), new NpcRewards(),
            Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 2, hp, shield, 400, 1, 400, 2, 0)] }),
            TimeProvider.System));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        var first = (await app.Services.GetRequiredService<WorldNpcs>().VisibleAsync(1, ct)).First();
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, first.X - 100, first.Y)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                script), app.Urls.Single(), Session.Dosid,
                destroyed.ToString().ToLowerInvariant() })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
        }
        finally { await app.StopAsync(ct); }
    }

    [Test]
    [Explicit("Runs the shipped Spacemap script in jsdom against live Kestrel combat endpoints.")]
    public async Task SpacemapClient_TargetsFiresAndRendersNpcDamage_OverLiveHttp()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
        var variables = new SpeedVariables();
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddSingleton<IAdminCommandDispatcher, RecordingAdmin>();
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        builder.Services.AddSingleton<NpcRewards>();
        builder.Services.AddSingleton(sp => new WorldCollectibles(sp.GetRequiredService<IDistributedCache>(),
            sp.GetRequiredService<IServerMeshMutex>(), new Wallet(), TimeProvider.System));
        builder.Services.AddSingleton<WorldNpcs>(sp => new WorldNpcs(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>(),
            sp.GetRequiredService<NpcRewards>(), Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] }),
            TimeProvider.System, variables));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        var npc = (await app.Services.GetRequiredService<WorldNpcs>().VisibleAsync(1, ct)).Single();
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, npc.X - 1100, npc.Y)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-combat-live.mjs"), app.Urls.Single(), Session.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            var rewards = app.Services.GetRequiredService<NpcRewards>();
            for (var i = 0; i < 200 && Volatile.Read(ref rewards.Calls) == 0; i++)
                await Task.Delay(50, ct);
            Assert.That(rewards.Calls, Is.EqualTo(1), "The live client did not kill the NPC");
            await app.Services.GetRequiredService<IDistributedCache>()
                .RemoveAsync("world:npc:target:73", ct);
            variables.CreditsReward = 5;
            variables.StreunerCount = 0;
            Assert.That(await app.Services.GetRequiredService<WorldSessionStore>()
                .TeleportAsync(73, 1, 0, 0, false, ct), Is.Not.Null);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("finalTarget").GetString(), Is.EqualTo("None"));
            Assert.That(result.RootElement.GetProperty("status").GetString(), Does.StartWith("Destroyed"));
            Assert.That(rewards.Calls, Is.EqualTo(2));
            Assert.That(rewards.LastCredited!.Credits, Is.EqualTo(400));
        }
        finally { await app.StopAsync(ct); }
    }

    [Test]
    [Category("Integration")]
    [Explicit("Runs the shipped Spacemap page across two Kestrel replicas with separate Redis connections.")]
    public async Task SpacemapClient_StaleMoveAcrossRealRedisReplicas_RefreshesAndRetries()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var redis = new ContainerBuilder("redis:8.6").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections")).Build();
        await redis.StartAsync(ct);
        var endpoint = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";
        await using var muxA = await ConnectionMultiplexer.ConnectAsync(endpoint);
        await using var muxB = await ConnectionMultiplexer.ConnectAsync(endpoint);
        using var cacheA = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(muxA) }));
        using var cacheB = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(muxB) }));

        WebApplication BuildReplica(IDistributedCache cache, IConnectionMultiplexer mux)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(cache);
            builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
            builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
            builder.Services.AddSingleton<IRuntimeVariableRegistry>(new SpeedVariables());
            builder.Services.AddSingleton<IServerMeshMutex>(new RedisServerMeshMutex(mux));
            builder.Services.AddSingleton(sp => new WorldSessionStore(cache,
                sp.GetRequiredService<IServerMeshMutex>()));
            var app = builder.Build();
            WorldHttpBridge.Map(app);
            return app;
        }

        await using var replicaA = BuildReplica(cacheA, muxA);
        await using var replicaB = BuildReplica(cacheB, muxB);
        await cacheA.SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, 19500, 1300)), ct);
        await replicaA.StartAsync(ct);
        await replicaB.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-stale-tab-move-live.mjs"), replicaA.Urls.Single(), Session.Dosid,
                replicaB.Urls.Single() }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(25), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("first").GetInt32(), Is.EqualTo(200));
            Assert.That(result.RootElement.GetProperty("stale").GetInt32(), Is.EqualTo(409));
            Assert.That(result.RootElement.GetProperty("retry").GetInt32(), Is.EqualTo(200));
            Assert.That(result.RootElement.GetProperty("rendered").GetString(), Is.EqualTo("17850 / 11135"));
            var saved = JsonSerializer.Deserialize<WorldTravel>((await cacheA.GetStringAsync(
                "world:position:73", ct))!);
            Assert.That((saved!.TargetX, saved.TargetY), Is.EqualTo((17850, 11135)));
        }
        finally
        {
            await replicaB.StopAsync(ct);
            await replicaA.StopAsync(ct);
        }
    }

    [Test]
    [Category("Integration")]
    [Explicit("Starts a Redis container and runs the shipped Spacemap script against two live Kestrel replicas.")]
    public async Task SpacemapClient_KillOnReplicaA_RetriesRewardOnReplicaB_OverRealRedis()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var redis = new ContainerBuilder("redis:8.6").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections")).Build();
        await redis.StartAsync(ct);
        var endpoint = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";
        await using var muxA = await ConnectionMultiplexer.ConnectAsync(endpoint);
        await using var muxB = await ConnectionMultiplexer.ConnectAsync(endpoint);
        using var cacheA = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(muxA) }));
        using var cacheB = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(muxB) }));
        var variables = new SpeedVariables();
        var rewards = new NpcRewards();
        var options = Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] });

        WebApplication BuildReplica(IDistributedCache cache, IConnectionMultiplexer mux)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(cache);
            builder.Services.AddSingleton<IGameAuthSessionStore, Auth>();
            builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25));
            builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
            builder.Services.AddSingleton<IAdminCommandDispatcher, RecordingAdmin>();
            builder.Services.AddSingleton<IServerMeshMutex>(new RedisServerMeshMutex(mux));
            builder.Services.AddSingleton(sp => new WorldSessionStore(cache,
                sp.GetRequiredService<IServerMeshMutex>()));
            builder.Services.AddSingleton(sp => new WorldCollectibles(cache,
                sp.GetRequiredService<IServerMeshMutex>(), new Wallet(), TimeProvider.System));
            builder.Services.AddSingleton(sp => new WorldNpcs(cache,
                sp.GetRequiredService<IServerMeshMutex>(), rewards, options, TimeProvider.System, variables));
            var app = builder.Build();
            WorldHttpBridge.Map(app);
            return app;
        }

        await using var replicaA = BuildReplica(cacheA, muxA);
        await using var replicaB = BuildReplica(cacheB, muxB);
        var npc = (await replicaA.Services.GetRequiredService<WorldNpcs>().VisibleAsync(1, ct)).Single();
        await cacheA.SetStringAsync("world:position:73",
            JsonSerializer.Serialize(new WorldPosition(1, npc.X - 1100, npc.Y)), ct);
        await replicaA.StartAsync(ct);
        await replicaB.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-combat-live.mjs"), replicaA.Urls.Single(), Session.Dosid,
                replicaB.Urls.Single() }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                for (var i = 0; i < 200 && Volatile.Read(ref rewards.Calls) == 0; i++)
                    await Task.Delay(50, ct);
                Assert.That(rewards.Calls, Is.EqualTo(1), "Replica A did not credit the kill");
                await cacheA.RemoveAsync("world:npc:target:73", ct);
                variables.CreditsReward = 5;
                variables.StreunerCount = 0;
                Assert.That(await replicaA.Services.GetRequiredService<WorldSessionStore>()
                    .TeleportAsync(73, 1, 0, 0, false, ct), Is.Not.Null);
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(25), ct);
                var output = await stdout;
                Assert.That(process.ExitCode, Is.Zero, await stderr + output);
                TestContext.Out.WriteLine(output);
                using var result = JsonDocument.Parse(output);
                Assert.That(result.RootElement.GetProperty("retryStatus").GetInt32(), Is.EqualTo(409));
                Assert.That(result.RootElement.GetProperty("retryOrigin").GetString(),
                    Is.EqualTo(new Uri(replicaB.Urls.Single()).GetLeftPart(UriPartial.Authority)));
                Assert.That(rewards.Calls, Is.EqualTo(2));
                Assert.That(rewards.LastCredited!.Credits, Is.EqualTo(400));
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally
        {
            await replicaB.StopAsync(ct);
            await replicaA.StopAsync(ct);
        }
    }
    [Test]
    public async Task Jump_TransfersBetweenOwnersAndReplicas_AndRejectsDistantShip()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var first = new WorldSessionStore(cache);
        var second = new WorldSessionStore(cache);
        var now = DateTimeOffset.UtcNow;
        var nodes = new[]
        {
            new ServerMeshNode("node-a", "api-game", "http://a", "127.0.0.1", 61001,
                ServerMeshNodeStatus.Active, now, now, 0, [1], 0, 0),
            new ServerMeshNode("node-b", "api-game", "http://b", "127.0.0.1", 61002,
                ServerMeshNodeStatus.Active, now, now, 0, [2], 0, 0)
        };
        using var mesh = new ServiceCollection().AddSingleton<IServerMeshRegistry>(
            new FixedRegistry(new ServerMeshSnapshot(nodes,
                [new ServerMapLease(1, "node-a", 1, now.AddMinutes(1)),
                 new ServerMapLease(2, "node-b", 1, now.AddMinutes(1))]))).BuildServiceProvider();
        var auth = new Auth();
        var resolver = new Resolver();
        var distant = await SendAsync("POST", "jump", "{\"portalId\":101}", auth, resolver, first,
            services: mesh);
        Assert.That(distant.Response.StatusCode, Is.EqualTo(409));
        await first.PersistAsync(new WorldPilot(73, "mmo", 1, 1, "ship", 0, 0)
            { X = 19500, Y = 1300 }, default);
        var jumped = await SendAsync("POST", "jump", "{\"portalId\":101}", auth, resolver, first,
            services: mesh);
        Assert.That(jumped.Response.StatusCode, Is.EqualTo(200));
        using (var json = JsonDocument.Parse(Body(jumped)))
        {
            Assert.That(json.RootElement.GetProperty("map").GetProperty("id").GetInt32(), Is.EqualTo(2));
            Assert.That(json.RootElement.GetProperty("pilot").GetProperty("x").GetInt32(), Is.EqualTo(2500));
            Assert.That(json.RootElement.GetProperty("jump").GetProperty("targetNodeId").GetString(),
                Is.EqualTo("node-b"));
            Assert.That(json.RootElement.GetProperty("jump").GetProperty("targetTcpHostPort").GetString(),
                Is.EqualTo("127.0.0.1:61002"));
        }
        var other = await SendAsync("GET", "state", null, auth, resolver, second, services: mesh);
        Assert.That(other.Response.Headers["X-Mesh-Map-Owner-Node"].ToString(), Is.EqualTo("node-b"));
        using var refreshed = JsonDocument.Parse(Body(other));
        Assert.That(refreshed.RootElement.GetProperty("map").GetProperty("id").GetInt32(), Is.EqualTo(2));
        Assert.That(await second.ConnectedAsync(1, default), Is.Empty);
        Assert.That(await second.ConnectedAsync(2, default), Has.Count.EqualTo(1));
    }
    [Test]
    public async Task StateHeaders_DistinguishHandlingNodeFromLeaseOwner()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new ServerMeshNode("owner", "api-game", "http://owner", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [1], 0, 0);
        using var services = new ServiceCollection()
            .AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "handler" }))
            .AddSingleton<IServerMeshRegistry>(new FixedRegistry(new ServerMeshSnapshot([owner],
                [new ServerMapLease(1, "owner", 1, now.AddMinutes(1))])))
            .BuildServiceProvider();
        var response = await SendAsync("GET", "state", null, new Auth(), new Resolver(), Store(),
            services: services);
        Assert.That(response.Response.Headers["X-Mesh-Node-Id"].ToString(), Is.EqualTo("handler"));
        Assert.That(response.Response.Headers["X-Mesh-Map-Owner-Node"].ToString(), Is.EqualTo("owner"));
    }

    [Test]
    public async Task BrowserState_RepairsHullOnlyWithCurrentMapLease()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync("world:position:73", JsonSerializer.Serialize(new WorldPosition(1, 2000, 1300)));
        await cache.SetStringAsync("world:runtime:73", JsonSerializer.Serialize(
            new WorldRuntimeSnapshot(1, 1, 5000, 0, null, 1000)));
        var now = DateTimeOffset.UtcNow;
        var owner = new ServerMeshNode("owner", "api-game", "http://owner", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [1], 0, 0);
        var registry = new MutableRegistry(new ServerMeshSnapshot([owner], []));
        using var app = new ServiceCollection().AddSingleton<IServerMeshRegistry>(registry)
            .BuildServiceProvider();
        var store = new WorldSessionStore(cache, new NpcMutex());
        var auth = new Auth();
        var resolver = new Resolver();
        var rejected = await SendAsync("GET", "state", null, auth, resolver, store, services: app);
        Assert.That(rejected.Response.StatusCode, Is.EqualTo(409));
        Assert.That(Body(rejected), Does.Contain("map_lease_changed"));
        Assert.That(JsonSerializer.Deserialize<WorldRuntimeSnapshot>(
            (await cache.GetStringAsync("world:runtime:73"))!)!.HitPoints, Is.EqualTo(1000));

        registry.Snapshot = new ServerMeshSnapshot([owner],
            [new ServerMapLease(1, "owner", 2, now.AddMinutes(1))]);
        var changed = registry.Reads;
        registry.AfterRead = count =>
        {
            if (count == changed + 3) registry.Snapshot = new ServerMeshSnapshot([owner], []);
        };
        var reassigned = await SendAsync("GET", "state", null, auth, resolver, store, services: app);
        Assert.That(reassigned.Response.StatusCode, Is.EqualTo(409));
        Assert.That(JsonSerializer.Deserialize<WorldRuntimeSnapshot>(
            (await cache.GetStringAsync("world:runtime:73"))!)!.HitPoints, Is.EqualTo(1000));
        registry.AfterRead = null;
        registry.Snapshot = new ServerMeshSnapshot([owner],
            [new ServerMapLease(1, "owner", 3, now.AddMinutes(1))]);
        var accepted = await SendAsync("GET", "state", null, auth, resolver, store, services: app);
        Assert.That(accepted.Response.StatusCode, Is.EqualTo(200));
        using var body = JsonDocument.Parse(Body(accepted));
        Assert.That(body.RootElement.GetProperty("pilot").GetProperty("hp").GetInt64(),
            Is.EqualTo(3560));
        Assert.That(JsonSerializer.Deserialize<WorldRuntimeSnapshot>(
            (await cache.GetStringAsync("world:runtime:73"))!)!.HitPoints, Is.EqualTo(3560));
    }

    private sealed class FixedRegistry(ServerMeshSnapshot snapshot) : IServerMeshRegistry
    {
        public Task<ServerMeshSnapshot> GetSnapshotAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
        public Task<ServerMeshSnapshot> HeartbeatAsync(ServerMeshNode node, CancellationToken ct = default) =>
            Task.FromResult(snapshot);
        public Task<ServerMeshSnapshot> SetDrainingAsync(string nodeId, CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class MutableRegistry(ServerMeshSnapshot snapshot) : IServerMeshRegistry
    {
        public ServerMeshSnapshot Snapshot { get; set; } = snapshot;
        public int Reads { get; private set; }
        public Action<int>? AfterRead { get; set; }
        public Task<ServerMeshSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            var current = Snapshot;
            AfterRead?.Invoke(++Reads);
            return Task.FromResult(current);
        }
        public Task<ServerMeshSnapshot> HeartbeatAsync(ServerMeshNode node, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);
        public Task<ServerMeshSnapshot> SetDrainingAsync(string nodeId, CancellationToken ct = default) =>
            Task.FromResult(Snapshot);
    }

    [Test]
    public async Task BrowserCommands_RejectMissingLeaseAndResumeAfterReassignment()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var now = DateTimeOffset.UtcNow;
        static ServerMeshNode Node(string id, DateTimeOffset at) => new(id, "api-game", $"http://{id}",
            "127.0.0.1", 61001, ServerMeshNodeStatus.Active, at, at, 0, [1], 0, 0);
        var ownerA = Node("a", now);
        var ownerB = Node("b", now);
        var registry = new MutableRegistry(new ServerMeshSnapshot([ownerA],
            [new ServerMapLease(1, "a", 1, now.AddMinutes(1))]));
        using var app = new ServiceCollection().AddSingleton<IServerMeshRegistry>(registry)
            .BuildServiceProvider();
        var store = new WorldSessionStore(cache, new NpcMutex());
        var auth = new Auth();
        var resolver = new Resolver();
        var entered = await SendAsync("POST", "enter", null, auth, resolver, store, services: app);
        Assert.That(entered.Response.StatusCode, Is.EqualTo(200));

        registry.Snapshot = new ServerMeshSnapshot([ownerA], []);
        foreach (var (route, payload) in new[]
        {
            ("move", "{\"x\":4000,\"y\":3000}"),
            ("config", "{\"configuration\":2}"),
            ("jump", "{\"portalId\":101}")
        })
        {
            var rejected = await SendAsync("POST", route, payload, auth, resolver, store,
                services: app);
            Assert.That(rejected.Response.StatusCode, Is.EqualTo(409), route);
            Assert.That(Body(rejected), Does.Contain("map_lease_changed"), route);
        }
        Assert.That(store.Destination((await resolver.ResolveAsync(73, "test-session", default)).Pilot!),
            Is.EqualTo((2000, 1300)));
        Assert.That(store.Configuration(73), Is.EqualTo(1));

        registry.Snapshot = new ServerMeshSnapshot([ownerB],
            [new ServerMapLease(1, "b", 2, now.AddMinutes(1))]);
        var moved = await SendAsync("POST", "move", "{\"x\":4000,\"y\":3000}", auth,
            resolver, store, services: app);
        Assert.That(moved.Response.StatusCode, Is.EqualTo(200));
        Assert.That(moved.Response.Headers["X-Mesh-Map-Owner-Node"].ToString(), Is.EqualTo("b"));
        using var body = JsonDocument.Parse(Body(moved));
        Assert.That(body.RootElement.GetProperty("pilot").GetProperty("targetX").GetInt32(),
            Is.EqualTo(4000));
    }

    [Test]
    public async Task BrowserMove_RejectsLeaseThatChangesAfterRequestStarts()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var now = DateTimeOffset.UtcNow;
        static ServerMeshNode Node(string id, DateTimeOffset at) => new(id, "api-game", $"http://{id}",
            "127.0.0.1", 61001, ServerMeshNodeStatus.Active, at, at, 0, [1], 0, 0);
        var ownerA = Node("a", now);
        var ownerB = Node("b", now);
        var replacement = new ServerMeshSnapshot([ownerB],
            [new ServerMapLease(1, "b", 2, now.AddMinutes(1))]);
        var registry = new MutableRegistry(new ServerMeshSnapshot([ownerA],
            [new ServerMapLease(1, "a", 1, now.AddMinutes(1))]));
        using var app = new ServiceCollection().AddSingleton<IServerMeshRegistry>(registry)
            .BuildServiceProvider();
        var store = new WorldSessionStore(shared.GetRequiredService<IDistributedCache>(), new NpcMutex());
        var auth = new Auth();
        var resolver = new Resolver();
        await SendAsync("POST", "enter", null, auth, resolver, store, services: app);
        var before = registry.Reads;
        registry.AfterRead = count => { if (count == before + 3) registry.Snapshot = replacement; };
        var rejected = await SendAsync("POST", "move", "{\"x\":4000,\"y\":3000}",
            auth, resolver, store, services: app);
        Assert.That(rejected.Response.StatusCode, Is.EqualTo(409));
        Assert.That(Body(rejected), Does.Contain("map_lease_changed"));
        Assert.That(store.Destination((await resolver.ResolveAsync(73, "test-session", default)).Pilot!),
            Is.EqualTo((2000, 1300)));
        registry.AfterRead = null;
        var accepted = await SendAsync("POST", "move", "{\"x\":4000,\"y\":3000}",
            auth, resolver, store, services: app);
        Assert.That(accepted.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task BrowserCollectAndFire_DoNotAwardOrDamageWithoutLiveLease()
    {
        using var shared = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = shared.GetRequiredService<IDistributedCache>();
        var mutex = new NpcMutex();
        var wallet = new Wallet();
        var rewards = new NpcRewards();
        var collectibles = new WorldCollectibles(cache, mutex, wallet, TimeProvider.System);
        var npcs = new WorldNpcs(cache, mutex, rewards, Options.Create(new WorldNpcOptions
        { Templates = [new("Streuner", "npc_streuner", 1, 50, 0, 400, 1, 400, 2, 0)] }), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var owner = new ServerMeshNode("a", "api-game", "http://a", "127.0.0.1", 61001,
            ServerMeshNodeStatus.Active, now, now, 0, [1], 0, 0);
        var registry = new MutableRegistry(new ServerMeshSnapshot([owner],
            [new ServerMapLease(1, "a", 1, now.AddMinutes(1))]));
        using var app = new ServiceCollection().AddSingleton<IServerMeshRegistry>(registry)
            .AddSingleton(collectibles).AddSingleton(npcs).BuildServiceProvider();
        var store = new WorldSessionStore(cache, mutex);
        var auth = new Auth();
        var resolver = new Resolver(100);
        var npc = (await npcs.VisibleAsync(1, default)).Single();
        await SendAsync("POST", "enter", null, auth, resolver, store, services: app);
        await SendAsync("POST", "target", $"{{\"npcId\":{npc.Id}}}", auth, resolver, store,
            services: app);
        registry.Snapshot = new ServerMeshSnapshot([owner], []);
        var target = await SendAsync("POST", "target", $"{{\"npcId\":{npc.Id}}}", auth,
            resolver, store, services: app);
        var collect = await SendAsync("POST", "collect", "{\"boxId\":1001}", auth, resolver,
            store, services: app);
        var fire = await SendAsync("POST", "fire", $"{{\"npcId\":{npc.Id}}}", auth,
            resolver, store, services: app);
        Assert.Multiple(() =>
        {
            Assert.That(target.Response.StatusCode, Is.EqualTo(409));
            Assert.That(Body(target), Does.Contain("map_lease_changed"));
            Assert.That(collect.Response.StatusCode, Is.EqualTo(409));
            Assert.That(Body(collect), Does.Contain("map_lease_changed"));
            Assert.That(fire.Response.StatusCode, Is.EqualTo(409));
            Assert.That(Body(fire), Does.Contain("map_lease_changed"));
            Assert.That(wallet.Calls, Is.Zero);
            Assert.That(rewards.Calls, Is.Zero);
        });
    }

    [Test]
    public async Task SeparateReplicas_SharePresenceMovementAndConfigurationAcrossRequests()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var first = new WorldSessionStore(cache);
        var second = new WorldSessionStore(cache);
        var auth = new Auth();
        var resolver = new Resolver();

        await SendAsync("POST", "enter", null, auth, resolver, first);
        await second.EnterAsync(new WorldPilot(74, "mmo", 1, 1, "ship", 0, 0)
        {
            UserName = "Other pilot", Stats = new WorldShipStats(200, 1000, 0, 0, 0, [])
        }, default);
        Assert.That(await first.ConnectedAsync(1, default), Has.Count.EqualTo(2));
        Assert.That(await second.ConnectedAsync(1, default), Has.Count.EqualTo(2));
        var moved = await SendAsync("POST", "move", "{\"x\":4000,\"y\":3000}", auth, resolver, first);
        Assert.That(moved.Response.Headers["X-Mesh-Node-Id"].ToString(), Is.Not.Empty);
        Assert.That(moved.Response.Headers["X-Mesh-Map-Owner-Node"].ToString(), Is.Not.Empty);
        var otherState = await SendAsync("GET", "state", null, auth, resolver, second);
        using (var json = JsonDocument.Parse(Body(otherState)))
        {
            Assert.That(json.RootElement.GetProperty("pilot").GetProperty("targetX").GetInt32(), Is.EqualTo(4000));
            var pilots = json.RootElement.GetProperty("pilots");
            Assert.That(pilots.GetArrayLength(), Is.EqualTo(2));
            Assert.That(pilots.EnumerateArray().Single(item => item.GetProperty("id").GetInt64() == 73)
                .GetProperty("x").GetInt32(), Is.InRange(2000, 4000));
        }
        await SendAsync("POST", "config", "{\"configuration\":2}", auth, resolver, second);
        var firstState = await SendAsync("GET", "state", null, auth, resolver, first);
        using var refreshed = JsonDocument.Parse(Body(firstState));
        Assert.That(refreshed.RootElement.GetProperty("pilot").GetProperty("configuration").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public async Task StatePolls_OnOneReplica_ShareOneRemotePresenceAndPositionRead()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var counting = new CountingCache(cache);
        var nodeA = new WorldSessionStore(counting);
        var nodeB = new WorldSessionStore(cache);
        var auth = new Auth();
        var resolver = new Resolver();
        var remote = await nodeB.EnterAsync(new WorldPilot(811, "mmo", 1, 1, "ship", 0, 0)
        {
            UserName = "Remote pilot", X = 1000, Y = 1000, Stats = new WorldShipStats(300, 1000, 0, 0, 0, [])
        }, default);
        await nodeB.MoveAsync(remote, 4000, 2500, null, default);
        // The enter response itself builds the first map snapshot; start polling after it expires.
        await SendAsync("POST", "enter", null, auth, resolver, nodeA);
        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        counting.Reads.Clear();
        counting.Writes.Clear();

        for (var poll = 0; poll < 3; poll++)
        {
            using var json = JsonDocument.Parse(Body(await SendAsync("GET", "state", null, auth, resolver, nodeA)));
            var other = json.RootElement.GetProperty("pilots").EnumerateArray()
                .Single(item => item.GetProperty("id").GetInt64() == 811);
            Assert.That(other.GetProperty("name").GetString(), Is.EqualTo("Remote pilot"));
            Assert.That(other.GetProperty("x").GetInt32(), Is.InRange(1000, 4000));
        }
        await nodeA.RefreshLocalPresenceAsync(default);
        Assert.That(counting.Writes.Count(k => k == "world:presence:map:1"), Is.Zero,
            "State polls and mesh ticks within the heartbeat interval must not rewrite the map roster.");
        Assert.That(counting.Reads.Count(k => k.EndsWith(":811")), Is.EqualTo(1),
            "Three polls within the TTL must share one read of the remote pilot's saved position.");

        await Task.Delay(WorldSessionStore.RemoteSnapshotTtl + TimeSpan.FromMilliseconds(100));
        await SendAsync("GET", "state", null, auth, resolver, nodeA);
        Assert.That(counting.Reads.Count(k => k.EndsWith(":811")), Is.EqualTo(2),
            "An expired snapshot must re-read the remote pilot's saved position.");
    }

    [Test]
    public async Task MeshTick_WithSeveralDueLocalPilots_RewritesEachMapRosterOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var counting = new CountingCache(cache);
        var clock = new ManualClock();
        var nodeA = new WorldSessionStore(counting, clock: clock);
        var nodeB = new WorldSessionStore(cache);
        var stats = new WorldShipStats(300, 1000, 0, 0, 0, []);
        await nodeB.EnterAsync(new WorldPilot(811, "mmo", 1, 1, "ship", 0, 0)
            { UserName = "Remote pilot", X = 1000, Y = 1000, Stats = stats }, default);
        foreach (var id in new long[] { 901, 902, 903 })
            await nodeA.EnterAsync(new WorldPilot(id, "mmo", 1, 1, "ship", 0, 0)
                { UserName = $"Local {id}", X = 2000, Y = 2000, Stats = stats }, default);
        await nodeA.EnterAsync(new WorldPilot(904, "eic", 2, 1, "ship", 0, 0)
            { UserName = "Local 904", X = 2000, Y = 2000, Stats = stats }, default);
        counting.Writes.Clear();

        await nodeA.RefreshLocalPresenceAsync(default);
        Assert.That(counting.Writes.Count(k => k.StartsWith("world:presence:map:")), Is.Zero,
            "A tick inside the heartbeat interval must not touch any roster.");

        clock.Now += WorldSessionStore.PresenceHeartbeatInterval;
        await nodeA.RefreshLocalPresenceAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(counting.Writes.Count(k => k == "world:presence:map:1"), Is.EqualTo(1),
                "Three due pilots on map 1 must share one roster write.");
            Assert.That(counting.Writes.Count(k => k == "world:presence:map:2"), Is.EqualTo(1));
        });
        var roster = (await nodeB.ConnectedAsync(1, default)).Select(p => p.UserId).Order().ToArray();
        Assert.That(roster, Is.EqualTo(new long[] { 811, 901, 902, 903 }),
            "The batched write must keep the other node's pilot and every local pilot.");
        Assert.That((await nodeB.ConnectedAsync(2, default)).Single().UserId, Is.EqualTo(904));

        counting.Writes.Clear();
        await nodeA.RefreshLocalPresenceAsync(default);
        Assert.That(counting.Writes.Count(k => k.StartsWith("world:presence:map:")), Is.Zero,
            "The batch must restamp every pilot's heartbeat.");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingCache(IDistributedCache inner) : IDistributedCache
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Reads = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Writes = new();
        public byte[]? Get(string key) { Reads.Enqueue(key); return inner.Get(key); }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        { Reads.Enqueue(key); return inner.GetAsync(key, token); }
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        { Writes.Enqueue(key); inner.Set(key, value, options); }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        { Writes.Enqueue(key); return inner.SetAsync(key, value, options, token); }
    }

    private static readonly GameAuthSession Session = new()
    {
        AccountId = Guid.NewGuid(), Username = "Test pilot", GameUserId = 73, Company = "mmo",
        Dosid = "test-session", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    };

    [Test]
    public async Task EnterStateAndMove_UseOneWorldSession()
    {
        var store = Store();
        var auth = new Auth();
        var resolver = new Resolver();
        var entered = await SendAsync("POST", "enter", null, auth, resolver, store);
        var initial = JsonDocument.Parse(Body(entered));
        Assert.Multiple(() =>
        {
            Assert.That(initial.RootElement.GetProperty("map").GetProperty("name").GetString(), Is.EqualTo("1-1"));
            Assert.That(initial.RootElement.GetProperty("pilot").GetProperty("x").GetInt32(), Is.EqualTo(2000));
            Assert.That(store.Connected(1), Has.Count.EqualTo(1));
        });

        var moved = await SendAsync("POST", "move", "{\"x\":4000,\"y\":3000}", auth, resolver, store);
        Assert.That(moved.Response.StatusCode, Is.EqualTo(200));
        Assert.That(store.Destination(store.Connected(1).Single()), Is.EqualTo((4000, 3000)));
        var state = await SendAsync("GET", "state", null, auth, resolver, store);
        using var readback = JsonDocument.Parse(Body(state));
        Assert.That(readback.RootElement.GetProperty("pilot").GetProperty("targetX").GetInt32(), Is.EqualTo(4000));
        Assert.That(readback.RootElement.GetProperty("pilot").GetProperty("x").GetInt32(), Is.InRange(2000, 4000));
        Assert.That(resolver.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task ConfigAndOutOfBoundsMove_UseSocketMovementEngine()
    {
        var store = Store();
        var auth = new Auth();
        var resolver = new Resolver();
        var config = await SendAsync("POST", "config", "{\"configuration\":2}", auth, resolver, store);
        Assert.That(JsonDocument.Parse(Body(config)).RootElement.GetProperty("configuration").GetInt32(), Is.EqualTo(2));
        var move = await SendAsync("POST", "move", "{\"x\":99999,\"y\":-7}", auth, resolver, store);
        using var result = JsonDocument.Parse(Body(move));
        Assert.Multiple(() =>
        {
            Assert.That(result.RootElement.GetProperty("pilot").GetProperty("targetX").GetInt32(), Is.EqualTo(21000));
            Assert.That(result.RootElement.GetProperty("pilot").GetProperty("targetY").GetInt32(), Is.Zero);
            Assert.That(result.RootElement.GetProperty("pilot").GetProperty("configuration").GetInt32(), Is.EqualTo(2));
            Assert.That(result.RootElement.GetProperty("pilot").GetProperty("radiationWarning").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task ConfigCooldown_ReturnsConflictAndRetainsActiveAttributes()
    {
        var store = Store();
        var auth = new Auth();
        var resolver = new Resolver();
        await SendAsync("POST", "config", "{\"configuration\":2}", auth, resolver, store);
        var rejected = await SendAsync("POST", "config", "{\"configuration\":1}", auth, resolver, store);
        Assert.That(rejected.Response.StatusCode, Is.EqualTo(409));
        using var conflict = JsonDocument.Parse(Body(rejected));
        Assert.That(conflict.RootElement.GetProperty("cooldownRemainingMs").GetInt32(), Is.InRange(1, 5000));
        var state = await SendAsync("GET", "state", null, auth, resolver, store);
        using var json = JsonDocument.Parse(Body(state));
        Assert.That(json.RootElement.GetProperty("pilot").GetProperty("configuration").GetInt32(), Is.EqualTo(2));
        Assert.That(json.RootElement.GetProperty("pilot").GetProperty("speed").GetInt32(), Is.EqualTo(320));
    }

    [Test]
    public async Task State_IncludesCompanionCoordinatesAndLaserDamage()
    {
        var store = Store();
        var response = await SendAsync("POST", "enter", null, new Auth(), new Resolver(), store);
        using var json = JsonDocument.Parse(Body(response));
        var pilot = json.RootElement.GetProperty("pilot");
        Assert.That(pilot.GetProperty("laserDamage").GetInt32(), Is.EqualTo(0));
        var companions = pilot.GetProperty("companions");
        Assert.That(companions.GetArrayLength(), Is.EqualTo(1));
        Assert.That(companions[0].GetProperty("itemId").GetString(), Is.EqualTo("drone_flax"));
    }

    [Test]
    public async Task MissingCookie_CannotEnterOrMove()
    {
        var store = Store();
        var auth = new Auth();
        var resolver = new Resolver();
        foreach (var route in new[] { "enter", "state", "move", "config" })
        {
            var context = await SendAsync(route == "state" ? "GET" : "POST", route,
                route == "move" ? "{\"x\":1,\"y\":2}" : route == "config" ? "{\"configuration\":1}" : null,
                auth, resolver, store, false);
            Assert.That(context.Response.StatusCode, Is.EqualTo(401), route);
        }
        Assert.That(store.Connected(1), Is.Empty);
    }

    [Test]
    public void LaunchPage_HasCanvasAndControls()
    {
        Assert.That(LocalSpacemapPage.Html, Does.Contain("id=\"spaceCanvas\""));
        Assert.That(LocalSpacemapPage.Html, Does.Contain("id=\"minimap\""));
        Assert.That(LocalSpacemapPage.Html, Does.Contain("/spacemap/world/"));
    }

    [TestCase("internalStart")]
    [TestCase("internalMapRevolution")]
    public async Task SignedInStartActions_OpenCanvas(string action)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=" + action);
        context.Request.Headers.Cookie = "dosid=test-session";
        context.Response.Body = new MemoryStream();
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, new Auth(), null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(Body(context), Does.Contain("id=\"spaceCanvas\""));
        });
    }

    [Test]
    public async Task UnityInternalStart_RedirectsToDosidHandoff()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString("?action=internalStart&prc=100");
        context.Request.Headers.UserAgent = "DarkOrbit Unity Client 1.1.100";
        context.Request.Headers.Cookie = "dosid=test-session";
        context.Response.Body = new MemoryStream();
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, new Auth(), null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

        await endpoint.HandleAsync(default);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalWebGL&from=internalStart"));
        });

        var handoff = new DefaultHttpContext();
        handoff.Request.Method = "GET";
        handoff.Request.Path = "/indexInternal.es";
        handoff.Request.QueryString = new QueryString("?action=internalWebGL&from=internalStart");
        handoff.Request.Headers.UserAgent = "DarkOrbit Unity Client 1.1.100";
        handoff.Request.Headers.Cookie = "dosid=test-session";
        handoff.Response.Body = new MemoryStream();
        endpoint = Factory.Create<IndexInternalStartEndpoint>(handoff,
            NullLogger<IndexInternalStartEndpoint>.Instance, new Auth(), null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

        await endpoint.HandleAsync(default);

        Assert.That(handoff.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(Body(handoff), Is.EqualTo(Session.Dosid));
    }

    [Test]
    public async Task MappedEndpoints_AllowDosidCookieAuth_WhenFallbackPolicyRequiresAuthenticatedUser()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore>(new Auth(isAdmin: true));
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(25, isAdmin: true));
        var variables = new SpeedVariables();
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddSingleton<IAdminCommandDispatcher, RecordingAdmin>();
        builder.Services.AddSingleton<IServerMeshMutex, NpcMutex>();
        builder.Services.AddSingleton(sp => new WorldSessionStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<IServerMeshMutex>()));
        Microsoft.Extensions.Hosting.SecurityExtensions.AddKeycloakJwtAuthentication(builder.Services, builder.Configuration);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        WorldHttpBridge.Map(app);
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                BaseAddress = new Uri(app.Urls.Single())
            };
            using var unauthState = await client.GetAsync("/spacemap/world/state", ct);
            Assert.That((int)unauthState.StatusCode, Is.EqualTo(401));

            using var enterReq = new HttpRequestMessage(HttpMethod.Post, "/spacemap/world/enter");
            enterReq.Headers.Add("Cookie", $"dosid={Session.Dosid}");
            using var enterResp = await client.SendAsync(enterReq, ct);
            Assert.That((int)enterResp.StatusCode, Is.EqualTo(200));

            using var stateReq = new HttpRequestMessage(HttpMethod.Get, "/spacemap/world/state");
            stateReq.Headers.Add("Cookie", $"dosid={Session.Dosid}");
            using var stateResp = await client.SendAsync(stateReq, ct);
            Assert.That((int)stateResp.StatusCode, Is.EqualTo(200));
            var body = await stateResp.Content.ReadAsStringAsync(ct);
            Assert.That(body, Does.Contain("\"name\":\"Test pilot\""));
        }
        finally
        {
            await app.StopAsync(ct);
        }
    }

    private static WorldSessionStore Store()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        return new WorldSessionStore(services.GetRequiredService<IDistributedCache>());
    }

    private static async Task<DefaultHttpContext> SendAsync(string method, string route, string? json,
        IGameAuthSessionStore auth, IWorldPilotResolver resolver, WorldSessionStore store, bool cookie = true,
        IServiceProvider? services = null, IAdminCommandDispatcher? dispatcher = null,
        string? contentType = null)
    {
        var context = new DefaultHttpContext();
        if (services is not null) context.RequestServices = services;
        context.Request.Method = method;
        context.Request.Path = "/spacemap/world/" + route;
        if (cookie) context.Request.Headers.Cookie = "dosid=test-session";
        context.Request.ContentType = json is null ? null : contentType ?? "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json ?? ""));
        context.Response.Body = new MemoryStream();
        switch (route)
        {
            case "enter": await WorldHttpBridge.EnterAsync(context, auth, resolver, store, default); break;
            case "state": await WorldHttpBridge.StateAsync(context, auth, resolver, store, default); break;
            case "move": await WorldHttpBridge.MoveAsync(context, auth, resolver, store, default); break;
            case "config": await WorldHttpBridge.ConfigAsync(context, auth, resolver, store, default); break;
            case "jump": await WorldHttpBridge.JumpAsync(context, auth, resolver, store, default); break;
            case "collect": await WorldHttpBridge.CollectAsync(context, auth, resolver, store,
                services!.GetRequiredService<WorldCollectibles>(), default); break;
            case "target": await WorldHttpBridge.TargetAsync(context, auth, resolver, store,
                services!.GetRequiredService<WorldNpcs>(), default); break;
            case "fire": await WorldHttpBridge.FireAsync(context, auth, resolver, store,
                services!.GetRequiredService<WorldNpcs>(), default); break;
            case "admin/command": await WorldHttpBridge.AdminCommandAsync(context, auth, resolver,
                store, dispatcher!, default); break;
        }
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class Resolver(int laserDamage = 0, bool isAdmin = false) : IWorldPilotResolver
    {
        public int Calls { get; private set; }
        public Task<(WorldPilot? Pilot, WorldLoginError Error)> ResolveAsync(long userId, string dosid,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<(WorldPilot?, WorldLoginError)>(
                (new WorldPilot(userId, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
                {
                    UserName = "Test pilot", FactionId = 1, AccountId = Session.AccountId,
                    IsAdmin = isAdmin,
                    Stats = new WorldShipStats(370, 128000, 5000, 1500, laserDamage, []), ConfigurationTwoSpeed = 320,
                    Companions = [new WorldCompanion("drone", "drone_flax", 0, "standard")]
                }, default));
        }
    }

    private sealed class Auth(bool isAdmin = false) : IGameAuthSessionStore
    {
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken ct) =>
            Task.FromResult<GameAuthSession?>(dosid == Session.Dosid ? new GameAuthSession
            {
                AccountId = Session.AccountId, Username = Session.Username, GameUserId = Session.GameUserId,
                Company = Session.Company, Dosid = Session.Dosid, ExpiresAt = Session.ExpiresAt,
                IsAdmin = isAdmin
            } : null);
        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? token,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string token, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string token, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task InvalidateAsync(string dosid, CancellationToken ct) => throw new NotImplementedException();
    }
}
