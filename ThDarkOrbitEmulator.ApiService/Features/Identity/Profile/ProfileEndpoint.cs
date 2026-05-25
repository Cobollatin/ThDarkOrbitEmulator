using System.Security.Claims;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using ThDarkOrbitEmulator.ApiService.Common;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.Identity.Profile;

public sealed class ProfileEndpoint(
    IIdentityQueryService queryService,
    ILogger<ProfileEndpoint> logger) : EndpointWithoutRequest<ProfileResponse>
{
    public override void Configure()
    {
        Get("identity/me");
        Version(1);
        Description(description => description.WithTags("Identity"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var username = User.FindFirstValue("preferred_username")
            ?? User.FindFirstValue(ClaimTypes.Name)
            ?? User.Identity?.Name;

        if (string.IsNullOrWhiteSpace(username))
        {
            logger.LogWarning(
                "Profile rejected: authenticated principal has no username claim (IsAuthenticated={IsAuthenticated}).",
                User.Identity?.IsAuthenticated == true);
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityUnauthorized, StatusCodes.Status401Unauthorized, cancellation: ct);
            return;
        }

        var account = await queryService.ExecuteAsync(new GetAccountByUsernameQuery(username), ct);
        if (account is null)
        {
            await HttpContext.WriteApiErrorAsync(ApiErrorCode.IdentityAccountNotFound, StatusCodes.Status404NotFound, cancellation: ct);
            return;
        }

        await Send.OkAsync(new ProfileResponse
        {
            Username = account.Username,
            Email = account.Email,
            GameUserId = account.GameUserId
        }, cancellation: ct);
    }
}
