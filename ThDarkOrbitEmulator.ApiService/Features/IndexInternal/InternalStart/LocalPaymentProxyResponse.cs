using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// The action is named by captured news XML, but its official request and response
// have not been observed. Keep the local destination session-bound.
public static class LocalPaymentProxyResponse
{
    public static bool IsRequest(HttpRequest request) =>
        request.Path == "/"
        && string.Equals(request.Query["action"], "internalPaymentProxy", StringComparison.OrdinalIgnoreCase)
        && (ClientFacingGameHost.IsRedirectedGameHost(request)
            || Regex.IsMatch(request.Host.Host, @"\Agbl[1-9][0-9]*\.darkorbit\.com\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static async Task WriteAsync(HttpResponse response, bool hasSession, bool hasCompany,
        LocalEquipmentState? state, CancellationToken ct, IReadOnlyList<EconomyRewardOffer>? offers = null,
        IReadOnlyList<EconomyBalance>? balances = null)
    {
        if (!hasSession)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            await response.CompleteAsync();
            return;
        }

        if (!hasCompany)
        {
            ClientRedirectResponse.Apply(response, "/indexInternal.es?action=internalCompanyChoose");
            await response.CompleteAsync();
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=UTF-8";
        var page = LocalPayment.Page(state!, proxy: true, offers);
        var html = balances is null ? page : LocalEconomyHeader.Add(page, balances);
        await response.WriteAsync(IndexInternalStartEndpoint.AdaptHtmlForClient(response.HttpContext.Request, html), ct);
        await response.CompleteAsync();
    }
}
