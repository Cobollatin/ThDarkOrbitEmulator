using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.ConfigUcaCloudUnity3dCom;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.HcaptchaLogos;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.JsHcaptchaCom;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 section 5.13: hCaptcha and Unity remote-config hosts return local stand-ins.
public class ChallengeAndUnityConfigMockEndpointTests
{
    [Test]
    public async Task HcaptchaApiJs_IsAnInertSingleLineComment()
    {
        var create = (DefaultHttpContext c) => Factory.Create<ApiJsEndpoint>(c, NullLogger<ApiJsEndpoint>.Instance);
        var plain = await InvokeAsync(create, false);
        var noisy = await InvokeAsync(create, true);
        var body = Encoding.UTF8.GetString(plain.Bytes);
        Assert.Multiple(() =>
        {
            Assert.That(plain.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(plain.Type, Is.EqualTo("application/javascript; charset=UTF-8"));
            Assert.That(body, Is.EqualTo("/* mock hcaptcha api.js */"));
            Assert.That(body, Does.Not.Contain("\n").And.Not.Contain("http"));
            Assert.That(noisy.Status, Is.EqualTo(plain.Status));
            Assert.That(noisy.Type, Is.EqualTo(plain.Type));
            Assert.That(noisy.Bytes, Is.EqualTo(plain.Bytes));
            Assert.That(plain.Response.Headers.SetCookie, Is.Empty);
            Assert.That(noisy.Response.Headers.SetCookie, Is.Empty);
        });
        AssertGatewayRoute("hcaptcha-js", "/1/api.js", null);
    }

    [Test]
    public async Task HcaptchaLogo_IsAStableOnePixelPngSharedByAllLogoHosts()
    {
        var create = (DefaultHttpContext c) => Factory.Create<C47LogoEndpoint>(c, NullLogger<C47LogoEndpoint>.Instance);
        var plain = await InvokeAsync(create, false);
        var noisy = await InvokeAsync(create, true);
        Assert.Multiple(() =>
        {
            Assert.That(plain.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(plain.Type, Is.EqualTo("image/png"));
            Assert.That(plain.Bytes, Has.Length.GreaterThan(24));
            Assert.That(plain.Bytes[..8], Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.That(plain.Bytes[16..24], Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1 }));
            Assert.That(noisy.Bytes, Is.EqualTo(plain.Bytes));
            Assert.That(noisy.Type, Is.EqualTo(plain.Type));
            Assert.That(plain.Response.Headers.SetCookie, Is.Empty);
            Assert.That(noisy.Response.Headers.SetCookie, Is.Empty);
        });
        AssertGatewayRoute("hcaptcha-logo", "/logo.png", null);
    }

    [Test]
    public async Task HcaptchaLogo_MatchesTheBgcPixelBytes()
    {
        var logo = await InvokeAsync(c => Factory.Create<C47LogoEndpoint>(c, NullLogger<C47LogoEndpoint>.Instance), false);
        var bgc = await InvokeAsync(c => Factory.Create<ThDarkOrbitEmulator.ApiService.Features.Mocks.SharedservicesBpsecureCom.BgcImgEndpoint>(
            c, NullLogger<ThDarkOrbitEmulator.ApiService.Features.Mocks.SharedservicesBpsecureCom.BgcImgEndpoint>.Instance), false);
        Assert.That(logo.Bytes, Is.EqualTo(bgc.Bytes));
    }

    [Test]
    public async Task UnityConfig_ReplaysCapturedHeadersExactlyOnce()
    {
        var response = (await InvokeAsync(CreateConfig, false)).Response;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.ContentType, Is.EqualTo("application/json;charset=utf-8"));
            Assert.That(response.Headers["Access-Control-Allow-Origin"].ToArray(), Is.EqualTo(new[] { "*" }));
            Assert.That(response.Headers["Server"].ToArray(), Is.EqualTo(new[] { "Jetty(9.4.z-SNAPSHOT)" }));
            Assert.That(response.Headers["Via"].ToArray(), Is.EqualTo(new[] { "1.1 google" }));
            Assert.That(response.Headers["Alt-Svc"].ToArray(), Is.EqualTo(new[] { "h3=\":443\"; ma=2592000" }));
            Assert.That(response.Headers.SetCookie, Is.Empty);
        });
        AssertGatewayRoute("unity-config", "/", "POST");
    }

    [Test]
    public async Task UnityConfig_BodyIsRequestIndependentAndEmptyOrAJsonObject()
    {
        var plain = await InvokeAsync(CreateConfig, false);
        var noisy = await InvokeAsync(CreateConfig, true);
        Assert.Multiple(() =>
        {
            Assert.That(noisy.Bytes, Is.EqualTo(plain.Bytes));
            Assert.That(noisy.Type, Is.EqualTo(plain.Type));
            Assert.That(noisy.Response.Headers.SetCookie, Is.Empty);
        });

        // The captured flow file is git-ignored; without it the loader yields an empty body.
        if (plain.Bytes.Length == 0)
            return;
        using var document = JsonDocument.Parse(plain.Bytes);
        Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void UnityConfigGatewayRoute_IsPostOnlySoRootGetsReachTheHomePage()
    {
        using var config = LoadGatewayConfig();
        var routes = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");
        var rootRoutes = routes.EnumerateObject()
            .Where(r => r.Value.GetProperty("Match").GetProperty("Path").GetString() == "/")
            .ToDictionary(r => r.Name, r => r.Value.GetProperty("Match").TryGetProperty("Methods", out var m) ? m.EnumerateArray().Select(x => x.GetString()).ToArray() : []);
        Assert.Multiple(() =>
        {
            Assert.That(rootRoutes["unity-config"], Is.EqualTo(new[] { "POST" }));
            Assert.That(rootRoutes.Where(r => r.Key != "unity-config").SelectMany(r => r.Value), Does.Not.Contain("POST"));
        });
    }

    private static EndpointWithoutRequest CreateConfig(DefaultHttpContext context)
        => Factory.Create<ConfigEndpoint>(context, NullLogger<ConfigEndpoint>.Instance);

    private static async Task<(int Status, string? Type, byte[] Bytes, HttpResponse Response)> InvokeAsync(
        Func<DefaultHttpContext, EndpointWithoutRequest> create, bool noisy)
    {
        var context = new DefaultHttpContext();
        using var output = new MemoryStream();
        context.Response.Body = output;
        if (noisy)
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.QueryString = new QueryString("?hl=es&render=explicit");
            context.Request.Headers.Cookie = "dosid=irrelevant; hmt_id=1";
            context.Request.Body = new MemoryStream("{\"ignored\":true}"u8.ToArray());
        }
        await create(context).HandleAsync(CancellationToken.None);
        return (context.Response.StatusCode, context.Response.ContentType, output.ToArray(), context.Response);
    }

    private static void AssertGatewayRoute(string routeId, string path, string? method)
    {
        using var config = LoadGatewayConfig();
        var route = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty(routeId);
        Assert.Multiple(() =>
        {
            Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(), Is.EqualTo(path));
            Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
            Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
            if (method is null)
                Assert.That(route.GetProperty("Match").TryGetProperty("Methods", out _), Is.False);
            else
                Assert.That(route.GetProperty("Match").GetProperty("Methods").EnumerateArray().Select(m => m.GetString()), Is.EqualTo(new[] { method }));
        });
    }

    private static JsonDocument LoadGatewayConfig()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ThDarkOrbitEmulator.Gateway", "appsettings.json")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
