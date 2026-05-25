using FastEndpoints;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.ApiService.Common;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Login;

public sealed class LoginEndpoint(
    IIdentityQueryService queryService,
    IFeatureManager featureManager,
    IKeycloakService keycloakService,
    ILogger<LoginEndpoint> logger) : Endpoint<LoginRequest, LoginResponse>
{
    public override void Configure()
    {
        Post("identity/login");
        Version(1);
        AllowAnonymous();
        Description(description => description.WithTags("Identity"));
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        if (!await featureManager.IsEnabledAsync("Identity.Login"))
        {
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityFeatureDisabled, StatusCodes.Status404NotFound, cancellation: ct);
            return;
        }

        var username = req.Username.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(req.Password))
        {
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityInvalidInput, StatusCodes.Status400BadRequest, cancellation: ct);
            return;
        }

        var accountExists = await queryService.ExecuteAsync(new ValidateCredentialsQuery(username), ct);
        if (!accountExists)
        {
            logger.LogWarning("Login rejected: no local account for '{Username}'.", username);
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityAccountNotRegistered, StatusCodes.Status401Unauthorized, cancellation: ct);
            return;
        }

        var accessToken = await keycloakService.RequestAccessTokenAsync(username, req.Password, ct);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            logger.LogWarning("Login rejected: Keycloak token request failed for '{Username}'.", username);
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityAuthenticationFailed, StatusCodes.Status401Unauthorized, cancellation: ct);
            return;
        }

        await Send.OkAsync(new LoginResponse
        {
            Authenticated = true,
            Token = accessToken
        }, cancellation: ct);
    }
}
