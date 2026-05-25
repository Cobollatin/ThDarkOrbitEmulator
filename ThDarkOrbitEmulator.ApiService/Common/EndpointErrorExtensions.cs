namespace ThDarkOrbitEmulator.ApiService.Common;

public static class EndpointErrorExtensions
{
    public static Task WriteApiErrorAsync(
        this HttpContext httpContext,
        string code,
        int statusCode,
        string? message = null,
        CancellationToken cancellation = default)
    {
        httpContext.Response.StatusCode = statusCode;
        return httpContext.Response.WriteAsJsonAsync(ApiErrorResponse.Create(code, message), cancellation);
    }
}
