using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.BatBingCom;

// DNS: bat.bing.com | GET /bat.js | flow ids: 87e64a9f-5e7d-41d5-be78-a60ec3764f7a
public sealed class BatJsEndpoint(ILogger<BatJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/bat.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:bat.bing.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("bat.js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock bat.js */", "application/javascript; charset=UTF-8", ct);
    }
}

// DNS: bat.bing.com | GET /p/action/97130958.js | flow ids: f74ea364-9bfd-4008-8b55-f8c8f3c2ba4b
public sealed class BatActionJsEndpoint(ILogger<BatActionJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/p/action/97130958.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:bat.bing.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("bat action js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock action js */", "application/javascript; charset=UTF-8", ct);
    }
}
