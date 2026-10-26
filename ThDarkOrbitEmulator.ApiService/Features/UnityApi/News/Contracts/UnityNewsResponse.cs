using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.News.Contracts;

// Verified in 20261021-224636-login flow 86 (application/json).
// The captured data array contained 19 items; an empty array is a synthetic test case.
public sealed record UnityNewsResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<UnityNewsItem> Data { get; init; } = [];
}

public sealed record UnityNewsItem
{
    [JsonPropertyName("newsId")]
    public string NewsId { get; init; } = string.Empty;

    [JsonPropertyName("seconds")]
    public int Seconds { get; init; }
}
