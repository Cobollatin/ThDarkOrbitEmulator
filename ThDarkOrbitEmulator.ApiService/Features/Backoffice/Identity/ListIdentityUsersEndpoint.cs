using FastEndpoints;
using ThDarkOrbitEmulator.Identity.Application.Backoffice;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.Identity;

public sealed class ListIdentityUsersEndpoint(IBackofficeIdentityService backoffice) : EndpointWithoutRequest<ListIdentityUsersResponse>
{
    public override void Configure()
    {
        Get("backoffice/identity/users");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.IdentityRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var users = await backoffice.ListAccountsAsync(limit: 100, ct);
        await Send.OkAsync(new ListIdentityUsersResponse
        {
            Users = users.Select(u => new IdentityUserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                GameUserId = u.GameUserId
            }).ToList()
        }, ct);
    }
}

public sealed class ListIdentityUsersResponse
{
    public List<IdentityUserDto> Users { get; init; } = [];
}

public sealed class IdentityUserDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = "";
    public string Email { get; init; } = "";
    public long GameUserId { get; init; }
}
