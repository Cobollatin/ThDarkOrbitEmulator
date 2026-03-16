using FastEndpoints;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Login;

public sealed class LoginEndpoint(
    IIdentityQueryService queryService,
    IFeatureManager featureManager,
    IKeycloakService keycloakService) : Endpoint<LoginRequest, LoginResponse>
{
    public override void Configure()
    {
        Post("/v{version:int}/identity/login");
        Version(1);
        AllowAnonymous();
        Description(description => description.WithTags("Identity"));
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        if (!await featureManager.IsEnabledAsync("Identity.Login"))
        {
            await SendNotFoundAsync(ct);
            return;
        }

        var authenticated = await queryService.ExecuteAsync(new ValidateCredentialsQuery(req.Username), ct);
        if (!authenticated)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var accessToken = await keycloakService.RequestAccessTokenAsync(req.Username, req.Password, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await SendOkAsync(new LoginResponse
        {
            Authenticated = true,
            Token = accessToken
        }, ct);
    }
}
