namespace ThDarkOrbitEmulator.AppHost.ClientTesting;

internal sealed class ClientTestingOptions
{
    public const string SectionName = "ClientTesting";

    public bool Enabled { get; set; }

    public string ClientInstallPath { get; set; } = @"D:\Games\DarkOrbit Unity";

    public string ClientExecutablePath { get; set; } = @"D:\Games\DarkOrbit Unity\DarkOrbit.exe";

    public string OutputDirectory { get; set; } = "client-testing";

    public string MappingPath { get; set; } = "client-testing/url-replacements.json";

    public string MitmProxyExecutable { get; set; } = "mitmproxy";

    public string LocalGatewayHttpsUrl { get; set; } = "https://localhost:60952";

    public string LocalGatewayHttpUrl { get; set; } = "http://localhost:60953";

    public string MitmLocalMode { get; set; } = @"local:DarkOrbit.exe,Vuplex WebView.vuplex";

    /// <summary>
    /// When true, passes <c>redirect_all=true</c> to mitmproxy (all HTTP/S → gateway). Default uses IP redirect in <c>_reversing/Mitmproxy/launch_redirect.py</c>.
    /// </summary>
    public bool MitmRedirectAll { get; set; }

    public string ApiServiceFeaturesPath { get; set; } = "ThDarkOrbitEmulator.ApiService/Features";
}
