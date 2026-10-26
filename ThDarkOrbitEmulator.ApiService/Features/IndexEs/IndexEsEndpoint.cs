using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexEs;

// DNS: gbl3.darkorbit.com | GET /index.es | flow ids: 0ff970ae-5ab2-405b-b2c7-336e4ba15efb [new]
public sealed class IndexEsEndpoint(ILogger<IndexEsEndpoint> logger, IGameAuthSessionStore sessions) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/index.es");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (LocalLauncherDownload.Targets(HttpContext.Request.Query))
        {
            // Static client URL only; no official response or installer was captured. The page reads
            // an existing dosid for its sign-in card but never creates one.
            HttpContext.Request.Cookies.TryGetValue("dosid", out var launcherDosid);
            await LocalLauncherDownload.WriteAsync(HttpContext.Response,
                await LocalLauncherDownload.ResolveAsync(HttpContext.Request.Query, launcherDosid, sessions, ct), ct);
            return;
        }

        var dosid = HttpContext.Request.Cookies.TryGetValue("dosid", out var existingDosid) && !string.IsNullOrWhiteSpace(existingDosid)
            ? existingDosid
            : Guid.NewGuid().ToString("N");

        logger.LogInformation("index.es mock matched wildcard response.");

        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Response.ContentType = "text/html; charset=UTF-8";
        HttpContext.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=none; secure; HttpOnly");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Set-Cookie", "aid=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "aip=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "aig=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "acr=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT; Max-Age=0; path=/; samesite=none; secure");
        HttpContext.Response.Headers.Append("Set-Cookie", "BP_DO_tracking_viewToReg_view=1; expires=Sun, 24-May-2026 21:59:59 GMT; Max-Age=10823");
        HttpContext.Response.Headers.Append("Vary", "Accept-Encoding");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Content-Language", "es");
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        var body = EmulatorRedirectHelper.RewriteLoginPageHtml(HttpContext.Request, FlowCaptureLoader.LoadResponseBody("0ff970ae-5ab2-405b-b2c7-336e4ba15efb"));
        await HttpContext.Response.WriteAsync(body, ct);
    }
}

