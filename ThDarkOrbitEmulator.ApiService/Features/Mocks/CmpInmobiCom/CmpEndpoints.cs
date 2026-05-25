using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.CmpInmobiCom;

// DNS: cmp.inmobi.com | GET /choice/qB5-sBEDaawtz/darkorbit.com/choice.js | flow ids: ea69ffee-ecca-43ac-811e-15f6d17b8f6c
public sealed class ChoiceJsEndpoint(ILogger<ChoiceJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/choice/qB5-sBEDaawtz/darkorbit.com/choice.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:cmp.inmobi.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("cmp choice.js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock choice.js */", "application/javascript; charset=UTF-8", ct);
    }
}

// DNS: cmp.inmobi.com | GET /tcfv2/cmp2.js | flow ids: 1bfd2242-a27a-40d0-8203-d40b3e029f17
public sealed class Cmp2JsEndpoint(ILogger<Cmp2JsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/tcfv2/cmp2.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:cmp.inmobi.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("cmp2.js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock cmp2.js */", "application/javascript; charset=UTF-8", ct);
    }
}

// DNS: cmp.inmobi.com | GET /geoip | flow ids: 4ae178d6-fa81-4187-9b89-cc27a25830f0, b3cfafc6-7729-4a88-8268-afebec94c878
public sealed class GeoIpEndpoint(ILogger<GeoIpEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/geoip");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:cmp.inmobi.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("cmp geoip mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "{}", "application/json; charset=UTF-8", ct);
    }
}

// DNS: cmp.inmobi.com | GET /GVL-v3/vendor-list-trimmed-v1.json | flow ids: 9e963ee9-5f1d-4b1f-9552-750c50498b7b
public sealed class VendorListEndpoint(ILogger<VendorListEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/GVL-v3/vendor-list-trimmed-v1.json");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:cmp.inmobi.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("cmp vendor list mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "{\"vendors\":[]}", "application/json; charset=UTF-8", ct);
    }
}
