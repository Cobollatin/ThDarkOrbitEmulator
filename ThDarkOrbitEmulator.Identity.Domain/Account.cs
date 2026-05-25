namespace ThDarkOrbitEmulator.Identity.Domain;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    public long GameUserId { get; set; }
}
