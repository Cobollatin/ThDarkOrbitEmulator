using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalSpaceSearchAction
{
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "filter", "planet", "q"];

    // The page accepts the shell's navigation parameters once each; anything else is rejected.
    // `filter` must be a listed filter and `planet` a planet-shaped id (unknown ids are a 404 later).
    // The post-search notice key `explored` must be lowercase and planet-shaped; unknown planets show no notice.
    // All page keys and the action value use their canonical spelling.
    // Menu-213: `q` is trimmed; blank shows every planet, otherwise 2-40 ASCII letters, digits, spaces, '-' or '_'.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query.Keys.Contains("action", StringComparer.Ordinal) && query["action"].Count == 1
        && query["action"].ToString() == "internalSpaceSearch"
        && query.All(pair => (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal) || pair.Key == "explored")
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl))
        && (!query.TryGetValue("filter", out var filter) || LocalSpaceSearch.IsFilter(filter.ToString()))
        && (!query.TryGetValue("planet", out var planet) || LocalSpaceSearch.IsPlanetIdShape(planet.ToString()))
        && (!query.TryGetValue("explored", out var explored) || LocalSpaceSearch.IsPlanetIdShape(explored.ToString()))
        && (!query.TryGetValue("q", out var search) || string.IsNullOrWhiteSpace(search.ToString())
            || LocalSpaceSearch.IsSearch(search.ToString().Trim()));

    public static string? Search(IQueryCollection query) =>
        query.TryGetValue("q", out var value) && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString().Trim() : null;

    public static string SuccessLocation(string planetId) =>
        $"/indexInternal.es?action=internalSpaceSearch&explored={planetId}";

    // The explore query carries at most one action, spelled exactly as the page form posts it.
    public static bool ValidExploreQuery(IQueryCollection query) =>
        query.Count == 0 || (query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
        && string.Equals(query["action"].ToString(), "internalSpaceSearchExplore", StringComparison.Ordinal));

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Space Search actions must be submitted with POST.", ct);
            return;
        }
        // The dispatcher routes on `action` case-insensitively; other casing, repeats or extra keys are tampering.
        if (!ValidExploreQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Space Search action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || string.IsNullOrWhiteSpace(dosid)
            || dosid.Length > LocalLogoutAction.MaxDosidLength
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Space Search.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a Space Search form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Space Search form could not be read.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "planet") || form.Keys.Single() != "planet" || form.Files.Count != 0)
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var planet = form["planet"].ToString();
        if (!LocalSpaceSearch.IsPlanetIdShape(planet))
        { await ErrorAsync(response, 400, "The planet value is invalid.", ct); return; }
        switch (await equipment.SearchPlanetResultAsync(session.AccountId, planet, ct))
        {
            case LocalSpaceSearchResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = SuccessLocation(planet);
                return;
            case LocalSpaceSearchResult.UnknownPlanet:
                await ErrorAsync(response, 404, "That planet is not part of Space Search.", ct); return;
            case LocalSpaceSearchResult.AlreadySearched:
                await ErrorAsync(response, 409, "This planet was already searched.", ct); return;
            case LocalSpaceSearchResult.InsufficientCredits:
                await ErrorAsync(response, 409, "You do not have enough local credits.", ct); return;
            case LocalSpaceSearchResult.InventoryFull:
                await ErrorAsync(response, 409, "Your Hangar cannot hold more of this item.", ct); return;
            case LocalSpaceSearchResult.Unavailable:
                await ErrorAsync(response, 409, "This search reward is not available.", ct); return;
            default:
                await ErrorAsync(response, 400, "The Space Search request was rejected.", ct); return;
        }
    }

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Space Search error</title></head>" +
            $"<body><main><h1>Space Search request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            "<a href=\"/indexInternal.es?action=internalSpaceSearch\">Back to Space Search</a></main></body></html>", ct);
    }
}
