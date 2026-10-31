using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

// POST /unityApi/setLang.php (form field "lang") is sent by LaunchShow.GetUnityPostLanguage when the launch-screen
// language dropdown changes. No live capture exists; the emulator stores it as the account language so the next
// login.php "lang" keeps the player's choice instead of resetting it.
public static class SetLangEndpoint
{
    public static async Task<int> SaveAsync(HttpRequest request, IGameAuthSessionStore sessions,
        LocalAccountPreferencesStore preferences, CancellationToken ct, ILogger? logger = null)
    {
        var dosid = request.Cookies.TryGetValue("dosid", out var value) ? value : null;
        var session = !string.IsNullOrWhiteSpace(dosid) && dosid.Length <= LocalLogoutAction.MaxDosidLength
            ? await sessions.TryGetByDosidAsync(dosid, ct)
            : null;
        if (session is null)
        {
            logger?.LogWarning("setLang.php without a valid dosid cookie (cookie present: {Present})", dosid is not null);
            return StatusCodes.Status401Unauthorized;
        }
        if (!request.HasFormContentType)
        {
            logger?.LogWarning("setLang.php with content type {ContentType}", request.ContentType);
            return StatusCodes.Status400BadRequest;
        }

        // The launch dropdown posts the client's own codes ("en_US", "pt_BR"); match them case-insensitively.
        var posted = (await request.ReadFormAsync(ct))["lang"].ToString().Trim();
        var language = LocalAccountPreferencesStore.Languages
            .FirstOrDefault(item => string.Equals(item.Code, posted, StringComparison.OrdinalIgnoreCase)).Code ?? posted;
        var current = await preferences.GetAsync(session.AccountId, ct);
        var change = await preferences.SaveAsync(session.AccountId, language,
            current.SoundEnabled ? "true" : "false", current.NotificationsEnabled ? "true" : "false", null, ct);
        logger?.LogInformation("setLang.php lang={Language} previous={Previous} result={Change}",
            language, current.Language, change);
        return change is AccountPreferencesChange.Applied or AccountPreferencesChange.Unchanged
            ? StatusCodes.Status200OK
            : StatusCodes.Status400BadRequest;
    }
}
