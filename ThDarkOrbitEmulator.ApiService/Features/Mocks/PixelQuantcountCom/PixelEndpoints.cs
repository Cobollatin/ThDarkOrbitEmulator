using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.PixelQuantcountCom;

// DNS: pixel.quantcount.com | POST /pixel | flow ids: 650efd1d-1f99-4bb5-99a2-1fa89e40e37c, 69a31366-2042-44a2-a9ca-1dc5f5a29f8a
public sealed class PostPixelEndpoint(ILogger<PostPixelEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/pixel");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:pixel.quantcount.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("quantcount pixel POST mock matched wildcard response.");
        await MockResponseHelpers.WriteNoContentAsync(HttpContext.Response, ct);
    }
}

// DNS: pixel.quantcount.com | GET /pixel | flow ids: d64126dd-67b0-42e0-a295-6d49a465c9b1, ab0009b8-ebb0-42de-989f-64dfcf1eff9b
public sealed class GetPixelEndpoint(ILogger<GetPixelEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/pixel");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:pixel.quantcount.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("quantcount pixel GET mock matched wildcard response.");
        await MockResponseHelpers.WriteNoContentAsync(HttpContext.Response, ct);
    }
}
