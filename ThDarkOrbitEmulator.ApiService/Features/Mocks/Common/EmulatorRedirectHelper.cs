using System.Text.RegularExpressions;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

public static partial class EmulatorRedirectHelper
{
    public const string PlaceholderLogoDataUri =
        "data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='420' height='120' viewBox='0 0 420 120'>" +
        "<rect width='420' height='120' rx='10' fill='%230b1929' stroke='%232e6d92' stroke-width='2'/>" +
        "<text x='210' y='54' text-anchor='middle' fill='%237fd4ff' font-family='monospace' font-size='22' font-weight='bold'>[ SPACE EMULATOR ]</text>" +
        "<text x='210' y='84' text-anchor='middle' fill='%239ab8cc' font-family='sans-serif' font-size='13'>LOCAL SERVER PORTAL (PLACEHOLDER)</text>" +
        "</svg>";

    public static bool IsEmulatorHost(HttpRequest request)
    {
        if (IsLoopbackOrGatewayHost(request.Host.Host))
        {
            return true;
        }

        var forwardedHost = FirstHeaderValue(request.Headers["X-Forwarded-Host"]);
        return !string.IsNullOrWhiteSpace(forwardedHost)
               && IsLoopbackOrGatewayHost(new HostString(forwardedHost).Host);
    }

    public static string ToRedirectLocation(HttpRequest request, string productionAbsoluteUrl, string emulatorRelativePath)
        => IsEmulatorHost(request) && !request.Headers.ContainsKey("X-Original-Host")
            ? emulatorRelativePath
            : productionAbsoluteUrl;

    public static string RewriteLoginPageHtml(HttpRequest request, string html)
    {
        if (string.IsNullOrWhiteSpace(html)
            || !IsEmulatorHost(request)
            || request.Headers.ContainsKey("X-Original-Host"))
        {
            return html;
        }

        var rewritten = html
            .Replace("https://sas.bpsecure.com/Sas/Authentication/Bigpoint", "/Sas/Authentication/Bigpoint", StringComparison.OrdinalIgnoreCase)
            .Replace("https://js.hcaptcha.com/1/api.js", "/1/api.js", StringComparison.OrdinalIgnoreCase)
            .Replace("https://sharedservices.bpsecure.com/bgc/js/bgc-2.0.0.min.js", "/bgc/js/bgc-2.0.0.min.js", StringComparison.OrdinalIgnoreCase)
            .Replace("//assets.bpsecure.com/bpid/bpid.js", "/bpid/bpid.js", StringComparison.OrdinalIgnoreCase)
            .Replace("<title>DarkOrbit Reloaded | MMO de acción espacial</title>", "<title>Space Emulator Portal | Local Login (Placeholder)</title>", StringComparison.OrdinalIgnoreCase)
            .Replace("&copy; Bigpoint", "Space Emulator Placeholder", StringComparison.OrdinalIgnoreCase);

        rewritten = LogoImgRegex().Replace(
            rewritten,
            $"<img src=\"{PlaceholderLogoDataUri}\" alt=\"Space Emulator Placeholder Logo\" class=\"eh_mc_logo_center\">");
        rewritten = FooterLogoRegex().Replace(rewritten, string.Empty);

        return rewritten;
    }

    [GeneratedRegex(@"<img\s+src=""https://darkorbit-22\.bpsecure\.com/do_img/global/externalHome/assets/logo_rework_shadow_420x168\.png[^""]*""\s+class=""eh_mc_logo_center"">", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogoImgRegex();

    [GeneratedRegex(@"<span\b[^>]*\bid=""gl_footer_element_logo""[^>]*>\s*<a\b[^>]*>\s*<img\b[^>]*>\s*</a>\s*</span>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FooterLogoRegex();

    internal static string? FirstHeaderValue(string? rawHeader)
    {
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return null;
        }

        var commaIndex = rawHeader.IndexOf(',', StringComparison.Ordinal);
        return (commaIndex >= 0 ? rawHeader[..commaIndex] : rawHeader).Trim();
    }

    internal static bool IsLoopbackHost(string? host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackOrGatewayHost(string? host)
        => IsLoopbackHost(host)
           || string.Equals(host, "api-game", StringComparison.OrdinalIgnoreCase);
}
