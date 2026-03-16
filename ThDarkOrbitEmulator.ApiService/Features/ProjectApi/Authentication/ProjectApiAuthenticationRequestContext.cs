namespace ThDarkOrbitEmulator.ApiService.Features.ProjectApi.Authentication;

public sealed class ProjectApiAuthenticationRequestContext
{
    public ProjectApiAuthenticationQuery Query { get; init; } = new();

    public ProjectApiAuthenticationCookies Cookies { get; init; } = new();

    public ProjectApiAuthenticationMetadata Metadata { get; init; } = new();

    public static ProjectApiAuthenticationRequestContext Parse(HttpRequest request)
    {
        var cookies = new ProjectApiAuthenticationCookies
        {
            Dosid = ReadCookie(request, "dosid"),
            Acr = ReadCookie(request, "acr"),
            AliYun = ReadCookie(request, "aliyungf_tc")
        };

        foreach (var cookie in request.Cookies)
        {
            if (!ProjectApiAuthenticationCookies.KnownCookieNames.Contains(cookie.Key, StringComparer.OrdinalIgnoreCase))
            {
                cookies.Additional[cookie.Key] = cookie.Value;
            }
        }

        return new ProjectApiAuthenticationRequestContext
        {
            Query = new ProjectApiAuthenticationQuery
            {
                AuthUser = request.Query["authUser"].ToString(),
                Token = request.Query["token"].ToString()
            },
            Cookies = cookies,
            Metadata = new ProjectApiAuthenticationMetadata
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

public sealed class ProjectApiAuthenticationQuery
{
    public string? AuthUser { get; init; }

    public string? Token { get; init; }
}

public sealed class ProjectApiAuthenticationCookies
{
    internal static readonly string[] KnownCookieNames = ["dosid", "acr", "aliyungf_tc"];

    public string? Dosid { get; init; }

    public string? Acr { get; init; }

    public string? AliYun { get; init; }

    public IDictionary<string, string> Additional { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectApiAuthenticationMetadata
{
    public string? UserAgent { get; init; }

    public string? Referer { get; init; }

    public string? Accept { get; init; }

    public string? AcceptEncoding { get; init; }

    public string? AcceptLanguage { get; init; }
}
