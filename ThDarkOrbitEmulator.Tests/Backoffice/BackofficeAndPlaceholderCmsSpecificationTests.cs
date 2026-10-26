using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.Auth;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.Operations;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.Keycloak;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.Identity;
using ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.Tests.Backoffice;

/// <summary>SPEC-09: CMS backoffice routes, RBAC and backoffice endpoint contracts, placeholder branding.</summary>
public class BackofficeAndPlaceholderCmsSpecificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string CmsSrc = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Cms", "src");
    private static readonly string FeaturesRoot = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features");

    // SPEC-09 §4: "VERB route" -> required permission ("" = authenticated, no permission policy).
    private static readonly IReadOnlyDictionary<string, string> BackofficeContract = new Dictionary<string, string>
    {
        ["GET backoffice/auth/permissions"] = "",
        ["GET backoffice/identity/users"] = EmulatorPermissions.IdentityRead,
        ["GET backoffice/identity/users/count"] = EmulatorPermissions.IdentityRead,
        ["DELETE backoffice/identity/users"] = EmulatorPermissions.IdentityDelete,
        ["GET backoffice/keycloak/users"] = EmulatorPermissions.KeycloakRead,
        ["GET backoffice/operations/overview"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/operations/mesh"] = EmulatorPermissions.OpsRead,
        ["POST backoffice/operations/cache/clear"] = EmulatorPermissions.CacheWrite,
        ["POST backoffice/activity-tracking/sessions"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/activity-tracking/sessions/{sessionId}"] = EmulatorPermissions.OpsRead,
        ["POST backoffice/activity-tracking/sessions/{sessionId}/events"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/activity-tracking/sessions/{sessionId}/events/stream"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/mesh/topology"] = EmulatorPermissions.OpsRead,
        ["PUT backoffice/mesh/scale"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/mesh/rebalance"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/mesh/nodes/{nodeId}/drain"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/mesh/nodes/{nodeId}/activate"] = EmulatorPermissions.OpsWrite,
        ["GET backoffice/world/overview"] = EmulatorPermissions.OpsRead,
        ["POST backoffice/world/pilots/{userId}/teleport"] = EmulatorPermissions.OpsWrite,
        ["GET backoffice/runtime-variables"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/runtime-vars"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/runtime-variables/history"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/runtime-vars/history"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/runtime-variables/{key}"] = EmulatorPermissions.OpsRead,
        ["GET backoffice/runtime-vars/{key}"] = EmulatorPermissions.OpsRead,
        ["PUT backoffice/runtime-variables/{key}"] = EmulatorPermissions.OpsWrite,
        ["PUT backoffice/runtime-vars/{key}"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/runtime-variables/{key}/reset"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/runtime-vars/{key}/reset"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/runtime-variables/history/{changeId}/rollback"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/runtime-vars/history/{changeId}/rollback"] = EmulatorPermissions.OpsWrite,
        ["POST backoffice/admin/command"] = EmulatorPermissions.OpsWrite,
        ["GET backoffice/admin/command/history"] = EmulatorPermissions.OpsRead
    };

    // SPEC-09 §2: Angular backoffice child route -> guard ("@admin" = economyAdminGuard realm-role check).
    private static readonly IReadOnlyDictionary<string, string> CmsBackofficeRouteGuards = new Dictionary<string, string>
    {
        ["overview"] = EmulatorPermissions.OpsRead,
        ["identity"] = EmulatorPermissions.IdentityRead,
        ["keycloak"] = EmulatorPermissions.KeycloakRead,
        ["operations"] = EmulatorPermissions.CacheWrite,
        ["mesh"] = EmulatorPermissions.OpsRead,
        ["runtime-vars"] = EmulatorPermissions.OpsRead,
        ["economy"] = "@admin"
    };

    [Test]
    public void BackofficeEndpoints_MatchSpecifiedVerbRouteAndPermissionContract()
    {
        var discovered = DiscoverBackofficeEndpoints();

        Assert.Multiple(() =>
        {
            Assert.That(discovered.Keys, Is.EquivalentTo(BackofficeContract.Keys),
                "Every /api/v1/backoffice/* FastEndpoint must be listed in SPEC-09 §4 and vice versa.");
            foreach (var (route, permission) in BackofficeContract)
            {
                Assert.That(discovered.GetValueOrDefault(route), Is.EqualTo(permission), route);
            }
        });
    }

    [Test]
    public void BackofficeEndpoints_WriteVerbsNeverUseReadOnlyPermissionsExceptActivityTracking()
    {
        var violations = BackofficeContract
            .Where(entry => !entry.Key.StartsWith("GET ", StringComparison.Ordinal))
            .Where(entry => !entry.Key.Contains("/activity-tracking/", StringComparison.Ordinal))
            .Where(entry => entry.Value.EndsWith(".read", StringComparison.Ordinal) || entry.Value.Length == 0)
            .Select(entry => entry.Key)
            .ToArray();

        Assert.That(violations, Is.Empty, "Mutating backoffice routes must require a write/delete permission.");
    }

    [Test]
    public async Task BackofficePermissions_PrefersIdentityNameAndDeduplicatesEachClaimCollection()
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "operator"),
            new Claim("preferred_username", "fallback"),
            new Claim(EmulatorClaimTypes.Permission, EmulatorPermissions.OpsRead),
            new Claim(EmulatorClaimTypes.Permission, EmulatorPermissions.OpsRead),
            new Claim(EmulatorClaimTypes.RealmRole, EmulatorRoles.Viewer),
            new Claim(EmulatorClaimTypes.RealmRole, EmulatorRoles.Viewer),
            new Claim(ClaimTypes.Role, EmulatorRoles.Viewer),
            new Claim(ClaimTypes.Role, EmulatorRoles.Viewer)
        ], "Bearer");

        using var response = await PermissionsResponseAsync(new ClaimsPrincipal(identity));
        var root = response.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(Property(root, "username").GetString(), Is.EqualTo("operator"));
            Assert.That(Property(root, "permissions").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { EmulatorPermissions.OpsRead }));
            Assert.That(Property(root, "realmRoles").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { EmulatorRoles.Viewer }));
            Assert.That(Property(root, "roles").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { EmulatorRoles.Viewer }));
        });
    }

    [TestCase("preferred_username", "fallback")]
    [TestCase("unrelated", "")]
    public async Task BackofficePermissions_UsesSpecifiedUsernameFallback(string claimType, string expected)
    {
        var identity = new ClaimsIdentity([new Claim(claimType, "fallback")], "Bearer");

        using var response = await PermissionsResponseAsync(new ClaimsPrincipal(identity));
        var root = response.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(Property(root, "username").GetString(), Is.EqualTo(expected));
            Assert.That(Property(root, "permissions").GetArrayLength(), Is.Zero);
            Assert.That(Property(root, "realmRoles").GetArrayLength(), Is.Zero);
            Assert.That(Property(root, "roles").GetArrayLength(), Is.Zero);
        });
    }

    [Test]
    public async Task BackofficePermissions_ProjectsClaimsAcrossIdentitiesInFirstSeenOrder()
    {
        var first = new ClaimsIdentity([
            new Claim("preferred_username", "first-name"),
            new Claim(EmulatorClaimTypes.Permission, "backoffice.ops.read"),
            new Claim(EmulatorClaimTypes.Permission, "backoffice.ops.read"),
            new Claim(EmulatorClaimTypes.RealmRole, "viewer"),
            new Claim(ClaimTypes.Role, "viewer")
        ], "Bearer");
        var second = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "second-name"),
            new Claim(EmulatorClaimTypes.Permission, "backoffice.ops.write"),
            new Claim(EmulatorClaimTypes.Permission, "backoffice.ops.read"),
            new Claim(EmulatorClaimTypes.Permission, "Backoffice.Ops.Read"),
            new Claim(EmulatorClaimTypes.RealmRole, "admin"),
            new Claim(EmulatorClaimTypes.RealmRole, "viewer"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "viewer")
        ], "Bearer");

        using var response = await PermissionsResponseAsync(new ClaimsPrincipal([first, second]));
        var root = response.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(Property(root, "username").GetString(), Is.EqualTo("first-name"));
            Assert.That(Property(root, "permissions").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { "backoffice.ops.read", "backoffice.ops.write", "Backoffice.Ops.Read" }));
            Assert.That(Property(root, "realmRoles").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { "viewer", "admin" }));
            Assert.That(Property(root, "roles").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { "viewer", "admin" }));
        });
    }

    [Test]
    public async Task BackofficeOverview_ReturnsFixedServiceAndToolCatalogWithoutProbingDependencies()
    {
        var context = NewEndpointContext();

        await Factory.Create<BackofficeOverviewEndpoint>(context).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        var root = response.RootElement;
        var services = Property(root, "services").EnumerateArray().ToArray();
        var links = Property(root, "links").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(services.Select(x => Property(x, "name").GetString()), Is.EqualTo(new[]
                { "api-game", "gateway-players", "data-postgres", "sessions-redis", "auth-keycloak", "cms-players" }));
            Assert.That(services.Select(x => Property(x, "status").GetString()), Is.EqualTo(new[]
                { "healthy", "healthy", "running", "running", "running", "running" }));
            Assert.That(links.Select(x => Property(x, "label").GetString()), Is.EqualTo(new[]
                { "Aspire dashboard", "pgAdmin", "Grafana", "Prometheus", "RedisInsight (Aspire → sessions-redis)", "Keycloak admin" }));
            Assert.That(links.All(x => Uri.TryCreate(Property(x, "url").GetString(), UriKind.Absolute, out var url)
                && url.IsLoopback), Is.True);
        });
    }

    [Test]
    public async Task BackofficeKeycloakUsers_ProjectsEveryFieldAndPreservesServiceOrder()
    {
        var service = new RecordingKeycloakService([
            new("kc-2", "zeta", "zeta@example.test", false),
            new("kc-1", "alpha", "", true)
        ]);
        var context = NewEndpointContext();

        await Factory.Create<ListKeycloakUsersEndpoint>(context, service).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        var users = Property(response.RootElement, "users").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(service.Limit, Is.EqualTo(100));
            Assert.That(users, Has.Length.EqualTo(2));
            Assert.That(Property(users[0], "id").GetString(), Is.EqualTo("kc-2"));
            Assert.That(Property(users[0], "username").GetString(), Is.EqualTo("zeta"));
            Assert.That(Property(users[0], "email").GetString(), Is.EqualTo("zeta@example.test"));
            Assert.That(Property(users[0], "enabled").GetBoolean(), Is.False);
            Assert.That(Property(users[1], "id").GetString(), Is.EqualTo("kc-1"));
            Assert.That(Property(users[1], "username").GetString(), Is.EqualTo("alpha"));
            Assert.That(Property(users[1], "email").GetString(), Is.Empty);
            Assert.That(Property(users[1], "enabled").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task BackofficeKeycloakUsers_EmptyServiceResultReturnsEmptyArray()
    {
        var service = new RecordingKeycloakService([]);
        var context = NewEndpointContext();

        await Factory.Create<ListKeycloakUsersEndpoint>(context, service).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(response.RootElement, "users").GetArrayLength(), Is.Zero);
            Assert.That(service.Limit, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task BackofficeKeycloakUsers_PassesCancellationTokenToService()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new RecordingKeycloakService([]);
        var context = NewEndpointContext();

        await Factory.Create<ListKeycloakUsersEndpoint>(context, service).HandleAsync(cancellation.Token);

        Assert.That(service.CancellationToken, Is.EqualTo(cancellation.Token));
    }

    [Test]
    public async Task BackofficeIdentityUsers_ProjectsRowsInServiceOrderWithFixedLimit()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var service = new RecordingIdentityService([
            new(firstId, "zeta", "zeta@example.test", 9000000001),
            new(secondId, "alpha", "", 42)
        ]);
        var context = NewEndpointContext();

        await Factory.Create<ListIdentityUsersEndpoint>(context, service).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        var users = Property(response.RootElement, "users").EnumerateArray().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(service.Limit, Is.EqualTo(100));
            Assert.That(users, Has.Length.EqualTo(2));
            Assert.That(Property(users[0], "id").GetGuid(), Is.EqualTo(firstId));
            Assert.That(Property(users[0], "username").GetString(), Is.EqualTo("zeta"));
            Assert.That(Property(users[0], "email").GetString(), Is.EqualTo("zeta@example.test"));
            Assert.That(Property(users[0], "gameUserId").GetInt64(), Is.EqualTo(9000000001));
            Assert.That(Property(users[1], "id").GetGuid(), Is.EqualTo(secondId));
            Assert.That(Property(users[1], "username").GetString(), Is.EqualTo("alpha"));
            Assert.That(Property(users[1], "email").GetString(), Is.Empty);
            Assert.That(Property(users[1], "gameUserId").GetInt64(), Is.EqualTo(42));
        });
    }

    [Test]
    public async Task BackofficeIdentityUsers_EmptyServiceResultReturnsEmptyArray()
    {
        var service = new RecordingIdentityService([]);
        var context = NewEndpointContext();

        await Factory.Create<ListIdentityUsersEndpoint>(context, service).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(response.RootElement, "users").GetArrayLength(), Is.Zero);
            Assert.That(service.Limit, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task BackofficeIdentityCountAndDelete_ReturnServiceValuesIncludingZero()
    {
        var service = new RecordingIdentityService([]) { Count = 3000000000, Deleted = 0 };
        var countContext = NewEndpointContext();
        var deleteContext = NewEndpointContext();

        await Factory.Create<IdentityUserCountEndpoint>(countContext, service).HandleAsync(CancellationToken.None);
        await Factory.Create<DeleteAllIdentityUsersEndpoint>(deleteContext, service).HandleAsync(CancellationToken.None);

        using var countResponse = ParseResponse(countContext);
        using var deleteResponse = ParseResponse(deleteContext);
        Assert.Multiple(() =>
        {
            Assert.That(countContext.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(countResponse.RootElement, "count").GetInt64(), Is.EqualTo(3000000000));
            Assert.That(deleteContext.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(deleteResponse.RootElement, "deleted").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public async Task BackofficeIdentityEndpoints_ForwardCancellationToApplicationService()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new RecordingIdentityService([]);
        await Factory.Create<ListIdentityUsersEndpoint>(NewEndpointContext(), service).HandleAsync(cancellation.Token);
        Assert.That(service.ListCancellationToken, Is.EqualTo(cancellation.Token));
        await Factory.Create<IdentityUserCountEndpoint>(NewEndpointContext(), service).HandleAsync(cancellation.Token);
        Assert.That(service.CountCancellationToken, Is.EqualTo(cancellation.Token));
        await Factory.Create<DeleteAllIdentityUsersEndpoint>(NewEndpointContext(), service).HandleAsync(cancellation.Token);
        Assert.That(service.DeleteCancellationToken, Is.EqualTo(cancellation.Token));
    }

    [Test]
    public async Task ActivitySessionStart_ProjectsTrimmedFieldsAndAuthenticatedOperator()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var context = NewEndpointContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "operator-name"),
            new Claim("preferred_username", "fallback-name")
        ], "Bearer"));

        await Factory.Create<StartActivityTrackingSessionEndpoint>(context, tracker)
            .HandleAsync(new StartActivityTrackingSessionRequest
            {
                SubjectId = "  connection:7  ",
                Reason = "  inspect reconnect  "
            }, CancellationToken.None);

        using var response = ParseResponse(context);
        var session = Property(response.RootElement, "session");
        var id = Property(session, "id").GetString()!;
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(id, Has.Length.EqualTo(32));
            Assert.That(Guid.TryParseExact(id, "N", out _), Is.True);
            Assert.That(Property(session, "subjectId").GetString(), Is.EqualTo("connection:7"));
            Assert.That(Property(session, "reason").GetString(), Is.EqualTo("inspect reconnect"));
            Assert.That(Property(session, "operatorName").GetString(), Is.EqualTo("operator-name"));
            Assert.That(Property(session, "startedAtUtc").GetDateTimeOffset(), Is.InRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.That(tracker.TryGetSession(id, out var saved), Is.True);
            Assert.That(saved.SubjectId, Is.EqualTo("connection:7"));
        });
    }

    [TestCase("preferred_username", "claim-operator")]
    [TestCase("unrelated", "unknown")]
    public async Task ActivitySessionStart_UsesSpecifiedOperatorFallback(string claimType, string expected)
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var context = NewEndpointContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, "claim-operator")], "Bearer"));

        await Factory.Create<StartActivityTrackingSessionEndpoint>(context, tracker)
            .HandleAsync(new StartActivityTrackingSessionRequest { SubjectId = "subject", Reason = "reason" }, CancellationToken.None);

        using var response = ParseResponse(context);
        Assert.That(Property(Property(response.RootElement, "session"), "operatorName").GetString(), Is.EqualTo(expected));
    }

    [TestCase(" ", "reason")]
    [TestCase("subject", "\t")]
    public void ActivitySessionStart_RejectsBlankFieldsBeforeRecording(string subjectId, string reason)
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var context = NewEndpointContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "Bearer"));
        var endpoint = Factory.Create<StartActivityTrackingSessionEndpoint>(context, tracker);

        Assert.ThrowsAsync<ArgumentException>(async () => await endpoint.HandleAsync(
            new StartActivityTrackingSessionRequest { SubjectId = subjectId, Reason = reason }, CancellationToken.None));
        Assert.That(context.Response.Body.Length, Is.Zero);
    }

    [Test]
    public async Task ActivityEventAppend_ProjectsExplicitTimestampAndTrimmedFields()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        var instant = DateTimeOffset.Parse("2026-10-25T03:04:05+02:00");
        var context = NewActivityContext(session.Id);

        await Factory.Create<AppendActivityTrackingEventEndpoint>(context, tracker).HandleAsync(
            new AppendActivityTrackingEventRequest
            {
                Type = "  connected  ", Source = "  gateway  ", Details = "  handshake  ", OccurredAtUtc = instant
            }, CancellationToken.None);

        using var response = ParseResponse(context);
        var activityEvent = Property(response.RootElement, "event");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(activityEvent, "sequence").GetInt64(), Is.EqualTo(1));
            Assert.That(Property(activityEvent, "sessionId").GetString(), Is.EqualTo(session.Id));
            Assert.That(Property(activityEvent, "type").GetString(), Is.EqualTo("connected"));
            Assert.That(Property(activityEvent, "source").GetString(), Is.EqualTo("gateway"));
            Assert.That(Property(activityEvent, "details").GetString(), Is.EqualTo("handshake"));
            Assert.That(Property(activityEvent, "occurredAtUtc").GetDateTimeOffset(), Is.EqualTo(instant));
            Assert.That(tracker.GetSnapshot(session.Id), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ActivityEventAppend_UsesCurrentUtcWhenTimestampIsAbsent()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        var context = NewActivityContext(session.Id);
        var before = DateTimeOffset.UtcNow;

        await Factory.Create<AppendActivityTrackingEventEndpoint>(context, tracker).HandleAsync(
            new AppendActivityTrackingEventRequest { Type = "event", Source = "api" }, CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        using var response = ParseResponse(context);
        var timestamp = Property(Property(response.RootElement, "event"), "occurredAtUtc").GetDateTimeOffset();
        Assert.That(timestamp, Is.InRange(before, after));
        Assert.That(tracker.GetSnapshot(session.Id).Single().OccurredAtUtc, Is.EqualTo(timestamp));
    }

    [Test]
    public async Task ActivitySnapshot_ReturnsSessionAndEventsInSequenceOrder()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        tracker.AppendEvent(session.Id, "first", "api", "", DateTimeOffset.UnixEpoch);
        tracker.AppendEvent(session.Id, "second", "gateway", "", DateTimeOffset.UnixEpoch);
        var context = NewActivityContext(session.Id);

        await Factory.Create<GetActivityTrackingSnapshotEndpoint>(context, tracker).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(Property(response.RootElement, "session"), "id").GetString(), Is.EqualTo(session.Id));
            Assert.That(Property(response.RootElement, "events").EnumerateArray()
                .Select(item => Property(item, "sequence").GetInt64()), Is.EqualTo(new long[] { 1, 2 }));
        });
    }

    [Test]
    public async Task ActivitySnapshot_EmptySessionReturnsEmptyEvents()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        var context = NewActivityContext(session.Id);

        await Factory.Create<GetActivityTrackingSnapshotEndpoint>(context, tracker).HandleAsync(CancellationToken.None);

        using var response = ParseResponse(context);
        Assert.That(Property(response.RootElement, "events").GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task ActivityEndpoints_UnknownSessionReturns404WithoutCreatingOne()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var appendContext = NewActivityContext("missing");
        var snapshotContext = NewActivityContext("missing");

        await Factory.Create<AppendActivityTrackingEventEndpoint>(appendContext, tracker).HandleAsync(
            new AppendActivityTrackingEventRequest { Type = "event", Source = "api" }, CancellationToken.None);
        await Factory.Create<GetActivityTrackingSnapshotEndpoint>(snapshotContext, tracker).HandleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(appendContext.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(snapshotContext.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(tracker.TryGetSession("missing", out _), Is.False);
        });
    }

    [TestCase(" ", "api")]
    [TestCase("event", "\t")]
    public void ActivityEventAppend_RejectsBlankTypeOrSourceBeforeRecording(string type, string source)
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        var context = NewActivityContext(session.Id);

        Assert.ThrowsAsync<ArgumentException>(async () => await Factory.Create<AppendActivityTrackingEventEndpoint>(context, tracker)
            .HandleAsync(new AppendActivityTrackingEventRequest { Type = type, Source = source }, CancellationToken.None));
        Assert.That(tracker.GetSnapshot(session.Id), Is.Empty);
    }

    [Test]
    public async Task ActivityStream_UnknownSessionReturns404WithoutOpeningSse()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var context = NewActivityContext("missing");

        await Factory.Create<StreamActivityTrackingEventsEndpoint>(context, tracker).HandleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(context.Response.ContentType, Is.Not.EqualTo("text/event-stream"));
            Assert.That(context.Response.Body.Length, Is.Zero);
            Assert.That(tracker.TryGetSession("missing", out _), Is.False);
        });
    }

    [Test]
    public void ActivityStream_ReplaysAndDeliversLiveEventsWithSseFraming()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        tracker.AppendEvent(session.Id, "existing", "api", "line one\nline two", DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var context = NewActivityContext(session.Id);
        var completedFrames = 0;
        var body = new CallbackMemoryStream(() =>
        {
            if (++completedFrames == 1)
                tracker.AppendEvent(session.Id, "live", "gateway", "new event", DateTimeOffset.UnixEpoch);
            else
                cancellation.Cancel();
        });
        context.Response.Body = body;

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await Factory.Create<StreamActivityTrackingEventsEndpoint>(context, tracker).HandleAsync(cancellation.Token));

        body.Position = 0;
        using var reader = new StreamReader(body);
        var frames = reader.ReadToEnd().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Multiple(() =>
        {
            Assert.That(completedFrames, Is.EqualTo(2));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/event-stream"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(frames, Has.Length.EqualTo(2));
            Assert.That(frames[0], Does.StartWith("id: 1\nevent: activity\ndata: "));
            Assert.That(frames[1], Does.StartWith("id: 2\nevent: activity\ndata: "));
        });
        for (var index = 0; index < frames.Length; index++)
        {
            using var payload = JsonDocument.Parse(frames[index]["id: ".Length..].Split("\ndata: ")[1]);
            Assert.That(Property(payload.RootElement, "sequence").GetInt64(), Is.EqualTo(index + 1));
            Assert.That(Property(payload.RootElement, "sessionId").GetString(), Is.EqualTo(session.Id));
        }
    }

    private sealed class CallbackMemoryStream(Action onFlush) : MemoryStream
    {
        private int _completedFrames;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            var frames = System.Text.Encoding.UTF8.GetString(ToArray()).Split("\n\n").Length - 1;
            if (frames > _completedFrames)
            {
                _completedFrames = frames;
                onFlush();
            }
            return Task.CompletedTask;
        }
    }

    private static DefaultHttpContext NewActivityContext(string sessionId)
    {
        var context = NewEndpointContext();
        context.Request.RouteValues["sessionId"] = sessionId;
        return context;
    }

    private sealed class RecordingIdentityService(IReadOnlyList<BackofficeAccountRow> users) : IBackofficeIdentityService
    {
        public int Limit { get; private set; }
        public long Count { get; init; }
        public int Deleted { get; init; }
        public CancellationToken ListCancellationToken { get; private set; }
        public CancellationToken CountCancellationToken { get; private set; }
        public CancellationToken DeleteCancellationToken { get; private set; }

        public Task<IReadOnlyList<BackofficeAccountRow>> ListAccountsAsync(int limit, CancellationToken cancellationToken)
        {
            Limit = limit;
            ListCancellationToken = cancellationToken;
            return Task.FromResult(users);
        }

        public Task<long> CountAccountsAsync(CancellationToken cancellationToken)
        {
            CountCancellationToken = cancellationToken;
            return Task.FromResult(Count);
        }

        public Task<int> DeleteAllAccountsAsync(CancellationToken cancellationToken)
        {
            DeleteCancellationToken = cancellationToken;
            return Task.FromResult(Deleted);
        }
    }

    private sealed class RecordingKeycloakService(IReadOnlyList<BackofficeKeycloakUserRow> users) : IBackofficeKeycloakService
    {
        public int Limit { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<BackofficeKeycloakUserRow>> ListUsersAsync(int limit, CancellationToken cancellationToken)
        {
            Limit = limit;
            CancellationToken = cancellationToken;
            return Task.FromResult(users);
        }
    }

    private static async Task<JsonDocument> PermissionsResponseAsync(ClaimsPrincipal user)
    {
        var context = NewEndpointContext();
        context.User = user;
        await Factory.Create<BackofficePermissionsEndpoint>(context).HandleAsync(CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        return ParseResponse(context);
    }

    private static DefaultHttpContext NewEndpointContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonDocument ParseResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value))
        {
            return value;
        }

        return element.GetProperty(char.ToUpperInvariant(name[0]) + name[1..]);
    }

    [Test]
    public void EconomyAdministration_IsRestrictedToAdminRealmRoleOnServerAndCms()
    {
        var economy = File.ReadAllText(Path.Combine(FeaturesRoot, "Economy", "EconomyAdminEndpoints.cs"));
        var guard = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "auth", "permission.guard.ts"));
        var shell = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "shell", "backoffice-shell.component.html"));

        Assert.Multiple(() =>
        {
            Assert.That(economy, Does.Contain("MapGroup(\"/api/v1/economy\").RequireAuthorization(EmulatorRoles.Admin)"));
            Assert.That(EmulatorRoles.Admin, Is.EqualTo("emulator-admin"));
            Assert.That(guard, Does.Match(@"economyAdminGuard[\s\S]*getRealmRoles\(\)\.includes\('emulator-admin'\)"));
            Assert.That(shell, Does.Contain("rbac.getRealmRoles().includes('emulator-admin')"));
        });
    }

    [Test]
    public void KeycloakRealmRoles_MapToSpecifiedPermissionMatrix()
    {
        var matrix = KeycloakRbacDefinitions.RealmRolePermissions;

        Assert.Multiple(() =>
        {
            Assert.That(KeycloakRbacDefinitions.AllPermissions, Is.EqualTo(EmulatorPermissions.All));
            Assert.That(matrix.Keys, Is.EquivalentTo(EmulatorRoles.All));
            Assert.That(matrix[EmulatorRoles.Admin], Is.EquivalentTo(EmulatorPermissions.All));
            Assert.That(matrix[EmulatorRoles.Operator], Is.EquivalentTo(EmulatorPermissions.All.Except([EmulatorPermissions.IdentityDelete])));
            Assert.That(matrix[EmulatorRoles.Viewer], Is.EquivalentTo(new[]
            {
                EmulatorPermissions.IdentityRead, EmulatorPermissions.KeycloakRead, EmulatorPermissions.OpsRead
            }));
            Assert.That(KeycloakRbacDefinitions.DefaultRealmRole, Is.EqualTo(EmulatorRoles.Viewer));
            Assert.That(EmulatorPermissions.All, Has.All.StartWith("backoffice."));
            Assert.That(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead), Is.EqualTo("Permission:backoffice.ops.read"));
        });
    }

    [Test]
    public async Task KeycloakClaimsTransformation_RepeatedAuthenticationDoesNotDuplicateRolesOrPermissions()
    {
        var identity = new ClaimsIdentity([
            new Claim("realm_access", """{"roles":["emulator-viewer","emulator-viewer"]}"""),
            new Claim("resource_access", """{"thdarkorbit-api":{"roles":["backoffice.ops.read","backoffice.ops.read"]}}""")
        ], "Bearer");
        var principal = new ClaimsPrincipal(identity);
        var transformation = new KeycloakClaimsTransformation();

        await transformation.TransformAsync(principal);
        await transformation.TransformAsync(principal);

        Assert.Multiple(() =>
        {
            Assert.That(identity.FindAll(EmulatorClaimTypes.RealmRole).Select(c => c.Value),
                Is.EqualTo(new[] { EmulatorRoles.Viewer }));
            Assert.That(identity.FindAll(EmulatorClaimTypes.Permission).Select(c => c.Value),
                Is.EqualTo(new[] { EmulatorPermissions.OpsRead }));
            Assert.That(identity.FindAll(ClaimTypes.Role).Select(c => c.Value),
                Is.EqualTo(new[] { EmulatorRoles.Viewer, EmulatorPermissions.OpsRead }));
        });
    }

    [Test]
    public async Task KeycloakClaimsTransformation_UnauthenticatedIdentityIsNotExpanded()
    {
        var identity = new ClaimsIdentity([
            new Claim("realm_access", """{"roles":["emulator-admin"]}"""),
            new Claim("resource_access", """{"thdarkorbit-api":{"roles":["backoffice.ops.write"]}}""")
        ]);
        var principal = new ClaimsPrincipal(identity);

        var result = await new KeycloakClaimsTransformation().TransformAsync(principal);

        Assert.That(result, Is.SameAs(principal));
        Assert.That(identity.Claims.ToArray(), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task KeycloakClaimsTransformation_OtherClientRolesCannotGrantEmulatorPermissions()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("resource_access", """{"another-api":{"roles":["backoffice.ops.write"]}}""")
        ], "Bearer"));

        await new KeycloakClaimsTransformation().TransformAsync(principal);

        Assert.That(principal.HasClaim(EmulatorClaimTypes.Permission, EmulatorPermissions.OpsWrite), Is.False);
        Assert.That(principal.IsInRole(EmulatorPermissions.OpsWrite), Is.False);
    }

    [TestCase("realm_access", "[1,2]")]
    [TestCase("realm_access", "{\"roles\":[42,null,\"emulator-viewer\",{}]}")]
    [TestCase("resource_access", "[1,2]")]
    [TestCase("resource_access", "{\"thdarkorbit-api\":true}")]
    [TestCase("resource_access", "{\"thdarkorbit-api\":{\"roles\":[42,null,\"backoffice.ops.read\",{}]}}")]
    public async Task KeycloakClaimsTransformation_UnexpectedJsonShapesIgnoreInvalidRoleElements(string claimType, string value)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, value)], "Bearer"));

        await new KeycloakClaimsTransformation().TransformAsync(principal);

        var expected = claimType == "realm_access" && value.Contains("emulator-viewer", StringComparison.Ordinal)
            ? EmulatorRoles.Viewer
            : claimType == "resource_access" && value.Contains(EmulatorPermissions.OpsRead, StringComparison.Ordinal)
                ? EmulatorPermissions.OpsRead
                : null;
        Assert.That(principal.FindAll(claimType == "realm_access" ? EmulatorClaimTypes.RealmRole : EmulatorClaimTypes.Permission)
            .Select(c => c.Value), expected is null ? Is.Empty : Is.EqualTo(new[] { expected }));
    }

    [Test]
    public async Task AddEmulatorAuthorization_RegistersOnePolicyPerPermissionAndRoleWithoutFallback()
    {
        await using var provider = new ServiceCollection().AddLogging().AddEmulatorAuthorization().BuildServiceProvider();
        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        Assert.That(await policies.GetFallbackPolicyAsync(), Is.Null, "Game client endpoints rely on no fallback policy.");
        foreach (var permission in EmulatorPermissions.All)
        {
            var policy = await policies.GetPolicyAsync(EmulatorPermissions.PolicyName(permission));
            Assert.That(policy, Is.Not.Null, permission);
            Assert.That(policy!.Requirements.OfType<PermissionRequirement>().Select(r => r.Permission), Is.EqualTo(new[] { permission }));
        }

        foreach (var role in EmulatorRoles.All)
        {
            var policy = await policies.GetPolicyAsync(role);
            Assert.That(policy, Is.Not.Null, role);
            Assert.That(policy!.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>()
                .SelectMany(r => r.AllowedRoles), Is.EqualTo(new[] { role }));
        }
    }

    [Test]
    public void CmsTopLevelRoutes_ExposePortalAndGuardedBackoffice()
    {
        var identityRoutes = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "identity", "identity.routes.ts"));
        var backofficeRoutes = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "backoffice.routes.ts"));
        var appRoutes = File.ReadAllText(Path.Combine(CmsSrc, "app", "app.routes.ts"));

        Assert.Multiple(() =>
        {
            Assert.That(identityRoutes, Does.Match(@"path: '',\s*canActivate: \[authGuard\]"));
            Assert.That(identityRoutes, Does.Match(@"path: 'login',\s*canActivate: \[guestGuard\]"));
            Assert.That(identityRoutes, Does.Match(@"path: 'register',\s*canActivate: \[guestGuard\]"));
            Assert.That(backofficeRoutes, Does.Match(@"path: 'backoffice',\s*canActivate: \[backofficeGuard\]"));
            Assert.That(backofficeRoutes, Does.Match(@"path: '', redirectTo: 'overview', pathMatch: 'full'"));
            Assert.That(appRoutes, Does.Contain("{ path: '**', redirectTo: '' }"));
        });
    }

    [Test]
    public void CmsBackofficeChildRoutes_UseSpecifiedPermissionGuards()
    {
        var source = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "backoffice.routes.ts"));
        var routes = Regex.Matches(source, @"path: '([\w-]+)',\s*canActivate: \[(?:permissionGuard\('([^']+)'\)|(economyAdminGuard))\]")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[3].Success ? "@admin" : m.Groups[2].Value);

        Assert.That(routes, Is.EquivalentTo(CmsBackofficeRouteGuards));
        Assert.That(routes.Values.Where(p => p != "@admin").Distinct(), Is.SubsetOf(EmulatorPermissions.All));
    }

    [Test]
    public void CmsBackofficeGuards_RedirectAnonymousToLoginAndUnauthorizedToPortal()
    {
        var guard = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "auth", "permission.guard.ts"));
        var auth = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "identity", "auth", "auth.guard.ts"));

        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(guard, @"createUrlTree\(\['/login'\]\)").Count, Is.EqualTo(3));
            Assert.That(Regex.Matches(guard, @"createUrlTree\(\['/'\]\)").Count, Is.EqualTo(3));
            Assert.That(guard, Does.Contain("rbac.canAccessBackoffice()"));
            Assert.That(guard, Does.Contain("rbac.hasPermission(permission)"));
            Assert.That(auth, Does.Contain("createUrlTree(['/login'])"));
            Assert.That(auth, Does.Contain("createUrlTree(['/'])"));
        });
    }

    [Test]
    public void CmsRbacService_MirrorsServerPermissionCatalogAndApiClient()
    {
        var rbac = File.ReadAllText(Path.Combine(CmsSrc, "app", "core", "auth", "rbac.service.ts"));
        var adminBlock = Regex.Match(rbac, @"includes\('emulator-admin'\)\)\s*\{\s*return \[([^\]]+)\]").Groups[1].Value;
        var adminPermissions = Regex.Matches(adminBlock, @"'([^']+)'").Select(m => m.Groups[1].Value).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(adminPermissions, Is.EqualTo(EmulatorPermissions.All));
            Assert.That(rbac, Does.Contain($"apiClientId = '{KeycloakRbacDefinitions.ApiClientId}'"));
            Assert.That(rbac, Does.Contain("resource_access?.[this.apiClientId]?.roles"));
            Assert.That(rbac, Does.Contain("p.startsWith('backoffice.')"));
            Assert.That(rbac, Does.Contain("sessionStorage.getItem(this.settings.tokenStorageKey)"));
        });
    }

    [Test]
    public void CmsShellNavigation_HidesLinksBehindTheSameGuardsAsRoutes()
    {
        var shell = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "shell", "backoffice-shell.component.html"));
        var blocks = Regex.Matches(shell, @"@if \((rbac\..+?)\)\s*\{([\s\S]*?)\n    \}");
        var links = new Dictionary<string, string>();
        foreach (Match block in blocks)
        {
            var condition = block.Groups[1].Value;
            var guard = condition.Contains("emulator-admin", StringComparison.Ordinal)
                ? "@admin"
                : Regex.Match(condition, @"hasPermission\('([^']+)'\)").Groups[1].Value;
            foreach (Match link in Regex.Matches(block.Groups[2].Value, @"routerLink=""([\w-]+)"""))
            {
                links[link.Groups[1].Value] = guard;
            }
        }

        Assert.That(links, Is.EquivalentTo(CmsBackofficeRouteGuards));
    }

    [Test]
    public void CmsApiClient_CallsOnlyExistingBackofficeAndEconomyRoutes()
    {
        var api = File.ReadAllText(Path.Combine(CmsSrc, "app", "features", "backoffice", "data", "backoffice-api.service.ts"));
        var environment = File.ReadAllText(Path.Combine(CmsSrc, "environments", "environment.ts"));
        var backofficeRoutes = BackofficeContract.Keys.Select(k => Normalize(k[(k.IndexOf(' ') + 1)..])).ToHashSet();
        var economyRoutes = Regex.Matches(File.ReadAllText(Path.Combine(FeaturesRoot, "Economy", "EconomyAdminEndpoints.cs")),
                @"routes\.Map(?:Get|Post|Put|Delete)\(""/([^""]+)""")
            .Select(m => Normalize(m.Groups[1].Value)).ToHashSet();

        var backofficeCalls = ClientPaths(api, "url");
        var economyCalls = ClientPaths(api, "economyUrl");

        Assert.Multiple(() =>
        {
            Assert.That(environment, Does.Contain("apiBaseUrl: '/api/v1'"));
            Assert.That(api, Does.Contain("${this.settings.apiBaseUrl}/backoffice/${path}"));
            Assert.That(api, Does.Contain("${this.settings.apiBaseUrl}/economy/${path}"));
            Assert.That(backofficeCalls, Is.Not.Empty);
            Assert.That(economyCalls, Is.Not.Empty);
            Assert.That(backofficeCalls.Select(p => "backoffice/" + p).Where(p => !backofficeRoutes.Contains(p)), Is.Empty);
            Assert.That(economyCalls.Where(p => !economyRoutes.Contains(p)), Is.Empty);
        });
    }

    [Test]
    public void CmsSourcesAndAssets_NeverShipOfficialBranding()
    {
        var files = Directory.GetFiles(CmsSrc, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Cms", "public"), "*", SearchOption.AllDirectories))
            .ToArray();
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".html", ".css", ".json" };

        var violations = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (Regex.IsMatch(name, "darkorbit|bigpoint|bpsecure", RegexOptions.IgnoreCase))
            {
                violations.Add(name);
            }

            if (!textExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            // The Keycloak client id "thdarkorbit-api" is a technical identifier, never rendered in the UI.
            var text = File.ReadAllText(file).Replace("thdarkorbit-api", "", StringComparison.Ordinal);
            if (Regex.IsMatch(text, "darkorbit|bigpoint|bpsecure", RegexOptions.IgnoreCase))
            {
                violations.Add(Path.GetRelativePath(CmsSrc, file));
            }
        }

        Assert.That(violations, Is.Empty);
        Assert.That(files.Where(f => Regex.IsMatch(f, @"\.(png|jpe?g|gif|svg|webp)$", RegexOptions.IgnoreCase)), Is.Empty,
            "CMS must not ship logo images; the shell renders a [LOGO] placeholder badge.");
    }

    [Test]
    public void CmsPlaceholderTitlesAndBadges_ArePresentInEveryLocale()
    {
        var en = File.ReadAllText(Path.Combine(CmsSrc, "assets", "i18n", "en.json"));
        var es = File.ReadAllText(Path.Combine(CmsSrc, "assets", "i18n", "es.json"));
        var index = File.ReadAllText(Path.Combine(CmsSrc, "index.html"));
        var shell = File.ReadAllText(Path.Combine(CmsSrc, "app", "core", "layout", "app-shell.component.html"));

        Assert.Multiple(() =>
        {
            Assert.That(index, Does.Contain("<title>Space Emulator CMS (Placeholder)</title>"));
            Assert.That(en, Does.Contain("\"brand\": \"Space Emulator CMS\""));
            Assert.That(es, Does.Contain("CMS del Emulador Espacial"));
            Assert.That(shell, Does.Contain(">LOGO<"));
            Assert.That(shell, Does.Contain("PLACEHOLDER UI"));
        });
    }

    [Test]
    public void Spec09_DocumentsEveryBackofficeRouteAndCmsRoute()
    {
        var spec = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specs", "09-cms-backoffice-and-placeholder-ui.md"));

        Assert.Multiple(() =>
        {
            foreach (var route in BackofficeContract.Keys)
            {
                var parts = route.Split(' ');
                Assert.That(spec, Does.Contain($"`{parts[0]}` | `/api/v1/{parts[1]}`"), route);
            }

            foreach (var route in CmsBackofficeRouteGuards.Keys)
            {
                Assert.That(spec, Does.Contain($"`/backoffice/{route}`"), route);
            }

            Assert.That(spec, Does.Contain(nameof(BackofficeAndPlaceholderCmsSpecificationTests)));
        });
    }

    private static Dictionary<string, string> DiscoverBackofficeEndpoints()
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.GetFiles(FeaturesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var chunks = File.ReadAllText(file).Split("public override void Configure()");
            foreach (var chunk in chunks.Skip(1))
            {
                var configure = chunk.Split("HandleAsync", 2)[0];
                var verb = Regex.Match(configure, @"\b(Get|Post|Put|Delete|Patch)\(((?:\s*""backoffice/[^""]+""\s*,?)+)\)");
                if (!verb.Success)
                {
                    continue;
                }

                var policy = Regex.Match(configure, @"Policies\(EmulatorPermissions\.PolicyName\(EmulatorPermissions\.(\w+)\)\)");
                var permission = policy.Success
                    ? (string)typeof(EmulatorPermissions).GetField(policy.Groups[1].Value)!.GetValue(null)!
                    : "";
                foreach (Match path in Regex.Matches(verb.Groups[2].Value, @"""([^""]+)"""))
                {
                    result.Add($"{verb.Groups[1].Value.ToUpperInvariant()} {path.Groups[1].Value}", permission);
                }
            }
        }

        return result;
    }

    private static string[] ClientPaths(string source, string helper) =>
        Regex.Matches(source, $@"this\.{helper}\((?:'([^']+)'|`([^`]+)`)")
            .Select(m => Normalize(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value))
            .Distinct()
            .ToArray();

    private static string Normalize(string route) =>
        Regex.Replace(Regex.Replace(route, @"\$\{[^}]+\}", "{}"), @"\{[^}]*\}", "{}");

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
