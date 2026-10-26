using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.AssetsBpsecureCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.CmpInmobiCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.RulesQuantcountCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.SecureQuantserveCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.SharedservicesBpsecureCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.WwwGoogletagmanagerCom;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 section 5.12: browser support resources return deterministic local stand-ins.
public class BrowserSupportMockEndpointTests
{
    private static readonly (string RouteId, string Path, string Type, string Body, Func<DefaultHttpContext, EndpointWithoutRequest> Create)[] TextRoutes =
    [
        ("cmp-inmobi-choice", "/choice/qB5-sBEDaawtz/darkorbit.com/choice.js", "application/javascript; charset=UTF-8", "/* mock choice.js */", c => Factory.Create<ChoiceJsEndpoint>(c, NullLogger<ChoiceJsEndpoint>.Instance)),
        ("cmp-inmobi-cmp2", "/tcfv2/cmp2.js", "application/javascript; charset=UTF-8", "/* mock cmp2.js */", c => Factory.Create<Cmp2JsEndpoint>(c, NullLogger<Cmp2JsEndpoint>.Instance)),
        ("cmp-inmobi-geoip", "/geoip", "application/json; charset=UTF-8", "{}", c => Factory.Create<GeoIpEndpoint>(c, NullLogger<GeoIpEndpoint>.Instance)),
        ("cmp-inmobi-vendor-list", "/GVL-v3/vendor-list-trimmed-v1.json", "application/json; charset=UTF-8", "{\"vendors\":[]}", c => Factory.Create<VendorListEndpoint>(c, NullLogger<VendorListEndpoint>.Instance)),
        ("googletagmanager-gtm", "/gtm.js", "application/javascript; charset=UTF-8", "/* mock gtm.js */", c => Factory.Create<GtmEndpoint>(c, NullLogger<GtmEndpoint>.Instance)),
        ("googletagmanager-gtag", "/gtag/js", "application/javascript; charset=UTF-8", "/* mock gtag.js */", c => Factory.Create<GtagEndpoint>(c, NullLogger<GtagEndpoint>.Instance)),
        ("googletagmanager-ns", "/ns.html", "text/html; charset=UTF-8", "<!doctype html><html><body></body></html>", c => Factory.Create<NsHtmlEndpoint>(c, NullLogger<NsHtmlEndpoint>.Instance)),
        ("assets-bpid-js", "/bpid/bpid.js", "application/javascript; charset=UTF-8", "/* mock bpid.js */", c => Factory.Create<BpidJsEndpoint>(c, NullLogger<BpidJsEndpoint>.Instance)),
        ("secure-quantserve", "/quant.js", "application/javascript; charset=UTF-8", "/* mock quant.js */", c => Factory.Create<QuantJsEndpoint>(c, NullLogger<QuantJsEndpoint>.Instance)),
        ("quantcount-rules", "/rules-p-qB5-sBEDaawtz.js", "application/javascript; charset=UTF-8", "/* mock rules */", c => Factory.Create<RulesEndpoint>(c, NullLogger<RulesEndpoint>.Instance)),
        ("sharedservices-bgc", "/bgc/{**catch-all}", "application/javascript; charset=UTF-8", "/* mock sharedservices js */", c => Factory.Create<BgcJsEndpoint>(c, NullLogger<BgcJsEndpoint>.Instance)),
        ("sharedservices-bgc", "/bgc/{**catch-all}", "text/css; charset=UTF-8", "", c => Factory.Create<BgcCssEndpoint>(c, NullLogger<BgcCssEndpoint>.Instance)),
    ];

    [Test]
    public async Task TextResources_KeepExactBytesTypesAndIgnoreRequestData()
    {
        foreach (var (routeId, path, type, body, create) in TextRoutes)
        {
            var plain = await InvokeAsync(create, false);
            var noisy = await InvokeAsync(create, true);
            Assert.Multiple(() =>
            {
                Assert.That(plain.Status, Is.EqualTo(StatusCodes.Status200OK), path);
                Assert.That(plain.Type, Is.EqualTo(type), path);
                Assert.That(Encoding.UTF8.GetString(plain.Bytes), Is.EqualTo(body), path);
                Assert.That(noisy.Status, Is.EqualTo(plain.Status), path);
                Assert.That(noisy.Type, Is.EqualTo(plain.Type), path);
                Assert.That(noisy.Bytes, Is.EqualTo(plain.Bytes), path);
                Assert.That(noisy.SetCookie, Is.Zero, path);
                Assert.That(plain.SetCookie, Is.Zero, path);
            });
            AssertGatewayRoute(routeId, path);
        }
    }

    [Test]
    public void JsonResources_AreMinimalValidObjects()
    {
        foreach (var (routeId, _, _, body, _) in TextRoutes.Where(r => r.Type.StartsWith("application/json", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(body);
            Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object), routeId);
            if (routeId == "cmp-inmobi-vendor-list")
            {
                Assert.That(document.RootElement.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "vendors" }));
                Assert.That(document.RootElement.GetProperty("vendors").GetArrayLength(), Is.Zero);
            }
            else
                Assert.That(document.RootElement.EnumerateObject(), Is.Empty);
        }
    }

    [Test]
    public void ScriptResources_AreInertSingleLineComments()
    {
        foreach (var (routeId, _, type, body, _) in TextRoutes.Where(r => r.Type.StartsWith("application/javascript", StringComparison.Ordinal)))
        {
            Assert.Multiple(() =>
            {
                Assert.That(type, Is.EqualTo("application/javascript; charset=UTF-8"), routeId);
                Assert.That(body, Does.StartWith("/*").And.EndWith("*/"), routeId);
                Assert.That(body, Does.Not.Contain("\n").And.Not.Contain("http"), routeId);
            });
        }
    }

    [Test]
    public async Task BgcImage_IsAStableOnePixelPng()
    {
        var create = (DefaultHttpContext c) => Factory.Create<BgcImgEndpoint>(c, NullLogger<BgcImgEndpoint>.Instance);
        var plain = await InvokeAsync(create, false);
        var noisy = await InvokeAsync(create, true);
        Assert.Multiple(() =>
        {
            Assert.That(plain.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(plain.Type, Is.EqualTo("image/png"));
            Assert.That(plain.Bytes, Has.Length.GreaterThan(24));
            Assert.That(plain.Bytes[..8], Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.That(plain.Bytes[16..24], Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1 }));
            Assert.That(noisy.Status, Is.EqualTo(plain.Status));
            Assert.That(noisy.Type, Is.EqualTo(plain.Type));
            Assert.That(noisy.Bytes, Is.EqualTo(plain.Bytes));
            Assert.That(noisy.SetCookie, Is.Zero);
            Assert.That(plain.SetCookie, Is.Zero);
        });
        AssertGatewayRoute("sharedservices-bgc", "/bgc/{**catch-all}");
    }

    private static async Task<(int Status, string? Type, byte[] Bytes, int SetCookie)> InvokeAsync(
        Func<DefaultHttpContext, EndpointWithoutRequest> create, bool noisy)
    {
        var context = new DefaultHttpContext();
        using var output = new MemoryStream();
        context.Response.Body = output;
        if (noisy)
        {
            context.Request.QueryString = new QueryString("?id=other&debug=true");
            context.Request.Headers.Cookie = "dosid=irrelevant; consent=1";
            context.Request.Body = new MemoryStream("ignored"u8.ToArray());
        }
        await create(context).HandleAsync(CancellationToken.None);
        return (context.Response.StatusCode, context.Response.ContentType, output.ToArray(), context.Response.Headers.SetCookie.Count);
    }

    private static void AssertGatewayRoute(string routeId, string path)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty(routeId);
        Assert.Multiple(() =>
        {
            Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(), Is.EqualTo(path));
            Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
            Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
