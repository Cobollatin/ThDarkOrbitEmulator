using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalSkylabAction
{
    // Emulator-defined form lifetime and limits; no official Skylab form was captured.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 128;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang"];

    public static string CreateFormKey(string dosid, string target, string expected, DateTimeOffset? issued = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var seconds = (issued ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        return $"{nonce}.{seconds}.{Sign(dosid, target, expected, nonce, seconds)}";
    }

    private static string Sign(string dosid, string target, string expected, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"skylab:{target}:{expected}:{nonce}:{issued}"))).ToLowerInvariant();

    // Returns the issue time of a correctly signed key, or null for malformed or tampered keys.
    private static DateTimeOffset? KeyIssued(string dosid, string target, string expected, string key)
    {
        if (key.Length > MaxKeyLength) return null;
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _)
            || parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) || parts[1][0] == '0'
            || parts[2].Length != 64 || !long.TryParse(parts[1], out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]),
            Encoding.ASCII.GetBytes(Sign(dosid, target, expected, parts[0], parts[1])))) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    public static bool ValidProgressionKey(string dosid, string target, string expected, string key, DateTimeOffset now) =>
        KeyIssued(dosid, target, expected, key) is { } issued && issued <= now + ClockSkew && now - issued <= FormLifetime;

    // GET page accepts only exact lowercase shell keys, each once, at most 64 characters and without control characters.
    // The optional lowercase `detail` key names one collector by its lowercase ASCII id (existence is checked after the session).
    // Menu-121: the redirect targets add either lowercase `upgraded={collector}` (same shape as `detail`) or
    // lowercase `collected=1`; the two are mutually exclusive.
    // Menu-148: optional exact-case `filter` is 1-16 lowercase ASCII letters; unknown names are 404 after the session.
    // Menu-207: optional exact-case `q` searches collector ids and names; it cannot be combined with `detail`, so
    // detail panels and action redirects keep their existing contract.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalSkylab" && query.All(pair =>
            (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal) || pair.Key is "detail" or "upgraded" or "collected" or "filter" or "q")
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= 64
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key is not ("detail" or "upgraded") || ValidDetail(pair.Value.ToString()))
            && (pair.Key != "filter" || pair.Value.ToString() is { Length: >= 1 and <= 16 } filter && filter.All(char.IsAsciiLetterLower))
            && (pair.Key != "q" || ValidSearch(pair.Value.ToString()))
            && (pair.Key != "collected" || pair.Value.ToString() == "1"))
            && !(query.ContainsKey("upgraded") && query.ContainsKey("collected"))
            && !(query.ContainsKey("detail") && Search(query) is not null);

    // A blank search is the full list; otherwise the trimmed text is 2-40 ASCII letters, digits, spaces, '-' or '_'.
    public static bool ValidSearch(string value)
    {
        if (value.Any(char.IsControl)) return false;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        return trimmed.Length is >= 2 and <= 40
            && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_');
    }

    public static string? Search(IQueryCollection query) =>
        query.TryGetValue("q", out var value) && !string.IsNullOrWhiteSpace(value.ToString())
            ? value.ToString().Trim() : null;

    private static bool ValidDetail(string value) =>
        value.Length is >= 1 and <= 32 && value.All(char.IsAsciiLetterLower);

    public static string UpgradedLocation(string collectorId) =>
        $"/indexInternal.es?action=internalSkylab&upgraded={Uri.EscapeDataString(collectorId)}";

    public const string CollectedLocation = "/indexInternal.es?action=internalSkylab&collected=1";

    // Action forms post to exactly one lowercase action parameter in its canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
        && query["action"].ToString() is "internalSkylabUpgrade" or "internalSkylabCollect";

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Skylab error</title></head><body><h1>Skylab request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalSkylab\">Return to Skylab</a></body></html>", ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalSkylabStore skylab, CancellationToken ct, TimeProvider? clock = null)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        { await ErrorAsync(response, 405, "Skylab actions must be submitted with POST.", ct); return; }
        var action = request.Query["action"].ToString();
        if (!ValidActionQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Skylab action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Skylab.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a Skylab form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
        { await ErrorAsync(response, 400, "The Skylab form could not be read.", ct); return; }
        var upgrade = action == "internalSkylabUpgrade";
        if (!LocalHangarError.HasExactly(form, upgrade ? ["collector", "level", "key"] : ["time", "key"]))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var target = upgrade ? form["collector"].ToString() : "collect";
        var expected = form[upgrade ? "level" : "time"].ToString();
        var key = form["key"].ToString();
        var issued = KeyIssued(session.Dosid, target, expected, key);
        if (issued is null ||
            upgrade && (!int.TryParse(expected, out var level) || level is < 1 or > 10) ||
            !upgrade && (!long.TryParse(expected, out var ticks) || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks))
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (issued > now + ClockSkew)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (now - issued > FormLifetime)
        { await ErrorAsync(response, 409, "This form has expired. Reload Skylab and try again.", ct); return; }
        if (upgrade && !LocalSkylab.Catalog.Any(c => c.Id == target))
        { await ErrorAsync(response, 404, "This collector does not exist.", ct); return; }
        var result = await skylab.ChangeAsync(session.AccountId, upgrade ? target : null,
            upgrade ? int.Parse(expected) : 0,
            upgrade ? default : new DateTimeOffset(long.Parse(expected), TimeSpan.Zero), $"skylab:{key}", ct);
        if (result != SkylabChange.Applied)
        {
            await ErrorAsync(response, result == SkylabChange.Invalid ? 400 : 409, result switch
            {
                SkylabChange.InsufficientResources => "There are not enough resources for this upgrade.",
                SkylabChange.MaxLevel => "This collector is already at the maximum level.",
                SkylabChange.NothingToCollect => "Nothing to collect yet. Production is collected in whole minutes.",
                SkylabChange.Invalid => "The form is invalid or has been changed.",
                _ => "This form was already submitted or the collector state has changed."
            }, ct);
            return;
        }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = upgrade ? UpgradedLocation(target) : CollectedLocation;
    }
}
