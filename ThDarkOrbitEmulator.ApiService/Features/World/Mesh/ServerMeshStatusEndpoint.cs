using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

public sealed class ServerMeshStatusEndpoint(IServerMeshRegistry registry)
    : EndpointWithoutRequest<ServerMeshSnapshot>
{
    public override void Configure()
    {
        Get("backoffice/operations/mesh");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await registry.GetSnapshotAsync(ct), ct);
    }
}
