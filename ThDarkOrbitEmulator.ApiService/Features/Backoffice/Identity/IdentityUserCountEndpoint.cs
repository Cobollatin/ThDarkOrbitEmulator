using FastEndpoints;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Identity;

public sealed class IdentityUserCountEndpoint(IBackofficeIdentityService backoffice) : EndpointWithoutRequest<IdentityUserCountResponse>
{
    public override void Configure()
    {
        Get("backoffice/identity/users/count");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.IdentityRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var count = await backoffice.CountAccountsAsync(ct);
        await Send.OkAsync(new IdentityUserCountResponse { Count = count }, ct);
    }
}

public sealed class IdentityUserCountResponse
{
    public long Count { get; init; }
}
