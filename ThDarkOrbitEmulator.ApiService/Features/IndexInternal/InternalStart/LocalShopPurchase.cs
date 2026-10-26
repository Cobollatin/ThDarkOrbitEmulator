using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalShopPurchase
{
    // Emulator-defined form lifetime; stale Shop pages must be reloaded before buying.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public enum OrderCheck { Valid, Invalid, Expired }

    public static string CreateFormKey(string dosid, string itemId, DateTimeOffset? issuedAt = null, string currency = "credits")
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, itemId, currency, nonce, issued)}";
    }

    private static string Sign(string dosid, string itemId, string currency, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"shop:{itemId}:{currency}:{nonce}:{issued}"))).ToLowerInvariant();

    private static bool IsFieldText(string value) =>
        value.Length is >= 1 and <= 100 && !value.Any(char.IsControl) && value.Trim() == value;

    // Exactly 1-3 ASCII digits without a leading zero or sign, in 1..100.
    private static bool TryParseQuantity(string value, out int quantity)
    {
        quantity = 0;
        if (value.Length is < 1 or > 3 || value[0] == '0' || !value.All(char.IsAsciiDigit)) return false;
        quantity = int.Parse(value, CultureInfo.InvariantCulture);
        return quantity <= 100;
    }

    public static bool TryReadOrder(IFormCollection form, string dosid, out string itemId,
        out int quantity, out string currency, out string key) =>
        ReadOrder(form, dosid, DateTimeOffset.UtcNow, out itemId, out quantity, out currency, out key) == OrderCheck.Valid;

    public static OrderCheck ReadOrder(IFormCollection form, string dosid, DateTimeOffset now, out string itemId,
        out int quantity, out string currency, out string key)
    {
        itemId = currency = key = string.Empty;
        quantity = 0;
        if (form.Count != 4 || form.Keys.Any(name => name is not ("item" or "quantity" or "currency" or "idempotencyKey"))
            || form.Any(field => field.Value.Count != 1)) return OrderCheck.Invalid;
        itemId = form["item"].ToString();
        currency = form["currency"].ToString();
        key = form["idempotencyKey"].ToString();
        var parts = key.Split('.');
        if (!IsFieldText(itemId) || !IsFieldText(currency) || !TryParseQuantity(form["quantity"].ToString(), out quantity)
            || parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _)
            || parts[1].Length is < 1 or > 12 || parts[1][0] == '0' || !parts[1].All(char.IsAsciiDigit)
            || parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return OrderCheck.Invalid;
        var expected = Sign(dosid, itemId, currency, parts[0], parts[1]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]), Encoding.ASCII.GetBytes(expected)))
            return OrderCheck.Invalid;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedSeconds)
            || issuedSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return OrderCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(issuedSeconds);
        if (issued > now + ClockSkew) return OrderCheck.Invalid;
        return now - issued > FormLifetime ? OrderCheck.Expired : OrderCheck.Valid;
    }

    private const int MaxNavigationValueLength = 64;
    private static readonly string[] NavigationPageKeys = ["action", "from", "prc", "lang"];
    private static readonly string[] FilterPageKeys = ["category", "item"];

    // The Shop page accepts the shell's navigation parameters plus one category and item filter, once each.
    // Filters follow the purchase form's field rules; the 64-character navigation limit matches the other menus.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1
        && query.Keys.Contains("action", StringComparer.Ordinal)
        && query["action"].ToString() == "internalShop"
        && query.All(pair => pair.Value.Count == 1 && (
            NavigationPageKeys.Contains(pair.Key, StringComparer.Ordinal)
                ? pair.Value.ToString().Length <= MaxNavigationValueLength && !pair.Value.ToString().Any(char.IsControl)
                : FilterPageKeys.Contains(pair.Key, StringComparer.Ordinal) && IsFieldText(pair.Value.ToString())
                    || pair.Key == "q" && ValidSearch(pair.Value.ToString())));

    public static bool ValidSearch(string value) => value.Length is >= 2 and <= 40
        && value == value.Trim() && !value.Any(char.IsControl);

    internal static Task ErrorAsync(HttpResponse response, int status, string message) =>
        ErrorAsync(response, status, message, CancellationToken.None);

    public static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Shop error</title></head><body><h1>Shop request failed ({status})</h1><p>{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalShop\">Return to Shop</a></body></html>", ct);
    }
    // Retained for the cache-only contract tests; the routed endpoint uses the database overload.
    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore inventory, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (string.IsNullOrWhiteSpace(session.Company))
        { response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (!request.HasFormContentType)
        { response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var form = await request.ReadFormAsync(ct);
        var itemId = form["item"].ToString();
        if (!int.TryParse(form["quantity"], out var quantity)
            || !await inventory.PurchaseAsync(session.AccountId, itemId, quantity, ct))
        { response.StatusCode = StatusCodes.Status400BadRequest; return; }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = $"/indexInternal.es?action=internalShop&item={itemId}";
    }

    // Purchase forms post to exactly one "action" parameter, with the key in canonical casing.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1;

    public static Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore inventory, EconomyStore economy, CancellationToken ct) =>
        HandleAsync(context, sessions, inventory, economy, TimeProvider.System, ct);

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore inventory, EconomyStore economy, TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, StatusCodes.Status405MethodNotAllowed, "Shop purchases must be submitted with POST.");
            return;
        }
        if (!ValidActionQuery(request.Query) || request.Query["action"].ToString() != "internalShopBuy")
        {
            await ErrorAsync(response, StatusCodes.Status400BadRequest, "Invalid Shop action.");
            return;
        }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        {
            await ErrorAsync(response, StatusCodes.Status401Unauthorized, "Your session has expired. Sign in again.");
            return;
        }
        if (ThDarkOrbitEmulator.Identity.Application.CompanyCatalog.Find(session.Company) is null)
        {
            await ErrorAsync(response, StatusCodes.Status403Forbidden, "Choose a valid company before buying.");
            return;
        }
        if (!request.HasFormContentType)
        {
            await ErrorAsync(response, StatusCodes.Status400BadRequest, "Submit the Shop purchase form.");
            return;
        }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        {
            await ErrorAsync(response, StatusCodes.Status400BadRequest, "The Shop purchase form could not be read.");
            return;
        }
        var check = ReadOrder(form, session.Dosid, clock.GetUtcNow(), out var itemId, out var quantity, out var currency, out var key);
        if (check == OrderCheck.Invalid)
        {
            await ErrorAsync(response, StatusCodes.Status400BadRequest, "The purchase form is invalid or has been changed.");
            return;
        }
        if (check == OrderCheck.Expired)
        {
            await ErrorAsync(response, StatusCodes.Status409Conflict, "This purchase form has expired. Reload Shop and try again.");
            return;
        }
        if (!(await economy.ListingsAsync(ct)).Any(listing => listing.Id == itemId))
        {
            await ErrorAsync(response, StatusCodes.Status404NotFound, "This Shop item does not exist.");
            return;
        }
        await economy.ImportAsync(session.AccountId, await inventory.GetAsync(session.AccountId, ct), ct);
        var result = await economy.PurchaseAsync(session.AccountId, itemId, quantity, currency, key, ct);
        if (result != EconomyPurchaseResult.Applied)
        {
            var status = result is EconomyPurchaseResult.Unavailable or EconomyPurchaseResult.Duplicate
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            var message = result switch
            {
                EconomyPurchaseResult.Duplicate => "This purchase was already submitted.",
                EconomyPurchaseResult.Unavailable => "This listing is unavailable, sold out, or over your limit.",
                EconomyPurchaseResult.InsufficientFunds => "You do not have enough currency for this purchase.",
                _ => "The item, currency, or purchase key is invalid."
            };
            await ErrorAsync(response, status, message);
            return;
        }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = $"/indexInternal.es?action=internalShop&item={Uri.EscapeDataString(itemId)}";
    }
}
