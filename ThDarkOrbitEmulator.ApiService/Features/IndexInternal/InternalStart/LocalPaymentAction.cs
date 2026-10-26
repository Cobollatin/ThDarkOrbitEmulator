using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalPaymentAction
{
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", DetailKey, ClaimedKey, FilterKey, SearchKey];
    public const string DetailKey = "detail";
    public const string ClaimedKey = "claimed";
    public const string FilterKey = "filter";
    public const string SearchKey = "q";

    // The page accepts the shell's navigation parameters once each; anything else is rejected.
    // The optional package detail and claimed keys carry a well-formed package id; the optional filter
    // has 1-16 lowercase letters (Menu-146). Menu-199: every key is exact lowercase and the action value
    // is the canonical `internalPayment`, like Dock, Map overview and Events.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query.Keys.Contains("action", StringComparer.Ordinal) && query["action"].Count == 1
        && query["action"].ToString() == "internalPayment"
        && query.All(pair => AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl)
            && (pair.Key is not (DetailKey or ClaimedKey) || LocalPayment.IsWellFormed(pair.Value.ToString()))
            && (pair.Key != FilterKey || LocalPayment.IsWellFormedFilter(pair.Value.ToString()))
            && (pair.Key != SearchKey || LocalPayment.IsWellFormedSearch(pair.Value.ToString())));

    // Call only after ValidPageQuery has accepted the query.
    public static string? DetailId(IQueryCollection query) =>
        query.TryGetValue(DetailKey, out var value) ? value.ToString() : null;

    // Call only after ValidPageQuery has accepted the query.
    public static string? ClaimedId(IQueryCollection query) =>
        query.TryGetValue(ClaimedKey, out var value) ? value.ToString() : null;

    // Call only after ValidPageQuery has accepted the query; a missing filter means "all".
    public static string Filter(IQueryCollection query) =>
        query.TryGetValue(FilterKey, out var value) ? value.ToString() : "all";

    public static string? Search(IQueryCollection query) =>
        query.TryGetValue(SearchKey, out var value) ? value.ToString() : null;

    // Where a successful claim lands; the page confirms the grant only if the account really claimed it.
    public static string ClaimedLocation(string packageId) =>
        $"/indexInternal.es?action=internalPayment&{ClaimedKey}={Uri.EscapeDataString(packageId)}";

    // The claim query carries at most one action, spelled exactly as the page form posts it.
    public static bool ValidClaimQuery(IQueryCollection query) =>
        query.Count == 0 || (query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
            && string.Equals(query["action"].ToString(), "internalPaymentClaim", StringComparison.Ordinal));

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Payment error</title></head><body><h1>Payment request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalPayment\">Return to Payment</a></body></html>", ct);
    }

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await ErrorAsync(response, 405, "Package claims must be submitted with POST.", ct);
            return;
        }
        if (!ValidClaimQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid payment action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before claiming packages.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit the package form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The package form could not be read.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "package"))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var packageId = form["package"].ToString();
        if (!LocalPayment.IsWellFormed(packageId))
        { await ErrorAsync(response, 400, "Package ids use 1-32 letters, digits, hyphens, or underscores.", ct); return; }
        switch (await equipment.ClaimPaymentPackageResultAsync(session.AccountId, packageId, ct))
        {
            case PaymentClaimResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = ClaimedLocation(packageId);
                return;
            case PaymentClaimResult.NotFound:
                await ErrorAsync(response, 404, "This package does not exist.", ct); return;
            case PaymentClaimResult.AlreadyClaimed:
                await ErrorAsync(response, 409, "This package was already claimed on this account.", ct); return;
            case PaymentClaimResult.BalanceLimit:
                await ErrorAsync(response, 409, "This package would exceed the local credit limit.", ct); return;
            case PaymentClaimResult.Unavailable:
                await ErrorAsync(response, 410, "This package is no longer available.", ct); return;
            default:
                await ErrorAsync(response, 400, "This package cannot be claimed.", ct); return;
        }
    }
}
