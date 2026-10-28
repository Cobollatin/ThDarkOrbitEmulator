using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

// Routes named by the installed StreamingAssets/config.txt. The response bodies for
// routes absent from the retained HAR are local contracts, not captured Live replies.
public static class UnityConfigRoutes
{
    private static async Task<GameAuthSession?> SessionAsync(HttpContext context, IGameAuthSessionStore sessions, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = UnityLoginAction.CacheControl;
        var dosid = context.Request.Cookies.TryGetValue("dosid", out var cookie) ? cookie : null;
        if (string.IsNullOrWhiteSpace(dosid) || dosid.Length > LocalLogoutAction.MaxDosidLength) return null;
        return await sessions.TryGetByDosidAsync(dosid, ct);
    }

    private static IResult Navigate(string action) => Results.Redirect($"/indexInternal.es?action={action}");

    public static void MapUnityConfigRoutes(this WebApplication app)
    {
        app.MapGet("/unityApi/instance.php", async (HttpContext context, IGameAuthSessionStore sessions,
            CockpitCatalogStore catalog, CancellationToken ct) =>
        {
            var session = await SessionAsync(context, sessions, ct);
            if (session is null) return Results.Unauthorized();
            var servers = (await catalog.ServersAsync(ct)).Where(item => item.Enabled).ToArray();
            var host = ClientFacingGameHost.Resolve(context.Request);
            var entries = servers.Select((item, index) => new
            {
                continent = "local", langCode = "en", name = item.InstanceName, active = item.Status == "online" ? 1 : 0,
                location = item.Code, id = index + 1, lang = "en", title = item.InstanceName,
                url = $"{host}/indexInternal.es?action=internalWebGL&from=internalStart", recommended = item.IsDefault ? 1 : 0
            }).ToArray();
            var region = new { serverCount = entries.Length, serverList = entries };
            var emptyRegion = new { serverCount = 0, serverList = Array.Empty<object>() };
            return Results.Json(new { data = new { faction = session.Company ?? "", hangars = Array.Empty<object>(),
                euWest = region, euEast = (object)emptyRegion, usWest = emptyRegion, usEast = emptyRegion, test = emptyRegion } });
        }).AllowAnonymous();

        app.MapPost("/unityApi/changeInstance.php", async (HttpContext context, IGameAuthSessionStore sessions,
            CockpitCatalogStore catalog, CancellationToken ct) =>
        {
            if (await SessionAsync(context, sessions, ct) is null) return Results.Unauthorized();
            if (!context.Request.HasFormContentType) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(ct);
            if (form.Count != 1 || !form.TryGetValue("instanceId", out var ids) || ids.Count != 1
                || !int.TryParse(ids[0], out var id)) return Results.BadRequest();
            var servers = (await catalog.ServersAsync(ct)).Where(item => item.Enabled).ToArray();
            if (id < 1 || id > servers.Length) return Results.NotFound();
            if (servers[id - 1].Status != "online") return Results.Conflict();
            return Results.Json(new { result = "ok", status = 0, error = (string?)null,
                link = $"{ClientFacingGameHost.Resolve(context.Request)}/indexInternal.es?action=internalWebGL&from=internalStart" });
        }).AllowAnonymous();

        app.MapPost("/unityApi/agent.php", async (HttpContext context, IGameAuthSessionStore sessions, CancellationToken ct) =>
        {
            if (await SessionAsync(context, sessions, ct) is null) return Results.Unauthorized();
            if (!context.Request.HasFormContentType) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(ct);
            return form.Count == 1 && form.TryGetValue("functionId", out var ids) && ids.Count == 1
                && ids[0] is { Length: > 0 and <= 80 } ? Results.Json(new { result = true }) : Results.BadRequest();
        }).AllowAnonymous();

        app.MapPost("/unityApi/setLang.php", async (HttpContext context, IGameAuthSessionStore sessions,
            CockpitCatalogStore catalog, LocalAccountPreferencesStore preferences, CancellationToken ct) =>
        {
            var session = await SessionAsync(context, sessions, ct);
            if (session is null) return Results.Unauthorized();
            if (!context.Request.HasFormContentType) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(ct);
            if (form.Count != 1 || !form.TryGetValue("lang", out var langs) || langs.Count != 1
                || langs[0] is not { } language
                || !(await catalog.LanguagesAsync(ct)).Any(item => item.Enabled && item.Code == language)) return Results.BadRequest();
            var saved = await preferences.GetAsync(session.AccountId, ct);
            await preferences.SaveAsync(session.AccountId, language, saved.SoundEnabled.ToString().ToLowerInvariant(),
                saved.NotificationsEnabled.ToString().ToLowerInvariant(), ct);
            return Results.Json(new { result = true, data = new { language } });
        }).AllowAnonymous();

        app.MapGet("/unityApi/evoucher.php", async (HttpContext context, IGameAuthSessionStore sessions, CancellationToken ct) =>
            await SessionAsync(context, sessions, ct) is null ? Results.Unauthorized() : Navigate("internalPayment")).AllowAnonymous();
        app.MapGet("/unityApi/package.php", async (HttpContext context, IGameAuthSessionStore sessions, CancellationToken ct) =>
            await SessionAsync(context, sessions, ct) is null ? Results.Unauthorized() : Navigate("internalShop")).AllowAnonymous();
        app.MapGet("/unityApi/close.php", async (HttpContext context, IGameAuthSessionStore sessions, CancellationToken ct) =>
        {
            var session = await SessionAsync(context, sessions, ct);
            if (session is null) return Results.Unauthorized();
            await LocalLogoutAction.InvalidateAsync(context.Response, session.Dosid, sessions, ct);
            return Results.Json(new { data = Array.Empty<object>() });
        }).AllowAnonymous();
    }
}
