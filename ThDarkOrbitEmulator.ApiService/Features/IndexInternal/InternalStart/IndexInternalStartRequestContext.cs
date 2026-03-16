namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public sealed class IndexInternalStartRequestContext
{
    public IndexInternalStartQuery Query { get; init; } = new();

    public IndexInternalStartCookies Cookies { get; init; } = new();

    public IndexInternalStartMetadata Metadata { get; init; } = new();

    public static IndexInternalStartRequestContext Parse(HttpRequest request)
    {
        var cookies = new IndexInternalStartCookies
        {
            Dosid = ReadCookie(request, "dosid"),
            AliYun = ReadCookie(request, "aliyungf_tc")
        };

        foreach (var cookie in request.Cookies)
        {
            if (!IndexInternalStartCookies.KnownCookieNames.Contains(cookie.Key, StringComparer.OrdinalIgnoreCase))
            {
                cookies.Additional[cookie.Key] = cookie.Value;
            }
        }

        return new IndexInternalStartRequestContext
        {
            Query = new IndexInternalStartQuery
            {
                Action = request.Query["action"].ToString(),
                Prc = request.Query["prc"].ToString(),
                From = request.Query["from"].ToString()
            },
            Cookies = cookies,
            Metadata = new IndexInternalStartMetadata
            {
                UserAgent = request.Headers.UserAgent.ToString(),
                Referer = request.Headers.Referer.ToString(),
                Accept = request.Headers.Accept.ToString(),
                AcceptEncoding = request.Headers.AcceptEncoding.ToString(),
                AcceptLanguage = request.Headers.AcceptLanguage.ToString()
            }
        };
    }

    private static string? ReadCookie(HttpRequest request, string name)
        => request.Cookies.TryGetValue(name, out var value) ? value : null;
}

public sealed class IndexInternalStartQuery
{
    public string? Action { get; init; }

    public string? Prc { get; init; }

    public string? From { get; init; }
}

public sealed class IndexInternalStartCookies
{
    internal static readonly string[] KnownCookieNames = ["dosid", "aliyungf_tc"];

    public string? Dosid { get; init; }

    public string? AliYun { get; init; }

    public IDictionary<string, string> Additional { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class IndexInternalStartMetadata
{
    public string? UserAgent { get; init; }

    public string? Referer { get; init; }

    public string? Accept { get; init; }

    public string? AcceptEncoding { get; init; }

    public string? AcceptLanguage { get; init; }
}
