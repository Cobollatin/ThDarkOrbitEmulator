namespace ThDarkOrbitEmulator.ApiService.Features.GameAuth;

public sealed class GameAuthSession
{
    public Guid AccountId { get; init; }

    public string Username { get; init; } = string.Empty;

    public long GameUserId { get; init; }

    public string? Company { get; set; }

    public int InstanceId { get; set; }

    public string Dosid { get; init; } = string.Empty;

    public string? KeycloakAccessToken { get; init; }

    public bool IsAdmin { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class GameAuthSessionCreateResult
{
    public required GameAuthSession Session { get; init; }

    public required string RedirectToken { get; init; }
}

public sealed class GameAuthTokenRotationResult
{
    public required GameAuthSession Session { get; init; }

    public required string RedirectToken { get; init; }
}
