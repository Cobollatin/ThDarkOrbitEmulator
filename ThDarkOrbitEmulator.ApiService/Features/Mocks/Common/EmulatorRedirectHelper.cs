namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

internal static class EmulatorRedirectHelper
{
    public static bool IsEmulatorHost(HttpRequest request)
        => string.Equals(request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);

    public static string ToRedirectLocation(HttpRequest request, string productionAbsoluteUrl, string emulatorRelativePath)
        => IsEmulatorHost(request) ? emulatorRelativePath : productionAbsoluteUrl;
}
