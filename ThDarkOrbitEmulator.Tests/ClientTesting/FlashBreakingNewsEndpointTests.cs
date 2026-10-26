using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 section 5.9: /flashAPI/brNews.php and the /xml/CommandCenter.xml alias.
public class FlashBreakingNewsEndpointTests
{
    private static readonly string[] ExpectedHeaderNames =
        ["Expires", "Cache-Control", "Pragma", "Content-Security-Policy", "Strict-Transport-Security"];

    [Test]
    public async Task BrNews_ReturnsCapturedXmlWithExactHeadersOnce()
    {
        var (context, body) = await InvokeAsync(ctx =>
            Factory.Create<BrNewsEndpoint>(ctx, NullLogger<BrNewsEndpoint>.Instance));

        AssertCommandCenterResponse(context, body);
    }

    [Test]
    public async Task LegacyCommandCenterXml_ReturnsSameBodyAndHeadersAsBrNews()
    {
        var (brContext, brBody) = await InvokeAsync(ctx =>
            Factory.Create<BrNewsEndpoint>(ctx, NullLogger<BrNewsEndpoint>.Instance));
        var (legacyContext, legacyBody) = await InvokeAsync(ctx => Factory.Create<LegacyXmlCommandCenterEndpoint>(ctx));

        AssertCommandCenterResponse(legacyContext, legacyBody);
        Assert.Multiple(() =>
        {
            Assert.That(legacyBody, Is.EqualTo(brBody));
            foreach (var name in ExpectedHeaderNames)
                Assert.That(legacyContext.Response.Headers[name].ToString(),
                    Is.EqualTo(brContext.Response.Headers[name].ToString()), name);
        });
    }

    [Test]
    public async Task BrNews_IgnoresQueryCookiesAndSetsNoCookie()
    {
        var (_, plain) = await InvokeAsync(ctx =>
            Factory.Create<BrNewsEndpoint>(ctx, NullLogger<BrNewsEndpoint>.Instance));
        var (context, body) = await InvokeAsync(ctx =>
        {
            ctx.Request.QueryString = new QueryString("?lang=en&layer=discord&countdown=0");
            ctx.Request.Headers.Cookie = "dosid=abc; lastLogin=1";
            return Factory.Create<BrNewsEndpoint>(ctx, NullLogger<BrNewsEndpoint>.Instance);
        });

        Assert.Multiple(() =>
        {
            Assert.That(body, Is.EqualTo(plain));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [Test]
    public async Task Writer_IsDeterministicAcrossCalls()
    {
        var first = await WriteAsync();
        var second = await WriteAsync();

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Xml_DeclaresUtf8AndUsesOneLineWithoutFormatting()
    {
        var xml = ClientStubResponses.BreakingNews;

        Assert.Multiple(() =>
        {
            Assert.That(xml, Does.StartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root>"));
            Assert.That(xml.Count(c => c == '\n'), Is.EqualTo(1));
            Assert.That(xml, Does.EndWith("</root>"));
            Assert.That(xml, Does.Not.Contain("\r"));
            Assert.That(XDocument.Parse(xml).Declaration!.Encoding, Is.EqualTo("UTF-8"));
        });
    }

    [Test]
    public void Xml_HasOneLayerPerCatalogEntryInCatalogOrder()
    {
        var layers = Layers();
        var entries = CapturedNewsCatalog.MenuEntries;

        Assert.Multiple(() =>
        {
            Assert.That(layers, Has.Length.EqualTo(19));
            Assert.That(layers.Select(l => (string?)l.Attribute("name")), Is.EqualTo(entries.Select(e => e.Name)));
            Assert.That(layers.Select(l => (string?)l.Attribute("prio")),
                Is.EqualTo(entries.Select(e => e.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(layers.Select(l => (string?)l.Attribute("category")), Is.EqualTo(entries.Select(e => e.Category)));
            Assert.That(layers.Select(l => (string?)l.Element("assignments")!.Element("background")!.Attribute("assetID")),
                Is.EqualTo(entries.Select(e => e.Background)));
        });
    }

    [Test]
    public void Xml_LayerNamesAreUniqueAndMatchUnityNewsIdsWithoutPrefix()
    {
        var names = Layers().Select(l => (string)l.Attribute("name")!).ToArray();
        var newsIds = CapturedNewsCatalog.News(CapturedNewsCatalog.SnapshotAt).Data.Select(i => i.NewsId).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.Unique);
            Assert.That(names.All(n => !n.StartsWith("BE_", StringComparison.Ordinal)), Is.True);
            Assert.That(newsIds, Is.EqualTo(names.Select(n => "BE_" + n)));
        });
    }

    [Test]
    public void Xml_PrioritiesAreNondecreasingAndCategoriesComeFromCapturedSet()
    {
        var layers = Layers();
        var priorities = layers.Select(l => int.Parse((string)l.Attribute("prio")!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(priorities, Is.Ordered);
            Assert.That(priorities.All(p => p >= 1), Is.True);
            Assert.That(layers.Select(l => (string?)l.Attribute("category")).Distinct(),
                Is.SubsetOf(new[] { "deals", "news", "events" }));
        });
    }

    [Test]
    public void Xml_EveryLayerHasCountdownEmptyConfigAndExactAttributeOrder()
    {
        foreach (var layer in Layers())
        {
            var name = (string)layer.Attribute("name")!;
            Assert.Multiple(() =>
            {
                Assert.That(layer.Attributes().Select(a => a.Name.LocalName),
                    Is.EqualTo(new[] { "name", "prio", "countdown", "category" }), name);
                Assert.That((string?)layer.Attribute("countdown"), Is.EqualTo("1"), name);
                Assert.That(layer.Attribute("categoryOverrideKey"), Is.Null, name);
                Assert.That(layer.Elements().Select(e => e.Name.LocalName),
                    Is.EqualTo(new[] { "config", "assignments", "actions" }), name);
                Assert.That(layer.Element("config")!.HasElements || layer.Element("config")!.HasAttributes, Is.False, name);
                Assert.That(layer.Element("assignments")!.Elements().Select(e => e.Name.LocalName),
                    Is.EqualTo(new[] { "background" }), name);
            });
        }
    }

    [Test]
    public void Xml_EveryLayerHasOneLocalPrimaryContinueButtonToCommandCenter()
    {
        foreach (var layer in Layers())
        {
            var name = (string)layer.Attribute("name")!;
            var buttons = layer.Element("actions")!.Elements().ToArray();
            Assert.That(buttons.Select(b => b.Name.LocalName), Is.EqualTo(new[] { "button" }), name);
            var button = buttons[0];
            var links = button.Element("onclick")!.Elements().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That((string?)button.Attribute("style"), Is.EqualTo("primary"), name);
                Assert.That((string?)button.Attribute("text"), Is.EqualTo("Continue"), name);
                Assert.That((string?)button.Attribute("position"), Is.EqualTo("center"), name);
                Assert.That(links.Select(l => l.Name.LocalName), Is.EqualTo(new[] { "link" }), name);
                Assert.That((string?)links[0].Attribute("href"), Is.EqualTo("/indexInternal.es?action=internalCommandCenter"), name);
            });
        }
    }

    [Test]
    public void Xml_BackgroundsAreBareJpgFileNamesAndActionsStayRelative()
    {
        var xml = ClientStubResponses.BreakingNews;
        var backgrounds = Layers().Select(l => (string)l.Element("assignments")!.Element("background")!.Attribute("assetID")!);

        Assert.Multiple(() =>
        {
            foreach (var background in backgrounds)
            {
                Assert.That(background, Does.EndWith(".jpg"), background);
                Assert.That(background, Does.Not.Contain("/").And.Not.Contain("\\").And.Not.Contain(":"), background);
            }

            Assert.That(xml, Does.Not.Contain("http://").And.Not.Contain("https://"));
            Assert.That(xml, Does.Not.Contain("<payment").And.Not.Contain("<overlay").And.Not.Contain("<event"));
        });
    }

    [Test]
    public void FlashApiGatewayRoute_ForwardsCatchAllToApiCluster()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty("flash-api");

        Assert.Multiple(() =>
        {
            Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
            Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(), Is.EqualTo("/flashAPI/{**catch-all}"));
            Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
        });
    }

    private static XElement[] Layers() =>
        XDocument.Parse(ClientStubResponses.BreakingNews).Root!.Elements("layer").ToArray();

    private static void AssertCommandCenterResponse(HttpContext context, string body)
    {
        var headers = context.Response.Headers;
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/xml; charset=UTF-8"));
            Assert.That(headers["Expires"].ToString(), Is.EqualTo("Thu, 19 Nov 1981 08:52:00 GMT"));
            Assert.That(headers.CacheControl.ToString(), Is.EqualTo("no-store, no-cache, must-revalidate"));
            Assert.That(headers.Pragma.ToString(), Is.EqualTo("no-cache"));
            Assert.That(headers.ContentSecurityPolicy.ToString(), Is.EqualTo("frame-ancestors https://*.darkorbit.com/;"));
            Assert.That(headers.StrictTransportSecurity.ToString(), Is.EqualTo("max-age=31536000; includeSubDomains"));
            foreach (var name in ExpectedHeaderNames)
                Assert.That(headers[name].Count, Is.EqualTo(1), name);
            Assert.That(headers.SetCookie.Count, Is.Zero);
            Assert.That(body, Is.EqualTo(ClientStubResponses.BreakingNews.Replace(
                "/indexInternal.es?action=internalCommandCenter",
                "https://gbl3.darkorbit.com/indexInternal.es?action=internalCommandCenter", StringComparison.Ordinal)));
        });
    }

    private static async Task<string> WriteAsync()
    {
        var context = new DefaultHttpContext();
        await using var stream = new MemoryStream();
        context.Response.Body = stream;
        await CommandCenterNewsResponse.WriteAsync(context.Response, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<(HttpContext Context, string Body)> InvokeAsync(Func<DefaultHttpContext, EndpointWithoutRequest> create)
    {
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        var endpoint = create(context);

        await endpoint.HandleAsync(CancellationToken.None);

        return (context, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
