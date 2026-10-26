namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// Static asset manifest captured from the successful 2026-10-21 Live login.
public static class CapturedCommandCenterResources
{
    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";
    private static readonly Lazy<string> Manifest = new(() =>
    {
        using var stream = typeof(CapturedCommandCenterResources).Assembly
            .GetManifestResourceStream("CommandCenterResources.xml")
            ?? throw new InvalidOperationException("Embedded command center manifest is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Xml => Manifest.Value;

    public static async Task WriteResponseAsync(HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/xml";
        response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        await response.WriteAsync(Xml, ct);
    }
}
