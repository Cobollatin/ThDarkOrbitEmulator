using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAuctionFormKeyTests
{
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _store = new LocalHangarEquipmentStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task Bid_WithValidKey_IsApplied()
    {
        var context = await PostAsync("internalAuctionBid",
            $"item=drone_flax&amount=200&key={Key("bid", "drone_flax")}");
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(state.Credits, Is.EqualTo(9800));
        });
    }

    [TestCase("internalAuctionBid", "item=drone_flax&amount=200")]
    [TestCase("internalAuctionClaim", "item=drone_flax")]
    public async Task MissingKey_Returns400AndAppliesNothing(string action, string body)
    {
        var context = await PostAsync(action, body);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(context), Does.Contain("missing, repeated, or unexpected"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task RepeatedKey_Returns400()
    {
        var key = Key("bid", "drone_flax");
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={key}&key={key}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task KeyForAnotherLot_Returns400()
    {
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={Key("bid", "laser_lf3")}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(context), Does.Contain("invalid or has been changed"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ClaimKeyUsedForBid_Returns400()
    {
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={Key("claim", "drone_flax")}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task KeyFromAnotherSession_Returns400()
    {
        var other = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "other", null, 8), null, CancellationToken.None)).Session;
        var key = Uri.EscapeDataString(LocalAuctionAction.CreateFormKey(other.Dosid, "bid", "drone_flax"));
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={key}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [TestCase("not-a-key")]
    [TestCase("")]
    [TestCase("0123456789abcdef0123456789abcdef.1.zz")]
    public async Task MalformedKey_Returns400(string key)
    {
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={Uri.EscapeDataString(key)}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task TamperedSignature_Returns400()
    {
        var key = LocalAuctionAction.CreateFormKey(_session.Dosid, "bid", "drone_flax");
        var tampered = key[..^1] + (key[^1] == '0' ? '1' : '0');
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={tampered}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task OversizedKey_Returns400()
    {
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={new string('a', 161)}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ExpiredKey_Returns409AndAppliesNothing()
    {
        var key = Uri.EscapeDataString(LocalAuctionAction.CreateFormKey(_session.Dosid, "bid", "drone_flax",
            DateTimeOffset.UtcNow - LocalAuctionAction.FormLifetime - TimeSpan.FromMinutes(1)));
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={key}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(409));
        Assert.That(Body(context), Does.Contain("expired"));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task FutureKeyBeyondClockSkew_Returns400()
    {
        var key = Uri.EscapeDataString(LocalAuctionAction.CreateFormKey(_session.Dosid, "bid", "drone_flax",
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30)));
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={key}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task MissingSession_Returns401BeforeKeyCheck()
    {
        var context = await PostAsync("internalAuctionBid", "item=drone_flax&amount=200&key=bad", "not-a-session");
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task LocalPage_BidKeyIsAcceptedAndClaimFormIsSigned()
    {
        var page = LocalAuction.Page(_session, await _store.GetAsync(_session.AccountId, CancellationToken.None), DateTimeOffset.UtcNow);
        var key = FormKey(page, "internalAuctionBid", "drone_flax");
        var context = await PostAsync("internalAuctionBid", $"item=drone_flax&amount=200&key={Uri.EscapeDataString(key)}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        // The same key is bound to drone_flax and cannot bid on another lot.
        var reused = await PostAsync("internalAuctionBid", $"item=laser_lf3&amount=700&key={Uri.EscapeDataString(key)}");
        Assert.That(reused.Response.StatusCode, Is.EqualTo(400));

        var later = DateTimeOffset.UtcNow.AddMinutes(10);
        var ended = LocalAuction.Page(_session, await _store.GetAsync(_session.AccountId, CancellationToken.None), later);
        Assert.That(FormKey(ended, "internalAuctionClaim", "drone_flax"), Is.Not.Empty);
    }

    [Test]
    public void DatabasePage_SignsBidAndClaimForms()
    {
        var now = DateTimeOffset.UtcNow;
        var html = LocalAuction.PageFromDatabase(_session,
        [
            new EconomyAuctionLot("open_lot", "laser_lf3", "Open", 900, null, null, false, true),
            new EconomyAuctionLot("ended_lot", "drone_flax", "Ended", 200, 250, now.AddMinutes(-1), false, true)
        ], [new EconomyBalance("credits", "currency", 1000)], now);
        Assert.Multiple(() =>
        {
            Assert.That(FormKey(html, "internalAuctionBid", "open_lot"), Is.Not.Empty);
            Assert.That(FormKey(html, "internalAuctionClaim", "ended_lot"), Is.Not.Empty);
        });
    }

    private static string FormKey(string html, string action, string lot)
    {
        var match = Regex.Match(html, $"action={action}\\\"><input type=\\\"hidden\\\" name=\\\"item\\\" value=\\\"{lot}\\\"><input type=\\\"hidden\\\" name=\\\"key\\\" value=\\\"([^\\\"]+)\\\">");
        Assert.That(match.Success, Is.True, $"{action} form for {lot} has no key");
        return match.Groups[1].Value;
    }

    private string Key(string operation, string lot) =>
        Uri.EscapeDataString(LocalAuctionAction.CreateFormKey(_session.Dosid, operation, lot));

    private async Task<DefaultHttpContext> PostAsync(string action, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalAuctionAction.HandleAsync(context, _sessions, _store, TimeProvider.System, CancellationToken.None);
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private async Task AssertUnchangedAsync()
    {
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
    }
}
