namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local destinations advertised by unityApi/links.php and unityApi/login.php.
// The destination page contracts have not been captured.
public static class LocalAccountLinkResponse
{
    public static bool IsKnownAction(string? action) =>
        string.Equals(action, "internalSupport", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "internalAccount", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "internalForum", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "internalHelp", StringComparison.OrdinalIgnoreCase);

    public static async Task WriteAsync(HttpResponse response, string action, bool authorized, CancellationToken ct)
    {
        if (!authorized)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (string.Equals(action, "internalForum", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "internalHelp", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/html; charset=UTF-8";
            await response.WriteAsync(IndexInternalStartEndpoint.AdaptHtmlForClient(
                response.HttpContext.Request,
                LocalForumHelp.Page(string.Equals(action, "internalForum", StringComparison.OrdinalIgnoreCase))), ct);
            return;
        }

        if (string.Equals(action, "internalSupport", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/html; charset=UTF-8";
            await response.WriteAsync(IndexInternalStartEndpoint.AdaptHtmlForClient(
                response.HttpContext.Request, LocalSupport.Page()), ct);
            return;
        }

        var (title, id) = action.ToLowerInvariant() switch
        {
            "internalsupport" => ("Support", "localSupport"),
            "internalaccount" => ("Account center", "localAccountCenter"),
            "internalforum" => ("Forum", "localForum"),
            "internalhelp" => ("Help", "localHelp"),
            _ => throw new ArgumentException("Unknown local link action.", nameof(action))
        };
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=UTF-8";
        await response.WriteAsync(IndexInternalStartEndpoint.AdaptHtmlForClient(
            response.HttpContext.Request,
            $"<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>{title}</title></head>" +
            $"<body><main id=\"{id}\"><h1>{title}</h1><p>This section is being built in the local emulator.</p>" +
            "<a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to game</a></main></body></html>"), ct);
    }
}
