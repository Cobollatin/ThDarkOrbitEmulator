using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalShopPurchaseHardeningTests
{
    private const string Dosid = "session-one";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestCase("1", 1)]
    [TestCase("7", 7)]
    [TestCase("100", 100)]
    public void ReadOrder_AcceptsPlainQuantities(string quantity, int expected)
    {
        var check = Read(Order(quantity: quantity), out var order);
        Assert.Multiple(() =>
        {
            Assert.That(check, Is.EqualTo(LocalShopPurchase.OrderCheck.Valid));
            Assert.That(order.Quantity, Is.EqualTo(expected));
        });
    }

    [TestCase("+5")]
    [TestCase("05")]
    [TestCase("005")]
    [TestCase(" 5")]
    [TestCase("5 ")]
    [TestCase("1e2")]
    [TestCase("5.0")]
    [TestCase("٥")]
    [TestCase("５")]
    [TestCase("101")]
    [TestCase("0")]
    [TestCase("")]
    [TestCase("1000")]
    public void ReadOrder_RejectsNonCanonicalQuantities(string quantity) =>
        Assert.That(Read(Order(quantity: quantity), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));

    [TestCase(" laser_lf3", "credits")]
    [TestCase("laser_lf3 ", "credits")]
    [TestCase("laser\nlf3", "credits")]
    [TestCase("laser_lf3", " credits")]
    [TestCase("laser_lf3", "cred\u0000its")]
    [TestCase("laser_lf3", "")]
    public void ReadOrder_RejectsPaddedOrControlCharacterFields(string item, string currency) =>
        Assert.That(Read(Order(item: item, currency: currency, key: LocalShopPurchase.CreateFormKey(Dosid, item, Now)), out _),
            Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));

    [Test]
    public void ReadOrder_RejectsOverlongFields()
    {
        var item = new string('a', 101);
        Assert.Multiple(() =>
        {
            Assert.That(Read(Order(item: item, key: LocalShopPurchase.CreateFormKey(Dosid, item, Now)), out _),
                Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
            Assert.That(Read(Order(currency: new string('c', 101)), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
            var longest = new string('a', 100);
            Assert.That(Read(Order(item: longest, key: LocalShopPurchase.CreateFormKey(Dosid, longest, Now)), out _),
                Is.EqualTo(LocalShopPurchase.OrderCheck.Valid));
        });
    }

    [Test]
    public void ReadOrder_ExpiresKeysAfterOneHourAndRejectsFutureKeys()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Read(Order(key: Key(Now - LocalShopPurchase.FormLifetime)), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Valid));
            Assert.That(Read(Order(key: Key(Now - LocalShopPurchase.FormLifetime - TimeSpan.FromSeconds(1))), out _),
                Is.EqualTo(LocalShopPurchase.OrderCheck.Expired));
            Assert.That(Read(Order(key: Key(Now.AddMinutes(5))), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Valid));
            Assert.That(Read(Order(key: Key(Now.AddMinutes(5).AddSeconds(1))), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
        });
    }

    [Test]
    public void ReadOrder_RejectsLegacyAlteredAndForeignKeys()
    {
        var key = Key(Now);
        var parts = key.Split('.');
        var shiftedIssue = $"{parts[0]}.{long.Parse(parts[1]) - 1}.{parts[2]}";
        var paddedIssue = $"{parts[0]}.0{parts[1]}.{parts[2]}";
        var upperSignature = $"{parts[0]}.{parts[1]}.{parts[2].ToUpperInvariant()}";
        var legacy = $"{parts[0]}.{parts[2]}";
        Assert.Multiple(() =>
        {
            foreach (var bad in new[] { shiftedIssue, paddedIssue, upperSignature, legacy, key + ".x", "", new string('a', 200) })
                Assert.That(Read(Order(key: bad), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid), bad);
            Assert.That(LocalShopPurchase.ReadOrder(Order(key: key), "other-session", Now, out _, out _, out _, out _),
                Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
            Assert.That(Read(Order(item: "drone_flax", key: key), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
        });
    }

    [Test]
    public void ReadOrder_BindsKeyToSelectedCurrencyButAllowsChosenQuantity()
    {
        var creditsKey = LocalShopPurchase.CreateFormKey(Dosid, "laser_lf3", Now, "credits");
        var uridiumKey = LocalShopPurchase.CreateFormKey(Dosid, "laser_lf3", Now, "uridium");
        Assert.Multiple(() =>
        {
            Assert.That(Read(Order(currency: "uridium", key: creditsKey), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
            Assert.That(Read(Order(currency: "credits", key: uridiumKey), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));
            Assert.That(Read(Order(currency: "uridium", quantity: "100", key: uridiumKey), out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Valid));
        });
    }

    [TestCase("253402300800")]
    [TestCase("999999999999")]
    public async Task RoutedPost_RejectsSignedOutOfRangeTimestampWithoutServerError(string seconds)
    {
        var (context, sessions, equipment, economy, session) = await ArrangeAsync();
        var key = SignedKey(session.Dosid, "laser_lf3", seconds);
        Assert.That(LocalShopPurchase.ReadOrder(Order(key: key), session.Dosid, Now,
            out _, out _, out _, out _), Is.EqualTo(LocalShopPurchase.OrderCheck.Invalid));

        SetBody(context, OrderBody(key));
        await LocalShopPurchase.HandleAsync(context, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(Body(context), Does.Contain("purchase form is invalid"));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [Test]
    public void ShopPageKey_RoundTripsThroughOrderValidation()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = Dosid, Company = "mmo" };
        var listing = new EconomyListing("laser_lf3", "laser_lf3", "LF-3 laser", "laser", "rare",
            JsonSerializer.Deserialize<JsonElement>("{}"), null, JsonSerializer.Deserialize<JsonElement>("{\"credits\":1500}"), 3, 5, true);
        var page = LocalShop.PageFromDatabase(session, [listing], [new EconomyBalance("credits", "currency", 7000)],
            new Dictionary<string, long>(), null, "laser_lf3", new Dictionary<string, long>());
        var key = Regex.Match(page, "name=\"idempotencyKey\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.That(LocalShopPurchase.TryReadOrder(Order(key: WebUtility.HtmlDecode(key)), Dosid, out _, out _, out _, out _), Is.True);
    }

    [Test]
    public void ShopPage_SignsEachCurrencyOptionSeparately()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = Dosid, Company = "mmo" };
        var listing = new EconomyListing("laser_lf3", "laser_lf3", "LF-3 laser", "laser", "rare",
            JsonSerializer.Deserialize<JsonElement>("{}"), null,
            JsonSerializer.Deserialize<JsonElement>("{\"credits\":1500,\"uridium\":50}"), null, null, true);
        var page = LocalShop.PageFromDatabase(session, [listing],
            [new EconomyBalance("credits", "currency", 7000), new EconomyBalance("uridium", "currency", 500)],
            new Dictionary<string, long>(), null, null);
        var options = Regex.Matches(page, "name=\"currency\" value=\"([^\"]+)\"[^>]*>.*?name=\"idempotencyKey\" value=\"([^\"]+)\"", RegexOptions.Singleline);
        Assert.That(options.Count, Is.EqualTo(2));
        foreach (Match option in options)
        {
            var currency = option.Groups[1].Value;
            var key = WebUtility.HtmlDecode(option.Groups[2].Value);
            Assert.That(LocalShopPurchase.TryReadOrder(Order(currency: currency, key: key), Dosid,
                out _, out _, out _, out _), Is.True);
            Assert.That(LocalShopPurchase.TryReadOrder(Order(currency: currency == "credits" ? "uridium" : "credits", key: key), Dosid,
                out _, out _, out _, out _),
                Is.False);
        }
    }

    [Test]
    public async Task RoutedPost_RejectsCurrencyChangedAfterRendering()
    {
        var (context, sessions, equipment, economy, session) = await ArrangeAsync();
        var creditsKey = LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3", Now, "credits");
        SetBody(context, $"item=laser_lf3&quantity=1&currency=uridium&idempotencyKey={creditsKey}");
        await LocalShopPurchase.HandleAsync(context, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(context), Does.Contain("purchase form is invalid"));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task RoutedPost_RejectsNonPostMethods(string method)
    {
        var (context, sessions, equipment, economy, _) = await ArrangeAsync();
        context.Request.Method = method;
        await LocalShopPurchase.HandleAsync(context, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
            Assert.That(Body(context), Does.Contain("Shop request failed (405)"));
        });
    }

    [TestCase("?action=internalShopBuy&from=internalShop")]
    [TestCase("?action=internalShopBuy&action=internalShopBuy")]
    [TestCase("?action=internalshopbuy")]
    [TestCase("?action=internalShopBuy&item=laser_lf3")]
    [TestCase("")]
    public async Task RoutedPost_RejectsUnexpectedQueries(string query)
    {
        var (context, sessions, equipment, economy, _) = await ArrangeAsync();
        context.Request.QueryString = new QueryString(query);
        await LocalShopPurchase.HandleAsync(context, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(context), Does.Contain("Invalid Shop action."));
        });
    }

    [Test]
    public async Task RoutedPost_ReturnsConflictForExpiredFormBeforeDatabaseAccess()
    {
        var (context, sessions, equipment, economy, session) = await ArrangeAsync();
        SetBody(context, OrderBody(LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3", Now.AddHours(-2))));
        await LocalShopPurchase.HandleAsync(context, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(Body(context), Does.Contain("expired. Reload Shop"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [Test]
    public async Task RoutedPost_RejectsNonFormBodiesAndNeverEchoesInput()
    {
        var (json, sessions, equipment, economy, session) = await ArrangeAsync();
        SetBody(json, "{\"item\":\"laser_lf3\"}");
        json.Request.ContentType = "application/json";
        await LocalShopPurchase.HandleAsync(json, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.That(json.Response.StatusCode, Is.EqualTo(400));

        var hostile = Request(session.Dosid, $"item=%3Cscript%3Ex%3C%2Fscript%3E&quantity=1&currency=credits&idempotencyKey={Key(Now)}");
        await LocalShopPurchase.HandleAsync(hostile, sessions, equipment, economy, new TestClock(Now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(hostile.Response.StatusCode, Is.EqualTo(400));
            Assert.That(Body(hostile), Does.Not.Contain("script"));
            Assert.That(Body(hostile), Does.Contain("Shop request failed (400)"));
            Assert.That(hostile.Response.ContentType, Does.StartWith("text/html"));
        });
    }

    // Arranges a signed-in mmo pilot and a valid purchase form; the economy store has no database.
    private static async Task<(DefaultHttpContext, GameAuthSessionStore, LocalHangarEquipmentStore, EconomyStore, GameAuthSession)> ArrangeAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(new AccountSummary(Guid.NewGuid(), "pilot", null, 42), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var context = Request(session.Dosid, OrderBody(LocalShopPurchase.CreateFormKey(session.Dosid, "laser_lf3", Now)));
        return (context, sessions, new LocalHangarEquipmentStore(cache), new EconomyStore(new ConfigurationBuilder().Build()), session);
    }

    private static LocalShopPurchase.OrderCheck Read(IFormCollection form, out (string Item, int Quantity, string Currency) order)
    {
        var check = LocalShopPurchase.ReadOrder(form, Dosid, Now, out var item, out var quantity, out var currency, out _);
        order = (item, quantity, currency);
        return check;
    }

    private static string Key(DateTimeOffset issued) => LocalShopPurchase.CreateFormKey(Dosid, "laser_lf3", issued);

    private static string SignedKey(string dosid, string item, string seconds)
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"shop:{item}:credits:{nonce}:{seconds}"))).ToLowerInvariant();
        return $"{nonce}.{seconds}.{signature}";
    }

    private static IFormCollection Order(string item = "laser_lf3", string quantity = "2", string currency = "credits", string? key = null) =>
        new FormCollection(new Dictionary<string, StringValues>
        {
            ["item"] = item, ["quantity"] = quantity, ["currency"] = currency, ["idempotencyKey"] = key ?? Key(Now)
        });

    private static string OrderBody(string key) => $"item=laser_lf3&quantity=1&currency=credits&idempotencyKey={key}";

    private static DefaultHttpContext Request(string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalShopBuy");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();
        SetBody(context, body);
        return context;
    }

    private static void SetBody(DefaultHttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
