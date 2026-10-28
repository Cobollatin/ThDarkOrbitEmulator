using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalClanAction
{
    // Emulator-defined form lifetime; stale pages must be reloaded before submitting.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 160;
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang"];

    private enum KeyCheck { Valid, Invalid, Expired }

    public static string CreateFormKey(string dosid, string action, string clanId, DateTimeOffset? issuedAt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, action, clanId, nonce, issued)}";
    }

    private static string Sign(string dosid, string action, string clanId, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"clan:{action}:{clanId}:{nonce}:{issued}"))).ToLowerInvariant();

    private static KeyCheck CheckKey(string dosid, string action, string clanId, string key, DateTimeOffset now)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _) ||
            parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return KeyCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()),
                Encoding.ASCII.GetBytes(Sign(dosid, action, clanId, parts[0], parts[1])))) return KeyCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        if (issued > now + ClockSkew) return KeyCheck.Invalid;
        return now - issued > FormLifetime ? KeyCheck.Expired : KeyCheck.Valid;
    }

    internal static bool IsValidOperationKey(string dosid, string action, string subject, string key, DateTimeOffset now) =>
        key.Length <= MaxKeyLength && CheckKey(dosid, action, subject, key, now) == KeyCheck.Valid;

    // Directory ids are lowercase ASCII words joined by single hyphens, e.g. mmo-guard.
    public static bool IsWellFormedClanId(string clanId) =>
        clanId.Length is >= 1 and <= 64 && clanId[0] != '-' && clanId[^1] != '-' && !clanId.Contains("--")
        && clanId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    // The page accepts the shell's navigation parameters once each, plus lowercase `detail`
    // (a well-formed clan id), `filter` (all, recruiting, closed or empty) and one of the
    // post-action notices `joined` or `left` (a well-formed clan id) and a `q` search;
    // anything else is rejected.
    // Menu-197: every key must be exact lowercase and the action value must be the canonical `internalClan`.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalClan"
        && !(query.ContainsKey("joined") && query.ContainsKey("left")) && query.All(pair =>
            (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal) || pair.Key is "detail" or "filter" or "joined" or "left" or "q")
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key is not ("detail" or "joined" or "left") || IsWellFormedClanId(pair.Value.ToString()))
            && (pair.Key != "filter" || LocalClan.IsFilter(pair.Value.ToString()))
            && (pair.Key != "q" || LocalClan.IsWellFormedSearch(pair.Value.ToString())));

    // Successful changes return to the directory with an emulator-defined notice parameter.
    public static string SuccessLocation(bool joined, string clanId) =>
        $"/indexInternal.es?action=internalClan&{(joined ? "joined" : "left")}={clanId}";

    // Action forms post to exactly one "action" parameter, with the key in canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1;

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Clan error</title></head><body><h1>Clan request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalClan\">Return to Clan</a></body></html>", ct);
    }

    public static Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalClanStore clans, CancellationToken ct) => HandleAsync(context, sessions, clans, TimeProvider.System, ct);

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalClanStore clans, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Clan changes must be submitted with POST.", ct);
            return;
        }
        var action = request.Query["action"].ToString();
        if (!ValidActionQuery(request.Query) ||
            action is not ("internalClanJoin" or "internalClanLeave"))
        { await ErrorAsync(response, 400, "Invalid Clan action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Clan.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a Clan form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Clan form could not be read.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "clan", "key"))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var clanId = form["clan"].ToString();
        var key = form["key"].ToString();
        var joining = action == "internalClanJoin";
        if (!IsWellFormedClanId(clanId) || key.Length > MaxKeyLength)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var check = CheckKey(session.Dosid, joining ? "join" : "leave", clanId, key, clock.GetUtcNow());
        if (check == KeyCheck.Invalid)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (check == KeyCheck.Expired)
        { await ErrorAsync(response, 409, "This form has expired. Reload Clan and try again.", ct); return; }
        var entry = LocalClanStore.Find(clanId);
        // Leaving the pilot's own membership is allowed even after a company change or clan retirement.
        var leavingOwn = !joining && (entry is null || entry.Company != session.Company) &&
            await clans.GetMembershipAsync(session.AccountId, ct) == clanId;
        if (entry is null && !leavingOwn)
        { await ErrorAsync(response, 404, "This clan does not exist.", ct); return; }
        if (entry is not null && entry.Company != session.Company && !leavingOwn)
        { await ErrorAsync(response, 403, "This clan belongs to another company.", ct); return; }
        if (joining && !entry!.Recruiting)
        { await ErrorAsync(response, 409, "This clan is not recruiting.", ct); return; }
        var result = await clans.ChangeAsync(session.AccountId, session.Company!, joining ? clanId : null,
            joining ? null : clanId, $"clan:{key}", ct);
        if (result != ClanChange.Applied)
        { await ErrorAsync(response, result == ClanChange.Conflict ? 409 : 400,
            result == ClanChange.Conflict ? "This form was already submitted or membership has changed." : "Invalid clan membership change.", ct); return; }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = SuccessLocation(joining, clanId);
    }
}
