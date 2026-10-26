using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSpaceSearchNoticeTests
{
    [TestCase("action=internalSpaceSearch&explored=mars", true)]
    [TestCase("action=internalSpaceSearch&explored=pluto", true)]
    [TestCase("action=internalSpaceSearch&explored=mars&planet=venus&filter=searched", true)]
    [TestCase("action=internalSpaceSearch&explored=", false)]
    [TestCase("action=internalSpaceSearch&explored=Mars", false)]
    [TestCase("action=internalSpaceSearch&explored=mars1", false)]
    [TestCase("action=internalSpaceSearch&explored=%3Cb%3E", false)]
    [TestCase("action=internalSpaceSearch&explored=mars&explored=venus", false)]
    [TestCase("action=internalSpaceSearch&Explored=mars", false)]
    [TestCase("action=internalSpaceSearch&EXPLORED=mars", false)]
    public void PageQuery_ValidatesExploredNotice(string query, bool expected) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsExploredLength() =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query("action=internalSpaceSearch&explored=" + new string('a', 33))), Is.False);

    [Test]
    public void EverySuccessLocation_PassesPageValidation()
    {
        foreach (var planet in LocalSpaceSearch.Catalog)
        {
            var location = LocalSpaceSearchAction.SuccessLocation(planet.Id);
            Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(location.Split('?', 2)[1])), Is.True, location);
        }
    }

    [Test]
    public async Task Notice_IsBuiltFromSavedState()
    {
        var (store, account) = Store();
        Assert.That(await store.SearchPlanetAsync(account, "mars", CancellationToken.None), Is.True);
        var state = await store.GetAsync(account, CancellationToken.None);
        var notice = LocalSpaceSearch.ExploredNotice(state, "mars");
        Assert.Multiple(() =>
        {
            Assert.That(notice, Does.Contain("id=\"spaceSearchExplored\"").And.Contain("data-planet=\"mars\""));
            Assert.That(notice, Does.Contain("Searched Mars: found 1 LF-3 laser (owned: 5)"));
            Assert.That(notice, Does.Contain("Balance: 9900 local credits").And.Contain("Planets left: 4."));
            Assert.That(LocalSpaceSearch.Page(state, exploredPlanetId: "mars"), Does.Contain(notice));
        });
    }

    [Test]
    public async Task Notice_IsHidden_ForUnsearchedOrUnknownPlanet()
    {
        var (store, account) = Store();
        Assert.That(await store.SearchPlanetAsync(account, "mars", CancellationToken.None), Is.True);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(LocalSpaceSearch.ExploredNotice(state, "venus"), Is.Empty);
            Assert.That(LocalSpaceSearch.ExploredNotice(state, "pluto"), Is.Empty);
            Assert.That(LocalSpaceSearch.Page(state, exploredPlanetId: "venus"), Does.Not.Contain("spaceSearchExplored"));
            Assert.That(LocalSpaceSearch.Page(state), Does.Not.Contain("spaceSearchExplored"));
        });
    }

    [Test]
    public async Task Notice_ReportsShortfall_AndCompletion()
    {
        var (store, account) = Store();
        foreach (var planet in LocalSpaceSearch.Catalog)
            Assert.That(await store.SearchPlanetAsync(account, planet.Id, CancellationToken.None), Is.True, planet.Id);
        var done = await store.GetAsync(account, CancellationToken.None);
        Assert.That(LocalSpaceSearch.ExploredNotice(done, "saturn"), Does.Contain("Every planet has been searched."));

        var partial = done with { SearchedPlanetIds = ["mars"], Credits = 40 };
        Assert.That(LocalSpaceSearch.ExploredNotice(partial, "mars"),
            Does.Contain("Planets left: 4. You need 60 more local credits for the next search."));
    }

    [Test]
    public async Task ExplorePost_RedirectsToNotice_AndRejectedRepeatDoesNotRedirect()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalHangarEquipmentStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);

        var first = Post(session.Dosid, "planet=venus");
        await LocalSpaceSearchAction.HandleAsync(first, sessions, store, CancellationToken.None);
        var repeat = Post(session.Dosid, "planet=venus");
        await LocalSpaceSearchAction.HandleAsync(repeat, sessions, store, CancellationToken.None);
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(303));
            Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalSpaceSearch&explored=venus"));
            Assert.That(repeat.Response.StatusCode, Is.EqualTo(409));
            Assert.That(repeat.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(LocalSpaceSearch.Page(state, exploredPlanetId: "venus"), Does.Contain("Searched Venus: found 1 SG3N-B02 shield"));
        });
    }

    private static (LocalHangarEquipmentStore Store, Guid Account) Store()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        return (new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>()), Guid.NewGuid());
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private static DefaultHttpContext Post(string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
