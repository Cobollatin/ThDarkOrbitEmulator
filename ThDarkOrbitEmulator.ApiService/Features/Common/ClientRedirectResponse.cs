using Microsoft.AspNetCore.Http;

namespace ThDarkOrbitEmulator.ApiService.Features;

public static class ClientRedirectResponse
{
    public static void Apply(HttpResponse response, string location)
    {
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.Location = location;
        response.StatusCode = StatusCodes.Status302Found;
    }
}
