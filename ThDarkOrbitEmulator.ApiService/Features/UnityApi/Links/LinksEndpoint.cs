using System.Text.Json;
using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links;

// DNS: gbl3.darkorbit.com | GET /unityApi/links.php | flow ids: 33e6fd0c-1df9-47b6-b884-e2f817d55b7f
public sealed class LinksEndpoint(ILogger<LinksEndpoint> logger) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/links.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("Unity links semantic response.");

        AppendUnityHeaders();

        var body = JsonSerializer.Serialize(new
        {
            imprint = "Unknown",
            credits = "",
            forum = "Unknown",
            UnknownHelpDeskUrl = "Unknown",
            UnknownTermsUrl = "Unknown"
        });

        await HttpContext.Response.WriteAsync(body, ct);
    }

    private void AppendUnityHeaders()
    {
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.ContentType = "application/json";
        HttpContext.Response.Headers.Append("X-Custom-Header", "DarkOrbitUnity");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }
}
