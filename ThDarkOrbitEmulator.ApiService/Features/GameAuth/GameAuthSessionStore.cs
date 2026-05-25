using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.GameAuth;

public sealed class GameAuthSessionStore(IDistributedCache cache) : IGameAuthSessionStore
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GameAuthSessionCreateResult> CreateSessionAsync(
        AccountSummary account,
        string? keycloakAccessToken,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var redirectToken = CreateOpaqueToken();
        var session = new GameAuthSession
        {
            AccountId = account.Id,
            Username = account.Username,
            GameUserId = account.GameUserId,
            Dosid = Guid.NewGuid().ToString("N"),
            KeycloakAccessToken = keycloakAccessToken,
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime)
        };

        await SaveSessionAsync(session, redirectToken, cancellationToken);

        return new GameAuthSessionCreateResult
        {
            Session = session,
            RedirectToken = redirectToken
        };
    }

    public async Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken)
    {
        var session = await TryGetByRedirectTokenAsync(redirectToken, cancellationToken);
        if (session is null)
        {
            return null;
        }

        await cache.RemoveAsync(TokenKey(redirectToken), cancellationToken);

        var nextToken = CreateOpaqueToken();
        await SaveSessionAsync(session, nextToken, cancellationToken);

        return new GameAuthTokenRotationResult
        {
            Session = session,
            RedirectToken = nextToken
        };
    }

    public async Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(redirectToken))
        {
            return null;
        }

        var tokenPayload = await cache.GetStringAsync(TokenKey(redirectToken), cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenPayload))
        {
            return null;
        }

        var tokenRecord = JsonSerializer.Deserialize<TokenRecord>(tokenPayload, JsonOptions);
        if (tokenRecord is null || string.IsNullOrWhiteSpace(tokenRecord.Dosid))
        {
            return null;
        }

        return await TryGetByDosidAsync(tokenRecord.Dosid, cancellationToken);
    }

    public async Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dosid))
        {
            return null;
        }

        var payload = await cache.GetStringAsync(DosidKey(dosid), cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var session = JsonSerializer.Deserialize<GameAuthSession>(payload, JsonOptions);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return session;
    }

    private async Task SaveSessionAsync(GameAuthSession session, string redirectToken, CancellationToken cancellationToken)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = session.ExpiresAt
        };

        var sessionJson = JsonSerializer.Serialize(session, JsonOptions);
        await cache.SetStringAsync(DosidKey(session.Dosid), sessionJson, options, cancellationToken);

        var tokenRecord = JsonSerializer.Serialize(new TokenRecord { Dosid = session.Dosid }, JsonOptions);
        await cache.SetStringAsync(TokenKey(redirectToken), tokenRecord, options, cancellationToken);
    }

    private static string CreateOpaqueToken()
        => Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string TokenKey(string redirectToken) => $"gameauth:token:{redirectToken}";

    private static string DosidKey(string dosid) => $"gameauth:dosid:{dosid}";

    private sealed class TokenRecord
    {
        public string Dosid { get; set; } = string.Empty;
    }
}
