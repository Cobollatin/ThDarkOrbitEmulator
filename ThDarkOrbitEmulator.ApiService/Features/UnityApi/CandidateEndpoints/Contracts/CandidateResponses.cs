using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints.Contracts;

// Provisional wire shapes inferred from IL2CPP field names, not captured responses.
public sealed record SpaceSearchResponse
{
    [JsonPropertyName("planets")]
    public IReadOnlyList<string> Planets { get; init; } = [];

    [JsonPropertyName("events")]
    public IReadOnlyList<string> Events { get; init; } = [];

    [JsonPropertyName("rewards")]
    public IReadOnlyList<string> Rewards { get; init; } = [];
}

public sealed record SpaceSearchRewardResponse
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "synthetic-reward";

    [JsonPropertyName("special")]
    public bool Special { get; init; }

    [JsonPropertyName("time")]
    public int Time { get; init; }

    [JsonPropertyName("item")]
    public string Item { get; init; } = "synthetic-item";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "Synthetic reward";

    [JsonPropertyName("package")]
    public string Package { get; init; } = "synthetic-package";
}

public sealed record GalaxyGateRewardsResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<string> Data { get; init; } = [];
}

public sealed record PaymentResponse
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

public sealed record UploadResponse
{
    [JsonPropertyName("result")]
    public bool Result { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; init; } = "Upload unavailable in emulator";

    [JsonPropertyName("status")]
    public int Status { get; init; } = 501;
}
