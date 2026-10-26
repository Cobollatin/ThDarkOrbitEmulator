namespace ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

// Public CDN asset fetched using the version in the captured resource manifest.
public static class CapturedCommandCenterCategories
{
    private static readonly Lazy<string> Snapshot = new(() =>
    {
        using var stream = typeof(CapturedCommandCenterCategories).Assembly
            .GetManifestResourceStream("CommandCenterCategories.xml")
            ?? throw new InvalidOperationException("Embedded command center categories are missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Xml => Snapshot.Value;
    private static readonly Lazy<byte[]> Utf8Body = new(() => System.Text.Encoding.UTF8.GetBytes(Xml));

    public const string ManifestPath = "/resources/command-center/categories.xml";

    // The ETag comes from the manifest's categories entry, so a client holding the
    // manifest-versioned copy revalidates to 304 instead of re-downloading.
    public static Task WriteResponseAsync(HttpContext context, CancellationToken ct)
    {
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        var asset = CommandCenterVisualAssets.Find(ManifestPath);
        if (asset is not null)
        {
            var etag = CommandCenterVisualAssets.ETag(asset);
            response.Headers.ETag = etag;
            response.Headers.CacheControl = "public, max-age=3600";
            if (CommandCenterVisualAssets.IsNotModified(context.Request, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return Task.CompletedTask;
            }
        }
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/xml";
        response.ContentLength = Utf8Body.Value.Length;
        return response.Body.WriteAsync(Utf8Body.Value, ct).AsTask();
    }
}
