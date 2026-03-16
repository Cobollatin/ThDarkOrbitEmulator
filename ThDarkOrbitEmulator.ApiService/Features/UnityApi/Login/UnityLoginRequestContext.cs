namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public sealed class UnityLoginRequestContext
{
    public UnityLoginRequestCookies Cookies { get; init; } = new();

    public UnityLoginRequestMetadata Metadata { get; init; } = new();

    public static UnityLoginRequestContext Parse(HttpRequest request)
    {
        var cookies = new UnityLoginRequestCookies
        {
            Dosid = ReadCookie(request, "dosid"),
            LastLogin = ReadCookie(request, "lastLogin"),
            GoogleAnalytics = ReadCookie(request, "_ga"),
            GoogleAds = ReadCookie(request, "_gcl_au"),
            Quantcast = ReadCookie(request, "__qca"),
            BpId = ReadCookie(request, "__bpid"),
            UetSessionId = ReadCookie(request, "_uetsid"),
            UetVisitorId = ReadCookie(request, "_uetvid"),
            GoogleAnalyticsSession = ReadCookie(request, "_ga_TYEEV93NN7")
        };

        foreach (var cookie in request.Cookies)
        {
            if (!UnityLoginRequestCookies.KnownCookieNames.Contains(cookie.Key, StringComparer.OrdinalIgnoreCase))
            {
                cookies.Additional[cookie.Key] = cookie.Value;
            }
        }

        return new UnityLoginRequestContext
        {
            Cookies = cookies,
            Metadata = new UnityLoginRequestMetadata
            {
                UserAgent = request.Headers.UserAgent.ToString(),
                UnityVersion = request.Headers["X-Unity-Version"].ToString(),
                AcceptEncoding = request.Headers.AcceptEncoding.ToString(),
                Host = request.Host.Value
            }
        };
    }

    private static string? ReadCookie(HttpRequest request, string name)
        => request.Cookies.TryGetValue(name, out var value) ? value : null;
}

public sealed class UnityLoginRequestCookies
{
    internal static readonly string[] KnownCookieNames =
    [
        "dosid",
        "lastLogin",
        "_ga",
        "_gcl_au",
        "__qca",
        "__bpid",
        "_uetsid",
        "_uetvid",
        "_ga_TYEEV93NN7"
    ];

    public string? Dosid { get; init; }

    public string? LastLogin { get; init; }

    public string? GoogleAnalytics { get; init; }

    public string? GoogleAds { get; init; }

    public string? Quantcast { get; init; }

    public string? BpId { get; init; }

    public string? UetSessionId { get; init; }

    public string? UetVisitorId { get; init; }

    public string? GoogleAnalyticsSession { get; init; }

    public IDictionary<string, string> Additional { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class UnityLoginRequestMetadata
{
    public string? UserAgent { get; init; }

    public string? UnityVersion { get; init; }

    public string? AcceptEncoding { get; init; }

    public string? Host { get; init; }
}
