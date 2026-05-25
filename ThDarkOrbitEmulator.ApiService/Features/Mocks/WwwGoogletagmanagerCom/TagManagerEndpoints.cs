using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.WwwGoogletagmanagerCom;

// DNS: www.googletagmanager.com | GET /gtm.js | flow ids: 8338bf5c-bafa-4df3-917e-7014183e3a31, 31062f0f-31bf-439e-9ebf-d764e3424c74, ea9b3426-833b-486b-8044-936b112e9f3d, 2c8cbba0-cd85-4dfb-b800-c18ce33a5ab1, 7ab4e684-d37f-4e35-9ed9-53b19815b90d, 407587e6-de1f-4ceb-ab0b-97d602d81f31, 46b19cfb-f111-40c7-a82f-3801cc5b9abe, 3dbb6230-e2a6-46ed-ab49-86b4e2d4a218, a4b4173b-b23e-4448-a8d4-1ed908d360b4, 1b180516-9e67-4a45-a098-747497559476
public sealed class GtmEndpoint(ILogger<GtmEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/gtm.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:www.googletagmanager.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("GTM mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock gtm.js */", "application/javascript; charset=UTF-8", ct);
    }
}

// DNS: www.googletagmanager.com | GET /gtag/js | flow ids: 71500d7d-49fc-4351-b75a-bc958081cd86, cda444aa-a58c-4945-8cd0-985ed9f2c648, 07ec5b18-394a-4334-a9a9-c44ab6c5ce87
public sealed class GtagEndpoint(ILogger<GtagEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/gtag/js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:www.googletagmanager.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("gtag mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock gtag.js */", "application/javascript; charset=UTF-8", ct);
    }
}

// DNS: www.googletagmanager.com | GET /ns.html | referenced by the captured home page markup
public sealed class NsHtmlEndpoint(ILogger<NsHtmlEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/ns.html");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:www.googletagmanager.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("gtm ns.html mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "<!doctype html><html><body></body></html>", "text/html; charset=UTF-8", ct);
    }
}
