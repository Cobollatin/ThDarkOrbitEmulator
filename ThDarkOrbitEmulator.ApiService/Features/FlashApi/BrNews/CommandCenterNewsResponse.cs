namespace ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews;

public static class CommandCenterNewsResponse
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public static Task WriteAsync(HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=UTF-8";
        response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        response.Headers.Append("Pragma", "no-cache");
        response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        var host = ThDarkOrbitEmulator.ApiService.Features.Mocks.Common.ClientFacingGameHost.Resolve(response.HttpContext.Request);
        var body = ClientStubResponses.BreakingNews.Replace(
            "/indexInternal.es?action=internalCommandCenter",
            $"{host}/indexInternal.es?action=internalCommandCenter", StringComparison.Ordinal);
        return response.WriteAsync(body, ct);
    }
}
