using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Expedition;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class ExpeditionBoxesResponseTests
{
    [Test]
    public async Task LocalCatalogue_ReturnsRewardUsedByEventMenu()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await ExpeditionBoxesResponse.WriteAsync(context.Response, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(context.Response.ContentType, Is.EqualTo("application/xml"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        body.Position = 0;
        var xml = XDocument.Load(body);
        Assert.That(xml.Root!.Name.LocalName, Is.EqualTo("ExpeditionBoxes"));
        var box = xml.Root.Element("ExpeditionBox");
        Assert.That((int?)box?.Element("RateId"), Is.EqualTo(1));
        var reward = box?.Element("Rewards")?.Element("Reward");
        Assert.That((string?)reward?.Attribute("ItemId"), Is.EqualTo(LocalEvents.ExpeditionRewardItemId));
        Assert.That((int?)reward?.Attribute("Quantity"), Is.EqualTo(1));
    }
}
