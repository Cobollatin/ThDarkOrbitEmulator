namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Login;

public sealed class LoginResponse
{
    public bool Authenticated { get; set; }

    public string? Token { get; set; }
}
