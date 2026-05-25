using System.Text.Json;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

internal static class FlowCaptureLoader
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ResponseBodies = new(BuildResponseBodies);

    public static string LoadResponseBody(string flowId)
        => ResponseBodies.Value.TryGetValue(flowId, out var body) ? body : string.Empty;

    private static IReadOnlyDictionary<string, string> BuildResponseBodies()
    {
        var responses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in GetCandidatePaths())
        {
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            foreach (var line in File.ReadLines(sourcePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var id = idElement.GetString();
                if (string.IsNullOrWhiteSpace(id) || responses.ContainsKey(id))
                {
                    continue;
                }

                if (!document.RootElement.TryGetProperty("response", out var responseElement)
                    || responseElement.ValueKind != JsonValueKind.Object
                    || !responseElement.TryGetProperty("body", out var bodyElement)
                    || bodyElement.ValueKind != JsonValueKind.Object
                    || !bodyElement.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                responses[id] = contentElement.GetString() ?? string.Empty;
            }
        }

        return responses;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "_reversing", "Requests", "full_flow.jsonl");
            yield return Path.Combine(current.FullName, "_reversing", "Requests", "extracted_flow.jsonl");
        }
    }
}
