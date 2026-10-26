using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

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
            Company = LocalCompanySelection.IsValid(account.Company) ? account.Company : null,
            Dosid = Guid.NewGuid().ToString("N"),
            KeycloakAccessToken = keycloakAccessToken,
            IsAdmin = account.Username.Equals("owner", StringComparison.OrdinalIgnoreCase) || account.Username.Equals("admin", StringComparison.OrdinalIgnoreCase)
                || HasAdminRole(keycloakAccessToken),
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

    private static bool HasAdminRole(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32768) return false;
        var parts = token.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            using var document = JsonDocument.Parse(bytes);
            return HasRole(document.RootElement);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return false; }
    }

    private static bool HasRole(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() is "emulator-admin" or "backoffice.ops.write";
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(HasRole);
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property => HasRole(property.Value));
        return false;
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

        TokenRecord? tokenRecord;
        try
        {
            tokenRecord = JsonSerializer.Deserialize<TokenRecord>(tokenPayload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

        GameAuthSession? session;
        try
        {
            session = JsonSerializer.Deserialize<GameAuthSession>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return session;
    }

    public async Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken)
    {
        if (!LocalCompanySelection.IsValid(company))
            return false;

        var session = await TryGetByDosidAsync(dosid, cancellationToken);
        if (session is null)
            return false;

        session.Company = company;
        await cache.SetStringAsync(DosidKey(dosid), JsonSerializer.Serialize(session, JsonOptions),
            new DistributedCacheEntryOptions { AbsoluteExpiration = session.ExpiresAt }, cancellationToken);
        return true;
    }

    public Task InvalidateAsync(string dosid, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(dosid)
            ? Task.CompletedTask
            : cache.RemoveAsync(DosidKey(dosid), cancellationToken);

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
