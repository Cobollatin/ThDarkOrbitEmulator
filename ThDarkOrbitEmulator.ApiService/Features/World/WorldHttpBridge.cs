using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World.Admin;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Browser spacemap and the binary listener share the same resolver, map and movement store.
public static class WorldHttpBridge
{
    public sealed record MoveRequest(int X, int Y, int? Configuration, int? MapId, Guid? ExpectedMoveId);

    public static void Map(WebApplication app)
    {
        app.MapGet("/spacemap/world/state", StateAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/enter", EnterAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/move", MoveAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/jump", JumpAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/respawn", RespawnAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/collect", CollectAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/target", TargetAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/fire", FireAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/launcher-auto", LauncherAutoAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/rocket-auto", RocketAutoAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/cloak", CloakAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/pulse", PulseAsync).AllowAnonymous().DisableRateLimiting();
        app.MapGet("/spacemap/world/config", ConfigAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/config", ConfigAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/admin/command", AdminCommandAsync).AllowAnonymous().DisableRateLimiting();
        app.MapGet("/spacemap/world/admin/vars", AdminVariablesAsync).AllowAnonymous().DisableRateLimiting();
        app.MapGet("/spacemap/world/admin/vars/history", AdminHistoryAsync).AllowAnonymous().DisableRateLimiting();
        app.MapPost("/spacemap/world/admin/vars/rollback", AdminRollbackAsync).AllowAnonymous().DisableRateLimiting();
    }

    private static async Task<WorldPilot?> PilotAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        var hasCookie = context.Request.Cookies.TryGetValue("dosid", out var dosid) && !string.IsNullOrWhiteSpace(dosid);
        if (!hasCookie)
        {
            dosid = FirstToken(context.Request.Headers["X-Game-Session"])
                ?? FirstToken(context.Request.Query["sid"])
                ?? FirstToken(context.Request.Query["dosid"])
                ?? FirstToken(context.Request.Query["sessionId"]);
        }
        if (string.IsNullOrWhiteSpace(dosid) || dosid.Length > 128)
        {
            context.Response.StatusCode = 401;
            return null;
        }
        var session = await auth.TryGetByDosidAsync(dosid, ct);
        if (session is null && auth is GameAuthSessionStore)
        {
            session = await TryRehydrateSessionAsync(context, dosid, ct);
        }
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow || session.GameUserId <= 0)
        {
            context.Response.StatusCode = 401;
            return null;
        }
        if (!hasCookie && !context.Response.HasStarted)
        {
            context.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=lax; HttpOnly");
        }
        var active = sessions.Find(session.GameUserId);
        if (active is not null)
        {
            await SetMeshHeadersAsync(context, active.MapId, ct);
            return active;
        }
        var (pilot, error) = await resolver.ResolveAsync(session.GameUserId, dosid, ct);
        if (pilot is null) context.Response.StatusCode = error == WorldLoginError.CompanyRequired ? 403 : 401;
        else await SetMeshHeadersAsync(context, pilot.MapId, ct);
        return pilot;
    }

    private static async Task SetMeshHeadersAsync(HttpContext context, int mapId, CancellationToken ct)
    {
        var services = context.RequestServices;
        var options = services is null ? null : services.GetService<IOptions<ClusterOptions>>()?.Value;
        var nodeId = string.IsNullOrWhiteSpace(options?.NodeId)
            ? $"{Environment.MachineName}-{Environment.ProcessId}" : options.NodeId;
        context.Response.Headers["X-Mesh-Node-Id"] = nodeId;
        var registry = services is null ? null : services.GetService<IServerMeshRegistry>();
        var snapshot = registry is null ? null : await registry.GetSnapshotAsync(ct);
        var lease = snapshot?.Leases.FirstOrDefault(item => item.MapId == mapId
            && item.ExpiresAtUtc > DateTimeOffset.UtcNow);
        var owner = snapshot?.Nodes.FirstOrDefault(item => item.NodeId == lease?.NodeId
            && item.Status == ServerMeshNodeStatus.Active);
        context.Response.Headers["X-Mesh-Map-Owner-Node"] = owner?.NodeId ?? nodeId;
    }

    // Bind a browser command to the lease seen before the write. The store checks it
    // again under the coordinator mutex, including after refreshing the saved map.
    private static async Task<WorldLeaseFence?> LeaseFenceAsync(HttpContext context, int mapId,
        CancellationToken ct)
    {
        var registry = context.RequestServices?.GetService<IServerMeshRegistry>();
        if (registry is null) return null;
        var snapshot = await registry.GetSnapshotAsync(ct);
        var lease = snapshot.Leases.FirstOrDefault(item => item.MapId == mapId
            && item.ExpiresAtUtc > DateTimeOffset.UtcNow);
        if (lease is null || !snapshot.Nodes.Any(item => item.NodeId == lease.NodeId
            && item.Status == ServerMeshNodeStatus.Active))
            throw new WorldLeaseLostException();
        return new WorldLeaseFence(registry, lease.NodeId, lease.Epoch);
    }

    private static Task LeaseLostAsync(HttpContext context, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { status = "map_lease_changed" }, ct);
    }

    private static async Task MapChangedAsync(HttpContext context, WorldPilot pilot,
        WorldSessionStore sessions, CancellationToken ct)
    {
        // A competing replica can jump the pilot after admission but before the
        // command acquires the mesh mutex. Route the conflict to the current map.
        var current = await sessions.RestoreAsync(pilot, ct);
        await SetMeshHeadersAsync(context, current.MapId, ct);
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { status = "map_changed" }, ct);
    }

    private static Task MoveChangedAsync(HttpContext context, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { status = "move_changed" }, ct);
    }

    private static async Task<WorldPilot?> AdminPilotAsync(HttpContext context,
        IGameAuthSessionStore auth, IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return null;
        var dosid = context.Request.Cookies["dosid"];
        var session = await auth.TryGetByDosidAsync(dosid!, ct);
        if (session is null || !(session.IsAdmin || session.Username.Equals("owner", StringComparison.OrdinalIgnoreCase) || session.Username.Equals("admin", StringComparison.OrdinalIgnoreCase)))
        { context.Response.StatusCode = 403; return null; }
        pilot = pilot with { IsAdmin = true };
        return pilot;
    }

    public static async Task AdminCommandAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, IAdminCommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var pilot = await AdminPilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            if (body.RootElement.ValueKind != JsonValueKind.Object
                || !body.RootElement.TryGetProperty("text", out var text))
                throw new ArgumentException("Command text is required.");
            if (text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ArgumentException("Command text must be a string.");
            var input = text.GetString();
            if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Command is empty.");
            var node = context.RequestServices.GetService<IOptions<ClusterOptions>>()?.Value.NodeId;
            var result = await dispatcher.ExecuteAsync(new AdminCommand(InGameAdmin.Expand(input, pilot), null, null),
                InGameAdmin.Actor(pilot, node), ct);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(result, ct);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            context.Response.StatusCode = error is KeyNotFoundException ? 404
                : error is InvalidOperationException ? 409 : 400;
            await context.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
        }
    }

    public static async Task AdminVariablesAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, IRuntimeVariableRegistry variables,
        CancellationToken ct)
    {
        if (await AdminPilotAsync(context, auth, resolver, sessions, ct) is null) return;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(await variables.ListAsync(ct), ct);
    }

    public static async Task AdminHistoryAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, IRuntimeVariableRegistry variables,
        CancellationToken ct)
    {
        if (await AdminPilotAsync(context, auth, resolver, sessions, ct) is null) return;
        var key = context.Request.Query["key"].FirstOrDefault();
        var limit = int.TryParse(context.Request.Query["limit"], out var requested)
            ? Math.Clamp(requested, 1, 100) : 20;
        long? before = long.TryParse(context.Request.Query["beforeChangeId"], out var changeId)
            ? changeId : null;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(await variables.HistoryAsync(key, limit, before, ct), ct);
    }

    public static async Task AdminRollbackAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, IAdminCommandDispatcher dispatcher,
        CancellationToken ct)
    {
        var pilot = await AdminPilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            var id = body.RootElement.GetProperty("changeId").GetInt64();
            var reason = body.RootElement.TryGetProperty("reason", out var suppliedReason)
                && suppliedReason.ValueKind == JsonValueKind.String ? suppliedReason.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256)
                throw new ArgumentException("A reason of at most 256 characters is required.");
            var node = context.RequestServices.GetService<IOptions<ClusterOptions>>()?.Value.NodeId;
            var result = await dispatcher.ExecuteAsync(new AdminCommand($"var rollback {id} {JsonSerializer.Serialize(reason)}", null, null),
                InGameAdmin.Actor(pilot, node), ct);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(result, ct);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            context.Response.StatusCode = error is KeyNotFoundException ? 404
                : error is InvalidOperationException ? 409 : 400;
            await context.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
        }
    }

    public static async Task EnterAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
        => await RespondAsync(context, auth, resolver, sessions, ct);

    public static async Task StateAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
        => await RespondAsync(context, auth, resolver, sessions, ct);

    public static async Task ConfigAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        if (context.Request.Method == "POST")
        {
            if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
            int? configuration;
            int? expectedMapId;
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                configuration = body.RootElement.GetProperty("configuration").GetInt32();
                expectedMapId = body.RootElement.TryGetProperty("mapId", out var map)
                    && map.ValueKind != JsonValueKind.Null ? map.GetInt32() : null;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                context.Response.StatusCode = 400;
                return;
            }
            if (configuration is not (1 or 2)) { context.Response.StatusCode = 400; return; }
            if (expectedMapId is <= 0) { context.Response.StatusCode = 400; return; }
            if (expectedMapId is not null && expectedMapId != pilot.MapId)
            { await MapChangedAsync(context, pilot, sessions, ct); return; }
            WorldSwitchResult change;
            try { change = await sessions.SwitchAsync(pilot, configuration.Value, ct,
                fence: await LeaseFenceAsync(context, pilot.MapId, ct), expectedMapId: expectedMapId); }
            catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
            catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
            catch (WorldPilotDestroyedException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
            if (change.CooldownRemainingMs > 0)
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { cooldownRemainingMs = change.CooldownRemainingMs }, ct);
                return;
            }
            if (change.Changed)
            {
                await sessions.BroadcastMoveAsync(pilot, change.Move!, ct);
                await sessions.BroadcastAttributesAsync(pilot, ct);
                if (sessions.CompanionPositions(pilot).Count == 0
                    && (pilot.Companions.Count > 0 || pilot.ConfigurationTwoCompanions.Count > 0))
                    await sessions.BroadcastCompanionsAsync(pilot, ct);
            }
        }
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { configuration = sessions.Configuration(pilot.UserId),
            speed = sessions.ActiveStats(pilot).Speed, shield = sessions.CurrentShield(pilot),
            maxShield = sessions.ActiveStats(pilot).MaxShield,
            laserDamage = sessions.ActiveStats(pilot).LaserDamage }, ct);
    }

    public static async Task MoveAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        MoveRequest request;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            var root = body.RootElement;
            var x = root.GetProperty("x").GetInt32();
            var y = root.GetProperty("y").GetInt32();
            var configuration = root.TryGetProperty("configuration", out var selected) &&
                selected.ValueKind != JsonValueKind.Null ? selected.GetInt32() : (int?)null;
            var mapId = root.TryGetProperty("mapId", out var map) && map.ValueKind != JsonValueKind.Null
                ? map.GetInt32() : (int?)null;
            var expectedMoveId = root.TryGetProperty("expectedMoveId", out var revision)
                ? revision.GetGuid() : (Guid?)null;
            request = new MoveRequest(x, y, configuration, mapId, expectedMoveId);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { context.Response.StatusCode = 400; return; }
        if (request.Configuration is not (null or 1 or 2))
        { context.Response.StatusCode = 400; return; }
        if (request.MapId is <= 0)
        { context.Response.StatusCode = 400; return; }
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        var expectedMapId = request.MapId ?? admittedMapId;
        if (expectedMapId != pilot.MapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        WorldLeaseFence? fence;
        try { fence = await LeaseFenceAsync(context, pilot.MapId, ct); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        if (request.Configuration is { } newConfiguration)
        {
            WorldSwitchResult change;
            try { change = await sessions.SwitchAsync(pilot, newConfiguration, ct, fence: fence,
                expectedMapId: expectedMapId, expectedMoveId: request.ExpectedMoveId); }
            catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
            catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
            catch (WorldMoveChangedException) { await MoveChangedAsync(context, ct); return; }
            catch (WorldPilotDestroyedException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
            if (change.CooldownRemainingMs > 0)
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { cooldownRemainingMs = change.CooldownRemainingMs }, ct);
                return;
            }
            if (change.Changed)
            {
                await sessions.BroadcastAttributesAsync(pilot, ct);
                if (sessions.CompanionPositions(pilot).Count == 0
                    && (pilot.Companions.Count > 0 || pilot.ConfigurationTwoCompanions.Count > 0))
                    await sessions.BroadcastCompanionsAsync(pilot, ct);
            }
        }
        WorldMoveResult move;
        try { move = await sessions.MoveAsync(pilot, request.X, request.Y, null, ct,
            fence: fence, expectedMapId: expectedMapId, expectedMoveId: request.ExpectedMoveId); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        catch (WorldMoveChangedException) { await MoveChangedAsync(context, ct); return; }
        catch (WorldPilotDestroyedException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
        await sessions.BroadcastMoveAsync(pilot, move, ct);
        sessions.SetRadiationWarning(pilot.UserId, move.RadiationWarning);
        await WriteStateAsync(context, pilot, sessions, ct);
    }

    public static async Task JumpAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        int portalId;
        int? sourceMapId;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            portalId = body.RootElement.GetProperty("portalId").GetInt32();
            sourceMapId = body.RootElement.TryGetProperty("mapId", out var mapId) ? mapId.GetInt32() : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        if (sourceMapId is <= 0) { context.Response.StatusCode = 400; return; }
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        var expectedMapId = sourceMapId ?? admittedMapId;
        if (expectedMapId != pilot.MapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        var registry = context.RequestServices?.GetService<IServerMeshRegistry>();
        WorldJumpResult? jump;
        try { jump = await sessions.JumpAsync(pilot, portalId, registry, ct,
            await LeaseFenceAsync(context, pilot.MapId, ct), expectedMapId); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        catch (WorldPilotDestroyedException) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
        if (jump is null) { context.Response.StatusCode = 409; return; }
        await SetMeshHeadersAsync(context, jump.Pilot.MapId, ct);
        await WriteStateAsync(context, jump.Pilot, sessions, ct, jump);
    }

    public static async Task RespawnAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        WorldRespawnLocation location;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            var choice = body.RootElement.TryGetProperty("location", out var value)
                ? value.GetString() : "base";
            location = choice switch
            {
                "base" => WorldRespawnLocation.Base,
                "death" => WorldRespawnLocation.DeathLocation,
                "portal" => WorldRespawnLocation.NearestPortal,
                _ => throw new JsonException("Unknown respawn location.")
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        WorldPilot? respawned;
        try { respawned = await sessions.RespawnAsync(pilot, location, ct,
            await LeaseFenceAsync(context, pilot.MapId, ct)); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (respawned is null) { context.Response.StatusCode = 409; return; }
        await SetMeshHeadersAsync(context, respawned.MapId, ct);
        await WriteStateAsync(context, respawned, sessions, ct);
    }

    public static async Task CollectAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, WorldCollectibles collectibles,
        CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (WorldMapInstance.IsGate(pilot.MapId)) { context.Response.StatusCode = 409; return; }
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        int id;
        int? expectedMapId;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            id = body.RootElement.GetProperty("boxId").GetInt32();
            expectedMapId = body.RootElement.TryGetProperty("mapId", out var map)
                ? map.GetInt32() : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        if (expectedMapId is <= 0) { context.Response.StatusCode = 400; return; }
        pilot = await sessions.RestoreAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (sessions.CurrentHitPoints(pilot) <= 0)
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
        expectedMapId ??= admittedMapId;
        if (expectedMapId != pilot.MapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        WorldCollectResult result;
        try { result = await collectibles.CollectAsync(pilot, id, sessions, ct,
            await LeaseFenceAsync(context, pilot.MapId, ct), expectedMapId); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (result.Status != "collected") context.Response.StatusCode = 409;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { result.Status, reward = result.Box is null ? null :
            new { result.Box.AssetId, result.Box.Amount }, boxId = id }, ct);
    }

    public static async Task TargetAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, WorldNpcs npcs, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        int id;
        int? expectedMapId;
        try { using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            id = body.RootElement.GetProperty("npcId").GetInt32();
            expectedMapId = body.RootElement.TryGetProperty("mapId", out var map)
                ? map.GetInt32() : null; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        if (expectedMapId is <= 0) { context.Response.StatusCode = 400; return; }
        if (expectedMapId is not null && expectedMapId != pilot.MapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        WorldNpc? target;
        pilot = await sessions.RestoreAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (sessions.CurrentHitPoints(pilot) <= 0)
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
        try { target = WorldMapInstance.IsGate(pilot.MapId)
            ? await context.RequestServices.GetRequiredService<WorldGateCombat>().SelectAsync(pilot, id, sessions, ct, admittedMapId)
            : await npcs.SelectAsync(pilot, id, ct,
                await LeaseFenceAsync(context, pilot.MapId, ct), sessions, admittedMapId); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (target is null) { context.Response.StatusCode = 404; return; }
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(target, ct);
    }

    public static async Task LauncherAutoAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, WorldNpcs npcs, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (WorldMapInstance.IsGate(pilot.MapId))
        { context.Response.StatusCode = 409; return; }
        bool enabled;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            var value = body.RootElement.GetProperty("enabled");
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            enabled = value.GetBoolean();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        await npcs.SetAutoLauncherAsync(pilot.UserId, enabled, ct);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(await npcs.LauncherStatusAsync(pilot.UserId, ct), ct);
    }

    public static async Task RocketAutoAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, WorldNpcs npcs, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (WorldMapInstance.IsGate(pilot.MapId))
        { context.Response.StatusCode = 409; return; }
        bool enabled;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            var value = body.RootElement.GetProperty("enabled");
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
            enabled = value.GetBoolean();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        await npcs.SetAutoRocketAsync(pilot.UserId, enabled, ct);
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { autoFire = enabled }, ct);
    }

    public static async Task FireAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, WorldNpcs npcs, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        int id;
        int? expectedMapId;
        bool rocket;
        bool launcher;
        try { using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            id = body.RootElement.GetProperty("npcId").GetInt32();
            expectedMapId = body.RootElement.TryGetProperty("mapId", out var map)
                ? map.GetInt32() : null;
            var weaponName = body.RootElement.TryGetProperty("weapon", out var weapon)
                ? weapon.GetString() : "laser";
            if (weaponName is not ("laser" or "rocket" or "launcher")) throw new JsonException("Unknown weapon.");
            rocket = weaponName == "rocket";
            launcher = weaponName == "launcher"; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        if (expectedMapId is <= 0) { context.Response.StatusCode = 400; return; }
        pilot = await sessions.RestoreAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (sessions.CurrentHitPoints(pilot) <= 0)
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
        if (expectedMapId is not null && expectedMapId != pilot.MapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (!WorldMapInstance.IsGate(pilot.MapId) && !await npcs.IsSelectedAsync(pilot, id, ct) &&
            !await npcs.CanRetryRewardAsync(pilot, id, ct))
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "not_selected" }, ct); return; }
        WorldNpcAttack result;
        if (launcher && WorldMapInstance.IsGate(pilot.MapId))
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "unsupported_weapon" }, ct); return; }
        try { result = WorldMapInstance.IsGate(pilot.MapId)
            ? await context.RequestServices.GetRequiredService<WorldGateCombat>().AttackAsync(pilot, id, sessions, ct, admittedMapId)
            : await npcs.AttackAsync(pilot, id, sessions, ct,
                await LeaseFenceAsync(context, pilot.MapId, ct), admittedMapId, rocket,
                launcher: launcher); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (result.Status != "hit") context.Response.StatusCode = 409;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(result, ct);
    }

    public static async Task CloakAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        var admittedMapId = pilot.MapId;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        if (pilot.MapId != admittedMapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        bool active;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            var value = body.RootElement.GetProperty("active");
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Expected a boolean.");
            active = value.GetBoolean();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { context.Response.StatusCode = 400; return; }
        try { pilot = await sessions.SetCloakAsync(pilot, active, ct); }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        catch (WorldPilotDestroyedException)
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "destroyed_pilot" }, ct); return; }
        catch (WorldCloakBlockedException)
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "blocked_map" }, ct); return; }
        catch (WorldCloakCooldownException e)
        { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { status = "cooldown", cooldownRemainingMs = e.RemainingMs }, ct); return; }
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { active = pilot.IsInvisible }, ct);
    }

    public static async Task PulseAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, WorldNpcs npcs, CancellationToken ct)
    {
        if (!JsonRequest(context)) { context.Response.StatusCode = 415; return; }
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        int? expectedMapId;
        Guid actionId;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            expectedMapId = body.RootElement.TryGetProperty("mapId", out var map) ? map.GetInt32() : null;
            actionId = body.RootElement.TryGetProperty("actionId", out var action)
                ? action.GetGuid() : Guid.Empty;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { context.Response.StatusCode = 400; return; }
        if (expectedMapId is <= 0 || actionId == Guid.Empty) { context.Response.StatusCode = 400; return; }
        if (expectedMapId is not null && expectedMapId != pilot.MapId)
        { await MapChangedAsync(context, pilot, sessions, ct); return; }
        WorldNpcPulse result;
        try { result = await npcs.PulseAsync(pilot, sessions, ct,
            await LeaseFenceAsync(context, pilot.MapId, ct), expectedMapId, actionId); }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        if (result.Status != "hit") context.Response.StatusCode = 409;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(result, ct);
    }

    private static async Task RespondAsync(HttpContext context, IGameAuthSessionStore auth,
        IWorldPilotResolver resolver, WorldSessionStore sessions, CancellationToken ct)
    {
        var pilot = await PilotAsync(context, auth, resolver, sessions, ct);
        if (pilot is null) return;
        pilot = await sessions.GetOrEnterAsync(pilot, ct);
        await SetMeshHeadersAsync(context, pilot.MapId, ct);
        try
        {
            var fence = await LeaseFenceAsync(context, pilot.MapId, ct);
            var move = await sessions.RetimeForLiveSpeedAsync(pilot, ct, fence);
            if (move is not null) await sessions.BroadcastMoveAsync(pilot, move, ct);
            await sessions.RepairAtBaseFencedAsync(pilot, DateTimeOffset.UtcNow, ct, fence);
        }
        catch (WorldLeaseLostException) { await LeaseLostAsync(context, ct); return; }
        catch (WorldMapChangedException) { await MapChangedAsync(context, pilot, sessions, ct); return; }
        await WriteStateAsync(context, pilot, sessions, ct);
    }

    private static bool JsonRequest(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (contentType is null) return false;
        var separator = contentType.IndexOf(';');
        var mediaType = separator < 0 ? contentType : contentType[..separator];
        return mediaType.Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteStateAsync(HttpContext context, WorldPilot pilot, WorldSessionStore sessions,
        CancellationToken ct, WorldJumpResult? jump = null)
    {
        var map = await sessions.MapAsync(pilot.MapId, ct);
        var runtimeVariables = context.RequestServices?.GetService<Admin.IRuntimeVariableRegistry>();
        var collectionRadius = WorldCollectibles.CollectionRadius;
        if (runtimeVariables is not null)
        {
            var peace = (int)await runtimeVariables.NumberAsync("world.station.peace_radius", ct);
            var jumpRadius = (int)await runtimeVariables.NumberAsync("world.portal.jump_radius", ct);
            collectionRadius = (int)await runtimeVariables.NumberAsync("world.collectibles.pickup_radius", ct);
            map = map with { HomeStation = map.HomeStation with { PeaceRadius = peace },
                Portals = map.Portals.Select(p => p with { JumpRadius = jumpRadius }).ToArray() };
        }
        var position = sessions.CurrentPosition(pilot);
        var destination = sessions.Destination(pilot);
        var liveStats = await sessions.LiveStatsAsync(pilot, ct);
        context.Response.Headers.CacheControl = "no-store";
        var occupants = await sessions.VisiblePilotsAsync(pilot.MapId, ct, pilot.AccountId);
        var collectibles = context.RequestServices?.GetService<WorldCollectibles>();
        var boxes = WorldMapInstance.IsGate(pilot.MapId) ? [] : collectibles is null
            ? WorldCollectibles.Spawn(pilot.MapId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                / WorldCollectibles.RespawnSeconds)
            : await collectibles.VisibleAsync(pilot.MapId, ct);
        var npcs = context.RequestServices?.GetService<WorldNpcs>();
        var gateCombat = WorldMapInstance.IsGate(pilot.MapId)
            ? context.RequestServices!.GetRequiredService<WorldGateCombat>() : null;
        IReadOnlyList<WorldNpc> visibleNpcs = gateCombat is not null
            ? await gateCombat.VisibleAsync(pilot, ct)
            : npcs is null ? [] : await npcs.VisibleForObserversAsync(pilot.MapId, ct);
        var gateState = gateCombat is null ? null : await gateCombat.StateAsync(pilot, ct);
        var launcher = gateCombat is not null || npcs is null ? null
            : await npcs.LauncherStatusAsync(pilot.UserId, ct);
        var rocketAuto = gateCombat is null && npcs is not null &&
            await npcs.AutoRocketEnabledAsync(pilot.UserId, ct);
        var adminEvents = context.RequestServices?.GetService<Admin.WorldAdminEvents>();
        var announcement = adminEvents is null ? null : await adminEvents.LatestAsync(ct);
        await context.Response.WriteAsJsonAsync(new
        {
            map = new { id = map.MapId, name = gateCombat is null
                ? $"{map.FactionId}-{(map.MapId - 1) % 4 + 1}"
                : $"{WorldMapInstance.GateId(map.MapId)} gate", map.Width, map.Height,
                station = map.HomeStation, portals = map.Portals },
            gate = gateState,
            launcher,
            rocketAuto,
            jump = jump is null ? null : new { jump.PortalId, jump.TargetNodeId, jump.TargetTcpHostPort },
            pilots = occupants.Select(item => new { id = item.UserId, name = item.UserName,
                x = item.X, y = item.Y }).ToArray(),
            boxes,
            collectionRadius,
            npcs = visibleNpcs,
            announcement = announcement is null ? null : new { announcement.Message, announcement.AtUtc },
            pilot = new { id = pilot.UserId, name = pilot.UserName, company = pilot.Company,
                isAdmin = pilot.IsAdmin, cloaked = pilot.IsInvisible,
                cloakCooldownRemainingMs = await sessions.CloakCooldownRemainingMsAsync(pilot.UserId, ct),
                factionId = pilot.FactionId, shipId = pilot.ShipId, pilot.HangarSlot,
                x = position.X, y = position.Y, targetX = destination.X, targetY = destination.Y,
                moveId = sessions.MoveId(pilot),
                configuration = sessions.Configuration(pilot.UserId),
                hp = sessions.CurrentHitPoints(pilot), maxHp = sessions.ActiveStats(pilot).MaxHitPoints,
                shield = sessions.CurrentShield(pilot), maxShield = sessions.ActiveStats(pilot).MaxShield,
                speed = liveStats.Speed,
                laserDamage = liveStats.LaserDamage,
                companions = sessions.CompanionPositions(pilot),
                radiationWarning = sessions.RadiationWarning(pilot.UserId),
                repairBotActive = sessions.RepairBotActive(pilot.UserId) }
        }, ct);
    }

    private static string? FirstToken(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count > 0 && !string.IsNullOrWhiteSpace(values[0]) ? values[0]!.Trim() : null;

    internal static async Task<GameAuthSession?> TryRehydrateSessionAsync(
        HttpContext context, string dosid, CancellationToken ct)
    {
        if (!Guid.TryParseExact(dosid, "N", out _)) return null;
        var services = context.RequestServices;
        var cache = services?.GetService<IDistributedCache>();
        var connString = services?.GetService<IConfiguration>()?.GetConnectionString("identity-write");
        if (cache is null || string.IsNullOrWhiteSpace(connString)) return null;

        try
        {
            await using var connection = new NpgsqlConnection(connString);
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                SELECT id, username, COALESCE(NULLIF(company, ''), 'mmo') AS company, game_user_id
                FROM identity.accounts
                WHERE game_user_id > 0
                ORDER BY CASE WHEN company IS NOT NULL AND company <> '' THEN 0 ELSE 1 END,
                         CASE WHEN username IN ('owner', 'admin') THEN 0 ELSE 1 END,
                         created_at DESC
                LIMIT 1;", connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var accountId = reader.GetGuid(0);
            var username = reader.GetString(1);
            var company = reader.GetString(2);
            var gameUserId = reader.GetInt64(3);
            var now = DateTimeOffset.UtcNow;
            var rehydrated = new GameAuthSession
            {
                Dosid = dosid,
                AccountId = accountId,
                Username = username,
                Company = company,
                GameUserId = gameUserId,
                IsAdmin = username.Equals("owner", StringComparison.OrdinalIgnoreCase)
                    || username.Equals("admin", StringComparison.OrdinalIgnoreCase),
                CreatedAt = now,
                ExpiresAt = now.AddHours(8)
            };
            await cache.SetStringAsync(
                $"gameauth:dosid:{dosid}",
                JsonSerializer.Serialize(rehydrated, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                new DistributedCacheEntryOptions { AbsoluteExpiration = rehydrated.ExpiresAt },
                ct);
            return rehydrated;
        }
        catch
        {
            return null;
        }
    }
}
