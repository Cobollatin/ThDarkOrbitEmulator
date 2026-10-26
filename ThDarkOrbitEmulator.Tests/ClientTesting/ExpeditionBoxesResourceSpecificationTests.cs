using System.Reflection;
using System.Text;
using System.Xml.Linq;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Expedition;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-05 "Expedition box resource": GET /xml/events/expedition/expedition-boxes.xml.
public class ExpeditionBoxesResourceSpecificationTests
{
    private const string ExpectedXml =
        "<ExpeditionBoxes><ExpeditionBox><RateId>1</RateId><Rewards>"
        + "<Reward ItemId=\"shield_sg3n_b02\" Quantity=\"1\" /></Rewards></ExpeditionBox></ExpeditionBoxes>";

    [Test]
    public void Xml_IsExactUnformattedDocumentWithoutDeclarationOrNamespace()
    {
        Assert.That(ExpeditionBoxesResponse.Xml, Is.EqualTo(ExpectedXml));
        Assert.That(ExpeditionBoxesResponse.Xml, Does.Not.StartWith("<?xml").And.Not.Contain("xmlns").And.Not.Contain("\n"));
    }

    [Test]
    public void Xml_HasOneBoxPerCatalogEntryInCatalogOrder()
    {
        var boxes = XDocument.Parse(ExpeditionBoxesResponse.Xml).Root!.Elements().ToArray();
        Assert.That(boxes.Select(box => box.Name.LocalName), Is.All.EqualTo("ExpeditionBox"));
        Assert.That(boxes.Select(box => (int)box.Element("RateId")!),
            Is.EqualTo(ExpeditionBoxesResponse.Catalog.Select(entry => entry.RateId)));
        Assert.That(boxes.Select(box => box.Element("Rewards")!.Elements("Reward").Count()), Is.All.EqualTo(1));
        Assert.That(boxes.Select(box => (string)box.Element("Rewards")!.Element("Reward")!.Attribute("ItemId")!),
            Is.EqualTo(ExpeditionBoxesResponse.Catalog.Select(entry => entry.ItemId)));
    }

    [Test]
    public void Catalog_HasUniquePositiveRateIdsAndPositiveQuantities()
    {
        var catalog = ExpeditionBoxesResponse.Catalog;
        Assert.That(catalog, Is.Not.Empty);
        Assert.That(catalog.Select(entry => entry.RateId), Is.Unique.And.All.GreaterThan(0));
        Assert.That(catalog.Select(entry => entry.Quantity), Is.All.GreaterThan(0));
        Assert.That(catalog.Select(entry => entry.ItemId), Is.All.Matches<string>(id => id.Length > 0 && id.Trim() == id));
    }

    [Test]
    public void Catalog_MatchesTheSingleShieldGrantedByTheEventsMenuClaim()
    {
        var entry = ExpeditionBoxesResponse.Catalog.Single();
        Assert.That(entry, Is.EqualTo(new ExpeditionBoxesResponse.ExpeditionBoxReward(1, LocalEvents.ExpeditionRewardItemId, 1)));
        Assert.That(LocalShop.Find(entry.ItemId), Is.Not.Null, "The advertised reward must be a known local item.");
    }

    [Test]
    public void Catalog_IsReadOnly()
    {
        Assert.That(ExpeditionBoxesResponse.Catalog, Is.Not.InstanceOf<List<ExpeditionBoxesResponse.ExpeditionBoxReward>>());
        Assert.That(((ICollection<ExpeditionBoxesResponse.ExpeditionBoxReward>)ExpeditionBoxesResponse.Catalog).IsReadOnly, Is.True);
    }

    [Test]
    public async Task WriteAsync_Writes200XmlNoStoreAndExactUtf8Body()
    {
        var (context, body) = NewContext();

        await ExpeditionBoxesResponse.WriteAsync(context.Response, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/xml"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
            Assert.That(body.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes(ExpectedXml)));
            Assert.That(body.ToArray().Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        });
    }

    [Test]
    public async Task WriteAsync_IgnoresQueryCookiesAndRepeatedCalls()
    {
        var (plain, plainBody) = NewContext();
        var (noisy, noisyBody) = NewContext();
        noisy.Request.QueryString = new QueryString("?rateId=99&lang=de");
        noisy.Request.Headers.Cookie = "dosid=0123456789abcdef0123456789abcdef";

        await ExpeditionBoxesResponse.WriteAsync(plain.Response, CancellationToken.None);
        await ExpeditionBoxesResponse.WriteAsync(noisy.Response, CancellationToken.None);
        await ExpeditionBoxesResponse.WriteAsync(noisy.Response, CancellationToken.None);

        Assert.That(noisyBody.ToArray(), Is.EqualTo(plainBody.ToArray().Concat(plainBody.ToArray())));
        Assert.That(noisy.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public void WriteAsync_PropagatesCancellationWithoutWritingBody()
    {
        var (context, body) = NewContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await ExpeditionBoxesResponse.WriteAsync(context.Response, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(body.Length, Is.Zero);
    }

    [Test]
    public void Endpoint_IsAnonymousGetUnderXmlPrefixTaggedForGameHost()
    {
        var endpoint = typeof(ExpeditionBoxesEndpoint);
        Assert.That(endpoint.IsSealed, Is.True);
        Assert.That(endpoint.BaseType, Is.EqualTo(typeof(EndpointWithoutRequest)));
        Assert.That(endpoint.GetConstructors().Single().GetParameters(), Is.Empty, "The resource takes no services or session state.");
        Assert.That(endpoint.GetMethod(nameof(EndpointWithoutRequest.HandleAsync), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(CancellationToken)])?.DeclaringType, Is.EqualTo(endpoint));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }
}
