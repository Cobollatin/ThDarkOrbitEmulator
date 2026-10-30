using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

// Live returned 200 with the session ID inside the payload; rejection and caching rules are emulator-defined.
public static class UnityLoginAction
{
    public const string CacheControl = "no-store, no-cache, must-revalidate";

    // Returns the inner payload, or null after writing a 401 for a missing, oversized, unknown or expired cookie.
    public static async Task<string?> HandleAsync(HttpContext context, string? dosid,
        IGameAuthSessionStore sessions, LocalHangarStore hangars, LocalAccountPreferencesStore preferences,
        CancellationToken ct, IServerMeshRegistry? registry = null, WorldServerOptions? world = null,
        CockpitCatalogStore? catalog = null, WorldSessionStore? worldSessions = null)
    {
        var response = context.Response;
        response.Headers.CacheControl = CacheControl;

        var session = !string.IsNullOrWhiteSpace(dosid) && dosid.Length <= LocalLogoutAction.MaxDosidLength
            ? await sessions.TryGetByDosidAsync(dosid, ct)
            : null;
        if (session is null)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return null;
        }

        response.Headers.Append("X-Custom-Header", "DarkOrbitUnity");
        response.Cookies.Append("dosid", session.Dosid, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None
        });

        var activeSlot = await hangars.GetActiveSlotAsync(session.AccountId, ct);
        var saved = await preferences.GetAsync(session.AccountId, ct);
        var languages = catalog is null ? null : await catalog.LanguagesAsync(ct);
        var language = languages is null || languages.Any(item => item.Code == saved.Language && item.Enabled)
            ? saved.Language : languages.FirstOrDefault(item => item.IsDefault && item.Enabled)?.Code ?? "es";
        var servers = catalog is null ? null : await catalog.ServersAsync(ct);
        var instance = servers?.FirstOrDefault(item => item.IsDefault && item.Enabled)?.InstanceName ?? "Local emulator";
        return UnityLoginPayloadBuilder.Build(session, context.Request, activeSlot, language,
            registry is null ? null : await registry.GetSnapshotAsync(ct), world,
            catalog is null ? null : await catalog.HangarsAsync(session, activeSlot, ct), instance,
            languages?.Where(item => item.Enabled).Select(item => item.Code).ToHashSet(StringComparer.Ordinal),
            worldSessions is null ? 0 : await worldSessions.UnityDisplay2dAsync(session.GameUserId, ct));
    }
}
