using System.Text.Json;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

public static class UnityNewsAction
{
    public const string CacheControl = "no-store, no-cache, must-revalidate";
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    // The captured request carries no query and the response sets no cookie, so the
    // body depends only on the clock: query strings and cookies are ignored.
    public static Task WriteAsync(HttpResponse response, TimeProvider clock, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.Headers["X-Custom-Header"] = "DarkOrbitUnity";
        response.Headers.Expires = "Thu, 19 Nov 1981 08:52:00 GMT";
        response.Headers.CacheControl = CacheControl;
        response.Headers.Pragma = "no-cache";
        response.Headers.ContentSecurityPolicy = FrameAncestorsPolicy;
        response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";

        var body = JsonSerializer.Serialize(CapturedNewsCatalog.News(clock.GetUtcNow()));
        return response.WriteAsync(body, ct);
    }
}
