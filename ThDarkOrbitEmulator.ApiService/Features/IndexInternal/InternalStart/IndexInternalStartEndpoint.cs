using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// DNS: gbl3.darkorbit.com | GET /indexInternal.es | flow ids: 771b953a-11da-47a5-9b69-8736a73849f0, 5ac0f020-1134-40af-9d67-7fd7ab4fa419
public sealed class IndexInternalStartEndpoint(ILogger<IndexInternalStartEndpoint> logger) : EndpointWithoutRequest
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    public override void Configure()
    {
        Get("/indexInternal.es");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = IndexInternalStartRequestContext.Parse(HttpContext.Request);
        var dosid = string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid)
            ? Guid.NewGuid().ToString("N")
            : requestContext.Cookies.Dosid;

        var variant = string.Equals(requestContext.Query.Action, "internalWebGL", StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestContext.Query.From, "internalStart", StringComparison.OrdinalIgnoreCase)
            ? "internalWebGL"
            : string.Equals(requestContext.Query.Action, "externalLogout", StringComparison.OrdinalIgnoreCase)
                ? "externalLogout"
                : "redirect-to-internalWebGL";

        logger.LogInformation(
            "IndexInternal redirect. Variant: {Variant}, Action: {Action}, Prc: {Prc}, From: {From}, UserAgent: {UserAgent}",
            variant,
            requestContext.Query.Action,
            requestContext.Query.Prc,
            requestContext.Query.From,
            requestContext.Metadata.UserAgent);

        HttpContext.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=none; secure; HttpOnly");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Content-Language", "es");
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        if (string.Equals(requestContext.Query.Action, "externalLogout", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.Location = "/indexInternal.es?action=internalWebGL&from=externalLogout";
            HttpContext.Response.StatusCode = StatusCodes.Status302Found;
            await HttpContext.Response.CompleteAsync();
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalWebGL", StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestContext.Query.From, "externalLogout", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.Location = "/index.es?action=externalHome&loginError=94";
            HttpContext.Response.StatusCode = StatusCodes.Status302Found;
            await HttpContext.Response.CompleteAsync();
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalWebGL", StringComparison.OrdinalIgnoreCase)
            && string.Equals(requestContext.Query.From, "internalStart", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await HttpContext.Response.WriteAsync(dosid, ct);
            await HttpContext.Response.CompleteAsync();
            return;
        }

        HttpContext.Response.Headers.Location = "/indexInternal.es?action=internalWebGL&from=internalStart";
        HttpContext.Response.StatusCode = StatusCodes.Status302Found;
        await HttpContext.Response.CompleteAsync();
    }
}
