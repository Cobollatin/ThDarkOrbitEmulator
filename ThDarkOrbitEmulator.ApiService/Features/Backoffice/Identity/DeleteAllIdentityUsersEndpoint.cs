using FastEndpoints;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Identity;

public sealed class DeleteAllIdentityUsersEndpoint(IBackofficeIdentityService backoffice) : EndpointWithoutRequest<DeleteAllIdentityUsersResponse>
{
    public override void Configure()
    {
        Delete("backoffice/identity/users");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.IdentityDelete));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var deleted = await backoffice.DeleteAllAccountsAsync(ct);
        await Send.OkAsync(new DeleteAllIdentityUsersResponse { Deleted = deleted }, ct);
    }
}

public sealed class DeleteAllIdentityUsersResponse
{
    public int Deleted { get; init; }
}
