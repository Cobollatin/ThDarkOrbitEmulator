using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.BatBingCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.BatBingNet;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.CdpCloudUnity3dCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.ChromeCloudflareDnsCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.PixelQuantcountCom;
using GoogleAnalyticsCollect = ThDarkOrbitEmulator.ApiService.Features.Mocks.GoogleAnalyticsCom.CollectEndpoint;
using SyndicationCollect = ThDarkOrbitEmulator.ApiService.Features.Mocks.Pagead2GooglesyndicationCom.CollectEndpoint;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 section 5.11: third-party telemetry and DNS sink mocks.
public class TelemetrySinkMockEndpointTests
{
    private static readonly Func<DefaultHttpContext, EndpointWithoutRequest>[] NoContentSinks =
    [
        ctx => Factory.Create<GoogleAnalyticsCollect>(ctx, NullLogger<GoogleAnalyticsCollect>.Instance),
        ctx => Factory.Create<SyndicationCollect>(ctx, NullLogger<SyndicationCollect>.Instance),
        ctx => Factory.Create<PostPixelEndpoint>(ctx, NullLogger<PostPixelEndpoint>.Instance),
        ctx => Factory.Create<GetPixelEndpoint>(ctx, NullLogger<GetPixelEndpoint>.Instance),
    ];

    private static readonly Func<DefaultHttpContext, EndpointWithoutRequest>[] EmptyTextSinks =
    [
        ctx => Factory.Create<ActionpEndpoint>(ctx, NullLogger<ActionpEndpoint>.Instance),
        ctx => Factory.Create<ActionEndpoint>(ctx, NullLogger<ActionEndpoint>.Instance),
    ];

    [TestCaseSource(nameof(NoContentSinks))]
    public async Task CollectorSinks_Return204WithoutBodyContentTypeOrCookie(Func<DefaultHttpContext, EndpointWithoutRequest> create)
    {
        var (context, body) = await InvokeAsync(create);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status204NoContent));
            Assert.That(body, Is.Empty);
            Assert.That(context.Response.ContentType, Is.Null);
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [TestCaseSource(nameof(EmptyTextSinks))]
    public async Task BingBeaconSinks_Return200EmptyPlainText(Func<DefaultHttpContext, EndpointWithoutRequest> create)
    {
        var (context, body) = await InvokeAsync(create);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(body, Is.Empty);
            Assert.That(context.Response.ContentType, Is.EqualTo("text/plain; charset=UTF-8"));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [Test]
    public async Task BatJs_ReturnsInertJavaScriptComment()
    {
        var (context, body) = await InvokeAsync(ctx => Factory.Create<BatJsEndpoint>(ctx, NullLogger<BatJsEndpoint>.Instance));

        AssertInertScript(context, body, "/* mock bat.js */");
    }

    [Test]
    public async Task BatActionJs_ReturnsInertJavaScriptComment()
    {
        var (context, body) = await InvokeAsync(ctx => Factory.Create<BatActionJsEndpoint>(ctx, NullLogger<BatActionJsEndpoint>.Instance));

        AssertInertScript(context, body, "/* mock action js */");
    }

    [Test]
    public async Task UnityCdpEvents_Returns200EmptyWithCapturedEdgeHeadersOnce()
    {
        var (context, body) = await InvokeAsync(ctx => Factory.Create<EventsEndpoint>(ctx, NullLogger<EventsEndpoint>.Instance));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(body, Is.Empty);
            Assert.That(context.Response.ContentType, Is.Null);
            Assert.That(context.Response.Headers["Vary"].ToArray(), Is.EqualTo(new[] { "Origin" }));
            Assert.That(context.Response.Headers["Via"].ToArray(), Is.EqualTo(new[] { "1.1 google" }));
            Assert.That(context.Response.Headers["Alt-Svc"].ToArray(),
                Is.EqualTo(new[] { "h3=\":443\"; ma=2592000,h3-29=\":443\"; ma=2592000" }));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [Test]
    public async Task DnsQuery_Returns200EmptyDnsMessage()
    {
        var (context, body) = await InvokeAsync(ctx => Factory.Create<DnsQueryEndpoint>(ctx, NullLogger<DnsQueryEndpoint>.Instance));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(body, Is.Empty);
            Assert.That(context.Response.ContentType, Is.EqualTo("application/dns-message"));
        });
    }

    [Test]
    public async Task Sinks_IgnoreRequestBodyQueryAndCookies()
    {
        var factories = NoContentSinks.Concat(EmptyTextSinks)
            .Append(ctx => Factory.Create<EventsEndpoint>(ctx, NullLogger<EventsEndpoint>.Instance))
            .Append(ctx => Factory.Create<DnsQueryEndpoint>(ctx, NullLogger<DnsQueryEndpoint>.Instance));

        foreach (var create in factories)
        {
            var (plainContext, plainBody) = await InvokeAsync(create);
            var (noisyContext, noisyBody) = await InvokeAsync(ctx =>
            {
                ctx.Request.QueryString = new QueryString("?v=2&tid=G-XXXX&en=page_view");
                ctx.Request.Headers.Cookie = "dosid=abc; _ga=GA1.1.1";
                ctx.Request.Body = new MemoryStream("{\"events\":[1,2,3]}"u8.ToArray());
                ctx.Request.ContentType = "application/json";
                return create(ctx);
            });

            Assert.Multiple(() =>
            {
                Assert.That(noisyContext.Response.StatusCode, Is.EqualTo(plainContext.Response.StatusCode));
                Assert.That(noisyContext.Response.ContentType, Is.EqualTo(plainContext.Response.ContentType));
                Assert.That(noisyBody, Is.EqualTo(plainBody));
                Assert.That(noisyContext.Response.Headers.SetCookie.Count, Is.Zero);
            });
        }
    }

    [TestCase("unity-cdp-events", "/v1/events")]
    [TestCase("chrome-cloudflare-dns", "/dns-query")]
    [TestCase("bat-bing-actionp", "/actionp/{**catch-all}")]
    [TestCase("bat-bing-action", "/action/{**catch-all}")]
    [TestCase("pixel-quantcount", "/pixel")]
    [TestCase("bat-bing-js", "/bat.js")]
    [TestCase("bat-bing-action-js", "/p/action/97130958.js")]
    [TestCase("google-analytics-collect", "/g/collect")]
    [TestCase("googlesyndication-collect", "/ccm/collect")]
    public void GatewayRoutes_ForwardSinkPathsAnonymouslyToApiCluster(string routeId, string path)
    {
        var route = FindGatewayRouteByPath(path, routeId);

        Assert.Multiple(() =>
        {
            Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
            Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
        });
    }

    private static void AssertInertScript(HttpContext context, string body, string expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(body, Is.EqualTo(expected));
            Assert.That(body, Does.StartWith("/*").And.EndWith("*/"));
            Assert.That(body, Does.Not.Contain("http").And.Not.Contain("\n"));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/javascript; charset=UTF-8"));
        });
    }

    private static JsonElement FindGatewayRouteByPath(string path, string routeId)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var matches = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject()
            .Where(route => route.Value.TryGetProperty("Match", out var match)
                && match.TryGetProperty("Path", out var p)
                && p.GetString() == path)
            .ToArray();

        Assert.That(matches, Has.Length.EqualTo(1), $"Exactly one gateway route must match {path}.");
        Assert.That(matches[0].Name, Is.EqualTo(routeId));
        return matches[0].Value.Clone();
    }

    private static async Task<(HttpContext Context, string Body)> InvokeAsync(Func<DefaultHttpContext, EndpointWithoutRequest> create)
    {
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;
        var endpoint = create(context);

        await endpoint.HandleAsync(CancellationToken.None);

        return (context, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
