using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSpaceSearchTests
{
    [Test]
    public async Task Search_DebitsOnce_GrantsHangarItem_AndIsAccountScoped()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.SearchPlanetAsync(account, "mars", CancellationToken.None), Is.True);
        Assert.That(await store.SearchPlanetAsync(account, "mars", CancellationToken.None), Is.False);
        Assert.That(await store.SearchPlanetAsync(account, "unknown", CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(9900));
            Assert.That(state.Inventory.Single(item => item.Id == "laser_lf3").Quantity, Is.EqualTo(5));
            Assert.That(state.SearchedPlanetIds, Is.EquivalentTo(new[] { "mars" }));
            Assert.That(LocalSpaceSearch.Page(state), Does.Contain("Searched"));
        });
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Credits, Is.EqualTo(10000));
    }

    [Test]
    public async Task ExplorePost_RequiresSessionAndCompany_AndRejectsRepeat()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null);
        await LocalSpaceSearchAction.HandleAsync(anonymous, sessions, store, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid);
        await LocalSpaceSearchAction.HandleAsync(noCompany, sessions, store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var valid = Request(session.Dosid);
        await LocalSpaceSearchAction.HandleAsync(valid, sessions, store, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalSpaceSearch&explored=mars"));
        var repeat = Request(session.Dosid);
        await LocalSpaceSearchAction.HandleAsync(repeat, sessions, store, CancellationToken.None);
        Assert.That(repeat.Response.StatusCode, Is.EqualTo(409));
    }

    private static DefaultHttpContext Request(string? dosid)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes("planet=mars");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
