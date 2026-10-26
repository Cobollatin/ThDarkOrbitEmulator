using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSpaceSearchValidationTests
{
    private ServiceProvider _services = null!;
    private IDistributedCache _cache = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        _cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(_cache);
        _store = new LocalHangarEquipmentStore(_cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [TestCase("planet=")]
    [TestCase("")]
    [TestCase("planet=mars&planet=mars")]
    [TestCase("planet=mars&planet=venus")]
    [TestCase("planet=mars&credits=99999")]
    [TestCase("planet=mars&quantity=5")]
    [TestCase("planet=Mars")]
    [TestCase("planet=..%2Fmars")]
    [TestCase("planet=mars%00")]
    [TestCase("planet=-1")]
    [TestCase("planet=%3Cscript%3Emars")]
    [TestCase("planet=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Explore_RejectsTamperedForms_WithoutChangingState(string body)
    {
        var context = await SendAsync(body);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(await BodyAsync(context), Does.Contain("Space Search request failed (400)").And.Not.Contain("<script>"));
        await AssertUnchangedAsync();
    }

    [TestCase("lunar")]
    [TestCase("pluto")]
    public async Task UnknownPlanet_Returns404(string planet)
    {
        var context = await SendAsync($"planet={planet}");
        Assert.That(context.Response.StatusCode, Is.EqualTo(404));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ExtraQueryParameters_Return400()
    {
        var context = Request("planet=mars", _session.Dosid);
        context.Request.QueryString = new QueryString("?action=internalSpaceSearchExplore&planet=venus");
        await LocalSpaceSearchAction.HandleAsync(context, _sessions, _store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task GetMethod_Returns405_WithAllowHeader()
    {
        var context = Request("planet=mars", _session.Dosid);
        context.Request.Method = "GET";
        await LocalSpaceSearchAction.HandleAsync(context, _sessions, _store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task NonFormBody_Returns400()
    {
        var context = Request("{\"planet\":\"mars\"}", _session.Dosid);
        context.Request.ContentType = "application/json";
        await LocalSpaceSearchAction.HandleAsync(context, _sessions, _store, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task NoCompany_Returns403_AndExpiredSession_Returns401()
    {
        var other = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "other", null, 8), null, CancellationToken.None)).Session;
        var noCompany = Request("planet=mars", other.Dosid);
        await LocalSpaceSearchAction.HandleAsync(noCompany, _sessions, _store, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        var expired = Request("planet=mars", "not-a-session");
        await LocalSpaceSearchAction.HandleAsync(expired, _sessions, _store, CancellationToken.None);
        var body = await BodyAsync(expired);
        Assert.Multiple(() =>
        {
            Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
            Assert.That(expired.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(body, Does.Contain("Sign in again"));
        });
    }

    [Test]
    public async Task ReplayedSearch_Returns409_AndDebitsOnce()
    {
        Assert.That((await SendAsync("planet=venus")).Response.StatusCode, Is.EqualTo(303));
        var replay = await SendAsync("planet=venus");
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(10000 - LocalSpaceSearch.SearchCost));
            Assert.That(state.Inventory.Single(i => i.Id == "shield_sg3n_b02").Quantity, Is.EqualTo(5));
            Assert.That(state.SearchedPlanetIds, Is.EquivalentTo(new[] { "venus" }));
        });
    }

    [Test]
    public async Task ConcurrentSearches_ApplyExactlyOnce()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => SendAsync("planet=earth")));
        Assert.That(results.Count(c => c.Response.StatusCode == 303), Is.EqualTo(1));
        Assert.That(results.Count(c => c.Response.StatusCode == 409), Is.EqualTo(15));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(10000 - LocalSpaceSearch.SearchCost));
            Assert.That(state.Inventory.Single(i => i.Id == "drone_flax").Quantity, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task InsufficientCredits_Returns409_AndKeepsBalance()
    {
        await MutateAsync(state => state.Credits = LocalSpaceSearch.SearchCost - 1);
        Assert.That(LocalSpaceSearch.Page(await _store.GetAsync(_session.AccountId, CancellationToken.None)),
            Does.Contain("Not enough local credits").And.Not.Contain("<button"));
        var context = await SendAsync("planet=mars");
        Assert.That(context.Response.StatusCode, Is.EqualTo(409));
        Assert.That(await BodyAsync(context), Does.Contain("enough local credits"));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(LocalSpaceSearch.SearchCost - 1));
        Assert.That(state.SearchedPlanetIds, Is.Empty);
    }

    [Test]
    public async Task ExactBalance_AllowsSearch_ReachingZero()
    {
        await MutateAsync(state => state.Credits = LocalSpaceSearch.SearchCost);
        Assert.That((await SendAsync("planet=mars")).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await _store.GetAsync(_session.AccountId, CancellationToken.None)).Credits, Is.Zero);
        Assert.That((await SendAsync("planet=venus")).Response.StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task FullStack_Returns409_WithoutDebit()
    {
        await MutateAsync(state =>
        {
            var index = state.Inventory.FindIndex(i => i.Id == "laser_lf3");
            state.Inventory[index] = state.Inventory[index] with { Quantity = int.MaxValue };
        });
        var context = await SendAsync("planet=mars");
        Assert.That(context.Response.StatusCode, Is.EqualTo(409));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
        Assert.That(state.SearchedPlanetIds, Is.Empty);
    }

    [Test]
    public async Task UnownedReward_RendersAndGrantsNewInventoryRow()
    {
        await MutateAsync(state => state.Inventory.RemoveAll(i => i.Id == "laser_lf2"));
        var page = LocalSpaceSearch.Page(await _store.GetAsync(_session.AccountId, CancellationToken.None));
        Assert.That(page, Does.Contain("LF-2 laser").And.Contain("data-planet=\"jupiter\""));
        Assert.That((await SendAsync("planet=jupiter")).Response.StatusCode, Is.EqualTo(303));
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Inventory.Single(i => i.Id == "laser_lf2").Quantity, Is.EqualTo(1));
            Assert.That(state.Inventory.Single(i => i.Id == "laser_lf2").Category, Is.EqualTo("Equipment"));
            Assert.That(state.Credits, Is.EqualTo(10000 - LocalSpaceSearch.SearchCost));
        });
    }

    [Test]
    public async Task AllPlanets_CanBeSearchedOnce_AndPageCountsProgress()
    {
        foreach (var planet in LocalSpaceSearch.Catalog)
            Assert.That((await SendAsync($"planet={planet.Id}")).Response.StatusCode, Is.EqualTo(303), planet.Id);
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(LocalSpaceSearch.Catalog, Has.Count.EqualTo(5));
            Assert.That(LocalSpaceSearch.Catalog.Select(p => p.Id), Is.Unique);
            Assert.That(LocalSpaceSearch.Catalog.All(p => LocalSpaceSearch.IsPlanetIdShape(p.Id)), Is.True);
            Assert.That(state.Credits, Is.EqualTo(10000 - 5 * LocalSpaceSearch.SearchCost));
            Assert.That(state.SearchedPlanetIds, Is.EquivalentTo(LocalSpaceSearch.Catalog.Select(p => p.Id)));
            Assert.That(LocalSpaceSearch.Page(state), Does.Contain("Searched 5 of 5 planets").And.Not.Contain("<button"));
        });
    }

    [Test]
    public async Task EmptyAccount_IsRejected_ByStore()
    {
        Assert.That(await _store.SearchPlanetResultAsync(Guid.Empty, "mars", CancellationToken.None),
            Is.EqualTo(LocalSpaceSearchResult.Rejected));
        Assert.That(await _store.SearchPlanetAsync(Guid.Empty, "mars", CancellationToken.None), Is.False);
    }

    private async Task AssertUnchangedAsync()
    {
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.That(state.Credits, Is.EqualTo(10000));
        Assert.That(state.SearchedPlanetIds, Is.Empty);
        Assert.That(state.Inventory.Single(i => i.Id == "laser_lf3").Quantity, Is.EqualTo(4));
    }

    private async Task MutateAsync(Action<LocalEquipmentState> change)
    {
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        change(state);
        await _cache.SetStringAsync($"localhangar:equipment:{_session.AccountId:N}", JsonSerializer.Serialize(state));
    }

    private async Task<DefaultHttpContext> SendAsync(string body)
    {
        var context = Request(body, _session.Dosid);
        await LocalSpaceSearchAction.HandleAsync(context, _sessions, _store, CancellationToken.None);
        return context;
    }

    private static async Task<string> BodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static DefaultHttpContext Request(string body, string? dosid)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalSpaceSearchExplore");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
