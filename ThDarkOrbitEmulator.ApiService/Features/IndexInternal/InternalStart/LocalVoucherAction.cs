using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static class LocalVoucherAction
{
    private const int MaxPageValueLength = 64;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang", "filter", "voucher", "redeemed", "q"];

    // The page accepts canonical navigation and view parameters once each.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalVouchers"
        && query.All(pair => AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxPageValueLength
            && !pair.Value.ToString().Any(char.IsControl))
            // Menu-114: the redeem confirmation belongs to the list, not to a detail view.
            && !(query.ContainsKey("redeemed") && query.ContainsKey("voucher"));

    // Menu-114: a successful redeem names the canonical code so the list can confirm it.
    // Admin ids that the page would reject fall back to the plain list.
    public static string RedeemedLocation(string? id) =>
        LocalVouchers.IsWellFormed(id)
            ? $"/indexInternal.es?action=internalVouchers&redeemed={Uri.EscapeDataString(id!)}"
            : "/indexInternal.es?action=internalVouchers";

    // The redeem query carries at most one action, spelled exactly as the page form posts it.
    public static bool ValidRedeemQuery(IQueryCollection query) =>
        query.Count == 0 || (query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1
            && string.Equals(query["action"].ToString(), "internalVoucherRedeem", StringComparison.Ordinal));

    internal static async Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Voucher error</title></head><body><h1>Voucher request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p><a href=\"/indexInternal.es?action=internalVouchers\">Return to Vouchers</a></body></html>", ct);
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
            await ErrorAsync(response, 405, "Voucher codes must be submitted with POST.", ct);
            return;
        }
        if (!ValidRedeemQuery(request.Query))
        { await ErrorAsync(response, 400, "Invalid voucher action.", ct); return; }
        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        { await ErrorAsync(response, 401, "Your session is missing or expired. Sign in again.", ct); return; }
        if (CompanyCatalog.Find(session.Company) is null)
        { await ErrorAsync(response, 403, "Choose a valid company before redeeming vouchers.", ct); return; }
        if (!request.HasFormContentType)
        { await ErrorAsync(response, 400, "Submit the voucher form.", ct); return; }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        { await ErrorAsync(response, 400, "The voucher form could not be read.", ct); return; }
        if (!LocalHangarError.HasExactly(form, "code"))
        { await ErrorAsync(response, 400, "The form has missing, repeated, or unexpected fields.", ct); return; }
        var code = form["code"].ToString().Trim();
        if (!LocalVouchers.IsWellFormed(code))
        { await ErrorAsync(response, 400, "Voucher codes use 1-32 letters, digits, or hyphens.", ct); return; }
        var (result, redeemedId) = await equipment.RedeemVoucherWithIdAsync(session.AccountId, code, ct);
        switch (result)
        {
            case VoucherRedeemResult.Applied:
                response.StatusCode = StatusCodes.Status303SeeOther;
                response.Headers.Location = RedeemedLocation(redeemedId);
                return;
            case VoucherRedeemResult.NotFound:
                await ErrorAsync(response, 404, "This voucher code does not exist.", ct); return;
            case VoucherRedeemResult.AlreadyRedeemed:
                await ErrorAsync(response, 409, "This voucher was already redeemed on this account.", ct); return;
            case VoucherRedeemResult.BalanceLimit:
                await ErrorAsync(response, 409, "This voucher would exceed the local credit limit.", ct); return;
            case VoucherRedeemResult.Unavailable:
                await ErrorAsync(response, 410, "This voucher is no longer available.", ct); return;
            default:
                await ErrorAsync(response, 400, "This voucher cannot be redeemed.", ct); return;
        }
    }
}
