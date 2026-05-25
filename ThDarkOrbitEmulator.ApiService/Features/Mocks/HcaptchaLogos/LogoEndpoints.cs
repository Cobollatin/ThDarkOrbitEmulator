using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.HcaptchaLogos;

// DNS: c47f56fecf32.w.hcaptcha.com | 7dd0d2f1479d.w.hcaptcha.com | ac36ddf2442a.w.hcaptcha.com | GET /logo.png | flow ids: 756a62cd-a25a-46d8-bd08-49c0ca8266a2, 74a12246-ba1c-4c09-9bf5-a97d82946221, c74f6fcb-91f4-42d3-aa9f-d957176049f0
public sealed class C47LogoEndpoint(ILogger<C47LogoEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/logo.png");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:c47f56fecf32.w.hcaptcha.com", "DNS:7dd0d2f1479d.w.hcaptcha.com", "DNS:ac36ddf2442a.w.hcaptcha.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("hCaptcha logo mock matched wildcard response.");
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await MockResponseHelpers.WriteBytesAsync(HttpContext.Response, MockResponseHelpers.TinyPngBytes, "image/png", ct);
    }
}
