using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.WwwDarkorbitCom;

// GET / serves the public login page; the game-host internalPaymentProxy action
// is a separate, session-bound local destination named by captured news XML.
public sealed class HomeEndpoint(ILogger<HomeEndpoint> logger,
    IGameAuthSessionStore gameAuthSessionStore,
    LocalHangarEquipmentStore equipmentStore,
    EconomyStore economyStore) : EndpointWithoutRequest
{
    private static readonly string HomeHtml = MockResponseHelpers.LoadCapturedBody("_reversing\\Requests\\home_html_raw.log");
    private const string FallbackHtml = "<!doctype html><html><head><title>Space Emulator Portal | Local Login (Placeholder)</title></head><body></body></html>";

    public static string GetBody(HttpRequest request, string? capturedHtml)
        => string.IsNullOrWhiteSpace(capturedHtml)
            ? FallbackHtml
            : EmulatorRedirectHelper.RewriteLoginPageHtml(request, capturedHtml);

    public override void Configure()
    {
        Get("/");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:www.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (LocalPaymentProxyResponse.IsRequest(HttpContext.Request))
        {
            var paymentDosid = HttpContext.Request.Cookies["dosid"];
            var session = string.IsNullOrWhiteSpace(paymentDosid)
                ? null : await gameAuthSessionStore.TryGetByDosidAsync(paymentDosid, ct);
            await LocalPaymentProxyResponse.WriteAsync(HttpContext.Response, session is not null,
                LocalCompanySelection.IsValid(session?.Company),
                LocalCompanySelection.IsValid(session?.Company)
                    ? await equipmentStore.GetAsync(session!.AccountId, ct) : null, ct,
                session is not null && LocalCompanySelection.IsValid(session.Company)
                    ? await economyStore.RewardOffersAsync("payment", ct) : null,
                session is not null && LocalCompanySelection.IsValid(session.Company)
                    ? await economyStore.BalancesAsync(session.AccountId, ct) : null);
            return;
        }

        logger.LogInformation("Home HTML mock matched wildcard response.");

        var dosid = HttpContext.Request.Cookies.TryGetValue("dosid", out var existingDosid) && !string.IsNullOrWhiteSpace(existingDosid)
            ? existingDosid
            : Guid.NewGuid().ToString("N");

        HttpContext.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=none; secure; HttpOnly");
        HttpContext.Response.Headers.Append("Set-Cookie", "aid=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "aip=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "aig=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "acr=22; expires=Wed, 15-Apr-2026 00:12:29 GMT; Max-Age=2592000; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", "frame-ancestors https://*.darkorbit.com/ https://gameplanet.onet.pl/ https://www.gry.pl/ https://www.jetztspielen.de/ https://www.spielen.com/ https://www.jeux.fr/ https://www.jeu.fr/ https://www.games.co.uk/ https://www.spelletjes.nl/ https://www.spel.nl/ https://www.juegos.com/ https://www.gioco.it/ https://www.spela.se/ https://www.ourgames.ru/ https://www.gamesgames.com/ https://www.agame.com/ https://centralagier.wp.pl/ https://www.jetztspielen.ws/ https://www.1001spiele.de/ https://www.gierkionline.pl/ https://www.grajteraz.pl/ https://www.1001giochi.it/ https://www.giochixl.it/ https://www.1001hry.cz/ https://juegosjuegos.ws/ https://www.isladejuegos.com/ https://www.elkspel.nl/ https://www.spelo.se/ https://www.1001games.com/ https://www.speltuin.nl/ https://www.1001pelit.com/ https://www.1001jeux.fr/ https://www.1001games.fr/ https://www.1001spiele.at/ https://www.mmozone.com/ https://www.mmostation.com/ https://www.mmogratis.es/ https://www.gratismmo.de/ https://www.mmorpggratuits.com/ https://www.mmoitalia.it/ https://www.mmoportugal.com/ https://www.funnygames.nl/ https://www.clickjogos.com.br/ https://spele.nl/ https://www.dobregry.pl/ https://fotka.com/ https://www.1001games.co.uk/ https://www.1001jocuri.ro/ https://www.1001jogos.com.br/ https://www.1001jogos.pt/ https://www.igrixl.ru/ https://www.jatekokxl.hu/ https://www.juegosjuegos.ws/ https://www.paixnidiaxl.gr/ https://www.spillespill.no/ https://www.spilxl.dk/ https://www.jeux-gratuits.com/ https://www.minijuegos.com/ https://kizi.com/ https://www.browsergames.de/ https://www.isladejuegos.com/ https://www.juegosdenavegador.com/ https://www.jeuxparnavigateur.net/ https://www.jogosbrowser.com/ https://www.freemmorpglist.com/ https://www.puzzlepuzzles.de/ https://www.mmogratis.com/ https://www.sat1spiele.de/ https://www.flashgames.it/ https://www.prosiebengames.de/ https://www.oyunskor.com/ https://www.spielkarussell.de/ http://www.oyunkolu.com/ https://www.brincar.pt/ https://www.spelle.nl/ https://www.speeleiland.nl/ https://www.kongregate.com/ https://www.spacemmorpg.com/ https://*.y8.com https://darkorbit.rtl.de/;");
        HttpContext.Response.Headers.Append("Content-Language", "es");
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, GetBody(HttpContext.Request, HomeHtml), "text/html; charset=UTF-8", ct);
    }
}

