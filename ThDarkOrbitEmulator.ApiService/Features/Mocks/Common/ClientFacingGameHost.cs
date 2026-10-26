using System.Text.RegularExpressions;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

public static class ClientFacingGameHost
{
    public static string Resolve(HttpRequest request)
    {
        var originalHost = request.Headers["X-Original-Host"].ToString();
        if (IsRedirectedGameHost(request))
        {
            return $"https://{originalHost}";
        }

        if (!EmulatorRedirectHelper.IsEmulatorHost(request))
        {
            return "https://gbl3.darkorbit.com";
        }

        var forwardedHost = EmulatorRedirectHelper.FirstHeaderValue(request.Headers["X-Forwarded-Host"]);
        if (!string.IsNullOrWhiteSpace(forwardedHost)
            && EmulatorRedirectHelper.IsLoopbackHost(new HostString(forwardedHost).Host))
        {
            var forwardedProto = EmulatorRedirectHelper.FirstHeaderValue(request.Headers["X-Forwarded-Proto"]);
            var scheme = string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(forwardedProto, "http", StringComparison.OrdinalIgnoreCase)
                ? forwardedProto!.ToLowerInvariant()
                : request.Scheme;
            // Native requests may arrive on the compatibility listener at :443 while
            // WebView login uses :60952. Prefer the configured WebView origin when
            // a forwarded loopback host has no port.
            var parsed = new HostString(forwardedHost);
            var configured = (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IServiceProvidersFeature>()?.RequestServices
                ?.GetService(typeof(IConfiguration)) as IConfiguration)?["ClientGateway:HttpsOrigin"];
            if (parsed.Port is null && scheme == "https"
                && Uri.TryCreate(configured, UriKind.Absolute, out var origin)
                && origin.Scheme == "https" && EmulatorRedirectHelper.IsLoopbackHost(origin.Host)
                && string.Equals(origin.Host, parsed.Host, StringComparison.OrdinalIgnoreCase))
                return origin.GetLeftPart(UriPartial.Authority);
            var port = parsed.Port ?? (EmulatorRedirectHelper.IsLoopbackHost(request.Host.Host)
                ? request.Host.Port : null);
            return $"{scheme}://{(port is { } value ? new HostString(parsed.Host, value) : parsed).Value}";
        }

        return $"{request.Scheme}://{request.Host.Value}";
    }

    public static string ResolveHostName(HttpRequest request)
    {
        if (IsRedirectedGameHost(request))
        {
            return request.Headers["X-Original-Host"].ToString();
        }

        var forwardedHost = EmulatorRedirectHelper.FirstHeaderValue(request.Headers["X-Forwarded-Host"]);
        if (!string.IsNullOrWhiteSpace(forwardedHost))
        {
            var parsedHost = new HostString(forwardedHost).Host;
            if (EmulatorRedirectHelper.IsLoopbackHost(parsedHost))
            {
                return parsedHost;
            }
        }

        return request.Host.Host;
    }

    public static string ResolveScheme(HttpRequest request)
    {
        if (IsRedirectedGameHost(request))
        {
            return "https";
        }

        var forwardedHost = EmulatorRedirectHelper.FirstHeaderValue(request.Headers["X-Forwarded-Host"]);
        if (!string.IsNullOrWhiteSpace(forwardedHost)
            && EmulatorRedirectHelper.IsLoopbackHost(new HostString(forwardedHost).Host))
        {
            var forwardedProto = EmulatorRedirectHelper.FirstHeaderValue(request.Headers["X-Forwarded-Proto"]);
            if (string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase)
                || string.Equals(forwardedProto, "http", StringComparison.OrdinalIgnoreCase))
            {
                return forwardedProto!.ToLowerInvariant();
            }
        }

        return request.Scheme;
    }

    public static bool IsRedirectedGameHost(HttpRequest request)
        => EmulatorRedirectHelper.IsEmulatorHost(request)
           && Regex.IsMatch(request.Headers["X-Original-Host"].ToString(),
               @"\Agbl[1-9][0-9]*\.darkorbit\.com\z",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
