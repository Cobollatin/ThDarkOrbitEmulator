using System.Text.Json;
using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

// DNS: gbl3.darkorbit.com | GET /unityApi/news.php | flow ids: b77d5c17-ed5a-4db8-abb6-b24338b7eba0
public sealed class NewsEndpoint(ILogger<NewsEndpoint> logger) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/news.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Unity news semantic response.");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.ContentType = "application/json";
        HttpContext.Response.Headers.Append("X-Custom-Header", "DarkOrbitUnity");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        await HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new { data = Array.Empty<object>() }), ct);
    }
}
