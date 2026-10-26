using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Darkorbit22BpsecureCom;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarTests
{
    [Test]
    public async Task LoginSummary_UsesSavedLanguageAndServesBothLocalShipImages()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var accountId = Guid.NewGuid();
        var preferences = new LocalAccountPreferencesStore(services.GetRequiredService<IDistributedCache>());
        Assert.That(await preferences.SaveAsync(accountId, "de", "true", "true", CancellationToken.None), Is.True);
        var session = new GameAuthSession { AccountId = accountId, GameUserId = 7, Company = "mmo" };
        var request = new DefaultHttpContext().Request;
        var saved = await preferences.GetAsync(accountId, CancellationToken.None);
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, request, 2, saved.Language))!;
        Assert.That(payload["lang"]!.GetValue<string>(), Is.EqualTo("de"));
        Assert.That(payload["hangars"]!["data"]!["hangars"]![1]!["hangarActive"]!.GetValue<bool>(), Is.True);
        Assert.That(payload["hangars"]!["data"]!["hangars"]![0]!["tooltipIcon"]!.GetValue<string>(),
            Does.StartWith(LocalShipIcons.PusatPath));
        Assert.That(payload["hangars"]!["data"]!["hangars"]![1]!["tooltipIcon"]!.GetValue<string>(),
            Is.EqualTo(LocalShipIcons.PhoenixPath));
        Assert.That(LocalShipIcons.Pusat, Has.Length.GreaterThan(100));
        Assert.That(LocalShipIcons.Phoenix, Has.Length.GreaterThan(100));
        Assert.That(LocalShipIcons.Pusat.Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.That(LocalShipIcons.Pusat.Skip(16).Take(8), Is.EqualTo(new byte[] { 0, 0, 0, 100, 0, 0, 0, 100 }));
        Assert.That(LocalShipIcons.Pusat, Is.Not.EqualTo(LocalShipIcons.Phoenix));
        Assert.That(DecodeImageData(LocalShipIcons.Pusat), Has.Length.EqualTo(100 * (1 + 100 * 4)));
        Assert.That(DecodeImageData(LocalShipIcons.Phoenix), Has.Length.EqualTo(100 * (1 + 100 * 4)));
    }

    [Test]
    public async Task ActiveSlot_PersistsByAccount_AndRejectsInvalidSelection()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var store = new LocalHangarStore(cache);

        Assert.That(await store.GetActiveSlotAsync(first, CancellationToken.None), Is.EqualTo(1));
        Assert.That(await store.SelectAsync(first, 3, CancellationToken.None), Is.False);
        Assert.That(await store.SelectAsync(Guid.Empty, 2, CancellationToken.None), Is.False);
        Assert.That(await store.SelectAsync(first, 2, CancellationToken.None), Is.True);
        Assert.That(await new LocalHangarStore(cache).GetActiveSlotAsync(first, CancellationToken.None), Is.EqualTo(2));
        Assert.That(await store.GetActiveSlotAsync(second, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public void DockSelection_MatchesCapturedLoginHangarShape()
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "local", Company = "vru" };
        var hangars = LocalHangars.For(session, 2);
        var context = new DefaultHttpContext();
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request, 2))!;
        var wireHangars = payload["hangars"]!["data"]!["hangars"]!.AsArray();

        Assert.That(hangars, Has.Count.EqualTo(2));
        Assert.That(hangars.Select(h => h.HangarId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(hangars.All(h => h.HangarId.Length == 6 && h.HangarId.All(char.IsDigit)), Is.True);
        Assert.That(hangars.Count(h => h.HangarActive), Is.EqualTo(1));
        Assert.That(hangars[1].HangarActive, Is.True);
        Assert.That(wireHangars, Has.Count.EqualTo(2));
        Assert.That(wireHangars[1]!["hangarActive"]!.GetValue<bool>(), Is.True);
        Assert.That(wireHangars[0]!["hangarActive"]!.GetValue<bool>(), Is.False);
        Assert.That(wireHangars[1]!["mapId"]!.GetValue<string>(), Is.EqualTo("9"));
        Assert.That(wireHangars[0]!.AsObject().Select(p => p.Key), Is.EquivalentTo(new[]
        {
            "slotNumber", "shipId", "modelId", "hangarId", "shipName", "mapId",
            "shipDestroyed", "hangarActive", "tooltipIcon"
        }));
        Assert.That(LocalHangars.Page(session, 2), Does.Contain("value=\"1\""));
    }

    [TestCase(7L, "000007", "100007")]
    [TestCase(899999L, "899999", "999999")]
    [TestCase(900000L, "000000", "100000")]
    [TestCase(long.MaxValue, null, null)]
    [TestCase(long.MinValue, null, null)]
    public void LoginHangarIds_RemainDistinctSixDigitStringsAtAccountIdLimits(
        long gameUserId, string? expectedFirst, string? expectedSecond)
    {
        var session = new GameAuthSession { AccountId = Guid.NewGuid(), GameUserId = gameUserId, Company = "mmo" };
        var local = LocalHangars.For(session, 1);
        var json = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, new DefaultHttpContext().Request))!;
        var wire = json["hangars"]!["data"]!["hangars"]!.AsArray();
        var wireIds = wire.Select(h => h!["hangarId"]!.GetValue<string>()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(wireIds, Is.EqualTo(local.Select(h => h.HangarId)));
            Assert.That(wireIds, Has.Length.EqualTo(2));
            Assert.That(wireIds.Distinct().ToArray(), Has.Length.EqualTo(2));
            Assert.That(wireIds.All(id => id.Length == 6 && id.All(char.IsAsciiDigit)), Is.True);
            if (expectedFirst is not null) Assert.That(wireIds[0], Is.EqualTo(expectedFirst));
            if (expectedSecond is not null) Assert.That(wireIds[1], Is.EqualTo(expectedSecond));
        });
    }

    [Test]
    public async Task SelectionAction_RequiresCompanyAndOwnedSlot_ThenUpdatesLoginData()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var hangars = new LocalHangarStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;

        var noCookie = SelectionRequest(null, "slot=2");
        await LocalHangarSelection.HandleAsync(noCookie, sessions, hangars, CancellationToken.None);
        Assert.That(noCookie.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));

        var noCompany = SelectionRequest(session.Dosid, "slot=2");
        await LocalHangarSelection.HandleAsync(noCompany, sessions, hangars, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);

        var invalid = SelectionRequest(session.Dosid, "slot=3");
        await LocalHangarSelection.HandleAsync(invalid, sessions, hangars, CancellationToken.None);
        Assert.That(invalid.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None), Is.EqualTo(1));

        var signed = $"slot=2&current=1&key={LocalHangars.CreateFormKey(session.Dosid, 2, 1)}";
        var valid = SelectionRequest(session.Dosid, signed);
        await LocalHangarSelection.HandleAsync(valid, sessions, hangars, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalDock&activated=2"));

        var fresh = await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None);
        var login = JsonNode.Parse(UnityLoginPayloadBuilder.Build(fresh!, valid.Request,
            await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None)))!;
        Assert.That(login["hangars"]!["data"]!["hangars"]![1]!["hangarActive"]!.GetValue<bool>(), Is.True);

        var replay = SelectionRequest(session.Dosid, signed);
        await LocalHangarSelection.HandleAsync(replay, sessions, hangars, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        foreach (var body in new[] { "slot=-1", "slot=2147483648", "slot=2&slot=1", "slot=1&account=other", "" })
        {
            var tampered = SelectionRequest(session.Dosid, body);
            await LocalHangarSelection.HandleAsync(tampered, sessions, hangars, CancellationToken.None);
            Assert.That(tampered.Response.StatusCode, Is.EqualTo(400), body);
        }
        await sessions.InvalidateAsync(session.Dosid, CancellationToken.None);
        var expired = SelectionRequest(session.Dosid, "slot=1");
        await LocalHangarSelection.HandleAsync(expired, sessions, hangars, CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None), Is.EqualTo(2));
    }

    [Test]
    public async Task DockSelection_GuardsMethodQuerySessionAndCompanyBeforeReadingForm()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var hangars = new LocalHangarStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;

        var cases = new (string Method, string Query, string? Dosid, int Status)[]
        {
            ("GET", "?action=internalDockSelect", null, 405),
            ("POST", "?action=other", null, 404),
            ("POST", "?action=internalDockSelect&extra=1", null, 400),
            ("POST", "?ACTION=internalDockSelect", null, 400),
            ("POST", "?action=internalDockSelect", null, 401),
            ("POST", "?action=internalDockSelect", session.Dosid, 403)
        };
        foreach (var (method, query, dosid, status) in cases)
        {
            var context = SelectionRequest(dosid, "slot=2");
            context.Request.Method = method;
            context.Request.QueryString = new QueryString(query);
            context.Request.ContentType = "text/plain";
            await LocalHangarSelection.HandleAsync(context, sessions, hangars, CancellationToken.None);
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), $"{method} {query}");
        }
        Assert.That(await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task DockSelection_FormLifetimeAndForwardSkewAreInclusiveAtTheirLimits()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var hangars = new LocalHangarStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        async Task<int> Submit(DateTimeOffset issued, DateTimeOffset at)
        {
            var key = LocalHangars.CreateFormKey(session.Dosid, 2, 1, issued);
            var context = SelectionRequest(session.Dosid, $"slot=2&current=1&key={key}");
            await LocalHangarSelection.HandleAsync(context, sessions, hangars, CancellationToken.None,
                new FixedClock(at));
            return context.Response.StatusCode;
        }

        Assert.That(await Submit(now - LocalHangars.FormLifetime - TimeSpan.FromSeconds(1), now), Is.EqualTo(409));
        Assert.That(await Submit(now + LocalHangars.ClockSkew + TimeSpan.FromSeconds(1), now), Is.EqualTo(400));
        Assert.That(await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None), Is.EqualTo(1));
        Assert.That(await Submit(now - LocalHangars.FormLifetime, now), Is.EqualTo(303));
        Assert.That(await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None), Is.EqualTo(2));

        Assert.That(await hangars.ChangeAsync(account.Id, 1, CancellationToken.None), Is.EqualTo(LocalHangarChange.Applied));
        Assert.That(await Submit(now + LocalHangars.ClockSkew, now), Is.EqualTo(303));
        Assert.That(await hangars.GetActiveSlotAsync(account.Id, CancellationToken.None), Is.EqualTo(2));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DefaultHttpContext SelectionRequest(string? dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalDockSelect");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null)
            context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private static byte[] DecodeImageData(byte[] png)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16)
                | (png[offset + 2] << 8) | png[offset + 3];
            var kind = Encoding.ASCII.GetString(png, offset + 4, 4);
            offset += 8;
            if (kind == "IDAT")
            {
                using var data = new MemoryStream(png, offset, length);
                using var inflater = new ZLibStream(data, CompressionMode.Decompress);
                using var pixels = new MemoryStream();
                inflater.CopyTo(pixels);
                return pixels.ToArray();
            }
            offset += length + 4;
        }
        Assert.Fail("PNG image has no IDAT chunk");
        return [];
    }
}
