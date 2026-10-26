using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalQuestAction
{
    // Emulator-defined form lifetime; stale pages must be reloaded before claiming.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 160;
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "filter", "detail", "claimed", "q"];

    private enum KeyCheck { Valid, Invalid, Expired }

    // The key binds the claim form to one session and quest; the one-time reward itself
    // (durable ledger key quest:{id}) prevents replay and double-submit.
    public static string CreateFormKey(string dosid, string questId, DateTimeOffset? issuedAt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, questId, nonce, issued)}";
    }

    private static string Sign(string dosid, string questId, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"quest:claim:{questId}:{nonce}:{issued}"))).ToLowerInvariant();

    private static KeyCheck CheckKey(string dosid, string questId, string key, DateTimeOffset now)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _) ||
            parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return KeyCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()),
                Encoding.ASCII.GetBytes(Sign(dosid, questId, parts[0], parts[1])))) return KeyCheck.Invalid;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return KeyCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (issued > now + ClockSkew) return KeyCheck.Invalid;
        return now - issued > FormLifetime ? KeyCheck.Expired : KeyCheck.Valid;
    }

    // The page accepts the shell's navigation parameters once each; anything else is rejected.
    // Menu-195: every key (shell keys `action`, `from`, `prc`, `lang` included) and the action value
    // must use their canonical casing.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalQuests"
        && query.All(pair => AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key is not ("filter" or "detail" or "claimed") || pair.Value.ToString().Length > 0)
            && (pair.Key is not "q" || LocalAuction.IsSearchShape(pair.Value.ToString())));

    // Menu-209: optional quest search (emulator-defined), trimmed; a blank value shows the full list.
    public static string? Search(IQueryCollection query) =>
        query["q"].Count == 1 && query["q"].ToString().Trim() is { Length: > 0 } search ? search : null;

    // Menu-119: a successful claim returns to the list with a confirmation for that quest (emulator-defined).
    public static string ClaimedRedirect(string questId) =>
        $"/indexInternal.es?action=internalQuests&claimed={Uri.EscapeDataString(questId)}";

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync("<!doctype html><html><head><meta charset=\"utf-8\"><title>Quests error</title></head>" +
            $"<body><main><h1>Quest request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            "<a href=\"/indexInternal.es?action=internalQuests\">Return to Quests</a></main></body></html>", ct);
    }

    public static Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct) => HandleAsync(context, sessions, equipment, TimeProvider.System, ct);

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Quest claims must be submitted with POST.", ct);
            return;
        }
        // The dispatcher routes on `action` case-insensitively; other casing, repeats or extra keys are tampering.
        if (request.Query.Count != 1 || request.Query.Keys.Single() != "action" || request.Query["action"].Count != 1
            || !string.Equals(request.Query["action"], "internalQuestClaim", StringComparison.Ordinal))
        { await ErrorAsync(response, 400, "Invalid Quests action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before claiming quest rewards.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a quest claim form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The quest claim form could not be read.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "quest", "key"))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var questId = form["quest"].ToString();
        var key = form["key"].ToString();
        if (!LocalQuests.IsWellFormedId(questId) || key.Length > MaxKeyLength)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var check = CheckKey(session.Dosid, questId, key, clock.GetUtcNow());
        if (check == KeyCheck.Invalid)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (check == KeyCheck.Expired)
        { await ErrorAsync(response, 409, "This form has expired. Reload Quests and try again.", ct); return; }
        switch (await equipment.ClaimQuestResultAsync(session.AccountId, questId, ct))
        {
            case QuestClaimResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = ClaimedRedirect(questId);
                return;
            case QuestClaimResult.NotFound:
                await ErrorAsync(response, 404, "This quest does not exist.", ct); return;
            case QuestClaimResult.Unavailable:
                await ErrorAsync(response, 409, "This quest is currently unavailable.", ct); return;
            case QuestClaimResult.AlreadyClaimed:
                await ErrorAsync(response, 409, "This reward was already claimed.", ct); return;
            default:
                await ErrorAsync(response, 400, "This quest is not complete yet.", ct); return;
        }
    }
}
