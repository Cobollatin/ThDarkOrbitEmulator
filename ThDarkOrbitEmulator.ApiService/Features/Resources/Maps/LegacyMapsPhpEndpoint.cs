using FastEndpoints;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;

// DNS: gbl3.darkorbit.com | GET /xml/maps.php | IL2CPP literal; wire contract unverified.
// The Unity client prefixes the login payload's basePath, so it requests /spacemap/xml/maps.php.
public sealed class LegacyMapsPhpEndpoint(ThDarkOrbitEmulator.ApiService.Features.World.WorldServerOptions world,
    ThDarkOrbitEmulator.ApiService.Features.World.Mesh.IServerMeshRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/xml/maps.php", "/spacemap/xml/maps.php");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override Task HandleAsync(CancellationToken ct)
        => LocalMapsXml.WriteResponseAsync(HttpContext.Response, world, registry, ct);
}
