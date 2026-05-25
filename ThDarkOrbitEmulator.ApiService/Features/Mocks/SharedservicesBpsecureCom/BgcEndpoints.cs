using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.SharedservicesBpsecureCom;

// DNS: sharedservices.bpsecure.com | GET /bgc/css|js|img/* | flow ids: 38ae5789-f8dd-4a9e-89c5-59fce8d4b921, 9bf52510-7577-4deb-b26f-75d775ad8c15, 65999795-25dd-45a5-92b7-46db5b96e927, aabce54a-e3f2-4378-a5fa-d0025938e922, ca838a09-9e99-4dc3-84b0-3714cd4e43b5, c73bdf08-69a0-4199-b791-5b5ff213f8f2
public sealed class BgcCssEndpoint(ILogger<BgcCssEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/bgc/css/{**catch-all}");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:sharedservices.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("sharedservices CSS mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, string.Empty, "text/css; charset=UTF-8", ct);
    }
}

public sealed class BgcJsEndpoint(ILogger<BgcJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/bgc/js/{**catch-all}");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:sharedservices.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("sharedservices JS mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock sharedservices js */", "application/javascript; charset=UTF-8", ct);
    }
}

public sealed class BgcImgEndpoint(ILogger<BgcImgEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/bgc/img/{**catch-all}");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:sharedservices.bpsecure.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("sharedservices image mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteBytesAsync(HttpContext.Response, MockResponseHelpers.TinyPngBytes, "image/png", ct);
    }
}
