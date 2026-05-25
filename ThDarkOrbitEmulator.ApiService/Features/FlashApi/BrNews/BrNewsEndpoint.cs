using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews;

// DNS: gbl3.darkorbit.com | GET /flashAPI/brNews.php | flow ids: c0bc044d-98ff-4108-97ad-a360660eab5a
public sealed class BrNewsEndpoint(ILogger<BrNewsEndpoint> logger) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/brNews.php");
        RoutePrefixOverride("flashAPI");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Flash brNews semantic response.");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.ContentType = "text/xml; charset=UTF-8";
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        await HttpContext.Response.WriteAsync("<root><!-- UnknownFlashNews --></root>", ct);
    }
}
