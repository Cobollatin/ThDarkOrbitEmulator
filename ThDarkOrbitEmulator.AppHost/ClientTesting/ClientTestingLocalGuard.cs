namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal static class ClientTestingLocalGuard
{
    public static void EnsureAllowed(ClientTestingOptions options)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException(
                "Client testing is disabled. Set ClientTesting:Enabled=true in AppHost appsettings.Development.json (local machine only).");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Client testing requires Windows (PowerShell scripts, DarkOrbit.exe, mitmproxy local mode).");
        }

        if (IsCiEnvironment())
        {
            throw new InvalidOperationException(
                "Client testing cannot run in CI. It requires local game files and mitmproxy on your machine.");
        }
    }

    private static bool IsCiEnvironment() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
}
