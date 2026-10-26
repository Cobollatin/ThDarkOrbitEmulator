using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;

// DNS: gbl3.darkorbit.com | GET /xml/maps.php | IL2CPP literal; wire contract unverified.
public sealed class LegacyMapsPhpEndpoint(ThDarkOrbitEmulator.ApiService.Features.World.WorldServerOptions world,
    ThDarkOrbitEmulator.ApiService.Features.World.Mesh.IServerMeshRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/maps.php");
        RoutePrefixOverride("xml");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override Task HandleAsync(CancellationToken ct)
        => LocalMapsXml.WriteResponseAsync(HttpContext.Response, world, registry, ct);
}
