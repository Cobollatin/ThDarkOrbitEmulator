using FastEndpoints;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.ApiService.Common;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Register;

public sealed class RegisterEndpoint(
    IIdentityCommandService commandService,
    IKeycloakService keycloakService,
    IFeatureManager featureManager,
    ILogger<RegisterEndpoint> logger) : Endpoint<RegisterRequest, RegisterResponse>
{
    public override void Configure()
    {
        Post("identity/register");
        Version(1);
        AllowAnonymous();
        Description(description => description.WithTags("Identity"));
    }

    public override async Task HandleAsync(RegisterRequest req, CancellationToken ct)
    {
        if (!await featureManager.IsEnabledAsync("Identity.Register"))
        {
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityFeatureDisabled, StatusCodes.Status404NotFound, cancellation: ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        {
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityInvalidInput, StatusCodes.Status400BadRequest, cancellation: ct);
            return;
        }

        var username = req.Username.Trim();
        var keycloakCreated = await keycloakService.CreateUserAsync(username, req.Password, req.Email, ct);
        if (!keycloakCreated)
        {
            logger.LogWarning(
                "Registration aborted for '{Username}': Keycloak user was not created.",
                username);
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityKeycloakUnavailable, StatusCodes.Status503ServiceUnavailable, cancellation: ct);
            return;
        }

        var created = await commandService.ExecuteAsync(new RegisterAccountCommand(username, req.Email, req.Password), ct);
        if (!created)
        {
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityUsernameTaken, StatusCodes.Status409Conflict, cancellation: ct);
            return;
        }

        await Send.OkAsync(new RegisterResponse { Created = true }, cancellation: ct);
    }
}
