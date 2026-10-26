using System.Reflection;
using System.Text;
using System.Xml.Linq;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-05 "Command center category resource": GET /resources/command-center/categories.xml and GET /xml/Categories.xml.
public class CommandCenterCategoriesResourceSpecificationTests
{
    private const string ManifestETag = "\"cda88151fed5847635e9b06abc645100-1\"";

    [Test]
    public void Xml_IsTheEmbeddedCapturedSnapshotWithoutDeclarationOrBom()
    {
        var path = Path.Combine(RepositoryRoot(), "ThDarkOrbitEmulator.ApiService", "Features", "Resources",
            "CommandCenter", "command-center-categories.20261022.xml");
        Assert.That(CapturedCommandCenterCategories.Xml, Is.EqualTo(File.ReadAllText(path)));
        Assert.That(CapturedCommandCenterCategories.Xml, Does.StartWith("<root>").And.Not.Contain("xmlns"));
        Assert.That(CapturedCommandCenterCategories.ManifestPath, Is.EqualTo("/resources/command-center/categories.xml"));
    }

    [Test]
    public void Categories_AreFiveCapturedEntriesWithDerivedTextKeysAndOnlySalesSpecial()
    {
        var entries = XDocument.Parse(CapturedCommandCenterCategories.Xml).Root!.Elements().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(entry => entry.Name.LocalName), Is.All.EqualTo("category"));
            Assert.That(entries.Select(entry => (string)entry.Attribute("id")!),
                Is.EqualTo(new[] { "sales", "events", "news", "deals", "upselling" }).And.Unique);
            Assert.That(entries.Select(entry => (int)entry.Attribute("prio")!), Is.EqualTo(new[] { 5, 1, 3, 2, 5 }));
            Assert.That(entries.Select(entry => (string)entry.Attribute("textKey")!),
                Is.EqualTo(entries.Select(entry => "bn_category_" + (string)entry.Attribute("id")!)));
            Assert.That(entries.Where(entry => entry.Attribute("isSpecial") is not null)
                .Select(entry => ((string)entry.Attribute("id")!, (string)entry.Attribute("isSpecial")!)),
                Is.EqualTo(new[] { ("sales", "true") }));
            Assert.That(entries.Select(entry => entry.Attributes().Count()), Is.EqualTo(new[] { 4, 3, 3, 3, 3 }));
        });
    }

    [Test]
    public void Categories_CoverEveryCapturedNewsEntryCategory()
    {
        var ids = XDocument.Parse(CapturedCommandCenterCategories.Xml).Root!.Elements("category")
            .Select(entry => (string)entry.Attribute("id")!).ToHashSet(StringComparer.Ordinal);
        Assert.That(CapturedNewsCatalog.MenuEntries.Select(entry => entry.Category).Distinct(),
            Is.Not.Empty.And.All.Matches<string>(ids.Contains));
    }

    [Test]
    public void ManifestEntry_SuppliesTheStrongETag()
    {
        var asset = CommandCenterVisualAssets.Find(CapturedCommandCenterCategories.ManifestPath);
        Assert.That(asset, Is.Not.Null);
        Assert.That(asset!.Type, Is.EqualTo("xml"));
        Assert.That(CommandCenterVisualAssets.ETag(asset), Is.EqualTo(ManifestETag));
        Assert.That(CommandCenterVisualAssets.GetArtwork(asset.Path), Is.Null, "The XML entry is never served as artwork.");
    }

    [Test]
    public async Task WriteResponseAsync_Writes200XmlWithManifestValidatorsAndExactUtf8Body()
    {
        var (context, body) = NewContext();

        await CapturedCommandCenterCategories.WriteResponseAsync(context, CancellationToken.None);

        var expected = Encoding.UTF8.GetBytes(CapturedCommandCenterCategories.Xml);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/xml"));
            Assert.That(context.Response.ContentLength, Is.EqualTo(expected.Length));
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo(ManifestETag));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("public, max-age=3600"));
            Assert.That(context.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
            Assert.That(context.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
            Assert.That(body.ToArray(), Is.EqualTo(expected));
            Assert.That(body.ToArray().Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        });
    }

    [TestCase(ManifestETag)]
    [TestCase("W/" + ManifestETag)]
    [TestCase("*")]
    [TestCase("\"other\" ,  " + ManifestETag + " , \"another\"")]
    public async Task WriteResponseAsync_MatchingIfNoneMatchRevalidatesTo304WithoutBody(string ifNoneMatch)
    {
        var (context, body) = NewContext();
        context.Request.Headers.IfNoneMatch = ifNoneMatch;

        await CapturedCommandCenterCategories.WriteResponseAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
            Assert.That(body.Length, Is.Zero);
            Assert.That(context.Response.ContentType, Is.Null);
            Assert.That(context.Response.ContentLength, Is.Null);
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo(ManifestETag));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("public, max-age=3600"));
        });
    }

    [TestCase("\"cda88151fed5847635e9b06abc645100\"")]
    [TestCase("cda88151fed5847635e9b06abc645100-1")]
    [TestCase("\"CDA88151FED5847635E9B06ABC645100-1\"")]
    [TestCase("w/" + ManifestETag)]
    [TestCase("")]
    public async Task WriteResponseAsync_NonMatchingValidatorServesFullBody(string ifNoneMatch)
    {
        var (context, body) = NewContext();
        context.Request.Headers.IfNoneMatch = ifNoneMatch;

        await CapturedCommandCenterCategories.WriteResponseAsync(context, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(body.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes(CapturedCommandCenterCategories.Xml)));
    }

    [Test]
    public async Task WriteResponseAsync_IgnoresRequestPathQueryAndCookies()
    {
        var (plain, plainBody) = NewContext();
        var (noisy, noisyBody) = NewContext();
        noisy.Request.Path = "/xml/Categories.xml";
        noisy.Request.QueryString = new QueryString("?__cv=0&lang=de");
        noisy.Request.Headers.Cookie = "dosid=0123456789abcdef0123456789abcdef";

        await CapturedCommandCenterCategories.WriteResponseAsync(plain, CancellationToken.None);
        await CapturedCommandCenterCategories.WriteResponseAsync(noisy, CancellationToken.None);

        Assert.That(noisyBody.ToArray(), Is.EqualTo(plainBody.ToArray()));
        Assert.That(noisy.Response.Headers.ETag.ToString(), Is.EqualTo(ManifestETag));
        Assert.That(noisy.Response.Headers.ContainsKey("Set-Cookie"), Is.False);
    }

    [Test]
    public void WriteResponseAsync_PropagatesCancellationWithoutWritingBody()
    {
        var (context, body) = NewContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(async () => await CapturedCommandCenterCategories.WriteResponseAsync(context, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(body.Length, Is.Zero);
    }

    [TestCase(typeof(CommandCenterCategoriesEndpoint))]
    [TestCase(typeof(LegacyXmlCategoriesEndpoint))]
    public void Endpoints_AreSealedStatelessResourcesWithoutRequestBinding(Type endpoint)
    {
        Assert.That(endpoint.IsSealed, Is.True);
        Assert.That(endpoint.BaseType, Is.EqualTo(typeof(EndpointWithoutRequest)));
        Assert.That(endpoint.GetConstructors().Single().GetParameters(), Is.Empty, "The resource takes no services or session state.");
        Assert.That(endpoint.GetMethod(nameof(EndpointWithoutRequest.HandleAsync), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(CancellationToken)])?.DeclaringType, Is.EqualTo(endpoint));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = CapturedCommandCenterCategories.ManifestPath;
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
