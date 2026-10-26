using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalAuctionAction
{
    private const int MaxPageValueLength = 64;
    // Emulator-defined form lifetime; stale Auction pages must be reloaded before bidding or claiming.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 160;

    private enum KeyCheck { Valid, Invalid, Expired }

    // The key binds a form to one session, one operation (bid or claim) and one lot. Replays are harmless:
    // a repeated bid fails the 10% raise rule and a repeated claim is AlreadyClaimed.
    public static string CreateFormKey(string dosid, string operation, string lotId, DateTimeOffset? issuedAt = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var issued = (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{nonce}.{issued}.{Sign(dosid, operation, lotId, nonce, issued)}";
    }

    private static string Sign(string dosid, string operation, string lotId, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"auction:{operation}:{lotId}:{nonce}:{issued}"))).ToLowerInvariant();

    private static KeyCheck CheckKey(string dosid, string operation, string lotId, string key, DateTimeOffset now)
    {
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _) ||
            parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) ||
            parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit)) return KeyCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant()),
                Encoding.ASCII.GetBytes(Sign(dosid, operation, lotId, parts[0], parts[1])))) return KeyCheck.Invalid;
        var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1], CultureInfo.InvariantCulture));
        if (issued > now + ClockSkew) return KeyCheck.Invalid;
        return now - issued > FormLifetime ? KeyCheck.Expired : KeyCheck.Valid;
    }
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang"];

    // The page accepts the shell's navigation parameters once each, plus optional exact-case `lot` detail and
    // `placed`/`claimed` notice ids (Menu-124; the two notices are mutually exclusive) and a lowercase status
    // `filter` (Menu-144; an unknown well-formed filter is 404 after the session check); anything else is rejected.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalAuction"
        && query.All(pair => pair.Value.Count == 1
            && (pair.Key is "q" ? LocalAuction.IsSearchShape(pair.Value.ToString())
                : pair.Key is "filter" ? LocalAuction.IsFilterShape(pair.Value.ToString())
                : pair.Key is "lot" or "placed" or "claimed"
                ? LocalAuction.IsLotIdShape(pair.Value.ToString())
                : AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
                    && pair.Value.ToString().Length <= MaxPageValueLength
                    && !pair.Value.ToString().Any(char.IsControl)))
        && !(query.ContainsKey("placed") && query.ContainsKey("claimed"))
        && !(query.ContainsKey("lot") && !string.IsNullOrWhiteSpace(query["q"].ToString()));

    public static string? Search(IQueryCollection query) =>
        query.TryGetValue("q", out var value) && !string.IsNullOrWhiteSpace(value.ToString())
            ? value.ToString().Trim() : null;

    // Menu-124: successful actions redirect with the lot they touched; the notice text comes from saved state.
    public static string RedirectTarget(bool bid, string lotId) =>
        $"/indexInternal.es?action=internalAuction&{(bid ? "placed" : "claimed")}={Uri.EscapeDataString(lotId)}";

    public static string? NoticeLot(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var value) ? value.ToString() : null;

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, EconomyStore economy, TimeProvider clock, CancellationToken ct)
    {
        if (await ValidateAsync(context, sessions, clock, ct) is not { } request) return;
        await equipment.GetAsync(request.Session.AccountId, ct);
        var result = request.Bid is { } amount
            ? await economy.BidResultAsync(request.Session.AccountId, request.LotId, amount, clock.GetUtcNow(), ct)
            : await economy.ClaimAuctionResultAsync(request.Session.AccountId, request.LotId, clock.GetUtcNow(), ct);
        await CompleteAsync(context.Response, result, RedirectTarget(request.Bid is not null, request.LotId), ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, TimeProvider clock, CancellationToken ct)
    {
        if (await ValidateAsync(context, sessions, clock, ct) is not { } request) return;
        var result = request.Bid is { } amount
            ? await equipment.BidResultAsync(request.Session.AccountId, request.LotId, amount, clock.GetUtcNow(), ct)
            : await equipment.ClaimAuctionResultAsync(request.Session.AccountId, request.LotId, clock.GetUtcNow(), ct);
        await CompleteAsync(context.Response, result, RedirectTarget(request.Bid is not null, request.LotId), ct);
    }

    private sealed record AuctionRequest(GameAuthSession Session, string LotId, long? Bid);

    private static async Task<AuctionRequest?> ValidateAsync(HttpContext context, IGameAuthSessionStore sessions,
        TimeProvider clock, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Auction actions must be submitted with POST.", ct);
            return null;
        }
        // The dispatcher routes on `action`; any other or repeated query key is tampering.
        if (request.Query.Any(pair => pair.Key != "action") || request.Query["action"].Count > 1)
        { await ErrorAsync(response, 400, "Invalid auction action.", ct); return null; }
        var isBid = request.Query["action"].ToString() == "internalAuctionBid";
        if (!isBid && request.Query["action"].ToString() != "internalAuctionClaim")
        { await ErrorAsync(response, 404, "Unknown auction action.", ct); return null; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return null; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before using the Auction.", ct); return null; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit an Auction form.", ct); return null; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The Auction form could not be read.", ct); return null; }
        if (isBid ? !LocalHangarError.HasExactly(form, "item", "amount", "key") : !LocalHangarError.HasExactly(form, "item", "key"))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return null; }
        var lotId = form["item"].ToString();
        if (!LocalAuction.IsLotIdShape(lotId))
        { await ErrorAsync(response, 400, "The lot value is invalid.", ct); return null; }
        long? amount = null;
        if (isBid)
        {
            if (!LocalAuction.TryParseBid(form["amount"].ToString(), out var bid))
            { await ErrorAsync(response, 400, "The bid must be a whole number of credits between 1 and 999,999,999.", ct); return null; }
            amount = bid;
        }
        var key = form["key"].ToString();
        var check = key.Length > MaxKeyLength ? KeyCheck.Invalid
            : CheckKey(session.Dosid, isBid ? "bid" : "claim", lotId, key, clock.GetUtcNow());
        if (check == KeyCheck.Invalid)
        { await ErrorAsync(response, 400, "The form is invalid or has been changed.", ct); return null; }
        if (check == KeyCheck.Expired)
        { await ErrorAsync(response, 409, "This form has expired. Reload the Auction and try again.", ct); return null; }
        return new AuctionRequest(session, lotId, amount);
    }

    private static async Task CompleteAsync(HttpResponse response, LocalAuctionResult result, string redirect, CancellationToken ct)
    {
        switch (result)
        {
            case LocalAuctionResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = redirect;
                return;
            case LocalAuctionResult.UnknownLot:
                await ErrorAsync(response, 404, "That lot is not part of the Auction.", ct); return;
            case LocalAuctionResult.Unavailable:
                await ErrorAsync(response, 409, "This lot is not available.", ct); return;
            case LocalAuctionResult.BidTooLow:
                await ErrorAsync(response, 409, "Your bid is below the minimum. Raise it by at least 10%.", ct); return;
            case LocalAuctionResult.InsufficientCredits:
                await ErrorAsync(response, 409, "You do not have enough local credits for this bid.", ct); return;
            case LocalAuctionResult.Ended:
                await ErrorAsync(response, 409, "This auction has ended. Claim the item instead.", ct); return;
            case LocalAuctionResult.AlreadyClaimed:
                await ErrorAsync(response, 409, "This lot was already claimed.", ct); return;
            case LocalAuctionResult.NoBid:
                await ErrorAsync(response, 409, "You have not bid on this lot.", ct); return;
            case LocalAuctionResult.StillRunning:
                await ErrorAsync(response, 409, "This auction is still running.", ct); return;
            case LocalAuctionResult.InventoryFull:
                await ErrorAsync(response, 409, "Your Hangar cannot hold more of this item.", ct); return;
            default:
                await ErrorAsync(response, 400, "The Auction request was rejected.", ct); return;
        }
    }

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Auction error</title></head>" +
            $"<body><main><h1>Auction request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            "<a href=\"/indexInternal.es?action=internalAuction\">Back to Auction</a></main></body></html>", ct);
    }
}
