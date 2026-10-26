using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Darkorbit22BpsecureCom;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-01 section 5.14: portal static assets and image fallback precedence.
public class StaticAssetMockEndpointTests
{
    private const string ArtworkPath = "/do_img/new/bg/12th_birthday_s_201812.jpg";

    [Test]
    public async Task CssAndJsWildcards_ReturnInertStableBodiesWithoutCookies()
    {
        foreach (var (create, path, type, body) in new (Func<DefaultHttpContext, EndpointWithoutRequest>, string, string, string)[]
        {
            (c => Factory.Create<CssEndpoint>(c, NullLogger<CssEndpoint>.Instance), "/css/portal.css", "text/css; charset=UTF-8", ""),
            (c => Factory.Create<JsEndpoint>(c, NullLogger<JsEndpoint>.Instance), "/js/portal.js", "application/javascript; charset=UTF-8", "/* mock darkorbit-22 js */")
        })
        {
            var plain = await InvokeAsync(create, path);
            var noisy = await InvokeAsync(create, path, true);
            Assert.Multiple(() =>
            {
                Assert.That(plain.Status, Is.EqualTo(StatusCodes.Status200OK), path);
                Assert.That(plain.Type, Is.EqualTo(type), path);
                Assert.That(Encoding.UTF8.GetString(plain.Bytes), Is.EqualTo(body), path);
                Assert.That(noisy.Bytes, Is.EqualTo(plain.Bytes), path);
                Assert.That(noisy.Type, Is.EqualTo(plain.Type), path);
                Assert.That(plain.SetCookie, Is.Zero, path);
                Assert.That(noisy.SetCookie, Is.Zero, path);
            });
        }
    }

    [Test]
    public async Task KnownShipPaths_ReturnDistinctGeneratedPngsWithPublicCache()
    {
        foreach (var (path, expected) in new[] { (LocalShipIcons.PusatPath, LocalShipIcons.Pusat), (LocalShipIcons.PhoenixPath, LocalShipIcons.Phoenix) })
        {
            var result = await InvokeAsync(CreateImage, path, true);
            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK), path);
                Assert.That(result.Type, Is.EqualTo("image/png"), path);
                Assert.That(result.Bytes, Is.EqualTo(expected), path);
                Assert.That(result.CacheControl, Is.EqualTo("public, max-age=3600"), path);
                Assert.That(result.SetCookie, Is.Zero, path);
            });
        }
        Assert.That(LocalShipIcons.Pusat, Is.Not.EqualTo(LocalShipIcons.Phoenix));
    }

    [Test]
    public async Task ManifestArtwork_PrecedesFallbackAndPreservesRevalidation()
    {
        var asset = CommandCenterVisualAssets.Find(ArtworkPath)!;
        var first = await InvokeAsync(CreateImage, ArtworkPath);
        var repeat = await InvokeAsync(CreateImage, ArtworkPath.ToUpperInvariant(), true);
        var context = NewContext(ArtworkPath);
        context.Request.Headers.IfNoneMatch = "W/" + CommandCenterVisualAssets.ETag(asset);
        await CreateImage(context).HandleAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(first.Type, Is.EqualTo("image/png"));
            Assert.That(first.Bytes, Is.EqualTo(CommandCenterVisualAssets.GetArtwork(ArtworkPath)));
            Assert.That(first.Bytes, Is.EqualTo(repeat.Bytes));
            Assert.That(first.ETag, Is.EqualTo(CommandCenterVisualAssets.ETag(asset)));
            Assert.That(first.NoSniff, Is.EqualTo("nosniff"));
            Assert.That(first.Length, Is.EqualTo(first.Bytes.Length));
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status304NotModified));
            Assert.That(((MemoryStream)context.Response.Body).Length, Is.Zero);
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo(first.ETag));
        });
    }

    [Test]
    public async Task UnknownImageAndFavicon_ReturnSameTinyPngWithoutCacheOrCookie()
    {
        var unknown = await InvokeAsync(CreateImage, "/do_img/not-in-manifest.png", true);
        var longPath = await InvokeAsync(CreateImage, "/do_img/" + new string('x', CommandCenterVisualAssets.MaxPathLength), true);
        var favicon = await InvokeAsync(c => Factory.Create<FaviconEndpoint>(c, NullLogger<FaviconEndpoint>.Instance), "/favicon.ico", true);
        foreach (var result in new[] { unknown, longPath, favicon })
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
                Assert.That(result.Type, Is.EqualTo("image/png"));
                Assert.That(result.Bytes[..8], Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                Assert.That(result.Bytes[16..24], Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1 }));
                Assert.That(result.CacheControl, Is.Empty);
                Assert.That(result.ETag, Is.Empty);
                Assert.That(result.SetCookie, Is.Zero);
            });
        }
        Assert.That(longPath.Bytes, Is.EqualTo(unknown.Bytes));
        Assert.That(favicon.Bytes, Is.EqualTo(unknown.Bytes));
    }

    [Test]
    public void Gateway_ForwardsAllFourStaticPathsAnonymouslyToApi()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "ThDarkOrbitEmulator.Gateway", "appsettings.json")));
        var routes = config.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");
        foreach (var (name, path) in new[]
        {
            ("darkorbit-static-css", "/css/{**catch-all}"),
            ("darkorbit-static-js", "/js/{**catch-all}"),
            ("darkorbit-static-doimg", "/do_img/{**catch-all}"),
            ("darkorbit-static-favicon", "/favicon.ico")
        })
        {
            var route = routes.GetProperty(name);
            Assert.Multiple(() =>
            {
                Assert.That(route.GetProperty("Match").GetProperty("Path").GetString(), Is.EqualTo(path));
                Assert.That(route.GetProperty("ClusterId").GetString(), Is.EqualTo("api-cluster"));
                Assert.That(route.GetProperty("AuthorizationPolicy").GetString(), Is.EqualTo("anonymous"));
            });
        }
    }

    private static EndpointWithoutRequest CreateImage(DefaultHttpContext context)
        => Factory.Create<DoImageEndpoint>(context, NullLogger<DoImageEndpoint>.Instance);

    private static DefaultHttpContext NewContext(string path, bool noisy = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (noisy)
        {
            context.Request.QueryString = new QueryString("?v=999");
            context.Request.Headers.Cookie = "dosid=irrelevant";
        }
        return context;
    }

    private static async Task<(int Status, string? Type, byte[] Bytes, string CacheControl, string ETag, string NoSniff, long? Length, int SetCookie)> InvokeAsync(
        Func<DefaultHttpContext, EndpointWithoutRequest> create, string path, bool noisy = false)
    {
        var context = NewContext(path, noisy);
        await create(context).HandleAsync(CancellationToken.None);
        return (context.Response.StatusCode, context.Response.ContentType, ((MemoryStream)context.Response.Body).ToArray(),
            context.Response.Headers.CacheControl.ToString(), context.Response.Headers.ETag.ToString(),
            context.Response.Headers.XContentTypeOptions.ToString(), context.Response.ContentLength, context.Response.Headers.SetCookie.Count);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
