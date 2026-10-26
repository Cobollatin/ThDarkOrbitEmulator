using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.GameAuth;

// The forum link target captured in unityApi/links.php; the response and rejection rules are emulator-defined.
public static class GameApiForumAction
{
    public const string BoardAction = "portal.redirectToBoard";

    // Exactly one canonical action is served; anything else is a 404 before the session is looked up.
    public static async Task<LocalLinksRequest.Result> ResolveAsync(HttpRequest request, IGameAuthSessionStore sessions,
        CancellationToken ct)
    {
        // Query lookups ignore key case, so the key itself must also be the canonical "action".
        var actions = request.Query["action"];
        if (!request.Query.Keys.Contains("action", StringComparer.Ordinal) || actions.Count != 1
            || !string.Equals(actions.ToString(), BoardAction, StringComparison.Ordinal))
            return LocalLinksRequest.UnknownGameApiAction();

        var dosid = request.Cookies["dosid"];
        var session = !string.IsNullOrWhiteSpace(dosid) && dosid.Length <= LocalLogoutAction.MaxDosidLength
            ? await sessions.TryGetByDosidAsync(dosid, ct)
            : null;
        // Same validation as internalForum: one value per key, known topic and category, live session.
        return LocalLinksRequest.Resolve(request.Query, session, LocalLinksRequest.Page.Forum);
    }
}
