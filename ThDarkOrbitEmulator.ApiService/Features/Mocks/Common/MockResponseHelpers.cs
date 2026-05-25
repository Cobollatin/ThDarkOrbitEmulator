namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

internal static class MockResponseHelpers
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO5qZ2QAAAAASUVORK5CYII=");

    public static async ValueTask WriteTextAsync(HttpResponse response, string content, string contentType, CancellationToken ct)
    {
        response.ContentType = contentType;
        await response.WriteAsync(content, ct);
    }

    public static async ValueTask WriteBytesAsync(HttpResponse response, byte[] content, string contentType, CancellationToken ct)
    {
        response.ContentType = contentType;
        await response.Body.WriteAsync(content, ct);
    }

    public static async ValueTask WriteNoContentAsync(HttpResponse response, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status204NoContent;
        await response.CompleteAsync();
    }

    public static byte[] TinyPngBytes => TinyPng;

    public static string LoadCapturedBody(string relativePath)
    {
        foreach (var root in GetCandidateRoots())
        {
            var filePath = Path.Combine(root, relativePath);
            if (File.Exists(filePath))
            {
                return ExtractResponseBody(File.ReadAllText(filePath));
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetCandidateRoots()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static string ExtractResponseBody(string rawCapture)
    {
        var lines = rawCapture.Replace("\r\n", "\n").Split('\n');
        var responseStart = Array.FindIndex(lines, line => line.StartsWith("HTTP/1.1 ", StringComparison.OrdinalIgnoreCase));
        if (responseStart < 0)
        {
            return rawCapture;
        }

        var blankLineAfterHeaders = Array.FindIndex(lines, responseStart + 1, line => string.IsNullOrWhiteSpace(line));
        if (blankLineAfterHeaders < 0 || blankLineAfterHeaders + 1 >= lines.Length)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, lines[(blankLineAfterHeaders + 1)..]);
    }
}
