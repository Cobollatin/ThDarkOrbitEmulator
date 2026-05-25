using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.JsHcaptchaCom;

// DNS: js.hcaptcha.com | GET /1/api.js | flow ids: 3159c9c9-fe6e-43a6-bf4b-928af0987eab
public sealed class ApiJsEndpoint(ILogger<ApiJsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/1/api.js");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:js.hcaptcha.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("hCaptcha api.js mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteTextAsync(HttpContext.Response, "/* mock hcaptcha api.js */", "application/javascript; charset=UTF-8", ct);
    }
}
