using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;

namespace ThDarkOrbitEmulator.ApiService.Features.Mocks.ChromeCloudflareDnsCom;

// DNS: chrome.cloudflare-dns.com | POST /dns-query | flow ids: 194b8112-d6a7-465b-9714-e6b2effe01ca, 336a61bd-5b9f-4b91-a2fd-ddf519870c17, ba9085b3-cd34-48eb-afcc-99db8474c51f, 789eaa5b-4789-45c4-87a5-a520be7407f5, bd366f31-1bcc-483c-be3d-e22820237715, c22ee262-eb3c-4337-b3d5-22bd20f17dca, 23e519c6-adec-41ff-b9ba-422ed2e00d81, 30dc679d-1d8c-43c6-868f-cb7c1198b4c4, 7b63ebcc-8ba7-4c7b-8c24-502221ee607a, 038c910f-1deb-4bf3-957c-42991296d4dc, 44bb1e55-4616-413b-9487-80732f489af9, cbd806f1-09da-482c-84fe-8ba3f8a7ce97, 3bd3bb99-b669-4502-aa03-58a262df44a9, f513536d-38b4-4187-97ed-658156dd035b, eea758f2-737f-49b5-8370-b3e1edaf5d8f, 43c951a8-4e79-4a8f-b479-2eb260f787d5, 4b450146-1cff-4250-993c-57cd56b4666f, fe724fcc-a072-4e56-aeb8-47e576164896
public sealed class DnsQueryEndpoint(ILogger<DnsQueryEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/dns-query");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:chrome.cloudflare-dns.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        logger.LogInformation("DNS query mock matched wildcard response.");
        HttpContext.Response.ContentType = "application/dns-message";
        HttpContext.Response.StatusCode = StatusCodes.Status200OK;
        await HttpContext.Response.CompleteAsync();
    }
}
