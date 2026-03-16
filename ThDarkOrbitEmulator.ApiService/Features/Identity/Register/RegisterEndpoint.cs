using FastEndpoints;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Register;

public sealed class RegisterEndpoint(IIdentityCommandService commandService, IFeatureManager featureManager) : Endpoint<RegisterRequest, RegisterResponse>
{
    public override void Configure()
    {
        Post("/v{version:int}/identity/register");
        Version(1);
        AllowAnonymous();
        Description(description => description.WithTags("Identity"));
    }

    public override async Task HandleAsync(RegisterRequest req, CancellationToken ct)
    {
        if (!await featureManager.IsEnabledAsync("Identity.Register"))
        {
            await SendNotFoundAsync(ct);
            return;
        }

        var created = await commandService.ExecuteAsync(new RegisterAccountCommand(req.Username, req.Email), ct);
        if (!created)
        {
            await SendAsync(new RegisterResponse { Created = false }, statusCode: StatusCodes.Status409Conflict, cancellation: ct);
            return;
        }

        await SendOkAsync(new RegisterResponse { Created = true }, ct);
    }
}
