using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalTechFactoryAction
{
    // Emulator-defined form lifetime and limits; no official Tech Factory form was captured.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 128;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "detail", "filter", "started", "claimed", "q"];
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 40;

    // Menu-123: successful actions redirect with the recipe they touched; the notice text comes from saved state.
    public static string RedirectTarget(string parameter, string? recipeId) =>
        recipeId is not null && ValidRecipeId(recipeId)
            ? $"/indexInternal.es?action=internalTechFactory&{parameter}={recipeId}"
            : "/indexInternal.es?action=internalTechFactory";

    public static string CreateFormKey(string dosid, string action, string target, DateTimeOffset? issued = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var seconds = (issued ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        return $"{nonce}.{seconds}.{Sign(dosid, action, target, nonce, seconds)}";
    }

    private static string Sign(string dosid, string action, string target, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"tech_factory:{action}:{target}:{nonce}:{issued}"))).ToLowerInvariant();

    // Returns the issue time of a correctly signed key, or null for malformed or tampered keys.
    private static DateTimeOffset? KeyIssued(string dosid, string action, string target, string key)
    {
        if (key.Length > MaxKeyLength) return null;
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _)
            || parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) || parts[1][0] == '0'
            || parts[2].Length != 64 || !long.TryParse(parts[1], out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]),
            Encoding.ASCII.GetBytes(Sign(dosid, action, target, parts[0], parts[1])))) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    // Recipe ids are 1-64 lowercase ASCII letters, digits, '_' or '-'.
    internal static bool ValidRecipeId(string value) =>
        value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    // Job ids are rendered as 32 lowercase hex digits ("N" format).
    internal static bool ValidJobId(string value) =>
        value.Length == 32 && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    // GET page accepts an optional recipe detail, filter and search plus the shared shell parameters.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query.Keys.Contains("action", StringComparer.Ordinal) && query["action"].Count == 1
        && query["action"].ToString() == "internalTechFactory"
        && query.All(pair => AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= 64
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key is not ("detail" or "started" or "claimed") || ValidRecipeId(pair.Value.ToString()))
            && (pair.Key != "filter" || pair.Value.ToString() is "all" or "affordable" or "shortfall"))
        && !(query.ContainsKey("started") && query.ContainsKey("claimed"))
        && (Search(query) is not { } search
            || search.Length is >= MinSearchLength and <= MaxSearchLength && !query.ContainsKey("detail"));

    // Menu-205: optional `q` searches recipe ids and names; an empty or blank box means no search.
    // Nonblank text is trimmed, must be 2-40 characters, and cannot be combined with `detail`.
    public static string? Search(IQueryCollection query) =>
        query.TryGetValue("q", out var value) && value.ToString().Trim() is { Length: > 0 } text ? text : null;

    // Action forms post to exactly one lowercase action parameter in its canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
        && query["action"].ToString() is "internalTechFactoryStart" or "internalTechFactoryClaim";

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Tech Factory error</title></head><body><h1>Tech Factory request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalTechFactory\">Return to Tech Factory</a></body></html>", ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalTechFactoryStore factory, CancellationToken ct, TimeProvider? clock = null)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        { await ErrorAsync(response, 405, "Tech Factory actions must be submitted with POST.", ct); return; }
        var action = request.Query["action"].ToString();
        if (!ValidActionQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid Tech Factory action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using Tech Factory.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit a Tech Factory form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Tech Factory form could not be read.", ct); return; }
        var start = action == "internalTechFactoryStart";
        if (!LocalHangarError.HasExactly(form, start ? ["recipe", "key"] : ["job", "key"]))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var target = form[start ? "recipe" : "job"].ToString();
        if (start ? !ValidRecipeId(target) : !ValidJobId(target))
        { await ErrorAsync(response, 400, start ? "Invalid recipe ID." : "Invalid job ID.", ct); return; }
        var key = form["key"].ToString();
        var issued = KeyIssued(session.Dosid, start ? "start" : "claim", target, key);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (issued is null || issued > now + ClockSkew)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return; }
        if (now - issued > FormLifetime)
        { await ErrorAsync(response, 409, "This form has expired. Reload Tech Factory and try again.", ct); return; }
        var recipe = start ? (await factory.GetRecipesAsync(ct)).SingleOrDefault(r => r.Id == target) : null;
        if (start && recipe is null)
        { await ErrorAsync(response, 404, "This recipe does not exist.", ct); return; }
        // Pre-check the current state for a specific message; the store still enforces every rule atomically.
        var state = await factory.GetAsync(session.AccountId, ct);
        var precondition = start
            ? state.Job is not null ? "The production slot is busy. Claim the current job first."
                : state.Components < recipe!.ComponentCost
                    ? $"Not enough components: {recipe.ComponentCost} needed, {state.Components} available."
                    : null
            : state.Job is null || state.Job.Id.ToString("N") != target ? "There is no matching production job to claim."
                : state.Job.ReadyAt > now ? $"Production is not ready until {state.Job.ReadyAt:yyyy-MM-dd HH:mm} UTC."
                : state.ClaimOutputFull ? "The output resource wallet is full. Spend resources before claiming this job."
                : null;
        if (precondition is not null)
        { await ErrorAsync(response, 409, precondition, ct); return; }
        var result = start
            ? await factory.StartFormAsync(session.AccountId, target, $"tech:start:{key}", ct)
            : await factory.ClaimFormAsync(session.AccountId, Guid.ParseExact(target, "N"), $"tech:claim:{key}", ct);
        if (result != TechFactoryChange.Applied)
        {
            await ErrorAsync(response, result == TechFactoryChange.Invalid ? 400 : 409, result switch
            {
                TechFactoryChange.InsufficientComponents => "Not enough components.",
                TechFactoryChange.Invalid => "The form is invalid or has been changed.",
                _ => "This form was already submitted or the production state has changed."
            }, ct);
            return;
        }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = start ? RedirectTarget("started", target) : RedirectTarget("claimed", state.Job!.RecipeId);
    }
}
