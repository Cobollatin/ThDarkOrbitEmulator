using FastEndpoints;
using NetArchTest.Rules;
using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.ApiService.Features.Identity.Login;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class ApiServiceVerticalSliceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void WorldPilotResolver_GuardsSessionBeforeReadingAccountDetails()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var start = source.IndexOf("public sealed class WorldPilotResolver(", StringComparison.Ordinal);
        var end = source.IndexOf("public sealed class WorldSessionHandler(", start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var resolver = source[start..end];
        var stages = new[]
        {
            "string.IsNullOrWhiteSpace(dosid) || dosid.Length > 128",
            "sessions.TryGetByDosidAsync(dosid, ct)",
            "if (session is null)",
            "session.ExpiresAt <= DateTimeOffset.UtcNow",
            "session.GameUserId != userId || userId <= 0",
            "CompanyCatalog.Find(session.Company)",
            "if (company is null)",
            "state.GetAsync(session, ct)",
            "state.DetailsAsync(session, account.Slot, account.ShipId, ct)"
        };
        var positions = stages.Select(stage => resolver.IndexOf(stage, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered);
            Assert.That(resolver, Does.Not.Contain("HttpContext").And.Not.Contain("DbContext"));
        });
    }

    [Test]
    public void WorldHttpMutations_UseOneExactJsonMediaTypeBoundary()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldHttpBridge.cs"));
        var helper = source[source.IndexOf("private static bool JsonRequest(", StringComparison.Ordinal)..
            source.IndexOf("private static async Task WriteStateAsync", StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(source, @"if \(!JsonRequest\(context\)\)").Count, Is.EqualTo(8));
            Assert.That(helper, Does.Contain("mediaType.Trim().Equals(\"application/json\", StringComparison.OrdinalIgnoreCase)"));
            Assert.That(helper, Does.Not.Contain("StartsWith"));
        });
    }

    [Test]
    public void WorldEntryBurst_UsesOneWorldSliceEmitterForLoginAndJump()
    {
        var entry = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldEntryPackets.cs"));
        var session = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var stages = new[]
        {
            "MapInitializationCommand", "SpaceStationCommand", "foreach (var portal in map.Portals)",
            "JumpgateCreateCommand", "SafeZoneCommand", "HeroInitCommand"
        };
        var positions = stages.Select(stage => entry.IndexOf(stage, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered);
            Assert.That(Regex.Matches(session, @"WorldEntryPackets\.SendAsync\(").Count, Is.EqualTo(3));
            Assert.That(session, Does.Not.Contain("WriteAsync(WorldCommandId.HeroInitCommand"));
            Assert.That(entry, Does.Not.Contain("HttpContext").And.Not.Contain("IDistributedCache"));
        });
    }

    [Test]
    public void WorldEntryBurst_WidensCoordinatesBeforeSafeZoneDistanceArithmetic()
    {
        var entry = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldEntryPackets.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(entry, Does.Contain("(Int128)pilotX - map.HomeStation.X"));
            Assert.That(entry, Does.Contain("(Int128)pilotY - map.HomeStation.Y"));
            Assert.That(entry, Does.Contain("dx * dx + dy * dy <= radius * radius"));
        });
    }

    [Test]
    public void WorldListener_ValidatesBeforeEnabledGateAndBindsLoopbackOnly()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldServerHostedService.cs"));
        var execute = source[source.IndexOf("protected override async Task ExecuteAsync", StringComparison.Ordinal)..
            source.IndexOf("private async Task ServeAsync", StringComparison.Ordinal)];
        var stages = new[]
        {
            "options.Validate()", "if (!options.Enabled) return;",
            "options.DynamicTcpPort && options.TryGetPortRange(out var first, out var last)",
            "for (var port = first; port <= last; port++)",
            "when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)",
            "if (listener is null) throw new InvalidOperationException",
            "options.DynamicTcpPort ? 0 : options.Port",
            "options.Port = ((IPEndPoint)listener.LocalEndpoint).Port", "ActualPort = options.Port",
            "AcceptTcpClientAsync(stoppingToken)", "finally { listener.Stop(); }"
        };
        var positions = stages.Select(stage => execute.IndexOf(stage, StringComparison.Ordinal)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered, "validation precedes the enabled gate; the bound port is published before accepting");
            Assert.That(Regex.Matches(execute, @"new TcpListener\(IPAddress\.Loopback,").Count, Is.EqualTo(2));
            Assert.That(source, Does.Not.Contain("IPAddress.Any").And.Not.Contain("IPAddress.IPv6Any")
                .And.Not.Contain("IPAddress.Parse"));
            Assert.That(Regex.Matches(source, @"\.Validate\(\)").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void WorldListener_ClosesSocketBeforeWaitingForBackgroundShutdown()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldServerHostedService.cs"));
        var stop = source[source.IndexOf("public override async Task StopAsync", StringComparison.Ordinal)..];
        Assert.Multiple(() =>
        {
            Assert.That(stop, Does.Contain("listener?.Stop();"));
            Assert.That(stop.IndexOf("listener?.Stop();", StringComparison.Ordinal),
                Is.LessThan(stop.IndexOf("await base.StopAsync(cancellationToken)", StringComparison.Ordinal)));
            Assert.That(source, Does.Contain("public int ActualPort { get; private set; }"));
        });
    }

    [Test]
    public void WorldListener_DynamicBindingIgnoresConfiguredPortUntilSocketPublishesActualPort()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldServerHostedService.cs"));
        var execute = source[source.IndexOf("protected override async Task ExecuteAsync", StringComparison.Ordinal)..
            source.IndexOf("private async Task ServeAsync", StringComparison.Ordinal)];
        var range = execute.IndexOf("for (var port = first; port <= last; port++)", StringComparison.Ordinal);
        var unrestricted = execute.IndexOf("options.DynamicTcpPort ? 0 : options.Port", StringComparison.Ordinal);
        var publish = execute.IndexOf("options.Port = ((IPEndPoint)listener.LocalEndpoint).Port", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(range, Is.GreaterThanOrEqualTo(0));
            Assert.That(unrestricted, Is.GreaterThan(range));
            Assert.That(publish, Is.GreaterThan(unrestricted));
            Assert.That(Regex.Matches(execute, @"options\.Port\s*=").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void WorldListener_HandlesCancellationOnlyForItsPendingAcceptAndAlwaysClosesSocket()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldServerHostedService.cs"));
        var execute = source[source.IndexOf("protected override async Task ExecuteAsync", StringComparison.Ordinal)..
            source.IndexOf("private async Task ServeAsync", StringComparison.Ordinal)];
        var accept = execute[execute.IndexOf("logger.LogInformation(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(accept, Does.Contain("AcceptTcpClientAsync(stoppingToken)"));
            Assert.That(accept, Does.Contain("catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)"));
            Assert.That(accept, Does.Contain("catch (SocketException) when (stopping)"));
            Assert.That(accept, Does.Contain("catch (ObjectDisposedException) when (stopping)"));
            Assert.That(accept, Does.Contain("finally { listener.Stop(); }"));
            Assert.That(accept.IndexOf("AcceptTcpClientAsync(stoppingToken)", StringComparison.Ordinal),
                Is.LessThan(accept.IndexOf("finally { listener.Stop(); }", StringComparison.Ordinal)));
            Assert.That(accept, Does.Not.Contain("catch (Exception)"));
            var stop = source[source.IndexOf("public override async Task StopAsync", StringComparison.Ordinal)..];
            Assert.That(stop.IndexOf("stopping = true;", StringComparison.Ordinal),
                Is.LessThan(stop.IndexOf("listener?.Stop();", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void WorldListener_RangeRetryReleasesOnlyOccupiedCandidateAndNeverFallsBackToEphemeralPort()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldServerHostedService.cs"));
        var rangeStart = source.IndexOf("if (options.DynamicTcpPort && options.TryGetPortRange", StringComparison.Ordinal);
        var range = source[rangeStart..source.IndexOf("        else", rangeStart, StringComparison.Ordinal)];
        Assert.Multiple(() =>
        {
            Assert.That(range, Does.Contain("candidate.Start(); listener = candidate; break;"));
            Assert.That(range, Does.Contain("catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)"));
            Assert.That(range, Does.Contain("{ candidate.Stop(); }"));
            Assert.That(range, Does.Contain("if (listener is null) throw new InvalidOperationException"));
            Assert.That(range, Does.Not.Contain("new TcpListener(IPAddress.Loopback, 0)"));
        });
    }

    [Test]
    public void WorldListener_PortRangeParsingUsesInvariantCultureAndPublishesBoundsOnlyAfterValidation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldServerOptions.cs"));
        var parse = source[source.IndexOf("public bool TryGetPortRange", StringComparison.Ordinal)..];
        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(parse, @"int\.TryParse\(").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(parse, @"NumberStyles\.AllowLeadingSign, CultureInfo\.InvariantCulture").Count,
                Is.EqualTo(2));
            Assert.That(parse.IndexOf("parsedLast - parsedFirst > 1023", StringComparison.Ordinal),
                Is.LessThan(parse.IndexOf("first = parsedFirst;", StringComparison.Ordinal)));
            Assert.That(parse.IndexOf("first = parsedFirst;", StringComparison.Ordinal),
                Is.LessThan(parse.IndexOf("last = parsedLast;", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void MeshHeartbeatHost_WaitsForListenerThenPublishesPresenceAndDrainsOnExit()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "Mesh", "ServerMeshHostedService.cs"));
        var program = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Program.cs"));
        var stages = new[]
        {
            "options.Validate()", "var started = clock.GetUtcNow()", "if (worldOptions.Value.Enabled)",
            "while (world.ActualPort == 0", "var now = clock.GetUtcNow()", "process.Refresh()",
            "sessions.ActivePilotCount", "await registry.HeartbeatAsync(node, stoppingToken)",
            "await sessions.RefreshLocalPresenceAsync(stoppingToken)",
            "await Task.Delay(TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds), stoppingToken)",
            "finally", "await registry.SetDrainingAsync(id!, timeout.Token)"
        };
        var positions = stages.Select(stage => source.IndexOf(stage, StringComparison.Ordinal)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(program, Does.Contain("AddHostedService<ServerMeshHostedService>()"));
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered, "listener readiness precedes the first heartbeat; draining follows exit");
            Assert.That(source, Does.Contain("while (!stoppingToken.IsCancellationRequested)")
                .And.Contain("catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)")
                .And.Contain("new CancellationTokenSource(TimeSpan.FromSeconds(3))")
                .And.Contain("catch (Exception error) { logger.LogWarning(error"));
            Assert.That(source, Does.Not.Contain("SetActiveAsync").And.Not.Contain("RebalanceAsync")
                .And.Not.Contain("ScaleAsync"));
            Assert.That(Regex.Matches(source, @"registry\.HeartbeatAsync\(").Count, Is.EqualTo(1));
            Assert.That(Regex.Matches(source, @"registry\.SetDrainingAsync\(").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void HangarSelection_KeepsAuthenticationAndSignedFormChecksAheadOfMutation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "IndexInternal", "InternalStart", "LocalHangarSelectEndpoint.cs"));
        var handler = source[source.IndexOf("public static class LocalHangarSelection", StringComparison.Ordinal)..];
        var stages = new[]
        {
            "request.Query[\"action\"].Any", "HttpMethods.IsPost(request.Method)",
            "LocalHangarError.ValidActionQuery(request.Query)", "sessions.TryGetByDosidAsync(dosid, ct)",
            "LocalCompanySelection.IsValid(session.Company)", "request.HasFormContentType",
            "request.ReadFormAsync(ct)", "LocalHangarError.HasExactly(form, \"slot\", \"current\", \"key\")",
            "LocalHangars.KeyIssued(session.Dosid, slot, current", "issued > now + LocalHangars.ClockSkew",
            "now - issued > LocalHangars.FormLifetime", "hangars.ChangeAsync(session.AccountId, slot, current, ct)"
        };
        var positions = stages.Select(stage => handler.IndexOf(stage, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered, "validation must precede the state change");
            Assert.That(handler, Does.Contain("response.Headers.Location = LocalHangars.ActivatedLocation(slot)"));
        });
    }

    [Test]
    public void MeshReadEndpoints_UseSharedPresenceProjectionWithoutMutatingRegistry()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "Mesh", "BackofficeMeshEndpoints.cs"));
        var reads = source.Split("public sealed class MeshTopologyEndpoint", StringSplitOptions.None)[1]
            .Split("public sealed record MeshScaleRequest", StringSplitOptions.None)[0]
            + source.Split("public sealed class WorldOverviewEndpoint", StringSplitOptions.None)[1]
                .Split("public sealed record WorldTeleportRequest", StringSplitOptions.None)[0];
        var projection = source.Split("public static class MeshWorldOverview", StringSplitOptions.None)[1]
            .Split("public sealed record WorldMapOverview", StringSplitOptions.None)[0];

        Assert.Multiple(() =>
        {
            Assert.That(reads, Does.Contain("Get(\"backoffice/mesh/topology\")")
                .And.Contain("Get(\"backoffice/world/overview\")")
                .And.Contain("EmulatorPermissions.OpsRead")
                .And.Contain("MeshWorldOverview.ReadPilotsAsync(sessions, ct)"));
            Assert.That(reads, Does.Not.Contain("AllowAnonymous()")
                .And.Not.Contain("SetDrainingAsync").And.Not.Contain("ScaleAsync")
                .And.Not.Contain("RebalanceAsync"));
            Assert.That(projection, Does.Contain("ServerMeshCoordinator.GameMapIds")
                .And.Contain("sessions.ConnectedAsync(id, ct)")
                .And.Contain("GroupBy(p => p.UserId)"));
        });
    }

    [Test]
    public void CompanyOnboarding_KeepsMapAndServerFactsInCatalogAndMapsSlice()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "IndexInternal", "InternalStart", "Contracts", "LocalCompanySelection.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("CompanyCatalog.Companies"));
            Assert.That(source, Does.Contain("CompanyCatalog.Find(company)"));
            Assert.That(source, Does.Contain("LocalMapsXml.Maps.FirstOrDefault(map => map.Id == company.StarterMap)"));
            Assert.That(source, Does.Contain("LocalMapsXml.Maps.FirstOrDefault(map => map.Id == definition.StarterMap)"));
            Assert.That(source, Does.Not.Contain("shares the MMO starter map"));
        });
    }

    [Test]
    public void MeshRegistry_MutationsShareTheMutexAndKeepSnapshotReadsNonMutating()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "Mesh", "ServerMeshCoordinator.cs"));
        var read = source[source.IndexOf("public async Task<ServerMeshSnapshot> GetSnapshotAsync(", StringComparison.Ordinal)..
            source.IndexOf("private async Task<ServerMeshSnapshot> UpdateAsync(", StringComparison.Ordinal)];
        var write = source[source.IndexOf("private async Task<ServerMeshSnapshot> UpdateAsync(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            foreach (var method in new[] { "HeartbeatAsync", "SetDrainingAsync", "SetActiveAsync", "RebalanceAsync" })
                Assert.That(Regex.IsMatch(source,
                    $@"public Task<ServerMeshSnapshot> {method}\([^)]*\)\s*=>\s*UpdateAsync\("), Is.True, method);
            Assert.That(source, Does.Contain("return UpdateAsync((nodes, now) =>"));
            Assert.That(write.IndexOf("await mutex.AcquireAsync(ct)", StringComparison.Ordinal),
                Is.LessThan(write.IndexOf("await GetSnapshotAsync(ct)", StringComparison.Ordinal)));
            Assert.That(write.IndexOf("await mutex.AcquireAsync(ct)", StringComparison.Ordinal),
                Is.LessThan(write.IndexOf("await cache.SetAsync(StateKey", StringComparison.Ordinal)));
            Assert.That(read, Does.Not.Contain("cache.SetAsync").And.Not.Contain("mutex.AcquireAsync"));
        });
    }

    [Test]
    public void GameAuthSessionStore_UsesDistributedCacheBehindSharedFeatureBoundary()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "GameAuth");
        var program = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Program.cs"));
        var store = File.ReadAllText(Path.Combine(slice, "GameAuthSessionStore.cs"));
        var contract = File.ReadAllText(Path.Combine(slice, "IGameAuthSessionStore.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(program, Does.Contain("AddSingleton<IGameAuthSessionStore, GameAuthSessionStore>()"));
            Assert.That(store, Does.Contain("GameAuthSessionStore(IDistributedCache cache)"));
            Assert.That(store, Does.Contain("AbsoluteExpiration = session.ExpiresAt"));
            Assert.That(store, Does.Contain("cache.RemoveAsync(TokenKey(redirectToken)"));
            Assert.That(store, Does.Contain("cache.RemoveAsync(DosidKey(dosid)"));
            Assert.That(contract, Does.Contain("RotateRedirectTokenAsync"));
            Assert.That(contract, Does.Contain("InvalidateAsync"));
            Assert.That(store, Does.Not.Contain("IMemoryCache"));
        });
    }

    [Test]
    public void GameAuthSessionStore_FailsClosedWhenEitherCachedAuthenticationRecordIsMalformed()
    {
        var store = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "GameAuth", "GameAuthSessionStore.cs"));
        var tokenLookup = store[store.IndexOf("public async Task<GameAuthSession?> TryGetByRedirectTokenAsync(", StringComparison.Ordinal)..
            store.IndexOf("public async Task<GameAuthSession?> TryGetByDosidAsync(", StringComparison.Ordinal)];
        var sessionLookup = store[store.IndexOf("public async Task<GameAuthSession?> TryGetByDosidAsync(", StringComparison.Ordinal)..
            store.IndexOf("public async Task<bool> SelectCompanyAsync(", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(tokenLookup, Does.Contain("catch (JsonException)"));
            Assert.That(tokenLookup, Does.Contain("return await TryGetByDosidAsync(tokenRecord.Dosid, cancellationToken)"));
            Assert.That(sessionLookup, Does.Contain("catch (JsonException)"));
            Assert.That(sessionLookup, Does.Contain("session.ExpiresAt <= DateTimeOffset.UtcNow"));
        });
    }

    [Test]
    public void BackofficePermissions_UsesAuthenticatedIdentityAndRegisteredKeycloakTransformation()
    {
        var endpoint = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "Auth", "BackofficePermissionsEndpoint.cs"));
        var authorization = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ServiceDefaults", "Authorization", "AuthorizationExtensions.cs"));
        var transformation = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ServiceDefaults", "Authorization", "KeycloakClaimsTransformation.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("Get(\"backoffice/auth/permissions\")"));
            Assert.That(endpoint, Does.Not.Contain("AllowAnonymous()"));
            Assert.That(endpoint, Does.Contain("HttpContext.User"));
            Assert.That(authorization, Does.Contain("AddSingleton<IClaimsTransformation, KeycloakClaimsTransformation>()"));
            Assert.That(transformation, Does.Contain("public const string ApiClientId = \"thdarkorbit-api\""));
        });
    }

    [Test]
    public void BackofficePermissions_ProjectsOnlyPrincipalClaimsWithinAuthSlice()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "Auth");
        var endpoint = File.ReadAllText(Path.Combine(slice, "BackofficePermissionsEndpoint.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(Directory.GetFiles(slice, "*.cs").Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "BackofficePermissionsEndpoint.cs" }));
            Assert.That(endpoint, Does.Contain("user.FindAll(EmulatorClaimTypes.Permission)"));
            Assert.That(endpoint, Does.Contain("user.FindAll(EmulatorClaimTypes.RealmRole)"));
            Assert.That(endpoint, Does.Contain("user.FindAll(ClaimTypes.Role)"));
            Assert.That(Regex.Matches(endpoint, @"\.Distinct\(\)\.ToList\(\)").Count, Is.EqualTo(3));
            Assert.That(endpoint, Does.Not.Contain("IIdentityQueryService"));
            Assert.That(endpoint, Does.Not.Contain("IKeycloakService"));
        });
    }

    [Test]
    public void BackofficeIdentityEndpoints_StayInIdentitySliceAndDelegateToApplicationService()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "Identity");
        var endpoints = Directory.GetFiles(slice, "*Endpoint.cs");

        Assert.That(endpoints.Select(Path.GetFileName), Is.EquivalentTo(new[]
        {
            "ListIdentityUsersEndpoint.cs", "IdentityUserCountEndpoint.cs", "DeleteAllIdentityUsersEndpoint.cs"
        }));
        foreach (var file in endpoints)
        {
            var source = File.ReadAllText(file);
            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Identity;"), file);
                Assert.That(source, Does.Contain("IBackofficeIdentityService backoffice"), file);
                Assert.That(source, Does.Not.Contain("IdentityReadDbContext"), file);
                Assert.That(source, Does.Not.Contain("IdentityWriteDbContext"), file);
                Assert.That(source, Does.Contain("Version(1);"), file);
            });
        }

        var list = File.ReadAllText(Path.Combine(slice, "ListIdentityUsersEndpoint.cs"));
        var count = File.ReadAllText(Path.Combine(slice, "IdentityUserCountEndpoint.cs"));
        var delete = File.ReadAllText(Path.Combine(slice, "DeleteAllIdentityUsersEndpoint.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(list, Does.Contain("Get(\"backoffice/identity/users\")"));
            Assert.That(list, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.IdentityRead))"));
            Assert.That(list, Does.Contain("ListAccountsAsync(limit: 100, ct)"));
            Assert.That(count, Does.Contain("Get(\"backoffice/identity/users/count\")"));
            Assert.That(count, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.IdentityRead))"));
            Assert.That(delete, Does.Contain("Delete(\"backoffice/identity/users\")"));
            Assert.That(delete, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.IdentityDelete))"));
        });
    }

    [Test]
    public void BackofficeKeycloakListing_StaysInKeycloakSliceAndUsesApplicationBoundary()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "Keycloak");
        var endpoints = Directory.GetFiles(slice, "*Endpoint.cs");
        Assert.That(endpoints.Select(Path.GetFileName), Is.EqualTo(new[] { "ListKeycloakUsersEndpoint.cs" }));

        var source = File.ReadAllText(endpoints[0]);
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Keycloak;"));
            Assert.That(source, Does.Contain("IBackofficeKeycloakService backoffice"));
            Assert.That(source, Does.Contain("Get(\"backoffice/keycloak/users\")"));
            Assert.That(source, Does.Contain("Version(1);"));
            Assert.That(source, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.KeycloakRead))"));
            Assert.That(source, Does.Contain("ListUsersAsync(limit: 100, ct)"));
            Assert.That(source, Does.Not.Contain("AllowAnonymous()"));
            Assert.That(source, Does.Not.Contain("HttpClient"));
            Assert.That(source, Does.Not.Contain("KeycloakAdminOptions"));
        });
    }

    [Test]
    public void ActivityTrackingEndpoints_StayInBackofficeSliceAndRequireOpsRead()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "ActivityTracking");
        var endpoints = Directory.GetFiles(slice, "*Endpoint.cs");

        Assert.That(endpoints, Has.Length.EqualTo(4));
        foreach (var file in endpoints)
        {
            var source = File.ReadAllText(file);
            Assert.Multiple(() =>
            {
                Assert.That(source, Does.Contain("namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;"), file);
                Assert.That(source, Does.Contain("Version(1);"), file);
                Assert.That(source, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));"), file);
            });
        }
    }

    [Test]
    public void ActivityTracking_UsesOneProcessLocalTrackerAcrossEndpoints()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Program.cs"));
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "ActivityTracking");

        Assert.That(program, Does.Contain("AddSingleton<IManualActivityTracker, ManualActivityTracker>()"));
        Assert.That(program, Does.Not.Contain("AddScoped<IManualActivityTracker"));
        Assert.That(program, Does.Not.Contain("AddTransient<IManualActivityTracker"));
        foreach (var file in Directory.GetFiles(slice, "*Endpoint.cs"))
        {
            Assert.That(File.ReadAllText(file), Does.Contain("IManualActivityTracker tracker"), file);
        }
    }

    [Test]
    public void ActivityTrackingSessionStart_KeepsIdentityProjectionAndStorageInsideSlice()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "ActivityTracking");
        var endpoint = File.ReadAllText(Path.Combine(slice, "StartActivityTrackingSessionEndpoint.cs"));
        var tracker = File.ReadAllText(Path.Combine(slice, "IManualActivityTracker.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("User.Identity?.Name"));
            Assert.That(endpoint, Does.Contain("User.FindFirstValue(\"preferred_username\")"));
            Assert.That(endpoint, Does.Contain("tracker.StartSession(req.SubjectId, req.Reason, operatorName)"));
            Assert.That(endpoint, Does.Not.Contain("AllowAnonymous()"));
            Assert.That(tracker, Does.Contain("TimeProvider clock"));
            Assert.That(tracker, Does.Contain("ConcurrentDictionary<string, TrackedSession>"));
            Assert.That(tracker, Does.Not.Contain("DbContext"));
            Assert.That(tracker, Does.Not.Contain("IConnectionMultiplexer"));
        });
    }

    [Test]
    public void ActivityTrackingEventAndSnapshot_KeepRouteLookupAndProjectionInsideSlice()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "ActivityTracking");
        var append = File.ReadAllText(Path.Combine(slice, "AppendActivityTrackingEventEndpoint.cs"));
        var snapshot = File.ReadAllText(Path.Combine(slice, "GetActivityTrackingSnapshotEndpoint.cs"));
        var models = File.ReadAllText(Path.Combine(slice, "ActivityTrackingModels.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(append, Does.Contain("Post(\"backoffice/activity-tracking/sessions/{sessionId}/events\")"));
            Assert.That(append, Does.Contain("tracker.TryGetSession(sessionId, out _)"));
            Assert.That(append, Does.Contain("req.OccurredAtUtc ?? DateTimeOffset.UtcNow"));
            Assert.That(append, Does.Contain("tracker.AppendEvent("));
            Assert.That(snapshot, Does.Contain("Get(\"backoffice/activity-tracking/sessions/{sessionId}\")"));
            Assert.That(snapshot, Does.Contain("tracker.TryGetSession(sessionId, out var session)"));
            Assert.That(snapshot, Does.Contain("tracker.GetSnapshot(sessionId)"));
            Assert.That(models, Does.Contain("IReadOnlyList<ActivityTrackingEvent> Events"));
        });
    }

    [Test]
    public void ActivityTrackingStream_EnforcesSessionLookupAndSseBoundaryInsideSlice()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "ActivityTracking");
        var endpoint = File.ReadAllText(Path.Combine(slice, "StreamActivityTrackingEventsEndpoint.cs"));
        var tracker = File.ReadAllText(Path.Combine(slice, "IManualActivityTracker.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("tracker.TryGetSession(sessionId, out _)"));
            Assert.That(endpoint, Does.Contain("Send.NotFoundAsync(ct)"));
            Assert.That(endpoint, Does.Contain("text/event-stream"));
            Assert.That(endpoint, Does.Contain("CacheControl = \"no-store\""));
            Assert.That(endpoint, Does.Contain("tracker.SubscribeAsync(sessionId, ct)"));
            Assert.That(endpoint, Does.Contain("Response.Body.FlushAsync(ct)"));
            Assert.That(tracker, Does.Contain("trackedSession.AddSubscriber(channel.Writer)"));
            Assert.That(tracker, Does.Contain("trackedSession.RemoveSubscriber(channel.Writer)"));
        });
    }

    [Test]
    public void ActivityTrackingFanout_KeepsSubscriberStateInTrackerAndTransportInEndpoint()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "ActivityTracking");
        var tracker = File.ReadAllText(Path.Combine(slice, "IManualActivityTracker.cs"));
        var endpoint = File.ReadAllText(Path.Combine(slice, "StreamActivityTrackingEventsEndpoint.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(tracker, Does.Contain("Channel.CreateUnbounded<ActivityTrackingEvent>"));
            Assert.That(tracker, Does.Contain("private readonly HashSet<ChannelWriter<ActivityTrackingEvent>> _subscribers"));
            Assert.That(tracker, Does.Contain("lock (_gate)"));
            Assert.That(tracker, Does.Contain("finally"));
            Assert.That(tracker, Does.Contain("trackedSession.RemoveSubscriber(channel.Writer)"));
            Assert.That(tracker, Does.Not.Contain("HttpContext"));
            Assert.That(tracker, Does.Not.Contain("text/event-stream"));
            Assert.That(endpoint, Does.Not.Contain("ChannelWriter"));
            Assert.That(endpoint, Does.Not.Contain("_subscribers"));
        });
    }

    [Test]
    public void ActivityTrackingRetention_BoundsSnapshotsWithoutDiscardingSubscriberDelivery()
    {
        var tracker = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "Backoffice", "ActivityTracking", "IManualActivityTracker.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(tracker, Does.Contain("private const int MaxEventsPerSession = 250;"));
            Assert.That(tracker, Does.Contain("while (_events.Count > MaxEventsPerSession)"));
            Assert.That(tracker, Does.Contain("_events.Dequeue();"));
            Assert.That(tracker, Does.Contain("subscriber.TryWrite(activityEvent);"));
            Assert.That(tracker, Does.Contain("return _events.ToArray();"));
        });
    }

    [Test]
    public void BackofficeOperations_KeepReadAndCacheWriteEndpointsInTheirSlice()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Backoffice", "Operations");
        var files = Directory.GetFiles(slice, "*Endpoint.cs");
        Assert.That(files.Select(Path.GetFileName), Is.EquivalentTo(new[]
            { "BackofficeOverviewEndpoint.cs", "ClearCacheEndpoint.cs" }));

        var overview = File.ReadAllText(Path.Combine(slice, "BackofficeOverviewEndpoint.cs"));
        var clearCache = File.ReadAllText(Path.Combine(slice, "ClearCacheEndpoint.cs"));
        foreach (var source in new[] { overview, clearCache })
        {
            Assert.That(source, Does.Contain("namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Operations;"));
            Assert.That(source, Does.Contain("Version(1);"));
            Assert.That(source, Does.Not.Contain("AllowAnonymous()"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(overview, Does.Contain("Get(\"backoffice/operations/overview\")"));
            Assert.That(overview, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead))"));
            Assert.That(clearCache, Does.Contain("Post(\"backoffice/operations/cache/clear\")"));
            Assert.That(clearCache, Does.Contain("Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.CacheWrite))"));
            Assert.That(clearCache, Does.Contain("ExecuteAsync(\"FLUSHALL\")"));
        });
    }

    [Test]
    public void ApiService_ContainsNoEmptyDirectories()
    {
        var apiDir = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService");
        var emptyDirs = Directory.GetDirectories(apiDir, "*", SearchOption.AllDirectories)
            .Where(dir => !dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                       && !dir.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                       && !dir.EndsWith($"{Path.DirectorySeparatorChar}bin", StringComparison.OrdinalIgnoreCase)
                       && !dir.EndsWith($"{Path.DirectorySeparatorChar}obj", StringComparison.OrdinalIgnoreCase)
                       && !dir.EndsWith($"{Path.DirectorySeparatorChar}db-backups", StringComparison.OrdinalIgnoreCase))
            .Where(dir => Directory.GetFileSystemEntries(dir).Length == 0)
            .ToArray();

        Assert.That(emptyDirs, Is.Empty, "ThDarkOrbitEmulator.ApiService must not contain empty legacy folders.");
    }

    [Test]
    public void ApiService_AllFastEndpointsResideUnderFeaturesNamespace()
    {
        var endpointTypes = typeof(LoginEndpoint).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IEndpoint).IsAssignableFrom(type))
            .ToArray();

        Assert.That(endpointTypes, Is.Not.Empty);
        foreach (var endpointType in endpointTypes)
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    endpointType.Namespace,
                    Does.StartWith("ThDarkOrbitEmulator.ApiService.Features"),
                    $"Endpoint {endpointType.FullName} must reside in a Features vertical slice namespace.");
                Assert.That(
                    endpointType.Name,
                    Does.EndWith("Endpoint"),
                    $"Endpoint {endpointType.FullName} must end with 'Endpoint'.");
            });
        }
    }

    [Test]
    public void ApiService_FastEndpointsContractsAndValidatorsResideUnderFeaturesNamespace()
    {
        var apiTypes = typeof(LoginEndpoint).Assembly.GetTypes();
        var endpointTypes = apiTypes.Where(type => typeof(IEndpoint).IsAssignableFrom(type)).ToArray();
        var contractTypes = endpointTypes
            .SelectMany(type => EnumerateBaseTypes(type))
            .Where(type => type.IsGenericType && type.Namespace == "FastEndpoints")
            .Where(type => type.Name.StartsWith("Endpoint`", StringComparison.Ordinal)
                        || type.Name.StartsWith("EndpointWithoutRequest`", StringComparison.Ordinal))
            .SelectMany(type => type.GetGenericArguments())
            .Where(type => type.Assembly == typeof(LoginEndpoint).Assembly);
        var validatorTypes = apiTypes.Where(type => EnumerateBaseTypes(type)
            .Any(baseType => baseType.IsGenericType
                             && baseType.Namespace == "FastEndpoints"
                             && baseType.Name.StartsWith("Validator`", StringComparison.Ordinal)));
        var validatorContracts = validatorTypes.SelectMany(type => EnumerateBaseTypes(type))
            .Where(type => type.IsGenericType && type.Namespace == "FastEndpoints"
                           && type.Name.StartsWith("Validator`", StringComparison.Ordinal))
            .SelectMany(type => type.GetGenericArguments());
        var candidates = endpointTypes.Concat(contractTypes).Concat(validatorTypes).Concat(validatorContracts)
            .Where(type => type.Assembly == typeof(LoginEndpoint).Assembly)
            .Distinct()
            .ToArray();

        Assert.That(endpointTypes, Is.Not.Empty);
        Assert.That(candidates, Is.Not.Empty);
        Assert.That(candidates.Where(type => type.Namespace is null
                                             || !type.Namespace.StartsWith("ThDarkOrbitEmulator.ApiService.Features.", StringComparison.Ordinal))
            .Select(type => type.FullName), Is.Empty,
            "FastEndpoints endpoints, validators, and request/response contracts must live in a feature namespace.");
    }

    private static IEnumerable<Type> EnumerateBaseTypes(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    [Test]
    public void ApiService_DoesNotReferenceAppHostOrGateway()
    {
        var result = Types
            .InAssembly(typeof(LoginEndpoint).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.AppHost",
                "ThDarkOrbitEmulator.Gateway")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void WorldProtocol_CommandIdsRemainUniqueAndContiguousThroughPublishedRange()
    {
        var values = Enum.GetValues<WorldCommandId>().Select(value => (ushort)value).ToArray();
        Assert.That(values, Is.EqualTo(Enumerable.Range(1, 40).Select(value => (ushort)value)),
            "Changing a published command ID changes the provisional TCP wire contract.");
        Assert.That(typeof(WorldCommandId).Namespace,
            Is.EqualTo("ThDarkOrbitEmulator.ApiService.Features.World"));
    }

    [Test]
    public void WorldProtocol_FramingRemainsStreamOnlyInsideWorldSlice()
    {
        var codec = typeof(WorldPacketReader);
        var writer = typeof(WorldPacketWriter);
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldPacketCodec.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(codec.Namespace, Is.EqualTo("ThDarkOrbitEmulator.ApiService.Features.World"));
            Assert.That(writer.Namespace, Is.EqualTo(codec.Namespace));
            Assert.That(codec.GetConstructors().Single().GetParameters()[0].ParameterType, Is.EqualTo(typeof(Stream)));
            Assert.That(writer.GetConstructors().Single().GetParameters()[0].ParameterType, Is.EqualTo(typeof(Stream)));
            Assert.That(source, Does.Not.Contain("Microsoft.AspNetCore"));
            Assert.That(source, Does.Not.Contain("HttpContext"));
            Assert.That(source, Does.Not.Contain("WorldHttpBridge"));
        });
    }

    [Test]
    public void WorldProtocol_FrameIoPassesCallerCancellationToEveryStreamOperation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldPacketCodec.cs"));
        var reader = source[source.IndexOf("public sealed class WorldPacketReader", StringComparison.Ordinal)..
            source.IndexOf("public readonly record struct WorldPacket", StringComparison.Ordinal)];
        var writer = source[source.IndexOf("public sealed class WorldPacketWriter", StringComparison.Ordinal)..
            source.IndexOf("public static class WorldPayload", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(reader, Does.Contain("stream.ReadAsync(header, ct)"));
            Assert.That(reader, Does.Contain("stream.ReadExactlyAsync(header.AsMemory(count), ct)"));
            Assert.That(reader, Does.Contain("stream.ReadExactlyAsync(body, ct)"));
            Assert.That(writer, Does.Contain("stream.WriteAsync(frame, ct)"));
            Assert.That(reader, Does.Not.Contain("CancellationToken.None"));
            Assert.That(writer, Does.Not.Contain("CancellationToken.None"));
        });
    }

    [Test]
    public void WorldProtocol_FrameLimitChecksPrecedeAllocationAndBodyIo()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldPacketCodec.cs"));
        var reader = source[source.IndexOf("public sealed class WorldPacketReader", StringComparison.Ordinal)..
            source.IndexOf("public readonly record struct WorldPacket", StringComparison.Ordinal)];
        var writer = source[source.IndexOf("public sealed class WorldPacketWriter", StringComparison.Ordinal)..
            source.IndexOf("public static class WorldPayload", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(reader.IndexOf("length < 2 || length > maximumPacketBytes", StringComparison.Ordinal),
                Is.LessThan(reader.IndexOf("var body = new byte[length]", StringComparison.Ordinal)));
            Assert.That(reader.IndexOf("length < 2 || length > maximumPacketBytes", StringComparison.Ordinal),
                Is.LessThan(reader.IndexOf("stream.ReadExactlyAsync(body, ct)", StringComparison.Ordinal)));
            Assert.That(writer.IndexOf("payload.Length > maximumPacketBytes - 2", StringComparison.Ordinal),
                Is.LessThan(writer.IndexOf("var frame = new byte[6 + payload.Length]", StringComparison.Ordinal)));
            Assert.That(writer, Does.Contain("maximumPacketBytes = 65536"));
            Assert.That(reader, Does.Contain("maximumPacketBytes = 65536"));
        });
    }

    [Test]
    public void WorldSessionHandler_UsesConfiguredFrameLimitForBothDirections()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("new WorldPacketReader(stream, options.MaximumPacketBytes)"));
            Assert.That(source, Does.Contain("new WorldPacketWriter(stream, options.MaximumPacketBytes)"));
            Assert.That(source, Does.Not.Contain("new WorldPacketReader(stream)"));
            Assert.That(source, Does.Not.Contain("new WorldPacketWriter(stream)"));
        });
    }

    [Test]
    public void WorldProtocol_FrameBuffersOwnTheirPayloadAcrossAsyncIo()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldPacketCodec.cs"));
        var reader = source[source.IndexOf("public sealed class WorldPacketReader", StringComparison.Ordinal)..
            source.IndexOf("public readonly record struct WorldPacket", StringComparison.Ordinal)];
        var writer = source[source.IndexOf("public sealed class WorldPacketWriter", StringComparison.Ordinal)..
            source.IndexOf("public static class WorldPayload", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(reader, Does.Contain("var body = new byte[length];"));
            Assert.That(reader.IndexOf("await stream.ReadExactlyAsync(body, ct)", StringComparison.Ordinal),
                Is.LessThan(reader.IndexOf("body.AsMemory(2)", StringComparison.Ordinal)));
            Assert.That(writer, Does.Contain("var frame = new byte[6 + payload.Length];"));
            Assert.That(writer.IndexOf("payload.CopyTo(frame.AsMemory(6))", StringComparison.Ordinal),
                Is.LessThan(writer.IndexOf("await stream.WriteAsync(frame, ct)", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void WorldSessionHandler_RechecksVersionBeforeResolvingPreloginIdentity()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var prelogin = source[source.IndexOf("case WorldCommandId.VersionRequest:", StringComparison.Ordinal)..
            source.IndexOf("var restored = sessions is null ? pilot", StringComparison.Ordinal)];
        var stages = new[]
        {
            "versionAccepted = requested == options.Version;",
            "w.Write(versionAccepted);",
            "case WorldCommandId.LoginRequest:",
            "if (!versionAccepted)",
            "var login = WorldPayload.Decode(packet.Value.Payload, r => new",
            "login.Version != options.Version || login.InstanceId.Length is 0 or > 128",
            "await resolver.ResolveAsync(login.UserId, login.Dosid, ct)"
        };
        var positions = stages.Select(stage => prelogin.IndexOf(stage, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered);
            Assert.That(Regex.Matches(prelogin, @"WorldLoginError\.UnsupportedVersion").Count,
                Is.EqualTo(2));
        });
    }

    [Test]
    public void WorldSessionHandler_MapsMalformedPreloginPayloadsAfterHandshakeGate()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var prelogin = source[source.IndexOf("case WorldCommandId.VersionRequest:", StringComparison.Ordinal)..
            source.IndexOf("private async Task<WorldMapInstance> RuntimeMapAsync", StringComparison.Ordinal)];
        var versionDecode = prelogin.IndexOf("WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadString)",
            StringComparison.Ordinal);
        var handshakeGate = prelogin.IndexOf("if (!versionAccepted)", StringComparison.Ordinal);
        var loginDecode = prelogin.IndexOf("var login = WorldPayload.Decode(packet.Value.Payload, r => new",
            StringComparison.Ordinal);
        var identityLookup = prelogin.IndexOf("await resolver.ResolveAsync(login.UserId, login.Dosid, ct)",
            StringComparison.Ordinal);
        var malformedCatch = prelogin.IndexOf(
            "catch (Exception e) when (e is InvalidDataException or EndOfStreamException or DecoderFallbackException)",
            StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(versionDecode, Is.GreaterThanOrEqualTo(0));
            Assert.That(handshakeGate, Is.GreaterThan(versionDecode));
            Assert.That(loginDecode, Is.GreaterThan(handshakeGate));
            Assert.That(identityLookup, Is.GreaterThan(loginDecode));
            Assert.That(malformedCatch, Is.GreaterThan(identityLookup));
            Assert.That(prelogin[malformedCatch..], Does.Contain("await ErrorAsync(writer, WorldLoginError.Malformed, ct);"));
        });
    }

    [Test]
    public void WorldSessionHandler_RejectsOtherPreloginCommandsWithoutPayloadInterpretation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var prelogin = source[source.IndexOf("case WorldCommandId.VersionRequest:", StringComparison.Ordinal)..
            source.IndexOf("private async Task<WorldMapInstance> RuntimeMapAsync", StringComparison.Ordinal)];
        var fallback = prelogin.IndexOf("default:", StringComparison.Ordinal);
        var malformedCatch = prelogin.IndexOf("catch (Exception e) when (e is InvalidDataException", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(fallback, Is.GreaterThan(prelogin.IndexOf("case WorldCommandId.LoginRequest:", StringComparison.Ordinal)));
            Assert.That(fallback, Is.LessThan(malformedCatch));
            Assert.That(prelogin[fallback..malformedCatch], Does.Contain("WorldLoginError.UnknownCommand")
                .And.Contain("return;"));
            Assert.That(prelogin[fallback..malformedCatch], Does.Not.Contain("WorldPayload.Decode")
                .And.Not.Contain("resolver.ResolveAsync"));
        });
    }

    [Test]
    public void WorldSessionHandler_RejectsUnsupportedAuthenticatedCommandsAfterPayloadErrorBoundary()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var authenticated = source[source.IndexOf("if (connected is not null)", StringComparison.Ordinal)..
            source.IndexOf("case WorldCommandId.VersionRequest:", StringComparison.Ordinal)];
        var malformedCatch = authenticated.LastIndexOf(
            "catch (Exception e) when (e is InvalidDataException or EndOfStreamException or DecoderFallbackException)",
            StringComparison.Ordinal);
        var fallback = authenticated.LastIndexOf("await ErrorAsync(writer, WorldLoginError.UnknownCommand, ct);",
            StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(malformedCatch, Is.GreaterThanOrEqualTo(0));
            Assert.That(fallback, Is.GreaterThan(malformedCatch));
            Assert.That(authenticated[fallback..], Does.Contain("return;"));
            Assert.That(authenticated[malformedCatch..fallback], Does.Not.Contain("WorldPayload.Decode"));
        });
    }

    [Test]
    public void WorldSessionHandler_MapsMalformedAuthenticatedPayloadsToWireError()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldSessionHandler.cs"));
        var authenticated = source[source.IndexOf("if (connected is not null)", StringComparison.Ordinal)..
            source.IndexOf("case WorldCommandId.VersionRequest:", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(authenticated, Does.Contain("WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadString)"));
            Assert.That(authenticated.IndexOf("if (!connected.IsAdmin || adminCommands is null)", StringComparison.Ordinal),
                Is.LessThan(authenticated.IndexOf("WorldPayload.Decode(packet.Value.Payload, WorldPayload.ReadString)", StringComparison.Ordinal)));
            Assert.That(authenticated, Does.Contain("e is InvalidDataException or EndOfStreamException or DecoderFallbackException"));
            Assert.That(authenticated, Does.Contain("await ErrorAsync(writer, WorldLoginError.Malformed, ct);"));
        });
    }

    [Test]
    public void WorldProtocol_StringEncodingIsStrictForBothPayloadKinds()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldPacketCodec.cs"));
        var payload = source[source.IndexOf("public static class WorldPayload", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain("new(false, true)"));
            Assert.That(Regex.Matches(payload, @"StrictUtf8\.GetBytes\(value\)").Count, Is.EqualTo(2));
            Assert.That(Regex.Matches(payload, @"StrictUtf8\.GetString\(reader\.ReadBytesExactly\(length\)\)").Count,
                Is.EqualTo(2));
            Assert.That(payload, Does.Not.Contain("Encoding.UTF8.GetBytes(value)"));
        });
    }

    [Test]
    public void WorldProtocol_NumericPayloadsAndDecodingStayExplicitAndBounded()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldPacketCodec.cs"));
        var payload = source[source.IndexOf("public static class WorldPayload", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain("BinaryPrimitives.ReverseEndianness(value)"));
            Assert.That(payload, Does.Contain("BinaryPrimitives.ReverseEndianness(reader.ReadInt32())"));
            Assert.That(payload, Does.Contain("BinaryPrimitives.ReverseEndianness(reader.ReadInt64())"));
            Assert.That(payload, Does.Contain("stream.Position != stream.Length"));
            Assert.That(payload, Does.Not.Contain("reader.ReadString()"));
        });
    }

    [Test]
    public void WorldConnection_GatesEachSocketWriteAcrossTheWholeFrame()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldMovement.cs"));
        var connection = source[source.IndexOf("public sealed class WorldConnection(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(typeof(WorldConnection).Namespace,
                Is.EqualTo("ThDarkOrbitEmulator.ApiService.Features.World"));
            Assert.That(connection, Does.Contain("private readonly SemaphoreSlim writeGate"));
            Assert.That(connection, Does.Contain("await writeGate.WaitAsync(ct);"));
            Assert.That(connection, Does.Contain("try { await writer.WriteAsync(command, payload, ct); }"));
            Assert.That(connection, Does.Contain("finally { writeGate.Release(); }"));
        });
    }

    [Test]
    public void WorldConnection_CancellationCannotBypassOrStrandThePerSocketGate()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldMovement.cs"));
        var connection = source[source.IndexOf("public sealed class WorldConnection(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(connection, Does.Contain("await writeGate.WaitAsync(ct);"),
                "A cancelled waiter must never enter the write section.");
            Assert.That(connection, Does.Contain("writer.WriteAsync(command, payload, ct)"),
                "Cancellation must reach an active frame write.");
            Assert.That(connection, Does.Contain("finally { writeGate.Release(); }"),
                "An active cancelled write must release this socket's gate.");
        });
    }

    [Test]
    public void WorldMovement_StaysInWorldSliceAndCalculatesWithoutTransportOrStorage()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldMovement.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(typeof(WorldMovement).Namespace,
                Is.EqualTo("ThDarkOrbitEmulator.ApiService.Features.World"));
            Assert.That(typeof(WorldTravel).Namespace, Is.EqualTo(typeof(WorldMovement).Namespace));
            Assert.That(source, Does.Not.Contain("HttpContext"));
            Assert.That(source, Does.Not.Contain("IDistributedCache"));
            Assert.That(source, Does.Not.Contain("DbContext"));
            Assert.That(source, Does.Not.Contain("WorldHttpBridge"));
        });
    }

    [Test]
    public void WorldMovement_WidensCoordinateDifferencesAndSaturatesWireDuration()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldMovement.cs"));
        var begin = source[source.IndexOf("public static WorldMoveResult Begin(", StringComparison.Ordinal)..
            source.IndexOf("public static WorldTravel Travel(", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(begin, Does.Contain("(double)targetX - start.Item1"));
            Assert.That(begin, Does.Contain("(double)targetY - start.Item2"));
            Assert.That(begin, Does.Contain("Math.Min(durationMilliseconds, int.MaxValue)"));
            Assert.That(begin, Does.Not.Contain("(int)Math.Ceiling(distance"));
        });
    }

    [Test]
    public void WorldMovement_UsesOneMapScopedInterpolationRuleForDurationAndTravelOrigin()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldMovement.cs"));
        var begin = source[source.IndexOf("public static WorldMoveResult Begin(", StringComparison.Ordinal)..
            source.IndexOf("public static WorldTravel Travel(", StringComparison.Ordinal)];
        var travel = source[source.IndexOf("public static WorldTravel Travel(", StringComparison.Ordinal)..
            source.IndexOf("public sealed class WorldConnection", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(begin, Does.Contain("previous?.MapId == pilot.MapId ? previous.PositionAt(now) : (pilot.X, pilot.Y)"));
            Assert.That(travel, Does.Contain("previous?.MapId == pilot.MapId ? previous.PositionAt(now) : (pilot.X, pilot.Y)"));
            Assert.That(source, Does.Contain("Math.Clamp(elapsed * Speed / distance, 0, 1)"));
        });
    }

    [Test]
    public void WorldMovement_RoundsFractionalTravelBeforeRetargeting()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldMovement.cs"));
        var interpolation = source[source.IndexOf("public (int X, int Y) PositionAt(", StringComparison.Ordinal)..
            source.IndexOf("public sealed record WorldMoveResult", StringComparison.Ordinal)];
        var movement = source[source.IndexOf("public static WorldMoveResult Begin(", StringComparison.Ordinal)..
            source.IndexOf("public sealed class WorldConnection", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(interpolation, Does.Contain("(int)Math.Round(StartX + (TargetX - StartX) * fraction)"));
            Assert.That(interpolation, Does.Contain("(int)Math.Round(StartY + (TargetY - StartY) * fraction)"));
            Assert.That(movement, Does.Contain("previous.PositionAt(now)"));
            Assert.That(movement, Does.Not.Contain("previous.StartX"));
            Assert.That(movement, Does.Not.Contain("previous.StartY"));
        });
    }

    [Test]
    public void WorldShipStats_ResolveBoundedCatalogFieldsInsideTheWorldSlice()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldState.cs"));
        var calculator = source[source.IndexOf("public static class WorldShipStatCalculator", StringComparison.Ordinal)..
            source.IndexOf("public sealed record WorldPosition(", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(typeof(WorldShipStatCalculator).Namespace,
                Is.EqualTo("ThDarkOrbitEmulator.ApiService.Features.World"));
            Assert.That(calculator, Does.Contain("catalog?.FirstOrDefault(item => item.ItemId == itemId)"));
            Assert.That(calculator, Does.Contain("listing.Stats.TryGetProperty(field, out var stat)"));
            Assert.That(calculator, Does.Contain("stat.TryGetInt32(out var numeric)"));
            Assert.That(calculator, Does.Contain("numeric is >= 0 and <= 1000000"));
            Assert.That(calculator, Does.Contain("return defaults.GetValueOrDefault(itemId);"));
            Assert.That(calculator, Does.Contain("a.HangarSlot == slot && a.Configuration == configuration"));
            Assert.That(calculator, Does.Not.Contain("HttpContext"));
            Assert.That(calculator, Does.Not.Contain("DbContext"));
        });
    }

    [Test]
    public void WorldHazardAndRepair_KeepDamageAndFencedStateWritesInWorldSessionStore()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldState.cs"));
        var damage = source[source.IndexOf("private async Task ApplyBoundaryDamageAsync(", StringComparison.Ordinal)..
            source.IndexOf("public async Task RepairAtBaseAsync(", StringComparison.Ordinal)];
        var repair = source[source.IndexOf("public async Task RepairAtBaseAsync(", StringComparison.Ordinal)..
            source.IndexOf("public async Task RepairAtBaseFencedAsync(", StringComparison.Ordinal)];
        var fenced = source[source.IndexOf("public async Task RepairAtBaseFencedAsync(", StringComparison.Ordinal)..
            source.IndexOf("public async Task<WorldJumpResult?> JumpAsync(", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(damage, Does.Contain("now - last < TimeSpan.FromSeconds(1)"));
            Assert.That(damage, Does.Contain("Math.Max(1, CurrentHitPoints(pilot) - damage)"));
            Assert.That(damage, Does.Contain("await PersistRuntimeAsync(pilot, ct)"));
            Assert.That(repair, Does.Contain("position != target || dx * dx + dy * dy > (long)peaceRadius * peaceRadius"));
            Assert.That(repair, Does.Contain("await PersistRuntimeAsync(pilot, ct)"));
            Assert.That(fenced, Does.Contain("await CheckLeaseAsync(pilot.MapId, fence, ct)"));
            Assert.That(fenced, Does.Contain("pilot = await RefreshAsync(pilot, ct)"));
            Assert.That(fenced.IndexOf("await CheckLeaseAsync(pilot.MapId, fence, ct)", StringComparison.Ordinal),
                Is.LessThan(fenced.IndexOf("await RepairAtBaseAsync(pilot, now, ct)", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void WorldCollectibles_ReserveSharedClaimBeforeWalletCredit()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldCollectibles.cs"));
        var collect = source[source.IndexOf("public async Task<WorldCollectResult> CollectAsync(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(collect, Does.Contain("await using var held = await mutex.AcquireAsync(ct)"));
            Assert.That(collect, Does.Contain("await sessions.RestoreAsync(pilot, ct)"));
            Assert.That(collect, Does.Contain("await cache.GetStringAsync(claimKey, ct)"));
            Assert.That(collect, Does.Contain("await cache.SetStringAsync(claimKey"));
            Assert.That(collect, Does.Contain("await wallet.CreditAsync(pilot.AccountId, adjusted, claimKey, ct)"));
            Assert.That(collect.IndexOf("await cache.SetStringAsync(claimKey", StringComparison.Ordinal),
                Is.LessThan(collect.IndexOf("await wallet.CreditAsync(", StringComparison.Ordinal)));
            Assert.That(collect, Does.Not.Contain("cache.RemoveAsync(claimKey"));
        });
    }

    [Test]
    public void WorldCollectibles_ValidateUnderLeaseBeforeReserving_AndBroadcastOnlyAppliedCredits()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldCollectibles.cs"));
        var collect = source[source.IndexOf("public async Task<WorldCollectResult> CollectAsync(", StringComparison.Ordinal)..];
        int At(string marker)
        {
            var index = collect.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), marker);
            return index;
        }

        var order = new[]
        {
            At("if (pilot.AccountId == Guid.Empty) return new(\"invalid\")"),
            At("await mutex.AcquireAsync(ct)"),
            At("WorldSessionStore.CheckLeaseAsync(pilot.MapId, fence, ct)"),
            At("await sessions.RestoreAsync(pilot, ct)"),
            At("return new(\"missing\")"),
            At("return new(\"distant\")"),
            At("return new(\"claimed\")"),
            At("return new(\"cargo_full\")"),
            At("await cache.SetStringAsync(claimKey"),
            At("await wallet.CreditAsync("),
            At("if (!applied) return new(\"claimed\")"),
            At("await sessions.BroadcastCollectAsync(pilot, box, ct)")
        };
        Assert.That(order, Is.Ordered, "rejections run under the lease before reservation; only applied credits broadcast");
        Assert.That(collect, Does.Contain("dx * dx + dy * dy > (long)radius * radius"), "the pickup radius is inclusive");
    }

    [Test]
    public void WorldNpcKill_PersistsKillerAndRewardSnapshotBeforeCrediting()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldNpcs.cs"));
        var attack = source[source.IndexOf("public async Task<WorldNpcAttack> AttackAsync(", StringComparison.Ordinal)..];
        var save = attack.IndexOf("await cache.SetStringAsync(key, JsonSerializer.Serialize(vital with {",
            StringComparison.Ordinal);
        var credit = attack.IndexOf("await rewards.CreditAsync(pilot.AccountId, ApplyReward(npc!, reward!), key, ct)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(typeof(WorldNpcs).Namespace,
                Is.EqualTo("ThDarkOrbitEmulator.ApiService.Features.World"));
            Assert.That(attack, Does.Contain("Killer = dead ? pilot.AccountId : Guid.Empty,")
                .And.Contain("Reward = reward, Snapshot = dead ? npc : null"));
            Assert.That(attack, Does.Contain("if (vital.Killer != pilot.AccountId) return new(\"destroyed\")"));
            Assert.That(attack, Does.Contain("await rewards.CreditAsync(pilot.AccountId, ApplyReward(npc!, reward), key, ct)"));
            Assert.That(save, Is.GreaterThanOrEqualTo(0));
            Assert.That(credit, Is.GreaterThan(save));
        });
    }

    [Test]
    public void UnityCandidateEndpoints_AreAnonymousUnityApiRoutesWithFixedVerbs()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "UnityApi",
            "CandidateEndpoints", "UnityCandidateEndpoints.cs"));
        var expected = new (string Class, string Verb, string Route)[]
        {
            ("SpaceSearchEndpoint", "Get", "/events/spaceSearch.php"),
            ("SpaceSearchRewardsEndpoint", "Get", "/events/ssRewards.php"),
            ("GalaxyGateRewardsEndpoint", "Get", "/guide/ggRewards.php"),
            ("PaymentCandidateEndpoint", "Get", "/payment.php"),
            ("UploadCandidateEndpoint", "Post", "/upload.php")
        };
        var blocks = source.Split("public sealed class ").Skip(1).ToArray();
        Assert.That(blocks, Has.Length.EqualTo(expected.Length), "Every candidate route needs a SPEC-05 entry.");
        Assert.Multiple(() =>
        {
            foreach (var (name, verb, route) in expected)
            {
                var block = blocks.Single(item => item.StartsWith(name + "(", StringComparison.Ordinal)
                    || item.StartsWith(name + " ", StringComparison.Ordinal));
                Assert.That(block, Does.Contain($"{verb}(\"{route}\");"), name);
                Assert.That(block, Does.Contain("RoutePrefixOverride(\"unityApi\");"), name);
                Assert.That(block, Does.Contain("AllowAnonymous();"), name);
                Assert.That(block, Does.Contain("CandidateResponse.WriteAsync").Or.Contain("SpaceSearchCandidateAction.Handle"), name);
            }
            Assert.That(source, Does.Contain("internal static class CandidateResponse"));
            Assert.That(source, Does.Contain("Headers.CacheControl = \"no-store\";"));
        });
    }

    [Test]
    public void StaticUnityCandidates_KeepCatalogPaymentAndUploadResponsesStateless()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "UnityApi", "CandidateEndpoints", "UnityCandidateEndpoints.cs"));
        var blocks = source.Split("public sealed class ");
        var galaxy = blocks.Single(block => block.StartsWith("GalaxyGateRewardsEndpoint ", StringComparison.Ordinal));
        var payment = blocks.Single(block => block.StartsWith("PaymentCandidateEndpoint ", StringComparison.Ordinal));
        var upload = blocks.Single(block => block.StartsWith("UploadCandidateEndpoint ", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(galaxy, Does.Contain("LocalGalaxyGates.Catalog.Select(gate => gate.Id)"));
            Assert.That(galaxy, Does.Not.Contain("Session").And.Not.Contain("Store"));
            Assert.That(payment, Does.Contain("Url = \"/indexInternal.es?action=internalPayment\""));
            Assert.That(payment, Does.Not.Contain("Request.Cookies").And.Not.Contain("HttpClient"));
            Assert.That(upload, Does.Contain("new UploadResponse(), ct, StatusCodes.Status501NotImplemented"));
            Assert.That(upload, Does.Not.Contain("Request.Body").And.Not.Contain("Store"));
        });
    }

    [Test]
    public void SpaceSearchCandidate_ValidatesBeforeSessionLookupAndKeepsProgressAccountScoped()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "UnityApi", "CandidateEndpoints", "SpaceSearchCandidateAction.cs"));
        var search = source.Split("public static async Task HandleSearchAsync", StringSplitOptions.None)[1]
            .Split("public static async Task HandleRewardsAsync", StringSplitOptions.None)[0];
        var rewards = source.Split("public static async Task HandleRewardsAsync", StringSplitOptions.None)[1]
            .Split("private static async Task<LocalEquipmentState?> StateAsync", StringSplitOptions.None)[0];
        var state = source.Split("private static async Task<LocalEquipmentState?> StateAsync", StringSplitOptions.None)[1]
            .Split("private static Task WriteAsync", StringSplitOptions.None)[0];
        var validation = source.Split("public static bool ValidQuery", StringSplitOptions.None)[1]
            .Split("public static SpaceSearchResponse Search", StringSplitOptions.None)[0];
        Assert.Multiple(() =>
        {
            Assert.That(validation, Does.Contain("query.All(pair =>"));
            Assert.That(validation, Does.Contain("pair.Key.Length is >= 1 and <= MaxQueryValueLength"));
            Assert.That(validation, Does.Contain("pair.Value.Count == 1"));
            Assert.That(validation, Does.Contain("pair.Value.ToString().Length <= MaxQueryValueLength"));
            Assert.That(validation, Does.Contain("pair.Key.Any(char.IsControl)"));
            Assert.That(validation, Does.Contain("pair.Value.ToString().Any(char.IsControl)"));
            Assert.That(search.IndexOf("ValidQuery(context.Request.Query)", StringComparison.Ordinal),
                Is.LessThan(search.IndexOf("StateAsync(context", StringComparison.Ordinal)));
            Assert.That(rewards.IndexOf("ValidQuery(query)", StringComparison.Ordinal),
                Is.LessThan(rewards.IndexOf("LocalSpaceSearch.IsPlanetIdShape", StringComparison.Ordinal)));
            Assert.That(rewards.IndexOf("LocalSpaceSearch.Find(planetId)", StringComparison.Ordinal),
                Is.LessThan(rewards.IndexOf("StateAsync(context", StringComparison.Ordinal)));
            Assert.That(state, Does.Contain("CompanyCatalog.Find(session.Company) is not null"));
            Assert.That(state, Does.Contain("dosid.Length <= LocalLogoutAction.MaxDosidLength"));
            Assert.That(state.IndexOf("dosid.Length <= LocalLogoutAction.MaxDosidLength", StringComparison.Ordinal),
                Is.LessThan(state.IndexOf("sessions.TryGetByDosidAsync(dosid, ct)", StringComparison.Ordinal)));
            Assert.That(state, Does.Contain("equipment.GetAsync(session.AccountId, ct)"));
            Assert.That(state, Does.Not.Contain("SearchPlanetAsync").And.Not.Contain("SearchPlanetResultAsync"));
        });
    }

    [Test]
    public void SpaceSearchCandidate_RejectionPathsRemainOutsideTheIdentityAndEquipmentBoundary()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "UnityApi", "CandidateEndpoints", "SpaceSearchCandidateAction.cs"));
        var validation = source.Split("public static bool ValidQuery", StringSplitOptions.None)[1]
            .Split("public static SpaceSearchResponse Search", StringSplitOptions.None)[0];
        var handlers = source.Split("public static async Task HandleSearchAsync", StringSplitOptions.None)[1]
            .Split("private static async Task<LocalEquipmentState?> StateAsync", StringSplitOptions.None)[0];
        var state = source.Split("private static async Task<LocalEquipmentState?> StateAsync", StringSplitOptions.None)[1]
            .Split("private static Task WriteAsync", StringSplitOptions.None)[0];
        Assert.Multiple(() =>
        {
            Assert.That(validation, Does.Not.Contain("sessions").And.Not.Contain("equipment"));
            Assert.That(handlers, Does.Not.Contain("TryGetByDosidAsync").And.Not.Contain("equipment.GetAsync"));
            Assert.That(handlers, Does.Contain("ErrorAsync(context.Response, 400, \"Invalid Space Search request.\""));
            Assert.That(handlers, Does.Contain("ErrorAsync(context.Response, 400, \"Invalid Space Search reward request.\""));
            Assert.That(handlers, Does.Contain("ErrorAsync(context.Response, 400, \"The planet value is invalid.\""));
            Assert.That(handlers, Does.Contain("ErrorAsync(context.Response, 404, \"That planet is not part of Space Search.\""));
            Assert.That(state, Does.Contain("sessions.TryGetByDosidAsync(dosid, ct)"));
            Assert.That(state, Does.Contain("equipment.GetAsync(session.AccountId, ct)"));
        });
    }

    [Test]
    public void SpaceSearchCandidate_UsesDecodedQueryCollectionForBothValidationAndPlanetSelection()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "UnityApi", "CandidateEndpoints", "SpaceSearchCandidateAction.cs"));
        var handlers = source.Split("public static async Task HandleSearchAsync", StringSplitOptions.None)[1]
            .Split("private static async Task<LocalEquipmentState?> StateAsync", StringSplitOptions.None)[0];
        Assert.Multiple(() =>
        {
            Assert.That(handlers, Does.Contain("ValidQuery(context.Request.Query)"));
            Assert.That(handlers, Does.Contain("var query = context.Request.Query;"));
            Assert.That(handlers, Does.Contain("query.TryGetValue(\"planet\", out var planetValue)"));
            Assert.That(handlers, Does.Not.Contain("QueryString.Value")
                .And.Not.Contain("HttpUtility.ParseQueryString"));
        });
    }

    [Test]
    public void SpaceSearchCandidate_AdmitsOnlyCatalogCompaniesBeforeReadingAccountEquipment()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "UnityApi", "CandidateEndpoints", "SpaceSearchCandidateAction.cs"));
        var resolver = source.Split("private static async Task<LocalEquipmentState?> StateAsync", StringSplitOptions.None)[1]
            .Split("private static Task WriteAsync", StringSplitOptions.None)[0];
        var search = source.Split("public static SpaceSearchResponse Search", StringSplitOptions.None)[1]
            .Split("public static SpaceSearchRewardResponse Reward", StringSplitOptions.None)[0];
        var reward = source.Split("public static SpaceSearchRewardResponse Reward", StringSplitOptions.None)[1]
            .Split("public static async Task HandleSearchAsync", StringSplitOptions.None)[0];

        Assert.Multiple(() =>
        {
            Assert.That(resolver.IndexOf("sessions.TryGetByDosidAsync(dosid, ct)", StringComparison.Ordinal),
                Is.LessThan(resolver.IndexOf("CompanyCatalog.Find(session.Company) is not null", StringComparison.Ordinal)));
            Assert.That(resolver.IndexOf("CompanyCatalog.Find(session.Company) is not null", StringComparison.Ordinal),
                Is.LessThan(resolver.IndexOf("equipment.GetAsync(session.AccountId, ct)", StringComparison.Ordinal)));
            Assert.That(search, Does.Contain("LocalSpaceSearch.Catalog")
                .And.Contain("state.SearchedPlanetIds.Contains(planet.Id)"));
            Assert.That(reward, Does.Contain("LocalSpaceSearch.Catalog.FirstOrDefault")
                .And.Contain("state?.Inventory.FirstOrDefault(item => item.Id == planet.RewardItemId)"));
            Assert.That(search + reward, Does.Not.Contain("SearchPlanetAsync")
                .And.Not.Contain("SaveAsync").And.Not.Contain("equipment.GetAsync"));
        });
    }

    [Test]
    public void SpaceSearchExplore_BoundsSessionCookieBeforeLookupAndFormParsing()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "IndexInternal", "InternalStart", "LocalSpaceSearchAction.cs"));
        var handler = source.Split("public static async Task HandleAsync", StringSplitOptions.None)[1]
            .Split("internal static async Task ErrorAsync", StringSplitOptions.None)[0];
        var cookie = handler.IndexOf("request.Cookies.TryGetValue(\"dosid\"", StringComparison.Ordinal);
        var blank = handler.IndexOf("string.IsNullOrWhiteSpace(dosid)", StringComparison.Ordinal);
        var bound = handler.IndexOf("dosid.Length > LocalLogoutAction.MaxDosidLength", StringComparison.Ordinal);
        var lookup = handler.IndexOf("sessions.TryGetByDosidAsync(dosid, ct)", StringComparison.Ordinal);
        var form = handler.IndexOf("request.ReadFormAsync(ct)", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(cookie, Is.GreaterThanOrEqualTo(0));
            Assert.That(blank, Is.GreaterThan(cookie));
            Assert.That(bound, Is.GreaterThan(blank));
            Assert.That(lookup, Is.GreaterThan(bound));
            Assert.That(form, Is.GreaterThan(lookup));
        });
    }

    [Test]
    public void SpaceSearchPage_EncodesOwnedRewardNameInRowDetailAndNotice()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "IndexInternal", "InternalStart", "LocalSpaceSearch.cs"));
        const string encodedReward = "HtmlEncoder.Default.Encode(reward?.Name ?? planet.RewardName)";
        Assert.That(Regex.Matches(source, Regex.Escape(encodedReward)).Count, Is.EqualTo(3));
    }

    [Test]
    public void SpaceSearchDetail_KeepsTextSearchRowsAndBoundsNeighborNavigation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "IndexInternal", "InternalStart", "LocalSpaceSearch.cs"));
        var page = source.Split("public static string Page", StringSplitOptions.None)[1]
            .Split("public static string ExploredNotice", StringSplitOptions.None)[0];
        var detail = source.Split("private static string Detail", StringSplitOptions.None)[1];
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("var matching = Catalog.Where(planet => Matches(state, planet, search)).ToArray()"));
            Assert.That(page, Does.Contain("visible.Contains(selected) ? visible : matching"));
            Assert.That(detail, Does.Contain("at > 0 ? Link(sequence[at - 1], \"prev\") : null"));
            Assert.That(detail, Does.Contain("at >= 0 && at < sequence.Count - 1 ? Link(sequence[at + 1], \"next\") : null"));
        });
    }

    [Test]
    public void SpaceSearchExplore_ValidatesFormShapeBeforeEconomyMutation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features",
            "IndexInternal", "InternalStart", "LocalSpaceSearchAction.cs"));
        var handler = source.Split("public static async Task HandleAsync", StringSplitOptions.None)[1]
            .Split("internal static async Task ErrorAsync", StringSplitOptions.None)[0];
        var mediaType = handler.IndexOf("request.HasFormContentType", StringComparison.Ordinal);
        var parse = handler.IndexOf("request.ReadFormAsync(ct)", StringComparison.Ordinal);
        var exactFields = handler.IndexOf("LocalHangarError.HasExactly(form, \"planet\")", StringComparison.Ordinal);
        var noFiles = handler.IndexOf("form.Files.Count != 0", StringComparison.Ordinal);
        var shape = handler.IndexOf("LocalSpaceSearch.IsPlanetIdShape(planet)", StringComparison.Ordinal);
        var mutation = handler.IndexOf("equipment.SearchPlanetResultAsync(session.AccountId, planet, ct)", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(mediaType, Is.GreaterThanOrEqualTo(0));
            Assert.That(parse, Is.GreaterThan(mediaType));
            Assert.That(exactFields, Is.GreaterThan(parse));
            Assert.That(noFiles, Is.GreaterThan(exactFields));
            Assert.That(shape, Is.GreaterThan(noFiles));
            Assert.That(mutation, Is.GreaterThan(shape));
        });
    }

    [Test]
    public void AdminHttpEndpoints_DeclareReadOrWritePolicyForTheirOperation()
    {
        var adminDirectory = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "World", "Admin");
        var endpoints = new (string File, string Class, string Permission)[]
        {
            ("AdminCommandEndpoint.cs", "AdminCommandEndpoint", "OpsWrite"),
            ("AdminCommandEndpoint.cs", "AdminCommandHistoryEndpoint", "OpsRead"),
            ("RuntimeVariableEndpoints.cs", "ListRuntimeVariablesEndpoint", "OpsRead"),
            ("RuntimeVariableEndpoints.cs", "RuntimeVariableHistoryEndpoint", "OpsRead"),
            ("RuntimeVariableEndpoints.cs", "GetRuntimeVariableEndpoint", "OpsRead"),
            ("RuntimeVariableEndpoints.cs", "SetRuntimeVariableEndpoint", "OpsWrite"),
            ("RuntimeVariableEndpoints.cs", "ResetRuntimeVariableEndpoint", "OpsWrite"),
            ("RuntimeVariableEndpoints.cs", "RollbackRuntimeVariableEndpoint", "OpsWrite")
        };
        foreach (var (file, className, permission) in endpoints)
        {
            var source = File.ReadAllText(Path.Combine(adminDirectory, file));
            var body = Regex.Match(source,
                $@"public sealed class {className}\b[\s\S]*?(?=public sealed class|\z)").Value;
            Assert.That(body, Is.Not.Empty, $"Missing admin endpoint {className}.");
            Assert.That(body, Does.Contain($"Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.{permission}))"),
                $"{className} must retain its {permission} policy.");
        }
    }

    [Test]
    public void InGameAdminIngress_AuthenticatesBeforeDispatchAndUsesSharedShortcutAttribution()
    {
        var world = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "World");
        var http = File.ReadAllText(Path.Combine(world, "WorldHttpBridge.cs"));
        var tcp = File.ReadAllText(Path.Combine(world, "WorldSessionHandler.cs"));
        var httpCommand = http[http.IndexOf("public static async Task AdminCommandAsync(", StringComparison.Ordinal)..
            http.IndexOf("public static async Task AdminVariablesAsync(", StringComparison.Ordinal)];
        var tcpCommand = tcp[tcp.IndexOf("if (packet.Value.Command == WorldCommandId.AdminCommandRequest)", StringComparison.Ordinal)..
            tcp.IndexOf("if (packet.Value.Command == WorldCommandId.MoveRequest", StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(httpCommand.IndexOf("AdminPilotAsync(context", StringComparison.Ordinal),
                Is.LessThan(httpCommand.IndexOf("JsonDocument.ParseAsync", StringComparison.Ordinal)));
            Assert.That(httpCommand.IndexOf("if (pilot is null) return", StringComparison.Ordinal),
                Is.LessThan(httpCommand.IndexOf("dispatcher.ExecuteAsync", StringComparison.Ordinal)));
            Assert.That(httpCommand, Does.Contain("InGameAdmin.Expand(input, pilot)"));
            Assert.That(httpCommand, Does.Contain("InGameAdmin.Actor(pilot, node)"));
            Assert.That(tcpCommand.IndexOf("!connected.IsAdmin", StringComparison.Ordinal),
                Is.LessThan(tcpCommand.IndexOf("adminCommands.ExecuteAsync", StringComparison.Ordinal)));
            Assert.That(tcpCommand, Does.Contain("InGameAdmin.Expand(commandText, connected)"));
            Assert.That(tcpCommand, Does.Contain("InGameAdmin.Actor(connected, localNodeId)"));
        });
    }

    [Test]
    public void IdentityHttpSlices_GateOnFlagFirstAndDependOnlyOnApplicationContracts()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Identity");
        var register = File.ReadAllText(Path.Combine(root, "Register", "RegisterEndpoint.cs"));
        var login = File.ReadAllText(Path.Combine(root, "Login", "LoginEndpoint.cs"));
        var profile = File.ReadAllText(Path.Combine(root, "Profile", "ProfileEndpoint.cs"));
        var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly), Is.Empty);
            Assert.That(sources, Has.None.Contain("Identity.Infrastructure").And.None.Contain("DbContext")
                .And.None.Contain("SetCompanyAsync").And.None.Contain("EnsureRealmRoleAsync"));
            Assert.That(register.IndexOf("IsEnabledAsync(\"Identity.Register\")", StringComparison.Ordinal),
                Is.GreaterThan(0).And.LessThan(register.IndexOf("CreateUserAsync", StringComparison.Ordinal)));
            Assert.That(register.IndexOf("CreateUserAsync", StringComparison.Ordinal),
                Is.LessThan(register.IndexOf("new RegisterAccountCommand", StringComparison.Ordinal)));
            Assert.That(login.IndexOf("IsEnabledAsync(\"Identity.Login\")", StringComparison.Ordinal),
                Is.GreaterThan(0).And.LessThan(login.IndexOf("ValidateCredentialsQuery", StringComparison.Ordinal)));
            Assert.That(login.IndexOf("ValidateCredentialsQuery", StringComparison.Ordinal),
                Is.LessThan(login.IndexOf("RequestAccessTokenAsync", StringComparison.Ordinal)));
            Assert.That(login.IndexOf("string.IsNullOrWhiteSpace(req.Username)", StringComparison.Ordinal),
                Is.GreaterThan(0).And.LessThan(login.IndexOf("req.Username.Trim()", StringComparison.Ordinal)));
            Assert.That(register, Does.Contain("AllowAnonymous()"));
            Assert.That(login, Does.Contain("AllowAnonymous()"));
            Assert.That(profile, Does.Not.Contain("AllowAnonymous()").And.Not.Contain("IFeatureManager"));
        });
    }

    [Test]
    public void SasAuthentication_ChecksBlankThenAccountThenKeycloakBeforeSessionAndUsesSharedRedirects()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Sas", "Authentication");
        var endpoint = File.ReadAllText(Path.Combine(slice, "SasAuthenticationEndpoint.cs"));
        var sources = Directory.GetFiles(slice, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        int At(string text) => endpoint.IndexOf(text, StringComparison.Ordinal);
        var failureHelper = At("private async Task RedirectLoginFailureAsync");

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("Post(\"/Bigpoint\")").And.Contain("RoutePrefixOverride(\"Sas/Authentication\")")
                .And.Contain("AllowAnonymous()"));
            Assert.That(sources, Has.None.Contain("Identity.Infrastructure").And.None.Contain("DbContext")
                .And.None.Contain("CreateUserAsync").And.None.Contain("ValidateCredentialsQuery")
                .And.None.Contain("RotateRedirectTokenAsync").And.None.Contain("SelectCompanyAsync"));
            Assert.That(At("string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)"),
                Is.GreaterThan(0).And.LessThan(At("new GetAccountByUsernameQuery(username)")));
            Assert.That(At("new GetAccountByUsernameQuery(username)"), Is.LessThan(At("RequestAccessTokenAsync(username, password")));
            Assert.That(At("RequestAccessTokenAsync(username, password"), Is.LessThan(At("CreateSessionAsync(account, accessToken")));
            Assert.That(At("CreateSessionAsync(account, accessToken"), Is.LessThan(At("aliyungf_tc=")));
            Assert.That(At("aliyungf_tc="), Is.GreaterThan(0).And.LessThan(failureHelper));
            Assert.That(endpoint[failureHelper..], Does.Contain("EmulatorRedirectHelper.ToRedirectLocation")
                .And.Not.Contain("Set-Cookie").And.Not.Contain("Strict-Transport-Security"));
            Assert.That(Regex.Matches(endpoint, @"ClientRedirectResponse\.Apply\(").Count, Is.EqualTo(2));
            Assert.That(endpoint, Does.Contain("Uri.EscapeDataString(redirectToken)"));
        });
    }

    [Test]
    public void ClientFacingGameHost_DelegatesHostTrustToEmulatorRedirectHelperAndAnchorsGameHostPattern()
    {
        var common = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Mocks", "Common");
        var resolver = File.ReadAllText(Path.Combine(common, "ClientFacingGameHost.cs"));
        var helper = File.ReadAllText(Path.Combine(common, "EmulatorRedirectHelper.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(resolver, Does.Not.Contain("\"localhost\"").And.Not.Contain("\"127.0.0.1\"")
                .And.Not.Contain("\"api-game\"").And.Not.Contain(".Split("));
            Assert.That(resolver, Does.Contain("EmulatorRedirectHelper.IsEmulatorHost(request)")
                .And.Contain("EmulatorRedirectHelper.IsLoopbackHost(")
                .And.Contain("EmulatorRedirectHelper.FirstHeaderValue(request.Headers[\"X-Forwarded-Host\"])"));
            Assert.That(resolver, Does.Contain(@"@""\Agbl[1-9][0-9]*\.darkorbit\.com\z"""));
            Assert.That(helper, Does.Contain("!request.Headers.ContainsKey(\"X-Original-Host\")"));
            Assert.That(Regex.Matches(helper, @"IndexOf\(',', StringComparison\.Ordinal\)").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void ProjectApiAuthentication_RotatesBeforeCookiesAndKeepsSessionWorkInGameAuth()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "ProjectApi", "Authentication");
        var endpoint = File.ReadAllText(Path.Combine(slice, "ProjectApiAuthenticationEndpoint.cs"));
        var parser = File.ReadAllText(Path.Combine(slice, "ProjectApiAuthenticationRequestContext.cs"));
        int At(string value) => endpoint.IndexOf(value, StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("Get(\"/Authentication\")")
                .And.Contain("RoutePrefixOverride(\"ProjectApi\")").And.Contain("AllowAnonymous()"));
            Assert.That(At("RotateRedirectTokenAsync"), Is.GreaterThan(0).And.LessThan(At("if (rotation is null)")));
            Assert.That(At("if (rotation is null)"), Is.LessThan(At("AppendSessionCookies(dosid")));
            Assert.That(endpoint, Does.Contain("EmulatorRedirectHelper.ToRedirectLocation")
                .And.Contain("Uri.EscapeDataString(nextToken)")
                .And.Not.Contain("Identity.Infrastructure").And.Not.Contain("DbContext")
                .And.Not.Contain("CreateSessionAsync").And.Not.Contain("TryGetByDosidAsync"));
            Assert.That(parser, Does.Contain("request.Query[\"authUser\"]")
                .And.Contain("request.Query[\"token\"]"));
        });
    }

    [Test]
    public void UnityLogin_RejectsBeforeCookieRefreshAndResolvesGameServerOnlyFromMeshSnapshot()
    {
        var slice = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "UnityApi", "Login");
        var action = File.ReadAllText(Path.Combine(slice, "UnityLoginAction.cs"));
        var builder = File.ReadAllText(Path.Combine(slice, "UnityLoginPayloadBuilder.cs"));
        var endpoint = File.ReadAllText(Path.Combine(slice, "UnityLoginEndpoint.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("Get(\"/login.php\")").And.Contain("RoutePrefixOverride(\"unityApi\")"));
            Assert.That(action.IndexOf("Status401Unauthorized", StringComparison.Ordinal),
                Is.LessThan(action.IndexOf("Cookies.Append", StringComparison.Ordinal)));
            Assert.That(action.IndexOf("dosid.Length <= LocalLogoutAction.MaxDosidLength", StringComparison.Ordinal),
                Is.LessThan(action.IndexOf("TryGetByDosidAsync", StringComparison.Ordinal)));
            Assert.That(action, Does.Contain("registry.GetSnapshotAsync"));
            Assert.That(action, Does.Not.Contain("HeartbeatAsync").And.Not.Contain("RebalanceAsync")
                .And.Not.Contain("ScaleAsync").And.Not.Contain("SetDrainingAsync"));
            Assert.That(builder, Does.Not.Contain("IServerMeshRegistry").And.Not.Contain("LocalMapsXml"));
            Assert.That(builder, Does.Contain("ServerMeshNodeStatus.Active").And.Contain("ExpiresAtUtc > nowUtc"));
        });
    }

    [Test]
    public void UnityPublicResources_KeepLinksSessionOptionalAndNewsClockOnly()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "UnityApi");
        var linksEndpoint = File.ReadAllText(Path.Combine(root, "Links", "LinksEndpoint.cs"));
        var linksAction = File.ReadAllText(Path.Combine(root, "Links", "UnityLinksAction.cs"));
        var newsEndpoint = File.ReadAllText(Path.Combine(root, "News", "NewsEndpoint.cs"));
        var newsAction = File.ReadAllText(Path.Combine(root, "News", "UnityNewsAction.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(linksEndpoint, Does.Contain("Get(\"/links.php\")")
                .And.Contain("RoutePrefixOverride(\"unityApi\")")
                .And.Contain("AllowAnonymous();"));
            Assert.That(newsEndpoint, Does.Contain("Get(\"/news.php\")")
                .And.Contain("RoutePrefixOverride(\"unityApi\")")
                .And.Contain("AllowAnonymous();"));
            Assert.That(linksAction, Does.Contain("dosid.Length <= LocalLogoutAction.MaxDosidLength")
                .And.Contain("if (session is not null)")
                .And.Contain("ClientFacingGameHost.Resolve(request)"));
            Assert.That(newsAction, Does.Contain("CapturedNewsCatalog.News(clock.GetUtcNow())"));
            Assert.That(newsAction, Does.Not.Contain("IGameAuthSessionStore")
                .And.Not.Contain("Request.Cookies")
                .And.Not.Contain("SetCookie"));
        });
    }

    [Test]
    public void GameApiForum_UsesCanonicalActionGateBeforeBoundedSessionLookupAndSharedForumValidation()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features");
        var endpoint = File.ReadAllText(Path.Combine(root, "GameAuth", "GameApiForumEndpoint.cs"));
        var action = File.ReadAllText(Path.Combine(root, "GameAuth", "GameApiForumAction.cs"));
        var links = File.ReadAllText(Path.Combine(root, "IndexInternal", "InternalStart", "LocalLinksRequest.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("Get(\"/GameAPI.php\")")
                .And.Contain("RoutePrefixOverride(string.Empty)")
                .And.Contain("AllowAnonymous();")
                .And.Contain("LocalLinksRequest.WriteAsync"));
            Assert.That(action, Does.Contain("request.Query.Keys.Contains(\"action\", StringComparer.Ordinal)")
                .And.Contain("actions.Count != 1")
                .And.Contain("string.Equals(actions.ToString(), BoardAction, StringComparison.Ordinal)"));
            Assert.That(action.IndexOf("return LocalLinksRequest.UnknownGameApiAction()", StringComparison.Ordinal),
                Is.LessThan(action.IndexOf("TryGetByDosidAsync", StringComparison.Ordinal)));
            Assert.That(action.IndexOf("dosid.Length <= LocalLogoutAction.MaxDosidLength", StringComparison.Ordinal),
                Is.LessThan(action.IndexOf("TryGetByDosidAsync", StringComparison.Ordinal)));
            Assert.That(action, Does.Contain("LocalLinksRequest.Resolve(request.Query, session, LocalLinksRequest.Page.Forum)"));
            Assert.That(links, Does.Contain("response.Headers.CacheControl = \"no-store\";"));
        });
    }

    [Test]
    public void FlashBreakingNews_SharesOneStatelessWriterBetweenBrNewsAndCommandCenterAlias()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "FlashApi", "BrNews");
        var brNews = File.ReadAllText(Path.Combine(root, "BrNewsEndpoint.cs"));
        var legacy = File.ReadAllText(Path.Combine(root, "LegacyXmlCommandCenterEndpoint.cs"));
        var writer = File.ReadAllText(Path.Combine(root, "CommandCenterNewsResponse.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(brNews, Does.Contain("Get(\"/brNews.php\")")
                .And.Contain("RoutePrefixOverride(\"flashAPI\")")
                .And.Contain("AllowAnonymous();")
                .And.Contain("CommandCenterNewsResponse.WriteAsync(HttpContext.Response, ct)"));
            Assert.That(legacy, Does.Contain("Get(\"/CommandCenter.xml\")")
                .And.Contain("RoutePrefixOverride(\"xml\")")
                .And.Contain("AllowAnonymous();")
                .And.Contain("CommandCenterNewsResponse.WriteAsync(HttpContext.Response, ct)"));
            Assert.That(writer, Does.Contain("ClientStubResponses.BreakingNews"));
            foreach (var source in new[] { brNews, legacy, writer })
                Assert.That(source, Does.Not.Contain("IGameAuthSessionStore")
                    .And.Not.Contain("Request.Cookies")
                    .And.Not.Contain("Request.Query")
                    .And.Not.Contain("SetCookie")
                    .And.Not.Contain("TimeProvider"));
        });
    }

    [Test]
    public void TelemetrySinkMocks_AreAnonymousRootRoutedAndStatelessPerDnsFolder()
    {
        var mocks = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Mocks");
        var sinks = new (string Folder, string Dns, string[] Routes)[]
        {
            ("BatBingCom", "bat.bing.com", ["Get(\"/bat.js\")", "Get(\"/p/action/97130958.js\")"]),
            ("BatBingNet", "bat.bing.net", ["Post(\"/actionp/0\")", "Get(\"/action/0\")"]),
            ("CdpCloudUnity3dCom", "cdp.cloud.unity3d.com", ["Post(\"/v1/events\")"]),
            ("ChromeCloudflareDnsCom", "chrome.cloudflare-dns.com", ["Post(\"/dns-query\")"]),
            ("GoogleAnalyticsCom", "www.google-analytics.com", ["Post(\"/g/collect\")"]),
            ("Pagead2GooglesyndicationCom", "pagead2.googlesyndication.com", ["Post(\"/ccm/collect\")"]),
            ("PixelQuantcountCom", "pixel.quantcount.com", ["Post(\"/pixel\")", "Get(\"/pixel\")"]),
        };

        Assert.Multiple(() =>
        {
            foreach (var (folder, dns, routes) in sinks)
            {
                var files = Directory.GetFiles(Path.Combine(mocks, folder), "*.cs");
                Assert.That(files, Has.Length.EqualTo(1), folder);
                var source = File.ReadAllText(files[0]);
                var endpoints = source.Split("public sealed class ").Length - 1;

                Assert.That(source, Does.Contain($"namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.{folder};"), folder);
                foreach (var route in routes)
                    Assert.That(source, Does.Contain(route), folder);
                Assert.That(endpoints, Is.EqualTo(routes.Length), folder);
                Assert.That(source.Split("RoutePrefixOverride(string.Empty);").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source.Split("AllowAnonymous();").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source.Split($"WithTags(\"DNS:{dns}\")").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source, Does.Not.Contain("Request.")
                    .And.Not.Contain("IGameAuthSessionStore")
                    .And.Not.Contain("Cookies")
                    .And.Not.Contain("TimeProvider")
                    .And.Not.Contain("HttpClient"), folder);
            }
        });
    }

    [Test]
    public void BrowserSupportMocks_KeepHostSlicesAnonymousAndRequestIndependent()
    {
        var mocks = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Mocks");
        var hosts = new (string Folder, string Dns, int Endpoints)[]
        {
            ("CmpInmobiCom", "cmp.inmobi.com", 4),
            ("WwwGoogletagmanagerCom", "www.googletagmanager.com", 3),
            ("AssetsBpsecureCom", "assets.bpsecure.com", 1),
            ("SecureQuantserveCom", "secure.quantserve.com", 1),
            ("RulesQuantcountCom", "rules.quantcount.com", 1),
            ("SharedservicesBpsecureCom", "sharedservices.bpsecure.com", 3),
        };

        Assert.Multiple(() =>
        {
            foreach (var (folder, dns, endpoints) in hosts)
            {
                var files = Directory.GetFiles(Path.Combine(mocks, folder), "*.cs");
                Assert.That(files, Has.Length.EqualTo(1), folder);
                var source = File.ReadAllText(files[0]);
                Assert.That(source, Does.Contain($"namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.{folder};"), folder);
                Assert.That(source.Split("public sealed class ").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source.Split("RoutePrefixOverride(string.Empty);").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source.Split("AllowAnonymous();").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source.Split($"WithTags(\"DNS:{dns}\")").Length - 1, Is.EqualTo(endpoints), folder);
                Assert.That(source, Does.Not.Contain("Request.")
                    .And.Not.Contain("IGameAuthSessionStore")
                    .And.Not.Contain("Cookies")
                    .And.Not.Contain("TimeProvider")
                    .And.Not.Contain("HttpClient"), folder);
            }
        });
    }

    [Test]
    public void ChallengeAndUnityConfigMocks_KeepHostSlicesAnonymousAndRequestIndependent()
    {
        var mocks = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Mocks");
        var hosts = new (string Folder, string Tags, string Verb)[]
        {
            ("JsHcaptchaCom", "WithTags(\"DNS:js.hcaptcha.com\")", "Get(\"/1/api.js\");"),
            ("HcaptchaLogos", "WithTags(\"DNS:c47f56fecf32.w.hcaptcha.com\", \"DNS:7dd0d2f1479d.w.hcaptcha.com\", \"DNS:ac36ddf2442a.w.hcaptcha.com\")", "Get(\"/logo.png\");"),
            ("ConfigUcaCloudUnity3dCom", "WithTags(\"DNS:config.uca.cloud.unity3d.com\")", "Post(\"/\");"),
        };

        Assert.Multiple(() =>
        {
            foreach (var (folder, tags, verb) in hosts)
            {
                var files = Directory.GetFiles(Path.Combine(mocks, folder), "*.cs");
                Assert.That(files, Has.Length.EqualTo(1), folder);
                var source = File.ReadAllText(files[0]);
                Assert.That(source, Does.Contain($"namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.{folder};"), folder);
                Assert.That(source.Split("public sealed class ").Length - 1, Is.EqualTo(1), folder);
                Assert.That(source.Split("RoutePrefixOverride(string.Empty);").Length - 1, Is.EqualTo(1), folder);
                Assert.That(source.Split("AllowAnonymous();").Length - 1, Is.EqualTo(1), folder);
                Assert.That(source, Does.Contain(tags).And.Contain(verb), folder);
                Assert.That(source, Does.Not.Contain("Request.")
                    .And.Not.Contain("IGameAuthSessionStore")
                    .And.Not.Contain("Cookies")
                    .And.Not.Contain("TimeProvider")
                    .And.Not.Contain("HttpClient"), folder);
            }

            var config = File.ReadAllText(Path.Combine(mocks, "ConfigUcaCloudUnity3dCom", "ConfigEndpoint.cs"));
            Assert.That(config, Does.Contain("FlowCaptureLoader.LoadResponseBody(\"65d1d8c1-84c4-4036-893f-ffedc42657fd\")"));
        });
    }

    [Test]
    public void HomePage_DispatchesPaymentProxyBeforeCookiesAndRewritesOnlyThroughSharedHelper()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Mocks", "WwwDarkorbitCom");
        var files = Directory.GetFiles(root, "*.cs");
        var source = File.ReadAllText(Path.Combine(root, "HomeEndpoint.cs"));
        var proxyGate = source.IndexOf("LocalPaymentProxyResponse.IsRequest(HttpContext.Request)", StringComparison.Ordinal);
        var firstCookie = source.IndexOf("Headers.Append(\"Set-Cookie\"", StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(files.Select(Path.GetFileName), Is.EqualTo(new[] { "HomeEndpoint.cs" }));
            Assert.That(source.Split("public sealed class ").Length - 1, Is.EqualTo(1));
            Assert.That(source, Does.Contain("Get(\"/\");")
                .And.Contain("RoutePrefixOverride(string.Empty);")
                .And.Contain("AllowAnonymous();")
                .And.Contain("WithTags(\"DNS:www.darkorbit.com\")"));
            Assert.That(proxyGate, Is.GreaterThan(0));
            Assert.That(firstCookie, Is.GreaterThan(proxyGate));
            Assert.That(source.IndexOf("return;", proxyGate, StringComparison.Ordinal), Is.InRange(proxyGate, firstCookie));
            Assert.That(source.Split("Headers.Append(\"Set-Cookie\"").Length - 1, Is.EqualTo(5));
            Assert.That(source, Does.Contain("GetBody(HttpContext.Request, HomeHtml)"));
            Assert.That(source, Does.Contain("EmulatorRedirectHelper.RewriteLoginPageHtml(request, capturedHtml)"));
            Assert.That(source, Does.Not.Contain("Regex").And.Not.Contain(".Replace(").And.Not.Contain("HttpClient").And.Not.Contain("TimeProvider"));
        });
    }

    [Test]
    public void PortalStaticAssets_KeepAnonymousHostRoutesAndImageFallbackOrder()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Mocks", "Darkorbit22BpsecureCom");
        var source = File.ReadAllText(Path.Combine(root, "StaticAssetsEndpoints.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(source.Split("public sealed class ").Length - 1, Is.EqualTo(4));
            foreach (var route in new[] { "Get(\"/css/{**catch-all}\")", "Get(\"/js/{**catch-all}\")", "Get(\"/do_img/{**catch-all}\")", "Get(\"/favicon.ico\")" })
                Assert.That(source, Does.Contain(route));
            Assert.That(source.Split("RoutePrefixOverride(string.Empty);").Length - 1, Is.EqualTo(4));
            Assert.That(source.Split("AllowAnonymous();").Length - 1, Is.EqualTo(4));
            Assert.That(source.Split("WithTags(\"DNS:darkorbit-22.bpsecure.com\")").Length - 1, Is.EqualTo(4));
            Assert.That(source.IndexOf("CommandCenterVisualAssets.TryWriteArtworkAsync", StringComparison.Ordinal),
                Is.LessThan(source.IndexOf("LocalShipIcons.PusatPath", StringComparison.Ordinal)));
            Assert.That(source.IndexOf("LocalShipIcons.PhoenixPath", StringComparison.Ordinal),
                Is.LessThan(source.IndexOf("MockResponseHelpers.TinyPngBytes", StringComparison.Ordinal)));
            Assert.That(source, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("HttpClient"));
        });
    }

    [Test]
    public void ExpeditionBoxes_IsSingleFileStatelessAnonymousXmlResourceReusingEventsReward()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Resources", "Expedition");
        var source = File.ReadAllText(Path.Combine(root, "ExpeditionBoxesEndpoint.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(Directory.GetFiles(root, "*.cs").Select(Path.GetFileName), Is.EqualTo(new[] { "ExpeditionBoxesEndpoint.cs" }));
            Assert.That(source.Split("public sealed class ").Length - 1, Is.EqualTo(1));
            Assert.That(source, Does.Contain("Get(\"/events/expedition/expedition-boxes.xml\");")
                .And.Contain("RoutePrefixOverride(\"xml\");")
                .And.Contain("AllowAnonymous();")
                .And.Contain("WithTags(\"DNS:gbl3.darkorbit.com\")")
                .And.Contain("public sealed class ExpeditionBoxesEndpoint : EndpointWithoutRequest"));
            Assert.That(source, Does.Contain("LocalEvents.ExpeditionRewardItemId"));
            Assert.That(source, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("EconomyStore")
                .And.Not.Contain("HttpClient").And.Not.Contain("TimeProvider").And.Not.Contain("DateTime")
                .And.Not.Contain("Request."));
        });
    }

    [Test]
    public void CommandCenterCategoryAliases_ShareOneManifestValidatedWriterWithoutSessionState()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Resources", "CommandCenter");
        var cdn = File.ReadAllText(Path.Combine(root, "CommandCenterCategoriesEndpoint.cs"));
        var legacy = File.ReadAllText(Path.Combine(root, "LegacyXmlCategoriesEndpoint.cs"));
        var writer = File.ReadAllText(Path.Combine(root, "CapturedCommandCenterCategories.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(cdn, Does.Contain("Get(\"/command-center/categories.xml\");")
                .And.Contain("RoutePrefixOverride(\"resources\");")
                .And.Contain("WithTags(\"CDN:darkorbit-22.bpsecure.com\")"));
            Assert.That(legacy, Does.Contain("Get(\"/Categories.xml\");")
                .And.Contain("RoutePrefixOverride(\"xml\");")
                .And.Contain("WithTags(\"DNS:gbl3.darkorbit.com\")"));
            foreach (var endpoint in new[] { cdn, legacy })
            {
                Assert.That(endpoint, Does.Contain("AllowAnonymous();")
                    .And.Contain("CapturedCommandCenterCategories.WriteResponseAsync(HttpContext, ct)"));
                Assert.That(endpoint.Split("public sealed class ").Length - 1, Is.EqualTo(1));
                Assert.That(endpoint, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("HttpClient")
                    .And.Not.Contain("TimeProvider").And.Not.Contain("Query"));
            }
            Assert.That(writer, Does.Contain("GetManifestResourceStream(\"CommandCenterCategories.xml\")")
                .And.Contain("CommandCenterVisualAssets.Find(ManifestPath)")
                .And.Contain("CommandCenterVisualAssets.IsNotModified(context.Request, etag)"));
            Assert.That(writer, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("DateTime")
                .And.Not.Contain("Request.Path").And.Not.Contain("Request.Query").And.Not.Contain("Cookies"));
        });
    }

    [Test]
    public void CommandCenterManifestAliases_ShareCapturedWriterWithoutSessionState()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Resources", "CommandCenter");
        var captured = File.ReadAllText(Path.Combine(root, "ResourceCommandCenterEndpoint.cs"));
        var legacy = File.ReadAllText(Path.Combine(root, "LegacyXmlCommandCenterResourcesEndpoint.cs"));
        var writer = File.ReadAllText(Path.Combine(root, "CapturedCommandCenterResources.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(captured, Does.Contain("Get(\"/resource_command_center.xml\");")
                .And.Contain("RoutePrefixOverride(\"resources\");"));
            Assert.That(legacy, Does.Contain("Get(\"/resource_command_center.xml\");")
                .And.Contain("RoutePrefixOverride(\"xml\");"));
            foreach (var endpoint in new[] { captured, legacy })
            {
                Assert.That(endpoint, Does.Contain("AllowAnonymous();")
                    .And.Contain("WithTags(\"DNS:gbl3.darkorbit.com\")")
                    .And.Contain("CapturedCommandCenterResources.WriteResponseAsync(HttpContext.Response, ct)"));
                Assert.That(endpoint, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("HttpClient")
                    .And.Not.Contain("Request.Query").And.Not.Contain("Request.Cookies"));
            }
            Assert.That(writer, Does.Contain("GetManifestResourceStream(\"CommandCenterResources.xml\")"));
            Assert.That(writer, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("HttpClient")
                .And.Not.Contain("TimeProvider").And.Not.Contain("Request."));
        });
    }

    [Test]
    public void MapResourceAliases_UseOneMeshAwareWriterWithoutSessionState()
    {
        var root = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "Resources", "Maps");
        var php = File.ReadAllText(Path.Combine(root, "LegacyMapsPhpEndpoint.cs"));
        var xml = File.ReadAllText(Path.Combine(root, "LegacyMapsXmlEndpoint.cs"));
        var writer = File.ReadAllText(Path.Combine(root, "LocalMapsXml.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(Directory.GetFiles(root, "*.cs").Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "LegacyMapsPhpEndpoint.cs", "LegacyMapsXmlEndpoint.cs", "LocalMapsXml.cs" }));
            Assert.That(php, Does.Contain("Get(\"/maps.php\");"));
            Assert.That(xml, Does.Contain("Get(\"/maps.xml\");"));
            foreach (var endpoint in new[] { php, xml })
            {
                Assert.That(endpoint, Does.Contain("RoutePrefixOverride(\"xml\");")
                    .And.Contain("AllowAnonymous();")
                    .And.Contain("WithTags(\"DNS:gbl3.darkorbit.com\")")
                    .And.Contain("LocalMapsXml.WriteResponseAsync(HttpContext.Response, world, registry, ct)"));
                Assert.That(endpoint, Does.Not.Contain("IGameAuthSessionStore").And.Not.Contain("HttpClient"));
            }
            Assert.That(writer, Does.Contain("CompanyCatalog.Companies")
                .And.Contain("item.ExpiresAtUtc > now")
                .And.Contain("node.Status == ServerMeshNodeStatus.Active")
                .And.Contain("node.WorldTcpPort is > 0 and <= 65535")
                .And.Contain("registry.GetSnapshotAsync(ct)"));
        });
    }

    [Test]
    public void WorldNpcPackets_ArePureWorldPayloadEmitters_SentOnlyUnderTheirCommandIds()
    {
        var world = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "World");
        var emitter = File.ReadAllText(Path.Combine(world, "WorldNpcPackets.cs"));
        var callSites = Directory.GetFiles(world, "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "WorldNpcPackets.cs")
            .SelectMany(path => File.ReadAllLines(path))
            .Where(line => line.Contains("WorldNpcPackets.", StringComparison.Ordinal))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(emitter, Does.Contain("public static class WorldNpcPackets"));
            Assert.That(emitter, Does.Not.Contain("using ").And.Not.Contain("async").And.Not.Contain("Stream")
                .And.Not.Contain("Cache").And.Not.Contain("npc.Credits").And.Not.Contain("npc.Honor"));
            Assert.That(Regex.Matches(emitter, @"WorldPayload\.Write\w+\(w, npc\.\w+\)").Count, Is.EqualTo(14));
            Assert.That(callSites, Is.Not.Empty);
            foreach (var line in callSites.Where(line => line.Contains("WorldNpcPackets.Health(", StringComparison.Ordinal)))
                Assert.That(line, Does.Contain("WorldCommandId.NpcHealthCommand"));
            foreach (var line in callSites.Where(line => line.Contains("WorldNpcPackets.Create(", StringComparison.Ordinal)
                && line.Contains("WorldCommandId.", StringComparison.Ordinal)))
                Assert.That(line, Does.Contain("WorldCommandId.ShipCreateCommand"));
            Assert.That(callSites.Count(line => line.Contains("WorldCommandId.ShipCreateCommand, WorldNpcPackets.Create(npc)",
                StringComparison.Ordinal)), Is.EqualTo(2));
        });
    }

    [Test]
    public void WorldNpcSimulation_EmitsOnlyForLiveOwnedLeases_InTickOrder()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldNpcSimulationService.cs"));
        var stages = new[]
        {
            "variables.NumberAsync(\"world.tick_rate_ms\", stoppingToken)", "await Task.Delay(",
            "registry.GetSnapshotAsync(stoppingToken)", "adminEvents.LatestAsync(stoppingToken)",
            "announcement.AtUtc > lastAnnouncement", "sessions.BroadcastAnnouncementAsync(",
            "foreach (var mapId in known.Keys.Where(id => !owned.Contains(id)).ToArray())",
            "npcs.VisibleAsync(lease.MapId, stoppingToken)", "cycle != npcs.CurrentCycle",
            "if (ids.Add(npc.Id)) await sessions.BroadcastNpcSpawnAsync(",
            "await sessions.BroadcastNpcMoveAsync(", "ids.IntersectWith(visible.Select(n => n.Id))",
            "collectibles.VisibleAsync(lease.MapId, stoppingToken)", ".Where(b => b.Id < 0)",
            "if (boxIds.Add(box.Id))", "sessions.BroadcastAdminBoxAsync(",
            "boxIds.IntersectWith(boxes.Select(b => b.Id))"
        };
        var positions = stages.Select(stage => source.IndexOf(stage, StringComparison.Ordinal)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Has.All.GreaterThanOrEqualTo(0));
            Assert.That(positions, Is.Ordered, "the tick must pace, read leases, announce, prune, then emit");
            Assert.That(Regex.Matches(source,
                    @"snapshot\.Leases\.Where\(l => l\.NodeId == nodeId && l\.ExpiresAtUtc > DateTimeOffset\.UtcNow\)")
                .Count, Is.EqualTo(2), "pruning and emission must share the live-owned lease filter");
            Assert.That(source, Does.Contain("{ known.Remove(mapId); knownBoxes.Remove(mapId); cycles.Remove(mapId); }"));
            Assert.That(source, Does.Contain("variables is null ? 1000 :"));
            Assert.That(source, Does.Not.Contain("SetDrainingAsync").And.Not.Contain("HeartbeatAsync")
                .And.Not.Contain("TryAcquire").And.Not.Contain("WorldNpcPackets.")
                .And.Not.Contain("BroadcastNpcDamageAsync"), "the simulation is a read-only lease consumer");
        });
    }

    [Test]
    public void WorldCompanions_ArePureProjections_BuiltAtEntryAndPositionedOnlyByWorldState()
    {
        var worldDir = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features", "World");
        var source = File.ReadAllText(Path.Combine(worldDir, "WorldCompanions.cs"));
        var projection = source[source.IndexOf("public static class WorldCompanions", StringComparison.Ordinal)..];
        var callers = Directory.GetFiles(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService"), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && Path.GetFileName(path) != "WorldCompanions.cs")
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(projection, Does.Not.Contain("cache").And.Not.Contain("DateTime").And.Not.Contain("Random")
                .And.Not.Contain("async").And.Not.Contain("Task"), "companion projection must stay pure and synchronous");
            Assert.That(projection, Does.Contain("LocalHangarEquipmentStore.DroneSlots"),
                "the drone cap must reuse the hangar drone-slot limit");
            Assert.That(projection, Does.Contain("equipment.Inventory.Any(i => i.Id == assignment.ItemId && i.Quantity >= assignment.Quantity)"),
                "one exact-ID inventory row must cover each drone assignment in full");
            Assert.That(projection, Does.Contain("Math.Clamp((long)x + dx, 0, 21000)")
                .And.Contain("Math.Clamp((long)y + dy, 0, 13100)"),
                "companion offsets must be widened before map clamping");
            Assert.That(source, Does.Contain("$\"world:pet:{accountId:N}\"")
                .And.Contain("SlidingExpiration = TimeSpan.FromDays(30)"));
            Assert.That(source, Does.Not.Contain("AbsoluteExpiration =")
                .And.Not.Contain("AbsoluteExpirationRelativeToNow ="),
                "pet lifetime is sliding, without an earlier absolute cutoff");
            Assert.That(source.IndexOf("await cache.GetStringAsync(Key(accountId), ct)", StringComparison.Ordinal),
                Is.LessThan(source.IndexOf("        try", StringComparison.Ordinal)),
                "cache read failures must remain outside the malformed-JSON fallback");
            Assert.That(source, Does.Contain("catch (JsonException)")
                .And.Not.Contain("catch (Exception)"),
                "damaged pet JSON falls back locally without hiding cache or cancellation failures");
            Assert.That(source, Does.Contain("record LocalPet(bool Enabled = false, int Fuel = 0, string Formation = \"standard\")")
                .And.Contain("JsonSerializer.Deserialize<LocalPet>(json)"),
                "sparse cache records must retain inactive constructor defaults with exact JSON property names");
            Assert.That(source, Does.Contain("if (pet.Enabled && pet.Fuel > 0 && equipment.Assignments.Any("),
                "an enabled pet without positive fuel must remain absent from the world projection");
            Assert.That(projection, Does.Contain("a.ItemId == \"pet_10\" && a.Quantity > 0")
                .And.Contain("equipment.Inventory.Any(i => i.Id == \"pet_10\" && i.Quantity > 0)"),
                "P.E.T. visibility needs an exact positive assignment and a single exact positive inventory row");
            Assert.That(projection, Does.Contain("result.Add(new WorldCompanion(\"pet\", \"pet_10\", 0, pet.Formation))"),
                "one P.E.T. is appended after drone expansion even when assignments repeat");
            Assert.That(callers.Where(c => c.Text.Contains("WorldCompanions.FromEquipment(")).Select(c => c.Name),
                Is.EqualTo(new[] { "WorldSessionHandler.cs" }), "companions are derived only at world entry");
            Assert.That(callers.Where(c => c.Text.Contains("WorldCompanions.At(")).Select(c => c.Name),
                Is.EqualTo(new[] { "WorldState.cs" }), "companion positions are projected only by world state");
        });
    }

    [Test]
    public void WorldCompanionPositioning_UsesStoredIndexesAndPerEntryFormation()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldCompanions.cs"));
        var positioning = source[source.IndexOf("public static IReadOnlyList<WorldCompanionPosition> At(", StringComparison.Ordinal)..];

        Assert.Multiple(() =>
        {
            Assert.That(positioning, Does.Contain("companions.Count(c => c.Kind == \"drone\")"),
                "pets must not change the standard drone angle denominator");
            Assert.That(positioning, Does.Contain("c.Index + 1")
                .And.Contain("c.Index / 2")
                .And.Contain("c.Formation switch"),
                "positioning must use each stored index and formation instead of sequence position");
            Assert.That(positioning, Does.Contain("c.Kind == \"pet\" ? (-75, 55)"),
                "pet positioning must ignore its index and formation");
        });
    }

    [Test]
    public void CompanionState_ProjectsCurrentAndTargetFromSameActiveConfiguration_WithSharedWireWriter()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "World", "WorldState.cs"));
        var start = source.IndexOf("private byte[] CompanionsPayload(WorldPilot pilot)", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task BroadcastAsync(", start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));
        var payload = source[start..end];
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("writer.WriteAsync(WorldCommandId.CompanionStateCommand, CompanionsPayload(pilot), ct)"));
            Assert.That(source, Does.Contain("BroadcastAsync(pilot, WorldCommandId.CompanionStateCommand, CompanionsPayload(pilot), ct)"));
            Assert.That(payload, Does.Contain("var companions = CompanionPositions(pilot);"));
            Assert.That(payload, Does.Contain("var (targetX, targetY) = Destination(pilot);"));
            Assert.That(payload, Does.Contain("? pilot.ConfigurationTwoCompanions : pilot.Companions, targetX, targetY)"));
            Assert.That(payload, Does.Contain("WorldPayload.WriteInt32(w, companions.Count);"));
            Assert.That(payload, Does.Contain("for (var i = 0; i < companions.Count; i++)"));
            Assert.That(payload, Does.Contain("WorldPayload.WriteInt32(w, targets[i].X);"));
            Assert.That(payload, Does.Contain("WorldPayload.WriteInt32(w, targets[i].Y);"));
            Assert.That(payload, Does.Contain("WorldPayload.WriteInt32(w, duration);"));
            Assert.That(payload, Does.Contain("ActiveStats(pilot).Speed, DateTimeOffset.UtcNow).TimeToTargetMs"),
                "the provisional packet duration follows active base stats");
            Assert.That(payload, Does.Not.Contain("EffectiveSpeedAsync("),
                "the packet writer must not fetch mutable runtime speed independently of saved travel");
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
