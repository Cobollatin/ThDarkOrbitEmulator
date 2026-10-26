using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class UnityNewsEndpointHardeningTests
{
    private static readonly DateTimeOffset Snapshot = CapturedNewsCatalog.SnapshotAt;

    [Test]
    public async Task News_AtSnapshot_ReturnsCapturedItemsAndHeadersOnce()
    {
        var (context, json) = await WriteAsync(Snapshot);

        var captured = ClientStubResponses.News().Data;
        var items = json["data"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo(UnityNewsAction.CacheControl));
            Assert.That(context.Response.Headers.CacheControl.Count, Is.EqualTo(1));
            Assert.That(context.Response.Headers["X-Custom-Header"].ToString(), Is.EqualTo("DarkOrbitUnity"));
            Assert.That(context.Response.Headers["X-Custom-Header"].Count, Is.EqualTo(1));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(items.Select(i => i!["newsId"]!.GetValue<string>()), Is.EqualTo(captured.Select(i => i.NewsId)));
            Assert.That(items.Select(i => i!["seconds"]!.GetValue<int>()), Is.EqualTo(captured.Select(i => i.Seconds)));
        });
    }

    [Test]
    public async Task News_AnHourLater_CountsDownEveryItem()
    {
        var (_, json) = await WriteAsync(Snapshot.AddHours(1));

        var captured = ClientStubResponses.News().Data;
        var seconds = json["data"]!.AsArray().Select(i => i!["seconds"]!.GetValue<int>()).ToArray();
        for (var i = 0; i < captured.Count; i++)
            Assert.That(seconds[i], Is.EqualTo(captured[i].Seconds - 3600), captured[i].NewsId);
    }

    [Test]
    public void RemainingSeconds_BeforeSnapshot_ReturnsCapturedValue()
    {
        Assert.That(CapturedNewsCatalog.RemainingSeconds(65568, Snapshot.AddDays(-3)), Is.EqualTo(65568));
    }

    [TestCase(0, 65568)]
    [TestCase(65567, 1)]
    [TestCase(65568, 65568)]
    [TestCase(65569, 65567)]
    [TestCase(3 * 65568 + 10, 65558)]
    public void RemainingSeconds_RestartsWithCapturedDurationAfterExpiry(long elapsed, int expected)
    {
        Assert.That(CapturedNewsCatalog.RemainingSeconds(65568, Snapshot.AddSeconds(elapsed)), Is.EqualTo(expected));
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void RemainingSeconds_NonPositiveSnapshot_ReturnsZero(int snapshot)
    {
        Assert.That(CapturedNewsCatalog.RemainingSeconds(snapshot, Snapshot.AddDays(1)), Is.Zero);
    }

    [Test]
    public async Task News_FarFuture_KeepsEveryItemPositiveAndWithinCapturedDuration()
    {
        var (_, json) = await WriteAsync(Snapshot.AddYears(30));

        var captured = ClientStubResponses.News().Data;
        var seconds = json["data"]!.AsArray().Select(i => i!["seconds"]!.GetValue<int>()).ToArray();
        Assert.That(seconds, Has.Length.EqualTo(captured.Count));
        for (var i = 0; i < captured.Count; i++)
            Assert.That(seconds[i], Is.InRange(1, captured[i].Seconds), captured[i].NewsId);
    }

    [Test]
    public async Task News_IgnoresQueryAndCookies()
    {
        var plain = await WriteAsync(Snapshot.AddMinutes(5));
        var noisy = await WriteAsync(Snapshot.AddMinutes(5), context =>
        {
            context.Request.QueryString = new QueryString("?lang=de&newsId=<script>");
            context.Request.Headers.Cookie = "dosid=" + new string('x', 400);
        });

        Assert.Multiple(() =>
        {
            Assert.That(noisy.Json.ToJsonString(), Is.EqualTo(plain.Json.ToJsonString()));
            Assert.That(noisy.Json.ToJsonString(), Does.Not.Contain("script"));
            Assert.That(noisy.Context.Response.Headers.SetCookie.Count, Is.Zero);
        });
    }

    [Test]
    public async Task News_StaysInStepWithBrNewsLayers()
    {
        var (_, json) = await WriteAsync(Snapshot.AddDays(10));

        var layers = XDocument.Parse(ClientStubResponses.BreakingNews).Root!.Elements("layer")
            .Select(layer => "BE_" + (string)layer.Attribute("name")!);
        Assert.That(json["data"]!.AsArray().Select(i => i!["newsId"]!.GetValue<string>()), Is.EqualTo(layers));
    }

    [TestCase(0, 65568)]
    [TestCase(999, 65568)]
    [TestCase(1000, 65567)]
    [TestCase(65567999, 1)]
    [TestCase(65568000, 65568)]
    public void RemainingSeconds_TruncatesSubsecondsAndRestartsAtExactBoundary(
        long elapsedMilliseconds, int expected)
    {
        var now = Snapshot.AddMilliseconds(elapsedMilliseconds);
        Assert.That(CapturedNewsCatalog.RemainingSeconds(65568, now), Is.EqualTo(expected));
    }

    [Test]
    public void RemainingSeconds_UsesInstantAcrossTimeZoneOffsets()
    {
        var instant = Snapshot.AddSeconds(17);
        var shifted = instant.ToOffset(TimeSpan.FromHours(9));

        Assert.That(CapturedNewsCatalog.RemainingSeconds(65568, shifted),
            Is.EqualTo(CapturedNewsCatalog.RemainingSeconds(65568, instant)));
    }

    [Test]
    public async Task News_SerializedBodyHasOnlyCapturedStringIdsAndIntegerCountdowns()
    {
        var (_, json) = await WriteAsync(Snapshot);
        var data = json.AsObject();
        var items = data["data"]!.AsArray();

        Assert.Multiple(() =>
        {
            Assert.That(data.Select(pair => pair.Key), Is.EqualTo(new[] { "data" }));
            Assert.That(items, Has.Count.EqualTo(19));
            Assert.That(items.Select(item => item!["newsId"]!.GetValue<string>()), Is.Unique);
            foreach (var item in items)
            {
                Assert.That(item!.AsObject().Select(pair => pair.Key), Is.EqualTo(new[] { "newsId", "seconds" }));
                Assert.That(item["newsId"]!.GetValueKind(), Is.EqualTo(System.Text.Json.JsonValueKind.String));
                Assert.That(item["seconds"]!.GetValueKind(), Is.EqualTo(System.Text.Json.JsonValueKind.Number));
            }
        });
    }

    private static async Task<(DefaultHttpContext Context, JsonNode Json)> WriteAsync(
        DateTimeOffset now, Action<DefaultHttpContext>? arrange = null)
    {
        var context = new DefaultHttpContext();
        arrange?.Invoke(context);
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await UnityNewsAction.WriteAsync(context.Response, new FixedClock(now), CancellationToken.None);

        return (context, JsonNode.Parse(body.ToArray())!);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
