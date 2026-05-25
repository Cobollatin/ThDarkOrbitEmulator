namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Profile;

public sealed class ProfileResponse
{
    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    public long GameUserId { get; set; }
}
