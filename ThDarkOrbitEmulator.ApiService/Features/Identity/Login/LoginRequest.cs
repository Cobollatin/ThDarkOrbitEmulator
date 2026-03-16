namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Login;

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
