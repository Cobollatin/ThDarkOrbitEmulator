using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.Sas.Authentication.Contracts;

// Captured POST /Sas/Authentication/Bigpoint form fields. Values must never be logged.
public sealed record BigpointAuthenticationFormRequest
{
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}
