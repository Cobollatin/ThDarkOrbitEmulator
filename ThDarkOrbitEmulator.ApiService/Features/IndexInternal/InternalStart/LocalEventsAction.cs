using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalEventsAction
{
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "detail", "filter", "q"];
    private const int MaxPageValueLength = 64;

    // The page accepts the shell's navigation parameters once each; anything else is rejected.
    // Post-action notice keys are exact lowercase: `claimed` is tier-shaped (unknown tiers show no notice)
    // and `expedition` is `started` or `claimed`; only one notice key per request.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query.Keys.Contains("action", StringComparer.Ordinal) && query["action"].Count == 1
        && query["action"].ToString() == "internalEvents"
        && query.All(pair => (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal) || pair.Key is "claimed" or "expedition")
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key != "detail" || LocalEvents.IsTierIdShape(pair.Value.ToString()))
            // Menu-211: `filter` is one exact lowercase battle pass status tab.
            && (pair.Key != "filter" || LocalEvents.IsFilter(pair.Value.ToString()))
            && (pair.Key != "q" || string.IsNullOrWhiteSpace(pair.Value.ToString())
                || LocalEvents.IsSearch(pair.Value.ToString().Trim()))
            && (pair.Key != "claimed" || LocalEvents.IsTierIdShape(pair.Value.ToString()))
            && (pair.Key != "expedition" || pair.Value.ToString() is "started" or "claimed"))
        && !(query.ContainsKey("claimed") && query.ContainsKey("expedition"));

    public static string SuccessLocation(string action, string tier) => action switch
    {
        "internalBattlePassClaim" => $"/indexInternal.es?action=internalEvents&claimed={tier}",
        "internalExpeditionStart" => "/indexInternal.es?action=internalEvents&expedition=started",
        _ => "/indexInternal.es?action=internalEvents&expedition=claimed"
    };

    // Action forms post to exactly one "action" parameter, with the key and value in canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1;

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        var action = request.Query["action"].ToString();
        if (action is not ("internalBattlePassClaim" or "internalExpeditionStart" or "internalExpeditionClaim"))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Events actions must be submitted with POST.", ct);
            return;
        }
        if (!ValidActionQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Events action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Events.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit an Events form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The form could not be read.", ct); return; }
        var field = action switch
        {
            "internalBattlePassClaim" => "tier",
            "internalExpeditionStart" => "start",
            _ => "claim"
        };
        if (!LocalHangarError.HasExactly(form, field))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var value = form[field].ToString();
        if (field == "tier" ? !LocalEvents.IsTierIdShape(value) : value != "1")
        { await ErrorAsync(response, 400, "The form field value is invalid.", ct); return; }
        var result = action switch
        {
            "internalBattlePassClaim" => await equipment.ClaimBattlePassResultAsync(session.AccountId, value, ct),
            "internalExpeditionStart" => await equipment.StartExpeditionResultAsync(session.AccountId, clock.GetUtcNow(), ct),
            _ => await equipment.ClaimExpeditionResultAsync(session.AccountId, clock.GetUtcNow(), ct)
        };
        switch (result)
        {
            case LocalEventResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = SuccessLocation(action, value);
                return;
            case LocalEventResult.UnknownTier:
                await ErrorAsync(response, 404, "That battle pass tier does not exist.", ct); return;
            case LocalEventResult.Unavailable:
                await ErrorAsync(response, 409, "That battle pass tier is not available.", ct); return;
            case LocalEventResult.AlreadyClaimed:
                await ErrorAsync(response, 409, "This reward was already claimed or the expedition was already launched.", ct); return;
            case LocalEventResult.NotReady:
                await ErrorAsync(response, 409, "This reward is not ready yet.", ct); return;
            case LocalEventResult.InsufficientCredits:
                await ErrorAsync(response, 409, "You do not have enough local credits.", ct); return;
            case LocalEventResult.InventoryFull:
                await ErrorAsync(response, 409, "Your Hangar cannot hold more of this item.", ct); return;
            default:
                await ErrorAsync(response, 400, "The Events request was rejected.", ct); return;
        }
    }

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Events error</title></head>" +
            $"<body><main><h1>Events request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            "<a href=\"/indexInternal.es?action=internalEvents\">Back to Events</a></main></body></html>", ct);
    }
}
