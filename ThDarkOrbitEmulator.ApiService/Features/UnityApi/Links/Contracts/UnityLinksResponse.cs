using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Links.Contracts;

// Verified in 20261021-224636-login flow 83 (application/json).
// All six links were strings in that response; presence across sessions is unknown.
public sealed record UnityLinksResponse
{
    [JsonPropertyName("imprint")]
    public string Imprint { get; init; } = string.Empty;

    [JsonPropertyName("credits")]
    public string Credits { get; init; } = string.Empty;

    [JsonPropertyName("forum")]
    public string Forum { get; init; } = string.Empty;

    [JsonPropertyName("support")]
    public string Support { get; init; } = string.Empty;

    [JsonPropertyName("help")]
    public string Help { get; init; } = string.Empty;

    [JsonPropertyName("accountCenter")]
    public string AccountCenter { get; init; } = string.Empty;
}
