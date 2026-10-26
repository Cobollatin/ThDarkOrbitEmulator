using System.Text.Json;
using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints;

// Paths are installed-client string literals. Methods and response shapes are provisional.
public sealed class SpaceSearchEndpoint(IGameAuthSessionStore sessions, LocalHangarEquipmentStore equipment)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/events/spaceSearch.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SpaceSearchCandidateAction.HandleSearchAsync(HttpContext, sessions, equipment, ct);
}

public sealed class SpaceSearchRewardsEndpoint(IGameAuthSessionStore sessions, LocalHangarEquipmentStore equipment)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/events/ssRewards.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SpaceSearchCandidateAction.HandleRewardsAsync(HttpContext, sessions, equipment, ct);
}

public sealed class GalaxyGateRewardsEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/guide/ggRewards.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
    }

    public static GalaxyGateRewardsResponse LocalCatalogResponse() => new()
        {
            Data = LocalGalaxyGates.Catalog.Select(gate => gate.Id).ToArray()
        };

    public override Task HandleAsync(CancellationToken ct) =>
        CandidateResponse.WriteAsync(HttpContext, LocalCatalogResponse(), ct);
}

public sealed class PaymentCandidateEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/payment.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        CandidateResponse.WriteAsync(HttpContext, new PaymentResponse
        {
            Url = "/indexInternal.es?action=internalPayment"
        }, ct);
}

public sealed class UploadCandidateEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/upload.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        CandidateResponse.WriteAsync(HttpContext, new UploadResponse(), ct, StatusCodes.Status501NotImplemented);
}

internal static class CandidateResponse
{
    internal static async Task WriteAsync<T>(HttpContext context, T body, CancellationToken ct, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body), ct);
    }
}
