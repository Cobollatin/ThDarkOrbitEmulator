namespace ThDarkOrbitEmulator.ApiService.Features.Sas.Authentication;

public sealed class SasAuthenticationRequestContext
{
    public SasAuthenticationQuery Query { get; init; } = new();

    public SasAuthenticationPayload Payload { get; init; } = new();

    public SasAuthenticationRequestMetadata Metadata { get; init; } = new();

    public static async Task<SasAuthenticationRequestContext> ParseAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var payload = new SasAuthenticationPayload();

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            payload = new SasAuthenticationPayload
            {
                Username = form["username"].ToString(),
                Password = form["password"].ToString(),
                GRecaptchaResponse = form["g-recaptcha-response"].ToString(),
                HCaptchaResponse = form["h-captcha-response"].ToString()
            };

            foreach (var item in form)
            {
                if (!SasAuthenticationPayload.KnownFieldNames.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    payload.Additional[item.Key] = item.Value.ToString();
                }
            }
        }

        return new SasAuthenticationRequestContext
        {
            Query = new SasAuthenticationQuery
            {
                AuthUser = request.Query["authUser"].ToString(),
                Token = request.Query["token"].ToString()
            },
            Payload = payload,
            Metadata = new SasAuthenticationRequestMetadata
            {
                Host = request.Host.Value,
                Origin = request.Headers.Origin.ToString(),
                Referer = request.Headers.Referer.ToString(),
                UserAgent = request.Headers.UserAgent.ToString(),
                Accept = request.Headers.Accept.ToString(),
                AcceptEncoding = request.Headers.AcceptEncoding.ToString(),
                AcceptLanguage = request.Headers.AcceptLanguage.ToString(),
                SecFetchSite = request.Headers["Sec-Fetch-Site"].ToString(),
                SecFetchMode = request.Headers["Sec-Fetch-Mode"].ToString(),
                SecFetchDest = request.Headers["Sec-Fetch-Dest"].ToString(),
                SecFetchUser = request.Headers["Sec-Fetch-User"].ToString()
            }
        };
    }
}

public sealed class SasAuthenticationQuery
{
    public string? AuthUser { get; init; }

    public string? Token { get; init; }
}

public sealed class SasAuthenticationPayload
{
    internal static readonly string[] KnownFieldNames =
    [
        "username",
        "password",
        "g-recaptcha-response",
        "h-captcha-response"
    ];

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? GRecaptchaResponse { get; init; }

    public string? HCaptchaResponse { get; init; }

    public IDictionary<string, string> Additional { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class SasAuthenticationRequestMetadata
{
    public string? Host { get; init; }

    public string? Origin { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    public string? Accept { get; init; }

    public string? AcceptEncoding { get; init; }

    public string? AcceptLanguage { get; init; }

    public string? SecFetchSite { get; init; }

    public string? SecFetchMode { get; init; }

    public string? SecFetchDest { get; init; }

    public string? SecFetchUser { get; init; }
}
