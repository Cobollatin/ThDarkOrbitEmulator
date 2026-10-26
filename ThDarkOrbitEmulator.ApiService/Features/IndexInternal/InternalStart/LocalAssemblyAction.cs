using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalAssemblyAction
{
    // Emulator-defined form lifetime; stale pages must be reloaded before crafting.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    public const int MaxKeyLength = 160;
    public const int MaxRecipeIdLength = 64;
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang"];
    private static readonly string[] ViewPageKeys = ["status", "detail", "crafted", "q"];
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 40;

    private enum KeyCheck { Valid, Invalid, Expired }

    // Menu-122: a successful craft returns to the page with a confirmation for that recipe.
    public static string CraftedLocation(string recipeId) =>
        $"/indexInternal.es?action=internalAssembly&crafted={recipeId}";

    // The page accepts the shell's navigation parameters once each; anything else is rejected.
    // Menu-55: `status` and `detail` are emulator-defined view keys (`recipe` stays craft-only) and must use lowercase key casing.
    // Menu-122: `crafted` follows the `detail` rules; its notice text comes from saved craft progress, not the link.
    // Menu-203: `q` is trimmed, must be 2-40 characters when nonblank, and cannot be combined with `detail`.
    // Menu-193: the shell keys (`action`, `from`, `prc`, `lang`) and the action value must also be in their canonical casing.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalAssembly"
        && query.All(pair => (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
                || ViewPageKeys.Contains(pair.Key, StringComparer.Ordinal))
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl))
        && (!query.TryGetValue("status", out var status) || LocalAssembly.StatusFilters.Contains(status.ToString(), StringComparer.Ordinal))
        && (!query.TryGetValue("detail", out var detail) || IsWellFormedRecipeId(detail.ToString()))
        && (!query.TryGetValue("crafted", out var crafted) || IsWellFormedRecipeId(crafted.ToString()))
        && (Search(query) is not { } search
            || search.Length is >= MinSearchLength and <= MaxSearchLength && !query.ContainsKey("detail"));

    // Menu-203: optional `q` searches the recipe list; an empty or blank box (a submitted empty form) means no search.
    public static string? Search(IQueryCollection query) =>
        query.TryGetValue("q", out var value) && value.ToString().Trim() is { Length: > 0 } text ? text : null;

    // The craft form posts to exactly one action parameter in its canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.TryGetValue("action", out var action) && action.Count == 1
        && query.Keys.Single() == "action" && action.ToString() == "internalAssemblyCraft";

    // Recipe ids are lowercase ASCII words joined by underscores or hyphens.
    public static bool IsWellFormedRecipeId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxRecipeIdLength
        && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    // The key binds the craft form to one session and recipe. Its nonce becomes the durable
    // craft request key, so a replayed or double-submitted form crafts at most once.
    public static string CreateFormKey(string dosid, string recipeId, DateTimeOffset? issuedAt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, recipeId, nonce, issued)}";
    }

    private static string Sign(string dosid, string recipeId, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"assembly:craft:{recipeId}:{nonce}:{issued}"))).ToLowerInvariant();

    private static KeyCheck CheckKey(string dosid, string recipeId, string key, DateTimeOffset now)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _) ||
            parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return KeyCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()),
                Encoding.ASCII.GetBytes(Sign(dosid, recipeId, parts[0], parts[1])))) return KeyCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture));
        if (issued > now + ClockSkew) return KeyCheck.Invalid;
        return now - issued > FormLifetime ? KeyCheck.Expired : KeyCheck.Valid;
    }

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync("<!doctype html><html><head><meta charset=\"utf-8\"><title>Assembly error</title></head>" +
            $"<body><main><h1>Assembly request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            "<a href=\"/indexInternal.es?action=internalAssembly\">Return to Assembly</a></main></body></html>", ct);
    }

    // Retained for cache-only unit contracts; the routed action uses the database overload.
    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!context.Request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { response.StatusCode = 401; return; }
        if (string.IsNullOrWhiteSpace(session.Company)) { response.StatusCode = 403; return; }
        if (!context.Request.HasFormContentType || !await equipment.CraftAsync(session.AccountId,
            (await context.Request.ReadFormAsync(ct))["recipe"].ToString(), ct))
        { response.StatusCode = 400; return; }
        response.StatusCode = 303;
        response.Headers.Location = "/indexInternal.es?action=internalAssembly";
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, EconomyStore economy, CancellationToken ct) =>
        await HandleAsync(context, sessions, async (session, recipe, key, token) =>
        {
            await equipment.GetAsync(session.AccountId, token);
            return await economy.CraftAsync(session.AccountId, recipe, key, token);
        }, TimeProvider.System, ct);

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        Func<GameAuthSession, string, string, CancellationToken, Task<EconomyCraftResult>> craft,
        TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Submit the Assembly craft form.", ct);
            return;
        }
        if (!ValidActionQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Assembly action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Assembly.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit an Assembly craft form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
        { await ErrorAsync(response, 400, "The form could not be read.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "recipe", "key"))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var recipeId = form["recipe"].ToString();
        var key = form["key"].ToString();
        if (!IsWellFormedRecipeId(recipeId) || key.Length > MaxKeyLength)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        var check = CheckKey(session.Dosid, recipeId, key, clock.GetUtcNow());
        if (check == KeyCheck.Invalid)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (check == KeyCheck.Expired)
        { await ErrorAsync(response, 409, "This form has expired. Reload Assembly and try again.", ct); return; }
        switch (await craft(session, recipeId, $"assembly:{key.Split('.')[0]}", ct))
        {
            case EconomyCraftResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = CraftedLocation(recipeId);
                return;
            case EconomyCraftResult.NotFound:
                await ErrorAsync(response, 404, "This recipe does not exist.", ct); return;
            case EconomyCraftResult.Duplicate:
                await ErrorAsync(response, 409, "This form was already submitted.", ct); return;
            case EconomyCraftResult.InsufficientInputs:
                await ErrorAsync(response, 409, "Not enough components, currency, or items for this recipe.", ct); return;
            case EconomyCraftResult.Unavailable:
                await ErrorAsync(response, 409, "This recipe is currently unavailable.", ct); return;
            default:
                await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return;
        }
    }
}
