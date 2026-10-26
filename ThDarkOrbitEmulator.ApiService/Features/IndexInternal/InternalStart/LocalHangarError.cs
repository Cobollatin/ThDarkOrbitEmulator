using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

internal static class LocalHangarError
{
    public static bool HasExactly(IFormCollection form, params string[] keys) =>
        form.Count == keys.Length && keys.All(key =>
            form.TryGetValue(key, out var values) && values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]));

    // Hangar action POSTs carry exactly one canonical lower-case "action" key; query keys otherwise match case-insensitively.
    public static bool ValidActionQuery(IQueryCollection query) =>
        query.Count == 1 && query.Keys.Single() == "action" && query["action"].Count == 1;

    public static async Task WriteAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync($"<!doctype html><html><head><meta charset=\"utf-8\"><title>Hangar error</title></head>" +
            $"<body><main><h1>Hangar request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            "<a href=\"/indexInternal.es?action=internalDock\">Back to Hangar</a></main></body></html>", ct);
    }
}
