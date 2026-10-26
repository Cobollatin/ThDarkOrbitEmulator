using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalPaymentTests
{
    [Test]
    public async Task PackageGrant_IsOncePerAccount_AndFundsShop()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.ClaimPaymentPackageAsync(account, "hangar", CancellationToken.None), Is.True);
        Assert.That(await store.ClaimPaymentPackageAsync(account, "hangar", CancellationToken.None), Is.False);
        Assert.That(await store.ClaimPaymentPackageAsync(account, "missing", CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.EqualTo(13000));
        Assert.That(await store.PurchaseAsync(account, "pet_10", 1, CancellationToken.None), Is.True);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.EqualTo(10000));
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public async Task ClaimPost_RequiresSessionAndCompany_AndRejectsRepeat()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null);
        await LocalPaymentAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid);
        await LocalPaymentAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var valid = Request(session.Dosid);
        await LocalPaymentAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalPayment&claimed=starter"));
        var repeat = Request(session.Dosid);
        await LocalPaymentAction.HandleAsync(repeat, sessions, store, CancellationToken.None);
        Assert.That(repeat.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(11000));
    }

    [TestCase("")]
    [TestCase("package=")]
    [TestCase("package=starter&package=hangar")]
    [TestCase("package=starter&quantity=2")]
    [TestCase("package=starter&amount=-1000")]
    [TestCase("package=star%20ter")]
    [TestCase("package=../starter")]
    [TestCase("package=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ClaimPost_RejectsTamperedForm_WithHtml400(string body)
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var context = Request(session.Dosid, body);
        await LocalPaymentAction.HandleAsync(context, sessions, store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(await BodyAsync(context), Does.Contain("Payment request failed (400)"));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public async Task ClaimPost_RejectsExtraQuery_UnknownPackage_AndInvalidCompany()
    {
        var (sessions, store, session) = await CompanySessionAsync();
        var extra = Request(session.Dosid);
        extra.Request.QueryString = new QueryString("?action=internalPaymentClaim&package=armada");
        await LocalPaymentAction.HandleAsync(extra, sessions, store, CancellationToken.None);
        Assert.That(extra.Response.StatusCode, Is.EqualTo(400));
        var unknown = Request(session.Dosid, "package=platinum");
        await LocalPaymentAction.HandleAsync(unknown, sessions, store, CancellationToken.None);
        Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
        var wrongCase = Request(session.Dosid, "package=STARTER");
        await LocalPaymentAction.HandleAsync(wrongCase, sessions, store, CancellationToken.None);
        Assert.That(wrongCase.Response.StatusCode, Is.EqualTo(404));
        var expired = Request("expired-or-foreign-dosid");
        await LocalPaymentAction.HandleAsync(expired, sessions, store, CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        Assert.That(await BodyAsync(expired), Does.Contain("expired"));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public async Task ConcurrentClaims_GrantOnce_AndCacheExpiryResetsOnlyLocalState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            store.ClaimPaymentPackageResultAsync(account, "armada", CancellationToken.None)));
        Assert.That(results.Count(result => result == PaymentClaimResult.Applied), Is.EqualTo(1));
        Assert.That(results.Count(result => result == PaymentClaimResult.AlreadyClaimed), Is.EqualTo(15));
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(35000));
        Assert.That(state.ClaimedPaymentPackages, Is.EqualTo(new[] { "armada" }));
        Assert.That(await store.ClaimPaymentPackageResultAsync(Guid.Empty, "armada", CancellationToken.None),
            Is.EqualTo(PaymentClaimResult.Invalid));
        Assert.That(await store.ClaimPaymentPackageResultAsync(account, "", CancellationToken.None),
            Is.EqualTo(PaymentClaimResult.Invalid));
    }

    [Test]
    public async Task Claim_NeverOverflowsCredits_AndKeepsInventory()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var store = new LocalHangarEquipmentStore(cache);
        var account = Guid.NewGuid();
        var state = await store.GetAsync(account, CancellationToken.None);
        var inventory = state.Inventory.Sum(item => item.Quantity);
        state.Credits = int.MaxValue - 500;
        await cache.SetStringAsync($"localhangar:equipment:{account:N}", System.Text.Json.JsonSerializer.Serialize(state));
        var loaded = await store.GetAsync(account, CancellationToken.None);
        Assume.That(loaded.Credits, Is.EqualTo(int.MaxValue - 500), "cache key layout changed");
        Assert.That(await store.ClaimPaymentPackageResultAsync(account, "starter", CancellationToken.None),
            Is.EqualTo(PaymentClaimResult.BalanceLimit));
        loaded = await store.GetAsync(account, CancellationToken.None);
        Assert.That(loaded.Credits, Is.EqualTo(int.MaxValue - 500));
        Assert.That(loaded.ClaimedPaymentPackages, Is.Empty);
        Assert.That(loaded.Inventory.Sum(item => item.Quantity), Is.EqualTo(inventory));
    }

    [Test]
    public void Page_ShowsSummary_AndEscapesOfferNames()
    {
        var state = new LocalEquipmentState([], []) { Credits = 13000, ClaimedPaymentPackages = ["hangar"] };
        var html = LocalPayment.Page(state);
        Assert.That(html, Does.Contain("Claimed 1 of 5 packages (3,000 credits)"));
        Assert.That(html, Does.Contain("Armada supply"));
        Assert.That(html, Does.Contain("Balance: 13,000 local credits"));
        Assert.That(LocalPayment.Catalog.All(package => LocalPayment.IsWellFormed(package.Id)
            && package.Credits > 0 && package.Credits <= LocalPayment.MaxGrant), Is.True);
        Assert.That(LocalPayment.Catalog.Select(package => package.Id).Distinct().Count(), Is.EqualTo(LocalPayment.Catalog.Count));
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalHangarEquipmentStore Store, GameAuthSession Session)> CompanySessionAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session);
    }

    private static async Task<string> BodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static DefaultHttpContext Request(string? dosid, string body = "package=starter")
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
