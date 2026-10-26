using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalVoucherTests
{
    [Test]
    public async Task Redemption_IsAccountScoped_OneTime_AndFundsShop()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.RedeemVoucherAsync(account, "training-500", CancellationToken.None), Is.True);
        Assert.That(await store.RedeemVoucherAsync(account, "TRAINING-500", CancellationToken.None), Is.False);
        Assert.That(await store.RedeemVoucherAsync(account, "unknown", CancellationToken.None), Is.False);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.EqualTo(10500));
        Assert.That(await store.PurchaseAsync(account, "laser_lf3", 1, CancellationToken.None), Is.True);
        Assert.That((await store.GetAsync(account, CancellationToken.None)).Credits, Is.LessThan(10500));
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public async Task RedeemPost_RequiresSessionAndCompany_AndRejectsReuse()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null);
        await LocalVoucherAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid);
        await LocalVoucherAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var valid = Request(session.Dosid);
        await LocalVoucherAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalVouchers&redeemed=TRAINING-500"));
        var repeat = Request(session.Dosid);
        await LocalVoucherAction.HandleAsync(repeat, sessions, store, CancellationToken.None);
        Assert.That(repeat.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(10500));
    }

    [Test]
    public async Task FullCreditBalance_HidesRedeem_RejectsWithoutClaim_ThenAllowsRedemption()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);
        state.Credits = int.MaxValue - 250;
        await cache.SetStringAsync($"localhangar:equipment:{session.AccountId:N}",
            System.Text.Json.JsonSerializer.Serialize(state));

        var page = LocalVouchers.Page(await store.GetAsync(session.AccountId, CancellationToken.None), filter: "unavailable");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-voucher=\"TRAINING-500\" data-status=\"balance-limit\""));
            Assert.That(page, Does.Contain("data-voucher=\"HANGAR-750\" data-status=\"balance-limit\""));
            Assert.That(page, Does.Contain("Unavailable (2)"));
            Assert.That(page, Does.Not.Contain("name=\"code\" value=\"TRAINING-500\""));
        });
        var detail = LocalVouchers.DetailPage(await store.GetAsync(session.AccountId, CancellationToken.None),
            null, "unavailable", "TRAINING-500");
        Assert.That(detail, Does.Contain("Spend credits and try again"));
        Assert.That(detail, Does.Not.Contain("voucherDetailRedeem"));
        var blocked = Request(session.Dosid);
        await LocalVoucherAction.HandleAsync(blocked, sessions, store, CancellationToken.None);
        Assert.That(blocked.Response.StatusCode, Is.EqualTo(409));
        Assert.That(blocked.Response.Headers.Location.ToString(), Is.Empty);
        state = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(int.MaxValue - 250));
        Assert.That(state.RedeemedVoucherCodes, Is.Empty);

        state.Credits = int.MaxValue - 500;
        await cache.SetStringAsync($"localhangar:equipment:{session.AccountId:N}",
            System.Text.Json.JsonSerializer.Serialize(state));
        var redeemed = Request(session.Dosid);
        await LocalVoucherAction.HandleAsync(redeemed, sessions, store, CancellationToken.None);
        Assert.That(redeemed.Response.StatusCode, Is.EqualTo(303));
        state = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(int.MaxValue));
        Assert.That(state.RedeemedVoucherCodes, Is.EquivalentTo(new[] { "TRAINING-500" }));
    }

    private static DefaultHttpContext Request(string? dosid)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes("code=TRAINING-500");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
