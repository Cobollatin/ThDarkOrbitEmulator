namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Register;

public sealed class RegisterRequest
{
    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }
}
