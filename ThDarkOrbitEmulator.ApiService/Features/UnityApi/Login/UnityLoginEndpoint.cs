using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public sealed class UnityLoginEndpoint(
    ILogger<UnityLoginEndpoint> logger,
    IGameAuthSessionStore gameAuthSessionStore,
    LocalHangarStore localHangarStore,
    LocalAccountPreferencesStore preferencesStore, IServerMeshRegistry registry,
    WorldServerOptions world, CockpitCatalogStore catalog) : EndpointWithoutRequest<UnityLoginResponse>
{
    public override void Configure()
    {
        Get("/login.php");
        RoutePrefixOverride("unityApi");
        AllowAnonymous();
        Description(description => description.WithTags("UnityApi"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = UnityLoginRequestContext.Parse(HttpContext.Request);
        var dosid = requestContext.Cookies.Dosid;

        logger.LogInformation(
            "UnityApi login received. UnityVersion: {UnityVersion}, UserAgent: {UserAgent}, DosidPresent: {DosidPresent}",
            requestContext.Metadata.UnityVersion,
            requestContext.Metadata.UserAgent,
            !string.IsNullOrWhiteSpace(dosid));

        var innerPayload = await UnityLoginAction.HandleAsync(HttpContext, dosid, gameAuthSessionStore,
            localHangarStore, preferencesStore, ct, registry, world, catalog);
        if (innerPayload is null)
        {
            logger.LogWarning("UnityApi login rejected: missing, oversized or unknown dosid session.");
            await Send.UnauthorizedAsync(cancellation: ct);
            return;
        }

        await Send.OkAsync(new UnityLoginResponse
        {
            Data = innerPayload
        }, cancellation: ct);
    }
}
