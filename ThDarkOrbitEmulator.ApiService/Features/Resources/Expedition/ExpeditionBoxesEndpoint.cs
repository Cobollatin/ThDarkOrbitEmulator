using FastEndpoints;
using System.Xml.Linq;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.Resources.Expedition;

// DNS: gbl3.darkorbit.com | GET /xml/events/expedition/expedition-boxes.xml | IL2CPP literal; wire contract unverified.
public sealed class ExpeditionBoxesEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/events/expedition/expedition-boxes.xml");
        RoutePrefixOverride("xml");
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override Task HandleAsync(CancellationToken ct)
        => ExpeditionBoxesResponse.WriteAsync(HttpContext.Response, ct);
}

public static class ExpeditionBoxesResponse
{
    // Provisional local catalogue. IL2CPP supplies the path and candidate
    // RateId/Rewards member names, but no captured response establishes XML shape.
    public sealed record ExpeditionBoxReward(int RateId, string ItemId, int Quantity);

    public static IReadOnlyList<ExpeditionBoxReward> Catalog { get; } =
    [
        new(1, LocalEvents.ExpeditionRewardItemId, 1)
    ];

    public static string Xml { get; } = new XDocument(
        new XElement("ExpeditionBoxes", Catalog.Select(box =>
            new XElement("ExpeditionBox",
                new XElement("RateId", box.RateId),
                new XElement("Rewards",
                    new XElement("Reward",
                        new XAttribute("ItemId", box.ItemId),
                        new XAttribute("Quantity", box.Quantity)))))))
        .ToString(SaveOptions.DisableFormatting);

    public static Task WriteAsync(HttpResponse response, CancellationToken ct)
    {
        response.ContentType = "application/xml";
        response.Headers.CacheControl = "no-store";
        return response.WriteAsync(Xml, ct);
    }
}
