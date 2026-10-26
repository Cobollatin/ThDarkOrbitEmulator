using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalGalaxyGateAction
{
    // Emulator-defined form lifetime and limits; no official Galaxy Gates form was captured.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 128;
    public const int MaxSpinsPerForm = 10;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang"];

    public static string CreateFormKey(string dosid, string gateId, bool activate = false, DateTimeOffset? issued = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var seconds = (issued ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        return $"{nonce}.{seconds}.{Sign(dosid, gateId, nonce, seconds, activate)}";
    }

    private static string Sign(string dosid, string gateId, string nonce, string issued, bool activate) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"galaxy-gates:{(activate ? "activate" : "spin")}:{gateId}:{nonce}:{issued}"))).ToLowerInvariant();

    // Returns the issue time of a correctly signed key, or null for malformed, legacy or tampered keys.
    private static DateTimeOffset? KeyIssued(string dosid, string gateId, string key, bool activate)
    {
        if (key.Length > MaxKeyLength) return null;
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _)
            || parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) || parts[1][0] == '0'
            || parts[2].Length != 64 || !long.TryParse(parts[1], out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]),
            Encoding.ASCII.GetBytes(Sign(dosid, gateId, parts[0], parts[1], activate)))) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    // Gate ids are 1-32 lowercase ASCII letters.
    internal static bool ValidGateId(string value) =>
        value.Length is >= 1 and <= 32 && value.All(char.IsAsciiLetterLower);

    // Spin amounts are plain ASCII digits 1-10 without sign, spaces or leading zeros.
    internal static int? ParseAmount(string value) =>
        value.Length is >= 1 and <= 2 && value.All(char.IsAsciiDigit) && value[0] != '0'
            && int.Parse(value) is var amount && amount <= MaxSpinsPerForm ? amount : null;

    // GET page accepts the shared shell parameters, each once, at most 64 characters and without control characters,
    // plus optional exact-case `gate` detail and `spun`/`activated` notice ids shaped like gate ids (Menu-125; the two
    // notices are mutually exclusive). Menu-145: optional exact-case `filter` is 1-16 lowercase ASCII letters; a
    // well-formed but unknown value is 404 on the page. Menu-206: q searches the list,
    // and detail links leave the search so actions retain their existing form contract.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query.Keys.Any(key => key == "action")
        && query["action"].ToString() == "internalGalaxyGates"
        && query.All(pair => pair.Value.Count == 1
            && (pair.Key is "gate" or "spun" or "activated"
                ? ValidGateId(pair.Value.ToString())
                : pair.Key == "q"
                ? ValidSearch(pair.Value.ToString())
                : pair.Key == "filter"
                ? pair.Value.ToString() is { Length: >= 1 and <= 16 } filter && filter.All(char.IsAsciiLetterLower)
                : AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
                    && pair.Value.ToString().Length <= 64
                    && !pair.Value.ToString().Any(char.IsControl)))
        && !(query.ContainsKey("spun") && query.ContainsKey("activated"))
        && !(query.ContainsKey("gate") && !string.IsNullOrWhiteSpace(query["q"].ToString()));

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

    // Menu-125: successful actions redirect with the gate they touched; the notice text comes from saved state.
    public static string RedirectTarget(bool spinning, string gateId, string? filter = null) =>
        $"/indexInternal.es?action=internalGalaxyGates&{(spinning ? "spun" : "activated")}={Uri.EscapeDataString(gateId)}"
        + (filter is null or "all" ? "" : $"&filter={Uri.EscapeDataString(filter)}");

    public static string? NoticeGate(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var value) ? value.ToString() : null;

    // Action forms post to exactly one lowercase action parameter in its canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
        && query["action"].ToString() is "internalGalaxyGateSpin" or "internalGalaxyGateActivate";

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Galaxy Gates error</title></head><body><h1>Galaxy Gates request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalGalaxyGates\">Return to Galaxy Gates</a></body></html>", ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalGalaxyGateStore gates, CancellationToken ct, TimeProvider? clock = null)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            await ErrorAsync(response, 405, "Galaxy Gates actions must be submitted with POST.", ct); return;
        }
        var action = request.Query["action"].ToString();
        if (!ValidActionQuery(request.Query))
        {
            await ErrorAsync(response, 400, "Invalid gate action.", ct); return;
        }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        {
            await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return;
        }
        if (CompanyCatalog.Find(session.Company) is null)
        {
            await ErrorAsync(response, 403, "Choose a valid company before using Galaxy Gates.", ct); return;
        }
        if (!request.HasFormContentType)
        {
            await ErrorAsync(response, 400, "Submit a Galaxy Gates form.", ct); return;
        }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
        {
            await ErrorAsync(response, 400, "The Galaxy Gates form could not be read.", ct); return;
        }
        var spinning = action == "internalGalaxyGateSpin";
        var hasFilter = form.Keys.Any(key => key == "filter");
        if (!form.Keys.All(key => key is "gate" or "amount" or "key" or "filter")
            || !LocalHangarError.HasExactly(form, spinning
                ? hasFilter ? ["gate", "amount", "key", "filter"] : ["gate", "amount", "key"]
                : hasFilter ? ["gate", "key", "filter"] : ["gate", "key"])
            || hasFilter && !LocalGalaxyGates.IsFilter(form["filter"].ToString()))
        {
            await ErrorAsync(response, 400, "The gate form has missing, repeated, or unexpected fields.", ct); return;
        }
        var gateId = form["gate"].ToString();
        var filter = hasFilter ? form["filter"].ToString() : null;
        if (!ValidGateId(gateId))
        {
            await ErrorAsync(response, 400, "Invalid gate ID.", ct); return;
        }
        var amount = spinning ? ParseAmount(form["amount"].ToString()) : 0;
        if (amount is null)
        {
            await ErrorAsync(response, 400, $"Spin amount must be a whole number from 1 to {MaxSpinsPerForm}.", ct); return;
        }
        var key = form["key"].ToString();
        var issued = KeyIssued(session.Dosid, gateId, key, !spinning);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (issued is null || issued > now + ClockSkew)
        {
            await ErrorAsync(response, 400, "The gate form is invalid or has been changed.", ct); return;
        }
        if (now - issued > FormLifetime)
        {
            await ErrorAsync(response, 409, "This form has expired. Reload Galaxy Gates and try again.", ct); return;
        }
        if (!LocalGalaxyGates.Catalog.Any(g => g.Id == gateId))
        {
            await ErrorAsync(response, 404, "This gate does not exist.", ct); return;
        }
        // Pre-check the current state for a specific message; the store still enforces every rule atomically.
        var state = await gates.GetAsync(session.AccountId, ct);
        var gate = state.Gates.Single(g => g.Id == gateId);
        var remaining = gate.PartsRequired - gate.PartsOwned;
        var precondition = gate.Activated ? "This gate is already activated."
            : spinning && remaining == 0 ? "This gate is complete. Activate it instead of spinning."
            : spinning && amount > remaining ? $"Only {remaining} part(s) are missing for this gate."
            : !spinning && remaining > 0 ? $"This gate still needs {remaining} part(s) before activation."
            : null;
        if (precondition is not null)
        {
            await ErrorAsync(response, 409, precondition, ct); return;
        }
        if (spinning && state.Energy < amount)
        {
            await ErrorAsync(response, 400,
                $"There is not enough Galaxy Gate energy: {amount} needed, {state.Energy} available.", ct);
            return;
        }
        var result = await gates.ChangeAsync(session.AccountId, gateId, amount.Value, $"gate:{key}", ct);
        if (result != GalaxyGateChange.Applied)
        {
            await ErrorAsync(response, result == GalaxyGateChange.Conflict ? 409 : 400,
                result switch
                {
                    GalaxyGateChange.Conflict => "This form was already submitted or the gate state has changed.",
                    GalaxyGateChange.InsufficientEnergy => "There is not enough Galaxy Gate energy.",
                    _ => "Invalid Galaxy Gate action."
                }, ct);
            return;
        }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = RedirectTarget(spinning, gateId, filter);
    }
}
