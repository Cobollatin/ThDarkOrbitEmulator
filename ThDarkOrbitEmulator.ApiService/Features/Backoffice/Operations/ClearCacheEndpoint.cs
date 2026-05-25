using FastEndpoints;
using StackExchange.Redis;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Operations;

public sealed class ClearCacheEndpoint(IConnectionMultiplexer redis) : EndpointWithoutRequest<ClearCacheResponse>
{
    public override void Configure()
    {
        Post("backoffice/operations/cache/clear");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.CacheWrite));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var database = redis.GetDatabase();
        await database.ExecuteAsync("FLUSHALL");
        await Send.OkAsync(new ClearCacheResponse { Cleared = true }, ct);
    }
}

public sealed class ClearCacheResponse
{
    public bool Cleared { get; init; }
}
