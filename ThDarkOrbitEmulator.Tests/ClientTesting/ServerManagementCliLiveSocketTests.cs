using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using FastEndpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

/// <summary>
/// World-31: drives the shipped Server Management CLI (scripts/admin/Invoke-ServerAdminCommand.ps1) as a
/// real PowerShell process against a real Kestrel host serving the backoffice admin command route, while a
/// connected loopback TCP world client sharing the same runtime-variable registry observes the change.
/// The PostgreSQL-backed dispatcher/history is replaced by an in-memory registry so no container is needed.
/// </summary>
public class ServerManagementCliLiveSocketTests
{
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("sub", "cli-operator")], "Test")), "Test"))
                : AuthenticateResult.NoResult());
    }

    private sealed class MemoryVariables : IRuntimeVariableRegistry
    {
        private readonly object gate = new();
        private readonly Dictionary<string, string> values = new()
        {
            ["world.movement.speed_multiplier"] = "1",
            ["world.station.peace_radius"] = "1000",
            ["world.spawn.streuner_count"] = "1"
        };
        public readonly List<RuntimeVariableChangeEntry> History = [];
        public IReadOnlyList<RuntimeVariable> Catalog => [];

        public Task<double> NumberAsync(string key, CancellationToken ct = default)
        {
            lock (gate)
                return Task.FromResult(key switch
                {
                    _ when values.TryGetValue(key, out var value) => double.Parse(value, CultureInfo.InvariantCulture),
                    "world.ship.pusat_plus_base_speed" or "world.ship.phoenix_base_speed" => 300,
                    _ => 1
                });
        }

        public Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
            CancellationToken ct = default)
        {
            lock (gate)
            {
                if (!values.TryGetValue(key, out var previous)) throw new KeyNotFoundException(key);
                values[key] = value;
                var entry = new RuntimeVariableChangeEntry(History.Count + 1, key, "World", previous, value, "1",
                    "Number", actor.Surface, actor.ChangedBy, actor.NodeId, actor.Reason, actor.RawCommand,
                    DateTimeOffset.UtcNow);
                History.Add(entry);
                return Task.FromResult(entry);
            }
        }

        public Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
            CancellationToken ct = default)
        {
            RuntimeVariableChangeEntry target;
            lock (gate) target = History.Single(h => h.ChangeId == changeId);
            return SetAsync(target.VariableKey, target.PreviousValue, actor, ct);
        }

        public Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100, long? beforeChangeId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Only the var set/rollback verbs the CLI sends here; the real dispatcher needs PostgreSQL.</summary>
    private sealed class VariableDispatcher(IRuntimeVariableRegistry variables) : IAdminCommandDispatcher
    {
        public async Task<AdminCommandResult> ExecuteAsync(AdminCommand command, AdminCommandActor actor,
            CancellationToken ct = default)
        {
            var words = AdminCommandDispatcher.Parse(command.Text ?? string.Empty);
            RuntimeVariableActor Actor(int reasonIndex) => new(actor.Surface, actor.Actor, actor.NodeId,
                string.Join(' ', words.Skip(reasonIndex)), command.Text);
            return words switch
            {
                ["var", "set", var key, var value, ..] =>
                    new("var set", await variables.SetAsync(key, value, Actor(4), ct)),
                ["var", "rollback", var id, ..] =>
                    new("var rollback", await variables.RollbackAsync(long.Parse(id, CultureInfo.InvariantCulture), Actor(3), ct)),
                _ => throw new ArgumentException("Unsupported in this test host.")
            };
        }
    }

    private sealed class Resolver(WorldPilot pilot) : IWorldPilotResolver
    {
        public Task<(WorldPilot? Pilot, WorldLoginError Error)> ResolveAsync(long userId, string dosid,
            CancellationToken ct) => Task.FromResult<(WorldPilot?, WorldLoginError)>((pilot, default));
    }

    private sealed class TestNpcMutex : IServerMeshMutex
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new Handle(gate);
        }
        private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class TestNpcRewards : IWorldNpcRewards
    {
        public Task CreditAsync(Guid accountId, WorldNpc npc, string key, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class PickupWallet : IWorldRewardWallet
    {
        public int Credits { get; private set; }
        public Task<bool> CreditAsync(Guid accountId, WorldCollectible box, string key, CancellationToken ct)
        {
            Credits++;
            return Task.FromResult(true);
        }
        public Task<long> CargoUsedAsync(Guid accountId, CancellationToken ct) => Task.FromResult(0L);
    }

    private const string FastEndpointsResolverReason =
        "UseFastEndpoints leaves a process-wide resolver bound to this host's disposed provider, which breaks later Factory.Create tests in the same run.";

    /// <summary>
    /// Hosts the admin route, logs a loopback TCP client in as <paramref name="pilot"/>, drains the seven entry
    /// packets, then hands the idle socket and a CLI runner to <paramref name="scenario"/>.
    /// </summary>
    private static async Task RunWithCliAndSocketAsync(IRuntimeVariableRegistry variables, WorldPilot pilot,
        Func<WorldPacketReader, Func<string, Task<JsonElement>>, CancellationToken, Task> scenario,
        bool withNpcs = false)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The management CLI is a Windows PowerShell script.");
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFastEndpoints(options => options.Filter = type => type == typeof(AdminCommandEndpoint));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite), p => p.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "cli-node" }));
        builder.Services.AddSingleton<IAdminCommandDispatcher>(new VariableDispatcher(variables));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.UseFastEndpoints(options =>
        {
            options.Endpoints.RoutePrefix = "api";
            options.Versioning.Prefix = "v";
            options.Versioning.PrependToRoute = true;
        });
        await app.StartAsync(ct);

        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new WorldSessionStore(cache, variables: variables);
        var npcs = withNpcs ? new WorldNpcs(cache, new TestNpcMutex(), new TestNpcRewards(),
            Options.Create(new WorldNpcOptions
                { Templates = [new("Streuner", "npc_streuner", 1, 50, 20, 400, 1, 400, 2, 0)] }),
            TimeProvider.System, variables) : null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var server = await listener.AcceptTcpClientAsync(ct);
        listener.Stop();
        var worker = new WorldSessionHandler(new Resolver(pilot), new WorldServerOptions(), store,
            npcs: npcs, variables: variables).RunAsync(server.GetStream());
        try
        {
            var input = new WorldPacketReader(client.GetStream());
            var output = new WorldPacketWriter(client.GetStream());
            await output.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await input.ReadAsync();
            await output.WriteAsync(WorldCommandId.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt64(w, pilot.UserId); WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, "local-1"); WorldPayload.WriteString(w, "test-instance");
            }));
            for (var i = 0; i < 6; i++) await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);

            // A leading '!' marks a command the CLI must reject; the result is then {exitCode, stderr}.
            async Task<JsonElement> CliAsync(string commandText)
            {
                var expectFailure = commandText.StartsWith('!');
                if (expectFailure) commandText = commandText[1..];
                var start = new ProcessStartInfo("powershell")
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                };
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../scripts/admin/Invoke-ServerAdminCommand.ps1")),
                    "-CommandText", commandText, "-BaseUri", app.Urls.Single() })
                    start.ArgumentList.Add(argument);
                start.Environment["THDARKORBIT_ADMIN_TOKEN"] = Guid.NewGuid().ToString("N");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
                if (expectFailure)
                {
                    Assert.That(process.ExitCode, Is.Not.Zero, await stdout);
                    return JsonSerializer.SerializeToElement(new { exitCode = process.ExitCode, stderr = await stderr + await stdout });
                }
                Assert.That(process.ExitCode, Is.Zero, await stderr);
                return JsonDocument.Parse(await stdout).RootElement.GetProperty("data").Clone();
            }

            await scenario(input, CliAsync, ct);
        }
        finally
        {
            client.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await app.StopAsync(ct);
        }
    }

    [Test]
    [Explicit(FastEndpointsResolverReason)]
    public Task ServerManagementCli_SpawnCountSetAndRollback_RefreshIdleTcpNpcRoster()
    {
        var variables = new MemoryVariables();
        var pilot = new WorldPilot(854, "mmo", 1, 1, "ship", 0, 0)
            { Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        return RunWithCliAndSocketAsync(variables, pilot, async (input, CliAsync, ct) =>
        {
            async Task<int> NextNpcAsync(WorldCommandId command)
            {
                while (true)
                {
                    var packet = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                    Assert.That(packet.Command, Is.Not.EqualTo(WorldCommandId.LoginErrorCommand));
                    if (packet.Command == command)
                        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet.Payload.Span);
                }
            }

            var initialNpc = await NextNpcAsync(WorldCommandId.ShipCreateCommand);
            var set = await CliAsync("var set world.spawn.streuner_count 2 cli add streuner");
            Assert.That(set.GetProperty("surface").GetString(), Is.EqualTo("ServerManagement"));
            Assert.That(set.GetProperty("variableKey").GetString(), Is.EqualTo("world.spawn.streuner_count"));
            Assert.That(set.GetProperty("previousValue").GetString(), Is.EqualTo("1"));
            var addedNpc = await NextNpcAsync(WorldCommandId.ShipCreateCommand);
            Assert.That(addedNpc, Is.Not.EqualTo(initialNpc));

            var reset = await CliAsync("var set world.spawn.streuner_count 0 cli clear streuners");
            Assert.That(reset.GetProperty("newValue").GetString(), Is.EqualTo("0"));
            var removed = new HashSet<int>
            {
                await NextNpcAsync(WorldCommandId.ShipDestroyCommand),
                await NextNpcAsync(WorldCommandId.ShipDestroyCommand)
            };
            Assert.That(removed, Is.EquivalentTo(new[] { initialNpc, addedNpc }));

            var rollback = await CliAsync($"var rollback {reset.GetProperty("changeId").GetInt64()} cli restore streuners");
            Assert.That(rollback.GetProperty("newValue").GetString(), Is.EqualTo("2"));
            var restored = new HashSet<int>
            {
                await NextNpcAsync(WorldCommandId.ShipCreateCommand),
                await NextNpcAsync(WorldCommandId.ShipCreateCommand)
            };
            Assert.That(restored, Is.EquivalentTo(new[] { initialNpc, addedNpc }));
            Assert.That(variables.History.Select(h => (h.Surface, h.NewValue)), Is.EqualTo(new[]
            {
                ("ServerManagement", "2"), ("ServerManagement", "0"), ("ServerManagement", "2")
            }));
        }, withNpcs: true);
    }

    [Test]
    [Explicit(FastEndpointsResolverReason)]
    public Task ServerManagementCli_VarSetAndRollback_RefreshConnectedTcpClientShipSpeed()
    {
        var variables = new MemoryVariables();
        var pilot = new WorldPilot(831, "mmo", 1, 1, "ship", 0, 0)
        { BaseSpeedAtLoad = 300, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        return RunWithCliAndSocketAsync(variables, pilot, async (input, CliAsync, ct) =>
        {
            async Task AssertSpeedAsync(int expected)
            {
                var packet = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                Assert.That(packet.Command, Is.EqualTo(WorldCommandId.ShipAttributesCommand));
                var speed = WorldPayload.Decode(packet.Payload, r =>
                {
                    WorldPayload.ReadInt64(r); r.ReadByte();
                    var value = WorldPayload.ReadInt32(r);
                    WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r); WorldPayload.ReadInt64(r);
                    WorldPayload.ReadInt32(r);
                    return value;
                });
                Assert.That(speed, Is.EqualTo(expected));
            }

            var set = await CliAsync("var set world.movement.speed_multiplier 2 cli live socket");
            Assert.That(set.GetProperty("surface").GetString(), Is.EqualTo("ServerManagement"));
            Assert.That(set.GetProperty("changedBy").GetString(), Is.EqualTo("cli-operator"));
            Assert.That(set.GetProperty("nodeId").GetString(), Is.EqualTo("cli-node"));
            Assert.That(set.GetProperty("newValue").GetString(), Is.EqualTo("2"));
            await AssertSpeedAsync(600);

            var rollback = await CliAsync($"var rollback {set.GetProperty("changeId").GetInt64()} cli undo");
            Assert.That(rollback.GetProperty("surface").GetString(), Is.EqualTo("ServerManagement"));
            Assert.That(rollback.GetProperty("newValue").GetString(), Is.EqualTo("1"));
            await AssertSpeedAsync(300);
            Assert.That(variables.History.Select(h => h.RawCommand), Is.EqualTo(new[]
            {
                "var set world.movement.speed_multiplier 2 cli live socket", "var rollback 1 cli undo"
            }));
        });
    }

    /// <summary>
    /// World-35: the shipped CLI shrinks and restores the station peace radius; the idle TCP client, parked
    /// 800 units from home station 100, decodes the refreshed station and its safe-zone exit and re-entry.
    /// </summary>
    [Test]
    [Explicit(FastEndpointsResolverReason)]
    public Task ServerManagementCli_PeaceRadiusSetAndRollback_RefreshConnectedTcpClientSafeZone()
    {
        var variables = new MemoryVariables();
        var pilot = new WorldPilot(835, "mmo", 1, 1, "ship", 0, 0)
        { X = 2800, Y = 1300, BaseSpeedAtLoad = 300, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };
        return RunWithCliAndSocketAsync(variables, pilot, async (input, CliAsync, ct) =>
        {
            async Task AssertZoneAsync(int radius, byte safe)
            {
                var station = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                Assert.That(station.Command, Is.EqualTo(WorldCommandId.SpaceStationCommand));
                Assert.That(WorldPayload.Decode(station.Payload, r => (WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                    Is.EqualTo((100, 2000, 1300, radius)));
                var zone = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                Assert.That(zone.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
                Assert.That(zone.Payload.ToArray(), Is.EqualTo(new[] { safe }));
            }

            var set = await CliAsync("var set world.station.peace_radius 500 cli shrink peace zone");
            Assert.That(set.GetProperty("surface").GetString(), Is.EqualTo("ServerManagement"));
            Assert.That(set.GetProperty("variableKey").GetString(), Is.EqualTo("world.station.peace_radius"));
            Assert.That(set.GetProperty("previousValue").GetString(), Is.EqualTo("1000"));
            await AssertZoneAsync(500, 0);

            var rollback = await CliAsync($"var rollback {set.GetProperty("changeId").GetInt64()} cli restore zone");
            Assert.That(rollback.GetProperty("newValue").GetString(), Is.EqualTo("1000"));
            await AssertZoneAsync(1000, 1);
            Assert.That(variables.History.Select(h => (h.Surface, h.NewValue)), Is.EqualTo(new[]
            {
                ("ServerManagement", "500"), ("ServerManagement", "1000")
            }));
        });
    }

    /// <summary>
    /// World-47: the shipped CLI against the production <see cref="RuntimeVariableStore"/> on PostgreSQL and
    /// sessions-redis. Rolling back a change that a later change has superseded is rejected (HTTP 409) without
    /// touching the value; rolling back newest-first restores each step on the idle TCP client.
    /// </summary>
    [Test]
    [Category("Integration")]
    [Explicit("Starts Redis and PostgreSQL containers and the PowerShell CLI; run on demand.")]
    public async Task ServerManagementCli_StaleRollback_RealPostgres_IsRejectedAndSocketKeepsLiveValue()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword(Guid.NewGuid().ToString("N")).Build();
        await using var redis = new ContainerBuilder("redis:8.6").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections")).Build();
        await Task.WhenAll(postgres.StartAsync(ct), redis.StartAsync(ct));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(configuration, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
        await using var mux = await ConnectionMultiplexer.ConnectAsync($"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}");
        using var cache = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(mux) }));
        var variables = new RuntimeVariableStore(cache, configuration, new RedisServerMeshMutex(mux));
        var pilot = new WorldPilot(847, "mmo", 1, 1, "ship", 0, 0)
        { X = 2800, Y = 1300, BaseSpeedAtLoad = 300, Stats = new WorldShipStats(300, 1000, 0, 0, 30, []) };

        await RunWithCliAndSocketAsync(variables, pilot, async (input, CliAsync, token) =>
        {
            async Task AssertZoneAsync(int radius, byte safe)
            {
                var station = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), token))!.Value;
                Assert.That(station.Command, Is.EqualTo(WorldCommandId.SpaceStationCommand));
                Assert.That(WorldPayload.Decode(station.Payload, r => (WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                    Is.EqualTo((100, 2000, 1300, radius)));
                var zone = (await input.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), token))!.Value;
                Assert.That(zone.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
                Assert.That(zone.Payload.ToArray(), Is.EqualTo(new[] { safe }));
            }

            var first = await CliAsync("var set world.station.peace_radius 500 world-47 shrink");
            await AssertZoneAsync(500, 0);
            var second = await CliAsync("var set world.station.peace_radius 900 world-47 widen");
            await AssertZoneAsync(900, 1);

            var stale = await CliAsync($"!var rollback {first.GetProperty("changeId").GetInt64()} world-47 stale undo");
            Assert.That(stale.GetProperty("stderr").GetString(), Does.Contain("Rollback conflict"));
            Assert.That((await variables.GetAsync("world.station.peace_radius", token)).CurrentValue, Is.EqualTo("900"));
            // Idle through two polls; the next packets must be the 900 -> 500 rollback below, so a stray
            // station or safe-zone packet from the rejected rollback would fail AssertZoneAsync.
            await Task.Delay(TimeSpan.FromSeconds(2.5), token);

            var undoSecond = await CliAsync($"var rollback {second.GetProperty("changeId").GetInt64()} world-47 undo widen");
            Assert.That(undoSecond.GetProperty("newValue").GetString(), Is.EqualTo("500"));
            await AssertZoneAsync(500, 0);
            var undoFirst = await CliAsync($"var rollback {first.GetProperty("changeId").GetInt64()} world-47 undo shrink");
            Assert.That(undoFirst.GetProperty("newValue").GetString(), Is.EqualTo("1000"));
            await AssertZoneAsync(1000, 1);
        });

        var history = await variables.HistoryAsync("world.station.peace_radius", ct: ct);
        Assert.That(history.Select(h => h.NewValue), Is.EqualTo(new[] { "1000", "500", "900", "500" }),
            "The rejected rollback must not write a durable history row");
        Assert.That(history[0].Reason, Does.StartWith("Rollback of "));
    }

    private sealed class BrowserAuth(bool isAdmin = false) : IGameAuthSessionStore
    {
        public const string Dosid = "world-37-session";
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken ct) =>
            Task.FromResult<GameAuthSession?>(dosid == Dosid ? new GameAuthSession
            {
                AccountId = Guid.Empty, Username = "Live pilot", GameUserId = 837, Company = "mmo",
                Dosid = Dosid, IsAdmin = isAdmin, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            } : null);
        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? token,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string token, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string token, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// World-37: one Kestrel host serves both the backoffice admin route and the real /spacemap/world bridge
    /// over a shared registry. scripts/client-testing/verify-spacemap-live.mjs runs the shipped Spacemap
    /// page in jsdom with real HTTP fetches, invokes the shipped CLI between polls, and reports the
    /// safe-zone text and ship canvas position it rendered from live state responses.
    /// </summary>
    [Test]
    [Explicit(FastEndpointsResolverReason)]
    public async Task ServerManagementCli_PeaceRadiusSetAndRollback_UpdateLiveSpacemapSafeZoneDom()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The management CLI is a Windows PowerShell script.");
        var ct = TestContext.CurrentContext.CancellationToken;
        var variables = new MemoryVariables();
        var pilot = new WorldPilot(837, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
        {
            UserName = "Live pilot", FactionId = 1, BaseSpeedAtLoad = 300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, [])
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddFastEndpoints(options => options.Filter = type => type == typeof(AdminCommandEndpoint));
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite), p => p.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "cli-node" }));
        builder.Services.AddSingleton<IAdminCommandDispatcher>(new VariableDispatcher(variables));
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(variables);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IGameAuthSessionStore, BrowserAuth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(pilot));
        builder.Services.AddSingleton(sp => new WorldSessionStore(sp.GetRequiredService<IDistributedCache>(),
            variables: variables));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.UseFastEndpoints(options =>
        {
            options.Endpoints.RoutePrefix = "api";
            options.Versioning.Prefix = "v";
            options.Versioning.PrependToRoute = true;
        });
        WorldHttpBridge.Map(app);
        // Park the pilot 800 units east of home station 100 (2000,1300): inside radius 1000, outside 500.
        await app.Services.GetRequiredService<IDistributedCache>().SetStringAsync("world:position:837",
            JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing", "verify-spacemap-live.mjs"),
                app.Urls.Single(), BrowserAuth.Dosid })
                start.ArgumentList.Add(argument);
            start.Environment["THDARKORBIT_ADMIN_TOKEN"] = Guid.NewGuid().ToString("N");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(120), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            Assert.That(process.ExitCode, Is.Zero, await stderr);
            var output = await stdout;
            TestContext.Out.WriteLine(output);
            using var json = JsonDocument.Parse(output);
            Assert.That(json.RootElement.GetProperty("observed").EnumerateArray()
                .Take(3).Select(row => row.GetProperty("safeZone").GetString()),
                Is.EqualTo(new[] { "Protected", "Outside (radius 500)", "Protected" }));
            Assert.That(json.RootElement.GetProperty("requests").EnumerateArray()
                .Count(r => r.GetString() == "GET /spacemap/world/state"), Is.GreaterThanOrEqualTo(2));
            Assert.That(json.RootElement.GetProperty("requests").EnumerateArray()
                .Any(r => r.GetString() == "POST /spacemap/world/move"), Is.True);
            var draws = json.RootElement.GetProperty("shipDraws");
            Assert.That(draws.GetProperty("afterMove").GetProperty("x").GetDouble(),
                Is.GreaterThan(draws.GetProperty("beforeMove").GetProperty("x").GetDouble()));
            Assert.That(draws.GetProperty("afterMove").GetProperty("y").GetDouble(),
                Is.GreaterThan(draws.GetProperty("beforeMove").GetProperty("y").GetDouble()));
            Assert.That(variables.History.Select(h => (h.Surface, h.NewValue)), Is.EqualTo(new[]
            {
                ("ServerManagement", "500"), ("ServerManagement", "1000")
            }));
        }
        finally { await app.StopAsync(ct); }
    }

    /// <summary>
    /// Replica A's write path without PostgreSQL. It writes the changed variable to the shared distributed cache
    /// under the same key that <see cref="RuntimeVariableStore"/> writes after its commit. History stays in memory.
    /// </summary>
    private sealed class CacheWritingVariables(IDistributedCache cache, RuntimeVariableStore reader)
        : IRuntimeVariableRegistry
    {
        public readonly List<RuntimeVariableChangeEntry> History = [];
        public IReadOnlyList<RuntimeVariable> Catalog => reader.Catalog;
        public Task<double> NumberAsync(string key, CancellationToken ct = default) => reader.NumberAsync(key, ct);
        public Task<RuntimeVariable> GetAsync(string key, CancellationToken ct = default) => reader.GetAsync(key, ct);

        public async Task<RuntimeVariableChangeEntry> SetAsync(string key, string value, RuntimeVariableActor actor,
            CancellationToken ct = default)
        {
            var current = await reader.GetAsync(key, ct);
            await cache.SetStringAsync($"runtime-variable:v1:{key}",
                JsonSerializer.Serialize(current with { CurrentValue = value }), ct);
            var entry = new RuntimeVariableChangeEntry(History.Count + 1, key, current.Category, current.CurrentValue,
                value, current.DefaultValue, current.ValueType, actor.Surface, actor.ChangedBy, actor.NodeId,
                actor.Reason, actor.RawCommand, DateTimeOffset.UtcNow);
            History.Add(entry);
            return entry;
        }

        public async Task<RuntimeVariableChangeEntry> RollbackAsync(long changeId, RuntimeVariableActor actor,
            CancellationToken ct = default)
        {
            var target = History.Single(h => h.ChangeId == changeId);
            var current = await reader.GetAsync(target.VariableKey, ct);
            if (current.CurrentValue != target.NewValue)
                throw new InvalidOperationException($"Rollback conflict: {target.VariableKey} is now {current.CurrentValue}, not {target.NewValue} set by change {changeId}. Roll back the later change first.");
            return await SetAsync(target.VariableKey, target.PreviousValue, actor, ct);
        }

        public async Task<IReadOnlyList<RuntimeVariable>> ListAsync(CancellationToken ct = default)
        {
            var rows = new List<RuntimeVariable>();
            foreach (var variable in reader.Catalog) rows.Add(await reader.GetAsync(variable.Key, ct));
            return rows;
        }

        public Task<RuntimeVariableChangeEntry> ResetToDefaultAsync(string key, RuntimeVariableActor actor, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeVariableChangeEntry>> HistoryAsync(string? key = null, int limit = 100,
            long? beforeChangeId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeVariableChangeEntry>>(History.AsEnumerable().Reverse()
                .Where(h => (key is null || h.VariableKey == key) && (beforeChangeId is null || h.ChangeId < beforeChangeId))
                .Take(limit).ToList());
    }

    /// <summary>World-44: the shipped Spacemap admin panel edits and rolls back a variable over live HTTP.</summary>
    [Test]
    [Explicit("Spawns node/jsdom against live Kestrel; run on demand.")]
    public async Task InGameAdminPanel_VariableFormAndHistoryRollback_UpdateLiveSpacemap()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "mesh-node-a" }));
        builder.Services.AddSingleton<IGameAuthSessionStore>(new BrowserAuth(true));
        var pilot = new WorldPilot(837, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
        {
            UserName = "Live admin", FactionId = 1, IsAdmin = true, BaseSpeedAtLoad = 300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, [])
        };
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(pilot));
        builder.Services.AddSingleton(sp => new RuntimeVariableStore(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
        builder.Services.AddSingleton<CacheWritingVariables>(sp => new CacheWritingVariables(
            sp.GetRequiredService<IDistributedCache>(), sp.GetRequiredService<RuntimeVariableStore>()));
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(sp => sp.GetRequiredService<CacheWritingVariables>());
        builder.Services.AddSingleton<IAdminCommandDispatcher>(sp =>
            new VariableDispatcher(sp.GetRequiredService<IRuntimeVariableRegistry>()));
        builder.Services.AddSingleton(sp => new WorldSessionStore(sp.GetRequiredService<IDistributedCache>(),
            variables: sp.GetRequiredService<IRuntimeVariableRegistry>()));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        var cache = app.Services.GetRequiredService<IDistributedCache>();
        var registry = app.Services.GetRequiredService<RuntimeVariableStore>();
        foreach (var variable in registry.Catalog)
            await cache.SetStringAsync($"runtime-variable:v1:{variable.Key}", JsonSerializer.Serialize(variable), ct);
        await cache.SetStringAsync("world:position:837", JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);
        await app.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing", "verify-spacemap-admin-live.mjs"),
                app.Urls.Single(), BrowserAuth.Dosid }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            Assert.That(process.ExitCode, Is.Zero, await stderr + output);
            TestContext.Out.WriteLine(output);
            using var result = JsonDocument.Parse(output);
            Assert.That(result.RootElement.GetProperty("setState").GetProperty("zone").GetString(),
                Is.EqualTo("Outside (radius 500)"));
            Assert.That(result.RootElement.GetProperty("rollbackZone").GetString(), Is.EqualTo("Protected"));
            Assert.That(result.RootElement.GetProperty("conflict").GetString(), Does.StartWith("Rollback conflict:"));
            Assert.That(result.RootElement.GetProperty("draws").GetInt32(), Is.GreaterThan(0));
            Assert.That(app.Services.GetRequiredService<CacheWritingVariables>().History
                .Select(h => (h.Surface, h.ChangedBy, h.NodeId, h.PreviousValue, h.NewValue, h.Reason)), Is.EqualTo(new[]
                {
                    ("InGameAdmin", "Live admin:837", "mesh-node-a", "1000", "500", "World-46 safe-zone check"),
                    ("InGameAdmin", "Live admin:837", "mesh-node-a", "500", "1000", "World-46 restore safe zone")
                }));
        }
        finally { await app.StopAsync(ct); }
    }

    /// <summary>
    /// World-39: two Kestrel replicas share nothing except one distributed cache, which stands in for
    /// sessions-redis. Replica A ("mesh-node-a") serves only the backoffice admin route the shipped CLI calls.
    /// Replica B ("mesh-node-b") serves only the real /spacemap/world bridge, using its own production
    /// <see cref="RuntimeVariableStore"/> instance. The shipped Spacemap page polls B while the CLI sets and
    /// rolls back the peace radius on A. B's safe-zone DOM and connected TCP client must follow both changes.
    /// </summary>
    [Test]
    [Explicit(FastEndpointsResolverReason)]
    public async Task ServerManagementCli_OnReplicaA_UpdatesLiveSpacemapSafeZoneDomServedByReplicaB()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The management CLI is a Windows PowerShell script.");
        var ct = TestContext.CurrentContext.CancellationToken;
        using var sharedServices = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var shared = sharedServices.GetRequiredService<IDistributedCache>();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        // Warm the shared cache with every catalog default, as earlier reads on any replica would have.
        // Replica B then never needs PostgreSQL.
        foreach (var variable in new RuntimeVariableStore(shared, configuration).Catalog)
            await shared.SetStringAsync($"runtime-variable:v1:{variable.Key}", JsonSerializer.Serialize(variable), ct);
        var writer = new CacheWritingVariables(shared, new RuntimeVariableStore(shared, configuration));
        var replicaBVariables = new RuntimeVariableStore(shared, configuration);

        var builderA = WebApplication.CreateBuilder();
        builderA.WebHost.UseUrls("http://127.0.0.1:0");
        builderA.Services.AddFastEndpoints(options => options.Filter = type => type == typeof(AdminCommandEndpoint));
        builderA.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builderA.Services.AddAuthorization(options =>
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite), p => p.RequireAuthenticatedUser()));
        builderA.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "mesh-node-a" }));
        builderA.Services.AddSingleton<IAdminCommandDispatcher>(new VariableDispatcher(writer));
        await using var replicaA = builderA.Build();
        replicaA.UseAuthentication(); replicaA.UseAuthorization();
        replicaA.UseFastEndpoints(options =>
        {
            options.Endpoints.RoutePrefix = "api";
            options.Versioning.Prefix = "v";
            options.Versioning.PrependToRoute = true;
        });

        var pilot = new WorldPilot(837, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
        {
            UserName = "Live pilot", FactionId = 1, BaseSpeedAtLoad = 300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, [])
        };
        var builderB = WebApplication.CreateBuilder();
        builderB.WebHost.UseUrls("http://127.0.0.1:0");
        builderB.Services.AddSingleton(shared);
        builderB.Services.AddSingleton<IRuntimeVariableRegistry>(replicaBVariables);
        builderB.Services.AddSingleton<IGameAuthSessionStore, BrowserAuth>();
        builderB.Services.AddSingleton<IWorldPilotResolver>(new Resolver(pilot));
        var replicaBWorld = new WorldSessionStore(shared, variables: replicaBVariables);
        builderB.Services.AddSingleton(replicaBWorld);
        await using var replicaB = builderB.Build();
        WorldHttpBridge.Map(replicaB);
        await shared.SetStringAsync("world:position:837", JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);
        var socketPilot = pilot with { UserId = 838, UserName = "Socket pilot" };
        await shared.SetStringAsync("world:position:838", JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var tcpServer = await listener.AcceptTcpClientAsync(ct);
        listener.Stop();
        var worker = new WorldSessionHandler(new Resolver(socketPilot), new WorldServerOptions(), replicaBWorld,
            variables: replicaBVariables).RunAsync(tcpServer.GetStream());
        var packets = new WorldPacketReader(tcpClient.GetStream());
        var sender = new WorldPacketWriter(tcpClient.GetStream());
        await sender.WriteAsync(WorldCommandId.VersionRequest,
            WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
        await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        await sender.WriteAsync(WorldCommandId.LoginRequest, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, socketPilot.UserId); WorldPayload.WriteString(w, "opaque");
            WorldPayload.WriteString(w, "local-1"); WorldPayload.WriteString(w, "test-instance");
        }));
        for (var i = 0; i < 6; i++) await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        await replicaA.StartAsync(ct);
        await replicaB.StartAsync(ct);
        try
        {
            Assert.That(replicaA.Urls.Single(), Is.Not.EqualTo(replicaB.Urls.Single()));
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing", "verify-spacemap-live.mjs"),
                replicaB.Urls.Single(), BrowserAuth.Dosid, replicaA.Urls.Single() })
                start.ArgumentList.Add(argument);
            start.Environment["THDARKORBIT_ADMIN_TOKEN"] = Guid.NewGuid().ToString("N");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(120), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            Assert.That(process.ExitCode, Is.Zero, await stderr);
            var output = await stdout;
            TestContext.Out.WriteLine(output);
            using var json = JsonDocument.Parse(output);
            Assert.That(json.RootElement.GetProperty("observed").EnumerateArray()
                .Take(3).Select(row => row.GetProperty("safeZone").GetString()),
                Is.EqualTo(new[] { "Protected", "Outside (radius 500)", "Protected" }));
            Assert.That(json.RootElement.GetProperty("cliTarget").GetString(), Is.EqualTo(replicaA.Urls.Single()));
            Assert.That(json.RootElement.GetProperty("requests").EnumerateArray()
                .Any(r => r.GetString() == "POST /spacemap/world/move"), Is.True);
            async Task AssertSocketZoneAsync(int radius, byte safe)
            {
                WorldPacket? station = null;
                for (var i = 0; i < 8; i++)
                {
                    var packet = (await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                    if (packet.Command == WorldCommandId.SpaceStationCommand) { station = packet; break; }
                }
                Assert.That(station, Is.Not.Null, "TCP client did not receive the station update");
                Assert.That(WorldPayload.Decode(station.Value.Payload, r => (WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                    Is.EqualTo((100, 2000, 1300, radius)));
                var zone = (await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                Assert.That(zone.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
                Assert.That(zone.Payload.ToArray(), Is.EqualTo(new[] { safe }));
            }
            await AssertSocketZoneAsync(500, 0);
            await AssertSocketZoneAsync(1000, 1);
            Assert.That(writer.History.Select(h => (h.Surface, h.NodeId, h.PreviousValue, h.NewValue)), Is.EqualTo(new[]
            {
                ("ServerManagement", "mesh-node-a", "1000", "500"), ("ServerManagement", "mesh-node-a", "500", "1000")
            }));
            Assert.That(await replicaBVariables.NumberAsync("world.station.peace_radius", ct), Is.EqualTo(1000));
        }
        finally
        {
            tcpClient.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await replicaB.StopAsync(ct);
            await replicaA.StopAsync(ct);
        }
    }

    /// <summary>
    /// World-41: the Angular CMS surface. Replica A serves the real runtime-variable list, history, set and rollback
    /// endpoints. The shipped RuntimeVarsComponent runs under vitest/jsdom with its real BackofficeApiService and
    /// HttpClient and is driven by DOM input and button clicks. A connected WorldServer TCP client on replica B must
    /// decode the peace-radius change and its rollback through the shared distributed cache. The shipped
    /// Spacemap page on B must also render both transitions from live HTTP state polls.
    /// </summary>
    [Test]
    [Explicit(FastEndpointsResolverReason)]
    public async Task AdminCms_RuntimeVarsEditorOnReplicaA_UpdatesLiveSocketClientOnReplicaB()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The CMS live run is wired through npx.cmd on Windows.");
        var ct = TestContext.CurrentContext.CancellationToken;
        using var sharedServices = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var shared = sharedServices.GetRequiredService<IDistributedCache>();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        foreach (var variable in new RuntimeVariableStore(shared, configuration).Catalog)
            await shared.SetStringAsync($"runtime-variable:v1:{variable.Key}", JsonSerializer.Serialize(variable), ct);
        var writer = new CacheWritingVariables(shared, new RuntimeVariableStore(shared, configuration));
        var replicaBVariables = new RuntimeVariableStore(shared, configuration);

        var builderA = WebApplication.CreateBuilder();
        builderA.WebHost.UseUrls("http://127.0.0.1:0");
        builderA.Services.AddFastEndpoints(options => options.Filter = type =>
            type == typeof(ListRuntimeVariablesEndpoint) || type == typeof(RuntimeVariableHistoryEndpoint) ||
            type == typeof(SetRuntimeVariableEndpoint) || type == typeof(RollbackRuntimeVariableEndpoint));
        builderA.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builderA.Services.AddAuthorization(options =>
        {
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead), p => p.RequireAuthenticatedUser());
            options.AddPolicy(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite), p => p.RequireAuthenticatedUser());
        });
        builderA.Services.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { NodeId = "mesh-node-a" }));
        builderA.Services.AddSingleton<IRuntimeVariableRegistry>(writer);
        builderA.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        await using var replicaA = builderA.Build();
        replicaA.UseCors(); replicaA.UseAuthentication(); replicaA.UseAuthorization();
        replicaA.UseFastEndpoints(options =>
        {
            options.Endpoints.RoutePrefix = "api";
            options.Versioning.Prefix = "v";
            options.Versioning.PrependToRoute = true;
        });

        var socketPilot = new WorldPilot(839, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
        {
            UserName = "Socket pilot", FactionId = 1, BaseSpeedAtLoad = 300,
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, [])
        };
        var replicaBWorld = new WorldSessionStore(shared, variables: replicaBVariables);
        var browserPilot = socketPilot with { UserId = 837, UserName = "Live pilot" };
        var builderB = WebApplication.CreateBuilder();
        builderB.WebHost.UseUrls("http://127.0.0.1:0");
        builderB.Services.AddSingleton(shared);
        builderB.Services.AddSingleton<IRuntimeVariableRegistry>(replicaBVariables);
        builderB.Services.AddSingleton<IGameAuthSessionStore, BrowserAuth>();
        builderB.Services.AddSingleton<IWorldPilotResolver>(new Resolver(browserPilot));
        builderB.Services.AddSingleton(replicaBWorld);
        await using var replicaB = builderB.Build();
        WorldHttpBridge.Map(replicaB);
        await shared.SetStringAsync("world:position:837", JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);
        await shared.SetStringAsync("world:position:839", JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var tcpServer = await listener.AcceptTcpClientAsync(ct);
        listener.Stop();
        var worker = new WorldSessionHandler(new Resolver(socketPilot), new WorldServerOptions(), replicaBWorld,
            variables: replicaBVariables).RunAsync(tcpServer.GetStream());
        var packets = new WorldPacketReader(tcpClient.GetStream());
        var sender = new WorldPacketWriter(tcpClient.GetStream());
        await sender.WriteAsync(WorldCommandId.VersionRequest,
            WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
        await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        await sender.WriteAsync(WorldCommandId.LoginRequest, WorldPayload.Encode(w =>
        {
            WorldPayload.WriteInt64(w, socketPilot.UserId); WorldPayload.WriteString(w, "opaque");
            WorldPayload.WriteString(w, "local-1"); WorldPayload.WriteString(w, "test-instance");
        }));
        for (var i = 0; i < 6; i++) await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
        await replicaA.StartAsync(ct);
        await replicaB.StartAsync(ct);
        try
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var browserStart = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing", "verify-spacemap-cms-live.mjs"),
                replicaB.Urls.Single(), BrowserAuth.Dosid })
                browserStart.ArgumentList.Add(argument);
            using var browser = Process.Start(browserStart)!;
            var browserStdout = browser.StandardOutput.ReadToEndAsync(ct);
            var browserStderr = browser.StandardError.ReadToEndAsync(ct);
            try
            {
                // Give the browser process time to enter the world before the CMS changes the radius.
                await Task.Delay(1000, ct);
                var start = new ProcessStartInfo("cmd.exe")
                {
                    WorkingDirectory = Path.Combine(root, "ThDarkOrbitEmulator.Cms"), UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                foreach (var argument in new[] { "/c", "npx", "ng", "test", "--watch=false",
                    "--include=src/app/features/backoffice/runtime-vars/runtime-vars.component.live.spec.ts" })
                    start.ArgumentList.Add(argument);
                start.Environment["CMS_LIVE_API_BASE"] = replicaA.Urls.Single() + "/api/v1";
                start.Environment["NO_COLOR"] = "1";
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(180), ct); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
                var output = await stdout + await stderr;
                TestContext.Out.WriteLine(output);
                Assert.That(process.ExitCode, Is.Zero, output);
                Assert.That(output, Does.Contain("1 passed"), "The live CMS spec must run, not skip.");
                try { await browser.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(100), ct); }
                catch (TimeoutException) { browser.Kill(entireProcessTree: true); throw; }
                var browserOutput = await browserStdout;
                Assert.That(browser.ExitCode, Is.Zero, await browserStderr + browserOutput);
                TestContext.Out.WriteLine(browserOutput);
                using var browserResult = JsonDocument.Parse(browserOutput);
                Assert.That(browserResult.RootElement.GetProperty("observed").EnumerateArray()
                    .Select(row => row.GetProperty("safeZone").GetString()),
                    Is.EqualTo(new[] { "Protected", "Outside (radius 500)", "Protected" }));
                Assert.That(browserResult.RootElement.GetProperty("shipCanvasDraws").GetInt32(), Is.GreaterThan(0));
            }
            finally
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
            }

            async Task AssertSocketZoneAsync(int radius, byte safe)
            {
                WorldPacket? station = null;
                for (var i = 0; i < 8; i++)
                {
                    var packet = (await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                    if (packet.Command == WorldCommandId.SpaceStationCommand) { station = packet; break; }
                }
                Assert.That(station, Is.Not.Null, "TCP client did not receive the station update");
                Assert.That(WorldPayload.Decode(station.Value.Payload, r => (WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                    Is.EqualTo((100, 2000, 1300, radius)));
                var zone = (await packets.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                Assert.That(zone.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
                Assert.That(zone.Payload.ToArray(), Is.EqualTo(new[] { safe }));
            }
            await AssertSocketZoneAsync(500, 0);
            await AssertSocketZoneAsync(1000, 1);
            Assert.That(writer.History.Select(h => (h.Surface, h.NodeId, h.PreviousValue, h.NewValue, h.Reason)), Is.EqualTo(new[]
            {
                ("AdminCms", "mesh-node-a", "1000", "500", "World-41 CMS live check"),
                ("AdminCms", "mesh-node-a", "500", "1000", "World-41 CMS live check")
            }));
            Assert.That(await replicaBVariables.NumberAsync("world.station.peace_radius", ct), Is.EqualTo(1000));
        }
        finally
        {
            tcpClient.Client.Shutdown(SocketShutdown.Send);
            await worker.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await replicaB.StopAsync(ct);
            await replicaA.StopAsync(ct);
        }
    }

    /// <summary>
    /// World-43: the third admin surface. An admin pilot's WorldServer TCP socket on replica A sends provisional
    /// command 32 slash commands (/var set, /var rollback). The dispatcher on A writes through the shared
    /// distributed cache. A connected TCP client on replica B and the shipped Spacemap page polling B over live
    /// HTTP must both follow the change and its rollback.
    /// </summary>
    [Test]
    [Explicit("Spawns node/jsdom against live Kestrel and loopback sockets; run on demand.")]
    public async Task InGameAdminSocket_OnReplicaA_UpdatesLiveSpacemapAndSocketClientOnReplicaB()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        using var sharedServices = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var shared = sharedServices.GetRequiredService<IDistributedCache>();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        foreach (var variable in new RuntimeVariableStore(shared, configuration).Catalog)
            await shared.SetStringAsync($"runtime-variable:v1:{variable.Key}", JsonSerializer.Serialize(variable), ct);
        var writer = new CacheWritingVariables(shared, new RuntimeVariableStore(shared, configuration));
        await RunInGameAdminAcrossReplicasAsync(shared, shared, writer, new RuntimeVariableStore(shared, configuration),
            new RuntimeVariableStore(shared, configuration), "World-43",
            token => writer.HistoryAsync("world.station.peace_radius", ct: token), ct);
    }

    /// <summary>
    /// World-45: the World-43 in-game admin check without stand-ins. Each replica owns its own StackExchange.Redis
    /// connection to a real Redis container and its own production <see cref="RuntimeVariableStore"/> on a real
    /// PostgreSQL container, so the admin socket's set/rollback runs <c>ChangeAsync</c> (advisory lock, history
    /// insert, commit, Redis mesh mutex) on replica A and replica B reads only through Redis/PostgreSQL.
    /// </summary>
    [Test]
    [Category("Integration")]
    [Explicit("Starts Redis and PostgreSQL containers, node/jsdom and loopback sockets; run on demand.")]
    public async Task InGameAdminSocket_OnReplicaA_RealRedisAndPostgres_UpdatesLiveSpacemapAndSocketOnReplicaB()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword(Guid.NewGuid().ToString("N")).Build();
        await using var redis = new ContainerBuilder("redis:8.6").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections")).Build();
        await Task.WhenAll(postgres.StartAsync(ct), redis.StartAsync(ct));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:identity-write"] = postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(configuration, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);

        var redisEndpoint = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";
        await using var muxA = await ConnectionMultiplexer.ConnectAsync(redisEndpoint);
        await using var muxB = await ConnectionMultiplexer.ConnectAsync(redisEndpoint);
        using var cacheA = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(muxA) }));
        using var cacheB = new RedisCache(Options.Create(new RedisCacheOptions
        { ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(muxB) }));
        var replicaAVariables = new RuntimeVariableStore(cacheA, configuration, new RedisServerMeshMutex(muxA));
        var replicaBVariables = new RuntimeVariableStore(cacheB, configuration, new RedisServerMeshMutex(muxB));

        await RunInGameAdminAcrossReplicasAsync(cacheA, cacheB, replicaAVariables, replicaAVariables, replicaBVariables,
            "World-45", token => replicaBVariables.HistoryAsync("world.station.peace_radius", ct: token), ct);

        // The rows are durable PostgreSQL history, not cache entries: read them back over a fresh connection.
        await using var db = new NpgsqlConnection(postgres.GetConnectionString());
        await db.OpenAsync(ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM identity.runtime_variable_history WHERE variable_key='world.station.peace_radius'", db);
        Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture), Is.EqualTo(2));
        Assert.That(await muxB.GetDatabase().KeyExistsAsync("runtime-variable:v1:world.station.peace_radius"), Is.True,
            "Replica A's commit should publish the variable to sessions-redis");
    }

    private static async Task RunInGameAdminAcrossReplicasAsync(IDistributedCache cacheA, IDistributedCache cacheB,
        IRuntimeVariableRegistry writer, RuntimeVariableStore replicaAVariables, RuntimeVariableStore replicaBVariables,
        string label, Func<CancellationToken, Task<IReadOnlyList<RuntimeVariableChangeEntry>>> history,
        CancellationToken ct)
    {

        var replicaAWorld = new WorldSessionStore(cacheA, variables: replicaAVariables);
        var replicaBWorld = new WorldSessionStore(cacheB, variables: replicaBVariables);

        var basePilot = new WorldPilot(837, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
        {
            UserName = "Live pilot", FactionId = 1, BaseSpeedAtLoad = 300, AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 0, 0, 30, [])
        };
        var adminPilot = basePilot with { UserId = 840, UserName = "Admin pilot", IsAdmin = true };
        var observerPilot = basePilot with { UserId = 841, UserName = "Socket pilot" };
        foreach (var id in new[] { 837, 840, 841 })
            await cacheA.SetStringAsync($"world:position:{id}", JsonSerializer.Serialize(new WorldPosition(1, 2800, 1300)), ct);

        var builderB = WebApplication.CreateBuilder();
        builderB.WebHost.UseUrls("http://127.0.0.1:0");
        builderB.Services.AddSingleton(cacheB);
        builderB.Services.AddSingleton<IRuntimeVariableRegistry>(replicaBVariables);
        builderB.Services.AddSingleton<IGameAuthSessionStore, BrowserAuth>();
        builderB.Services.AddSingleton<IWorldPilotResolver>(new Resolver(basePilot));
        builderB.Services.AddSingleton(replicaBWorld);
        var pickupMutex = new TestNpcMutex();
        var pickupWallet = new PickupWallet();
        var adminEvents = new WorldAdminEvents(cacheB, pickupMutex);
        await adminEvents.SpawnAsync("box", 1, 3100, 1300, ct);
        builderB.Services.AddSingleton(new WorldCollectibles(cacheB, pickupMutex, pickupWallet,
            TimeProvider.System, replicaBVariables, adminEvents));
        await using var replicaB = builderB.Build();
        WorldHttpBridge.Map(replicaB);

        async Task<(TcpClient Client, Task Worker, WorldPacketReader Reader, WorldPacketWriter Writer)> ConnectAsync(
            WorldPilot pilot, WorldSessionHandler handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct);
            listener.Stop();
            var worker = handler.RunAsync(server.GetStream()).ContinueWith(_ => server.Dispose(), TaskScheduler.Default);
            var reader = new WorldPacketReader(client.GetStream());
            var sender = new WorldPacketWriter(client.GetStream());
            await sender.WriteAsync(WorldCommandId.VersionRequest,
                WorldPayload.Encode(w => WorldPayload.WriteString(w, "local-1")));
            await reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
            await sender.WriteAsync(WorldCommandId.LoginRequest, WorldPayload.Encode(w =>
            {
                WorldPayload.WriteInt64(w, pilot.UserId); WorldPayload.WriteString(w, "opaque");
                WorldPayload.WriteString(w, "local-1"); WorldPayload.WriteString(w, "test-instance");
            }));
            for (var i = 0; i < 6; i++) await reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct);
            return (client, worker, reader, sender);
        }

        var admin = await ConnectAsync(adminPilot, new WorldSessionHandler(new Resolver(adminPilot),
            new WorldServerOptions(), replicaAWorld, localNodeId: "mesh-node-a", variables: replicaAVariables,
            adminCommands: new VariableDispatcher(writer)));
        var observer = await ConnectAsync(observerPilot, new WorldSessionHandler(new Resolver(observerPilot),
            new WorldServerOptions(), replicaBWorld, variables: replicaBVariables));
        await replicaB.StartAsync(ct);
        long setChangeId = 0;
        try
        {
            async Task<JsonElement> AdminAsync(string commandText)
            {
                await admin.Writer.WriteAsync(WorldCommandId.AdminCommandRequest,
                    WorldPayload.Encode(w => WorldPayload.WriteString(w, commandText)));
                for (var i = 0; i < 16; i++)
                {
                    var packet = (await admin.Reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                    if (packet.Command != WorldCommandId.AdminCommandResponse) continue;
                    var (ok, body) = WorldPayload.Decode(packet.Payload, r =>
                    {
                        var success = r.ReadByte() == 1;
                        return (success, success ? WorldPayload.ReadAdminResult(r) : WorldPayload.ReadString(r));
                    });
                    Assert.That(ok, Is.True, body);
                    return JsonDocument.Parse(body).RootElement.Clone();
                }
                Assert.Fail("Admin socket did not receive AdminCommandResponse (33).");
                return default;
            }

            async Task AssertSocketZoneAsync(int radius, byte safe)
            {
                WorldPacket? station = null;
                for (var i = 0; i < 8; i++)
                {
                    var packet = (await observer.Reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                    if (packet.Command == WorldCommandId.SpaceStationCommand) { station = packet; break; }
                }
                Assert.That(station, Is.Not.Null, "Replica B TCP client did not receive the station update");
                Assert.That(WorldPayload.Decode(station.Value.Payload, r => (WorldPayload.ReadInt32(r),
                    WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r), WorldPayload.ReadInt32(r))),
                    Is.EqualTo((100, 2000, 1300, radius)));
                var zone = (await observer.Reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), ct))!.Value;
                Assert.That(zone.Command, Is.EqualTo(WorldCommandId.SafeZoneCommand));
                Assert.That(zone.Payload.ToArray(), Is.EqualTo(new[] { safe }));
            }

            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var browserStart = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing", "verify-spacemap-cms-live.mjs"),
                replicaB.Urls.Single(), BrowserAuth.Dosid, "InGameAdmin", "pickup" })
                browserStart.ArgumentList.Add(argument);
            using var browser = Process.Start(browserStart)!;
            var browserStdout = browser.StandardOutput.ReadToEndAsync(ct);
            var browserStderr = browser.StandardError.ReadToEndAsync(ct);
            try
            {
                // Let the page enter and render "Protected" before the admin shrinks the radius.
                await Task.Delay(1500, ct);
                var set = await AdminAsync($"/var set world.station.peace_radius 500 {label} in-game live check");
                TestContext.Out.WriteLine(set.GetRawText());
                await AssertSocketZoneAsync(500, 0);
                // The page polls every ~150 ms; give it time to render the outside state before rollback.
                await Task.Delay(1500, ct);
                setChangeId = (await history(ct)).Max(h => h.ChangeId);
                var rollback = await AdminAsync($"/var rollback {setChangeId} {label} in-game live check");
                TestContext.Out.WriteLine(rollback.GetRawText());
                await AssertSocketZoneAsync(1000, 1);
                await Task.Delay(700, ct);
                var pickupSet = await AdminAsync($"/var set world.collectibles.pickup_radius 400 {label} pickup range check");
                TestContext.Out.WriteLine(pickupSet.GetRawText());
                await Task.Delay(1500, ct);
                var pickupChangeId = (await writer.HistoryAsync("world.collectibles.pickup_radius", ct: ct))
                    .Max(h => h.ChangeId);
                var pickupRollback = await AdminAsync($"/var rollback {pickupChangeId} {label} pickup range check");
                TestContext.Out.WriteLine(pickupRollback.GetRawText());
                try { await browser.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(100), ct); }
                catch (TimeoutException) { browser.Kill(entireProcessTree: true); throw; }
                var browserOutput = await browserStdout;
                Assert.That(browser.ExitCode, Is.Zero, await browserStderr + browserOutput);
                TestContext.Out.WriteLine(browserOutput);
                using var browserResult = JsonDocument.Parse(browserOutput);
                Assert.That(browserResult.RootElement.GetProperty("observed").EnumerateArray()
                    .Select(row => (row.GetProperty("step").GetString(), row.GetProperty("safeZone").GetString())),
                    Is.EqualTo(new[]
                    {
                        ("entered", "Protected"), ("InGameAdmin set", "Outside (radius 500)"),
                        ("InGameAdmin rollback", "Protected")
                    }));
                Assert.That(browserResult.RootElement.GetProperty("shipCanvasDraws").GetInt32(), Is.GreaterThan(0));
                Assert.That(browserResult.RootElement.GetProperty("observedPickupRange").EnumerateArray()
                    .Select(value => value.GetString()), Is.EqualTo(new[] { "180 units", "400 units", "180 units" }));
                Assert.That(browserResult.RootElement.GetProperty("pickupDistance").GetDouble(),
                    Is.GreaterThan(180).And.LessThanOrEqualTo(400));
                Assert.That(browserResult.RootElement.GetProperty("collectStatus").GetInt32(), Is.EqualTo(200));
                Assert.That(pickupWallet.Credits, Is.EqualTo(1));
                Assert.That(await replicaBVariables.NumberAsync("world.collectibles.pickup_radius", ct), Is.EqualTo(180));
            }
            finally
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
            }

            var reason = $"{label} in-game live check";
            // The production store records which change was rolled back; the World-43 stand-in does not.
            var rollbackReason = writer is RuntimeVariableStore ? $"Rollback of {setChangeId}: {reason}" : reason;
            Assert.That((await history(ct)).OrderBy(h => h.ChangeId).Select(h => (h.Surface, h.ChangedBy, h.NodeId,
                h.PreviousValue, h.NewValue, h.Reason, h.RawCommand)), Is.EqualTo(new[]
            {
                ("InGameAdmin", "Admin pilot:840", "mesh-node-a", "1000", "500", reason,
                    $"var set world.station.peace_radius 500 {reason}"),
                ("InGameAdmin", "Admin pilot:840", "mesh-node-a", "500", "1000", rollbackReason,
                    $"var rollback {setChangeId} {reason}")
            }));
            Assert.That(await replicaBVariables.NumberAsync("world.station.peace_radius", ct), Is.EqualTo(1000));
        }
        finally
        {
            foreach (var (client, worker, _, _) in new[] { admin, observer })
            {
                client.Client.Shutdown(SocketShutdown.Send);
                await worker.WaitAsync(TimeSpan.FromSeconds(5), ct);
                client.Dispose();
            }
            await replicaB.StopAsync(ct);
        }
    }

    /// <summary>
    /// World-57: the Spacemap page's real <c>GET /spacemap/world/state</c> route served by Kestrel reads NPCs through
    /// the per-node observer snapshot. Concurrent browser polls share one NPC cache read, and a hit applied by a
    /// second replica on the same shared cache appears in the polled JSON once the snapshot expires. The shipped
    /// Spacemap page then renders an NPC count change after a remote kill over live HTTP.
    /// </summary>
    [Test]
    public async Task SpacemapStatePolls_ShareNpcSnapshotAndShowRemoteReplicaDamageAfterExpiry()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        using var sharedServices = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var shared = sharedServices.GetRequiredService<IDistributedCache>();
        var counting = new NpcReadCountingCache(shared);
        var mutex = new TestNpcMutex();
        var clock = new NpcSnapshotClock();
        var npcOptions = Options.Create(new WorldNpcOptions
            { Templates = [new("Streuner", "npc_streuner", 2, 100000, 20000, 400, 1, 400, 2, 0)] });
        var pilot = new WorldPilot(837, "mmo", 1, 1, "ship_pusat-plus", 0, 0)
        {
            UserName = "Live pilot", FactionId = 1, BaseSpeedAtLoad = 300, AccountId = Guid.NewGuid(),
            Stats = new WorldShipStats(300, 1000, 0, 100, 100, [])
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDistributedCache>(counting);
        builder.Services.AddSingleton<IGameAuthSessionStore, BrowserAuth>();
        builder.Services.AddSingleton<IWorldPilotResolver>(new Resolver(pilot));
        builder.Services.AddSingleton<IRuntimeVariableRegistry>(new MemoryVariables());
        builder.Services.AddSingleton(new WorldSessionStore(shared, mutex));
        builder.Services.AddSingleton(new WorldNpcs(counting, mutex, new TestNpcRewards(), npcOptions, clock));
        await using var app = builder.Build();
        WorldHttpBridge.Map(app);
        app.MapPost("/test/remote-kill/{id:int}", async (int id) =>
        {
            var remote = new WorldNpcs(shared, mutex, new TestNpcRewards(), npcOptions, clock);
            var npc = (await remote.VisibleAsync(1, ct)).Single(n => n.Id == id);
            var attacker = pilot with { UserId = 900, X = npc.X, Y = npc.Y,
                Stats = new WorldShipStats(300, 1000, 0, 100, 200000, []) };
            var hit = await remote.AttackAsync(attacker, id, new WorldSessionStore(shared, mutex), ct);
            if (hit.Destroyed) clock.Advance(WorldNpcs.ObserverSnapshotTtl + TimeSpan.FromMilliseconds(100));
            return hit.Destroyed ? Results.Ok() : Results.Conflict(hit.Status);
        });
        await app.StartAsync(ct);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
                { BaseAddress = new Uri(app.Urls.Single()) };
            http.DefaultRequestHeaders.Add("Cookie", $"dosid={BrowserAuth.Dosid}");
            async Task<JsonElement[]> PollAsync()
            {
                using var response = await http.GetAsync("/spacemap/world/state", ct);
                Assert.That(response.IsSuccessStatusCode, Is.True, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return json.RootElement.GetProperty("npcs").EnumerateArray().Select(e => e.Clone()).ToArray();
            }
            await PollAsync();
            counting.NpcReads = 0;
            clock.Advance(WorldNpcs.ObserverSnapshotTtl + TimeSpan.FromMilliseconds(100));
            var polls = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PollAsync()));
            Assert.That(polls.All(p => p.Length == 2), Is.True);
            Assert.That(counting.NpcReads, Is.EqualTo(2), "Six browser polls should share one snapshot (two NPCs).");

            var target = polls[0][0];
            var remoteReplica = new WorldNpcs(shared, mutex, new TestNpcRewards(), npcOptions, clock);
            var attacker = pilot with { UserId = 900, X = target.GetProperty("x").GetInt32(),
                Y = target.GetProperty("y").GetInt32() };
            var hit = await remoteReplica.AttackAsync(attacker, target.GetProperty("id").GetInt32(),
                new WorldSessionStore(shared, mutex), ct);
            Assert.That(hit.Status, Is.EqualTo("hit"));
            clock.Advance(WorldNpcs.ObserverSnapshotTtl + TimeSpan.FromMilliseconds(100));
            var after = (await PollAsync()).Single(n => n.GetProperty("id").GetInt32() == hit.Target!.Id);
            Assert.That(after.GetProperty("shield").GetInt32(), Is.EqualTo(hit.Target!.Shield));
            Assert.That(after.GetProperty("hp").GetInt32(), Is.EqualTo(hit.Target!.Hp));

            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { Path.Combine(root, "scripts", "client-testing",
                "verify-spacemap-npc-live.mjs"), app.Urls.Single(), BrowserAuth.Dosid })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
            Assert.That(process.ExitCode, Is.Zero, await stderr);
            TestContext.Out.WriteLine(await stdout);
        }
        finally { await app.StopAsync(ct); }
    }

    private sealed class NpcSnapshotClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan interval) => Interlocked.Add(ref ticks, interval.Ticks);
    }

    private sealed class NpcReadCountingCache(IDistributedCache inner) : IDistributedCache
    {
        public int NpcReads;
        private void Count(string key)
        { if (key.StartsWith("world:npc:", StringComparison.Ordinal)) Interlocked.Increment(ref NpcReads); }
        public byte[]? Get(string key) { Count(key); return inner.Get(key); }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        { Count(key); return inner.GetAsync(key, token); }
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default) => inner.SetAsync(key, value, options, token);
    }
}
