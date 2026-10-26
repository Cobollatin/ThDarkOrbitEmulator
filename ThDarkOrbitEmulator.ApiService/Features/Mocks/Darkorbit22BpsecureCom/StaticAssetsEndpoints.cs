using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Darkorbit22BpsecureCom;

// DNS: darkorbit-22.bpsecure.com | GET /css/... | flow ids: 13c648c4-1b28-4993-b278-bce31a91a638, 5f42de02-3db8-4c09-99ee-172b9622bbd0, da6d699b-6bd9-4ae5-8257-7598c170137e
public sealed class CssEndpoint(ILogger<CssEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/css/{**catch-all}");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:darkorbit-22.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("darkorbit-22 CSS mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, string.Empty, "text/css; charset=UTF-8", ct);
    }
}

// DNS: darkorbit-22.bpsecure.com | GET /js/... | flow ids: (referenced by captured home HTML)
public sealed class JsEndpoint(ILogger<JsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/js/{**catch-all}");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:darkorbit-22.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("darkorbit-22 JS mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock darkorbit-22 js */", "application/javascript; charset=UTF-8", ct);
    }
}

// DNS: darkorbit-22.bpsecure.com | GET /do_img/... | flow ids: 0ee8bfef-69b3-42fb-988c-938a5ebb5de7, 9dae331e-2bf5-400d-95d1-26d5e0b013f6
public sealed class DoImageEndpoint(ILogger<DoImageEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/do_img/{**catch-all}");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:darkorbit-22.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await CommandCenterVisualAssets.TryWriteArtworkAsync(HttpContext, ct))
            return;
        var icon = HttpContext.Request.Path.Value switch
        {
            LocalShipIcons.PusatPath => LocalShipIcons.Pusat,
            LocalShipIcons.PhoenixPath => LocalShipIcons.Phoenix,
            _ => null
        };
        if (icon is not null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.Headers.CacheControl = "public, max-age=3600";
            await MockResponseHelpers.WriteBytesAsync(HttpContext.Response, icon, "image/png", ct);
            return;
        }
        logger.LogInformation("darkorbit-22 image mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteBytesAsync(HttpContext.Response, MockResponseHelpers.TinyPngBytes, "image/png", ct);
    }
}

// DNS: darkorbit-22.bpsecure.com | GET /favicon.ico | referenced by captured home HTML
public sealed class FaviconEndpoint(ILogger<FaviconEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/favicon.ico");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:darkorbit-22.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("darkorbit-22 favicon mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteBytesAsync(HttpContext.Response, MockResponseHelpers.TinyPngBytes, "image/png", ct);
    }
}
