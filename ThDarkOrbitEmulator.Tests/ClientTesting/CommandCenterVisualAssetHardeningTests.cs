using System.Buffers.Binary;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class CommandCenterVisualAssetHardeningTests
{
    private const string BgPath = "/do_img/new/bg/12th_birthday_s_201812.jpg";
    private const string OverlayPngPath = "/do_img/new/overlay/banner_percentage_200.png";

    private static (string Path, string Type, string Hash, string Version)[] ManifestFiles()
    {
        var manifest = XDocument.Parse(CapturedCommandCenterResources.Xml);
        var locations = manifest.Root!.Elements("location").ToDictionary(
            e => (string)e.Attribute("id")!, e => (string)e.Attribute("path")!);
        return manifest.Root.Elements("file")
            .Select(e => ("/" + locations[(string)e.Attribute("location")!] + (string)e.Attribute("name")! + "." + (string)e.Attribute("type")!,
                (string)e.Attribute("type")!, (string)e.Attribute("hash")!, (string)e.Attribute("version")!))
            .ToArray();
    }

    private static DefaultHttpContext Request(string path, params string[] ifNoneMatch)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        if (ifNoneMatch.Length > 0)
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var value = 0xffffffffu;
        foreach (var b in bytes)
        {
            value ^= b;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~value;
    }

    private static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    [Test]
    public void Manifest_EveryEntryIsIndexedWithItsHashAndVersion()
    {
        var files = ManifestFiles();
        Assert.That(files, Has.Length.EqualTo(609));
        foreach (var file in files)
        {
            var asset = CommandCenterVisualAssets.Find(file.Path);
            Assert.That(asset, Is.Not.Null, file.Path);
            Assert.That(asset!.Path, Is.EqualTo(file.Path));
            Assert.That(asset.Type, Is.EqualTo(file.Type));
            Assert.That(asset.Hash, Does.Match("^[0-9a-f]{32}$"), file.Path);
            Assert.That(asset.Version, Is.EqualTo(file.Version));
        }
    }

    [Test]
    public void Artwork_ServedForEveryImageEntryAndNeverForXml()
    {
        foreach (var file in ManifestFiles())
        {
            var art = CommandCenterVisualAssets.GetArtwork(file.Path);
            if (file.Type is "xml")
                Assert.That(art, Is.Null, file.Path);
            else
                Assert.That(art, Is.Not.Null, file.Path);
        }
    }

    [Test]
    public void Artwork_IsIdenticalAcrossPathCasing()
    {
        var upper = CommandCenterVisualAssets.GetArtwork(BgPath.ToUpperInvariant());
        var canonical = CommandCenterVisualAssets.GetArtwork(BgPath);
        var mixed = CommandCenterVisualAssets.GetArtwork("/DO_IMG/new/BG/12th_Birthday_s_201812.JPG");
        Assert.Multiple(() =>
        {
            Assert.That(upper, Is.Not.Null);
            Assert.That(canonical, Is.EqualTo(upper));
            Assert.That(mixed, Is.EqualTo(canonical));
            Assert.That(CommandCenterVisualAssets.Find(BgPath.ToUpperInvariant())!.Path, Is.EqualTo(BgPath));
            Assert.That(CommandCenterVisualAssets.GetImagePath("12TH_BIRTHDAY_S_201812.JPG"), Is.EqualTo(BgPath));
        });
    }

    [Test]
    public void Artwork_IsDeterministicAndDistinctAcrossAllImages()
    {
        var images = ManifestFiles().Where(f => f.Type is not "xml").Select(f => f.Path).ToArray();
        var hashes = images.Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(CommandCenterVisualAssets.GetArtwork(p)!))).ToArray();
        Assert.That(hashes.Distinct().Count(), Is.EqualTo(images.Length));
        var firstArtwork = CommandCenterVisualAssets.GetArtwork(images[5]);
        var secondArtwork = CommandCenterVisualAssets.GetArtwork(images[5]);
        Assert.That(firstArtwork, Is.SameAs(secondArtwork));
    }

    [Test]
    public void Artwork_IsAValidPngWithCorrectChunkCrcs()
    {
        var png = CommandCenterVisualAssets.GetArtwork(OverlayPngPath)!;
        Assert.That(png.Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var offset = 8;
        var chunks = new List<string>();
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.That(crc, Is.EqualTo(Crc32(png.AsSpan(offset + 4, 4 + length))), type);
            chunks.Add(type);
            offset += 12 + length;
        }
        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));
            Assert.That(offset, Is.EqualTo(png.Length));
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), Is.EqualTo(303));
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)), Is.EqualTo(168));
        });
    }

    [TestCase("")]
    [TestCase("/")]
    [TestCase("/do_img/new/bg/")]
    [TestCase("/do_img/new/bg/12th_birthday_s_201812")]
    [TestCase("/do_img/new/bg/12th_birthday_s_201812.png")]
    [TestCase("/do_img/new/bg/12th_birthday_s_201812.jpg/")]
    [TestCase("/do_img/new/bg/../bg/12th_birthday_s_201812.jpg")]
    [TestCase("/do_img/new/bg/12th_birthday_s_201812.jpg%00")]
    [TestCase("/do_img/new/overlay/12th_birthday_s_201812.jpg")]
    [TestCase("do_img/new/bg/12th_birthday_s_201812.jpg")]
    [TestCase("/resources/command-center/positions.xml")]
    public void Lookup_RejectsNonManifestPaths(string path)
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandCenterVisualAssets.GetArtwork(path), Is.Null);
            Assert.That(CommandCenterVisualAssets.Find(path)?.Type, Is.Not.AnyOf("jpg", "png"));
        });
    }

    [Test]
    public void Lookup_IgnoresNullAndOversizedPaths()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandCenterVisualAssets.Find(null), Is.Null);
            Assert.That(CommandCenterVisualAssets.Find("/do_img/new/bg/" + new string('a', CommandCenterVisualAssets.MaxPathLength)), Is.Null);
            Assert.That(ManifestFiles().Max(f => f.Path.Length), Is.LessThan(CommandCenterVisualAssets.MaxPathLength));
        });
    }

    [Test]
    public async Task Endpoint_ServesPngWithManifestETagAndLength()
    {
        var context = Request(BgPath);
        Assert.That(await CommandCenterVisualAssets.TryWriteArtworkAsync(context, CancellationToken.None), Is.True);
        var asset = CommandCenterVisualAssets.Find(BgPath)!;
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo("image/png"));
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo("\"ee4f6348ce226795714891fbe1dc8300-1\""));
            Assert.That(CommandCenterVisualAssets.ETag(asset), Is.EqualTo(context.Response.Headers.ETag.ToString()));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("public, max-age=3600"));
            Assert.That(Body(context), Is.EqualTo(CommandCenterVisualAssets.GetArtwork(BgPath)));
            Assert.That(context.Response.ContentLength, Is.EqualTo(Body(context).Length));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [TestCase("\"ee4f6348ce226795714891fbe1dc8300-1\"")]
    [TestCase("W/\"ee4f6348ce226795714891fbe1dc8300-1\"")]
    [TestCase("\"other\", \"ee4f6348ce226795714891fbe1dc8300-1\"")]
    [TestCase("*")]
    public async Task Endpoint_MatchingIfNoneMatchReturns304WithoutBody(string header)
    {
        var context = Request(BgPath, header);
        Assert.That(await CommandCenterVisualAssets.TryWriteArtworkAsync(context, CancellationToken.None), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(304));
            Assert.That(Body(context), Is.Empty);
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo("\"ee4f6348ce226795714891fbe1dc8300-1\""));
        });
    }

    [TestCase("\"ee4f6348ce226795714891fbe1dc8300\"")]
    [TestCase("\"ee4f6348ce226795714891fbe1dc8300-2\"")]
    [TestCase("ee4f6348ce226795714891fbe1dc8300-1")]
    [TestCase("\"EE4F6348CE226795714891FBE1DC8300-1\"")]
    [TestCase("")]
    public async Task Endpoint_StaleOrMalformedIfNoneMatchReturnsFullImage(string header)
    {
        var context = Request(BgPath, header);
        await CommandCenterVisualAssets.TryWriteArtworkAsync(context, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(Body(context), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Endpoint_DifferentAssetsHaveDifferentETags()
    {
        var first = Request(BgPath);
        var second = Request(OverlayPngPath, "\"ee4f6348ce226795714891fbe1dc8300-1\"");
        await CommandCenterVisualAssets.TryWriteArtworkAsync(first, CancellationToken.None);
        await CommandCenterVisualAssets.TryWriteArtworkAsync(second, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(second.Response.StatusCode, Is.EqualTo(200));
            Assert.That(second.Response.Headers.ETag.ToString(), Is.EqualTo("\"7e44d06795b3c2a1a435cf534e2bc300-1\""));
            Assert.That(Body(second), Is.Not.EqualTo(Body(first)));
        });
    }

    [TestCase("/do_img/new/bg/not-in-manifest.jpg")]
    [TestCase("/do_img/global/items/ship/pusat-plus_100x100.png")]
    [TestCase("/resources/command-center/categories.xml")]
    public async Task Endpoint_NonImageOrUnknownPathsFallThroughUntouched(string path)
    {
        var context = Request(path);
        Assert.That(await CommandCenterVisualAssets.TryWriteArtworkAsync(context, CancellationToken.None), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(Body(context), Is.Empty);
            Assert.That(context.Response.Headers.ContainsKey("ETag"), Is.False);
            Assert.That(context.Response.Headers.ContainsKey("Cache-Control"), Is.False);
        });
    }

    [Test]
    public async Task Endpoint_ConcurrentRequestsReturnIdenticalBytes()
    {
        const string path = "/do_img/new/overlay/banner_percentage_56.png";
        var bodies = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            var context = Request(i % 2 == 0 ? path : path.ToUpperInvariant());
            await CommandCenterVisualAssets.TryWriteArtworkAsync(context, CancellationToken.None);
            return Body(context);
        }));
        Assert.That(bodies.Select(Convert.ToBase64String).Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Categories_ServeCapturedXmlWithManifestETag()
    {
        var context = Request(CapturedCommandCenterCategories.ManifestPath);
        await CapturedCommandCenterCategories.WriteResponseAsync(context, CancellationToken.None);
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(Body(context)));
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/xml"));
            Assert.That(context.Response.Headers.ETag.ToString(), Is.EqualTo("\"cda88151fed5847635e9b06abc645100-1\""));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("public, max-age=3600"));
            Assert.That(xml.Root!.Elements("category").Select(e => (string)e.Attribute("id")!),
                Is.EquivalentTo(new[] { "sales", "events", "news", "deals", "upselling" }));
        });
    }

    [Test]
    public async Task Categories_RevalidateTo304AndIgnoreOtherAssetsETags()
    {
        var fresh = Request("/xml/Categories.xml", "\"cda88151fed5847635e9b06abc645100-1\"");
        await CapturedCommandCenterCategories.WriteResponseAsync(fresh, CancellationToken.None);
        var foreign = Request(CapturedCommandCenterCategories.ManifestPath, "\"98c5bad74fc6123b4afbba328d395000-1\"");
        await CapturedCommandCenterCategories.WriteResponseAsync(foreign, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fresh.Response.StatusCode, Is.EqualTo(304));
            Assert.That(Body(fresh), Is.Empty);
            Assert.That(foreign.Response.StatusCode, Is.EqualTo(200));
            Assert.That(Body(foreign), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Categories_SendExactUtf8BytesWithLengthAndNoSniffHeader()
    {
        var context = Request(CapturedCommandCenterCategories.ManifestPath);
        context.Request.QueryString = new QueryString("?v=old");
        context.Request.Headers.Cookie = "unrelated=value";

        await CapturedCommandCenterCategories.WriteResponseAsync(context, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(Body(context), Is.EqualTo(System.Text.Encoding.UTF8.GetBytes(CapturedCommandCenterCategories.Xml)));
            Assert.That(context.Response.ContentLength, Is.EqualTo(Body(context).Length));
            Assert.That(context.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [Test]
    public async Task RevalidatedAssets_KeepValidatorsButHaveNoBodyOrContentLength()
    {
        var artwork = Request(BgPath, "*");
        var categories = Request(CapturedCommandCenterCategories.ManifestPath,
            "\"cda88151fed5847635e9b06abc645100-1\"");

        await CommandCenterVisualAssets.TryWriteArtworkAsync(artwork, CancellationToken.None);
        await CapturedCommandCenterCategories.WriteResponseAsync(categories, CancellationToken.None);

        foreach (var context in new[] { artwork, categories })
        {
            Assert.Multiple(() =>
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(304));
                Assert.That(Body(context), Is.Empty);
                Assert.That(context.Response.ContentLength, Is.Null);
                Assert.That(context.Response.Headers.ETag.ToString(), Is.Not.Empty);
                Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("public, max-age=3600"));
                Assert.That(context.Response.Headers.XContentTypeOptions.ToString(), Is.EqualTo("nosniff"));
                Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            });
        }
    }
}

