using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class SpaceSearchCandidateActionTests
{
    private static readonly string[] AllPlanets = LocalSpaceSearch.Catalog.Select(planet => planet.Id).ToArray();
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
    public async Task Search_WithoutSession_ListsTheWholeCatalog()
    {
        var (context, json) = await SearchAsync("", dosid: null);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(Strings(json["planets"]), Is.EqualTo(AllPlanets));
            Assert.That(Strings(json["rewards"]), Is.EqualTo(LocalSpaceSearch.Catalog.Select(planet => planet.RewardItemId)));
            Assert.That(Strings(json["events"]), Is.EqualTo(new[] { SpaceSearchCandidateAction.EventId }));
        });
    }

    [Test]
    public async Task Search_WithSession_ListsOnlyUnsearchedPlanetsInCatalogOrder()
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "venus", CancellationToken.None), Is.True);
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "jupiter", CancellationToken.None), Is.True);
        var (_, json) = await SearchAsync("");
        Assert.Multiple(() =>
        {
            Assert.That(Strings(json["planets"]), Is.EqualTo(new[] { "mars", "earth", "saturn" }));
            Assert.That(Strings(json["rewards"]), Is.EqualTo(new[] { "laser_lf3", "drone_flax", "shield_sg3n_a03" }));
            Assert.That(Strings(json["events"]), Is.EqualTo(new[] { SpaceSearchCandidateAction.EventId }));
        });
    }

    [Test]
    public async Task Search_AfterEveryPlanet_ReportsNoEventOrPlanets()
    {
        foreach (var planet in AllPlanets)
            Assert.That(await _store.SearchPlanetAsync(_session.AccountId, planet, CancellationToken.None), Is.True);
        var (context, json) = await SearchAsync("");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(json["planets"]!.AsArray(), Is.Empty);
            Assert.That(json["rewards"]!.AsArray(), Is.Empty);
            Assert.That(json["events"]!.AsArray(), Is.Empty);
        });
    }

    [TestCase("unknown-dosid")]
    [TestCase("   ")]
    public async Task Search_UnknownOrBlankSession_FallsBackToTheCatalog(string dosid)
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var (context, json) = await SearchAsync("", dosid);
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        Assert.That(Strings(json["planets"]), Is.EqualTo(AllPlanets));
    }

    [Test]
    public async Task Search_SessionWithoutCompany_FallsBackToTheCatalog()
    {
        var other = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "rookie", null, 8), null, CancellationToken.None)).Session;
        var (_, json) = await SearchAsync("", other.Dosid);
        Assert.That(Strings(json["planets"]), Is.EqualTo(AllPlanets));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("MMO")]
    [TestCase("unknown")]
    public async Task Candidates_ResolvedSessionWithoutCatalogCompany_ProjectsAnonymousData(string? company)
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var sessions = new CountingSessionStore(new GameAuthSession
        {
            AccountId = _session.AccountId,
            Company = company,
            Dosid = _session.Dosid
        });

        var (searchContext, search) = await RunAsync("", _session.Dosid,
            SpaceSearchCandidateAction.HandleSearchAsync, sessions);
        var (rewardContext, reward) = await RunAsync("", _session.Dosid,
            SpaceSearchCandidateAction.HandleRewardsAsync, sessions);

        Assert.Multiple(() =>
        {
            Assert.That(sessions.LookupCount, Is.EqualTo(2));
            Assert.That(searchContext.Response.StatusCode, Is.EqualTo(200));
            Assert.That(rewardContext.Response.StatusCode, Is.EqualTo(200));
            Assert.That(searchContext.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(rewardContext.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(Strings(search["planets"]), Is.EqualTo(AllPlanets));
            Assert.That(reward["name"]!.GetValue<string>(), Is.EqualTo("Mars"));
        });
    }

    [TestCase(false, 128, 1)]
    [TestCase(true, 128, 1)]
    [TestCase(false, 129, 0)]
    [TestCase(true, 129, 0)]
    public async Task Candidates_BoundCookieLookupAndUseAnonymousProjectionForOverlongTokens(
        bool rewards, int length, int expectedLookups)
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var selectedSession = await _sessions.TryGetByDosidAsync(_session.Dosid, CancellationToken.None);
        Assert.That(selectedSession, Is.Not.Null);
        var sessions = new CountingSessionStore(selectedSession!);
        var token = new string('a', length);
        var (context, json) = rewards
            ? await RunAsync("", token, SpaceSearchCandidateAction.HandleRewardsAsync, sessions)
            : await RunAsync("", token, SpaceSearchCandidateAction.HandleSearchAsync, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(sessions.LookupCount, Is.EqualTo(expectedLookups));
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(rewards ? json["name"]!.GetValue<string>() : Strings(json["planets"])[0],
                Is.EqualTo(length == 128 ? (rewards ? "Venus" : "venus") : (rewards ? "Mars" : "mars")));
        });
    }

    [Test]
    public async Task Search_ToleratesSingleUnknownKeys()
    {
        var (context, _) = await SearchAsync("?lang=en&v=1");
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    [TestCase("?lang=en&lang=de")]
    [TestCase("?v=%0A")]
    [TestCase("?%01=1")]
    public async Task Search_RepeatedOrControlCharacterQuery_Returns400(string query)
    {
        var (context, json) = await SearchAsync(query);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(json["status"]!.GetValue<int>(), Is.EqualTo(400));
            Assert.That(json.AsObject().Select(pair => pair.Key), Is.EquivalentTo(new[] { "error", "status" }));
        });
    }

    [Test]
    public async Task Search_OverlongValue_Returns400()
    {
        var (context, _) = await SearchAsync("?v=" + new string('a', SpaceSearchCandidateAction.MaxQueryValueLength + 1));
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        var (ok, _) = await SearchAsync("?v=" + new string('a', SpaceSearchCandidateAction.MaxQueryValueLength));
        Assert.That(ok.Response.StatusCode, Is.EqualTo(200));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Candidates_AcceptExactQueryBoundsAndIgnoreUnknownEmptyValues(bool rewards)
    {
        var key = new string('k', SpaceSearchCandidateAction.MaxQueryValueLength);
        var value = new string('v', SpaceSearchCandidateAction.MaxQueryValueLength);
        var (context, _) = rewards
            ? await RewardsAsync($"?{key}={value}&unused=")
            : await SearchAsync($"?{key}={value}&unused=");
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Candidates_CountDecodedUnicodeQueryTextInUtf16CodeUnits(bool rewards)
    {
        var atLimit = Uri.EscapeDataString(string.Concat(Enumerable.Repeat("🚀", 32)));
        var overLimit = Uri.EscapeDataString(string.Concat(Enumerable.Repeat("🚀", 33)));

        foreach (var query in new[] { $"?{atLimit}=1", $"?x={atLimit}" })
        {
            var (context, _) = rewards ? await RewardsAsync(query) : await SearchAsync(query);
            Assert.That(context.Response.StatusCode, Is.EqualTo(200), query);
        }

        foreach (var query in new[] { $"?{overLimit}=1", $"?x={overLimit}" })
        {
            var (context, json) = rewards ? await RewardsAsync(query) : await SearchAsync(query);
            Assert.Multiple(() =>
            {
                Assert.That(context.Response.StatusCode, Is.EqualTo(400), query);
                Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(rewards
                    ? "Invalid Space Search reward request."
                    : "Invalid Space Search request."), query);
                Assert.That(json["status"]!.GetValue<int>(), Is.EqualTo(400), query);
            });
        }
    }

    [TestCase("?PLANET=mars")]
    [TestCase("?%70lanet=mars")]
    public async Task Rewards_DecodedCaseInsensitivePlanetKeySelectsExplicitPlanet(string query)
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var (context, json) = await RewardsAsync(query);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(json["name"]!.GetValue<string>(), Is.EqualTo("Mars"));
            Assert.That(json["item"]!.GetValue<string>(), Is.EqualTo("laser_lf3"));
        });
    }

    [TestCase(false, "?=value")]
    [TestCase(true, "?=value")]
    [TestCase(false, "?%0A=value")]
    [TestCase(true, "?%0A=value")]
    [TestCase(false, "?x=%0A")]
    [TestCase(true, "?x=%0A")]
    [TestCase(false, "?x=1&x=2")]
    [TestCase(true, "?x=1&x=2")]
    public async Task Candidates_RejectMalformedUnknownQueryKeysAndValues(bool rewards, string query)
    {
        var (context, json) = rewards ? await RewardsAsync(query) : await SearchAsync(query);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(json.AsObject().Select(pair => pair.Key), Is.EquivalentTo(new[] { "error", "status" }));
            Assert.That(json["status"]!.GetValue<int>(), Is.EqualTo(400));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Candidates_RejectOverlongUnknownKeyBeforeReadingProgress(bool rewards)
    {
        var key = new string('k', SpaceSearchCandidateAction.MaxQueryValueLength + 1);
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var (context, json) = rewards
            ? await RewardsAsync($"?{key}=1")
            : await SearchAsync($"?{key}=1");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(rewards
                ? "Invalid Space Search reward request."
                : "Invalid Space Search request."));
            Assert.That(json.ToJsonString(), Does.Not.Contain("mars"));
        });
    }

    [Test]
    public async Task Rewards_InvalidQueryPrecedesUnknownPlanetAndAccountProgress()
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var (context, json) = await RewardsAsync("?planet=pluto&planet=mars");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo("Invalid Space Search reward request."));
            Assert.That(json.ToJsonString(), Does.Not.Contain("pluto").And.Not.Contain("mars"));
        });
    }

    [TestCase(false, "?x=1&X=2", "Invalid Space Search request.")]
    [TestCase(true, "?planet=mars&%70lanet=venus", "Invalid Space Search reward request.")]
    [TestCase(false, "?x=" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Invalid Space Search request.")]
    [TestCase(true, "?x=" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Invalid Space Search reward request.")]
    [TestCase(true, "?planet=Mars", "The planet value is invalid.")]
    [TestCase(true, "?planet=pluto", "That planet is not part of Space Search.")]
    public async Task RejectedRequests_DoNotResolveTheSession(bool rewards, string query, string error)
    {
        var sessions = new RejectingSessionStore();
        var (context, json) = rewards
            ? await RunAsync(query, _session.Dosid, SpaceSearchCandidateAction.HandleRewardsAsync, sessions)
            : await RunAsync(query, _session.Dosid, SpaceSearchCandidateAction.HandleSearchAsync, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(sessions.LookupCount, Is.Zero);
            Assert.That(context.Response.StatusCode, Is.EqualTo(error.StartsWith("That planet", StringComparison.Ordinal) ? 404 : 400));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(json.AsObject().Select(field => field.Key), Is.EquivalentTo(new[] { "error", "status" }));
            Assert.That(json["error"]!.GetValue<string>(), Is.EqualTo(error));
            Assert.That(json["status"]!.GetValue<int>(), Is.EqualTo(context.Response.StatusCode));
            Assert.That(json.ToJsonString(), Does.Not.Contain(_session.Dosid));
        });
    }

    [Test]
    public async Task Rewards_ForPlanet_DescribeThatPlanetsReward()
    {
        var (context, json) = await RewardsAsync("?planet=earth");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(json["name"]!.GetValue<string>(), Is.EqualTo("Earth"));
            Assert.That(json["item"]!.GetValue<string>(), Is.EqualTo("drone_flax"));
            Assert.That(json["title"]!.GetValue<string>(), Is.Not.Empty);
            Assert.That(json["package"]!.GetValue<string>(), Is.EqualTo(SpaceSearchCandidateAction.EventId));
            Assert.That(json["special"]!.GetValue<bool>(), Is.False);
            Assert.That(json["time"]!.GetValue<int>(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Rewards_WithoutPlanet_DescribeTheNextUnsearchedPlanet()
    {
        var (_, anonymous) = await RewardsAsync("", dosid: null);
        Assert.That(anonymous["name"]!.GetValue<string>(), Is.EqualTo("Mars"));

        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var (_, next) = await RewardsAsync("");
        Assert.That(next["name"]!.GetValue<string>(), Is.EqualTo("Venus"));
        Assert.That(next["item"]!.GetValue<string>(), Is.EqualTo("shield_sg3n_b02"));
    }

    [Test]
    public async Task Rewards_AfterEveryPlanet_FallBackToTheFirstPlanet()
    {
        foreach (var planet in AllPlanets)
            Assert.That(await _store.SearchPlanetAsync(_session.AccountId, planet, CancellationToken.None), Is.True);
        var (_, json) = await RewardsAsync("");
        Assert.That(json["name"]!.GetValue<string>(), Is.EqualTo("Mars"));
    }

    [Test]
    public async Task Rewards_ExplicitSearchedPlanet_RemainsAvailableWhileOmittedPlanetAdvances()
    {
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        var (_, explicitReward) = await RewardsAsync("?planet=mars");
        var (_, nextReward) = await RewardsAsync("");
        Assert.Multiple(() =>
        {
            Assert.That(explicitReward["name"]!.GetValue<string>(), Is.EqualTo("Mars"));
            Assert.That(explicitReward["item"]!.GetValue<string>(), Is.EqualTo("laser_lf3"));
            Assert.That(nextReward["name"]!.GetValue<string>(), Is.EqualTo("Venus"));
        });
    }

    [Test]
    public async Task Search_ProgressIsScopedToTheResolvedAccount()
    {
        var second = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "other-pilot", null, 9), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(second.Dosid, "eic", CancellationToken.None), Is.True);
        Assert.That(await _store.SearchPlanetAsync(_session.AccountId, "mars", CancellationToken.None), Is.True);
        Assert.That(await _store.SearchPlanetAsync(second.AccountId, "venus", CancellationToken.None), Is.True);

        var (_, firstSearch) = await SearchAsync("");
        var (_, secondSearch) = await SearchAsync("", second.Dosid);
        var (_, firstReward) = await RewardsAsync("");
        var (_, secondReward) = await RewardsAsync("", second.Dosid);
        Assert.Multiple(() =>
        {
            Assert.That(Strings(firstSearch["planets"]), Is.EqualTo(AllPlanets.Where(id => id != "mars")));
            Assert.That(Strings(secondSearch["planets"]), Is.EqualTo(AllPlanets.Where(id => id != "venus")));
            Assert.That(firstReward["name"]!.GetValue<string>(), Is.EqualTo("Venus"));
            Assert.That(secondReward["name"]!.GetValue<string>(), Is.EqualTo("Mars"));
        });
    }

    [Test]
    public void Reward_TitleUsesFirstOwnedItemNameAndFallsBackToCatalog()
    {
        var state = new LocalEquipmentState(
            [new("laser_lf3", "Custom laser", "Equipment", 1),
                new("laser_lf3", "Later name", "Equipment", 1)], []);
        var mars = LocalSpaceSearch.Find("mars")!;
        var venus = LocalSpaceSearch.Find("venus")!;
        Assert.Multiple(() =>
        {
            Assert.That(SpaceSearchCandidateAction.Reward(mars, state).Title, Is.EqualTo("Custom laser"));
            Assert.That(SpaceSearchCandidateAction.Reward(venus, state).Title, Is.EqualTo(venus.RewardName));
        });
    }

    [TestCase("?planet=")]
    [TestCase("?planet=Mars")]
    [TestCase("?planet=mars1")]
    [TestCase("?planet=mars&planet=venus")]
    [TestCase("?planet=abcdefghijklmnopqrstuvwxyzabcdefg")]
    public async Task Rewards_MalformedPlanet_Returns400WithoutEcho(string query)
    {
        var (context, json) = await RewardsAsync(query);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(json.ToJsonString(), Does.Not.Contain("Mars").And.Not.Contain("mars1").And.Not.Contain("venus"));
        });
    }

    [Test]
    public async Task Rewards_UnknownPlanet_Returns404()
    {
        var (context, json) = await RewardsAsync("?planet=pluto");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(404));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(json["status"]!.GetValue<int>(), Is.EqualTo(404));
            Assert.That(json.ToJsonString(), Does.Not.Contain("pluto"));
        });
    }

    [Test]
    public async Task Candidates_NeverChangeTheAccount()
    {
        var before = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        await SearchAsync("");
        await RewardsAsync("?planet=saturn");
        var after = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(after.Credits, Is.EqualTo(before.Credits));
            Assert.That(after.SearchedPlanetIds, Is.Empty);
            Assert.That(after.Inventory.Sum(item => item.Quantity), Is.EqualTo(before.Inventory.Sum(item => item.Quantity)));
        });
    }

    private Task<(HttpContext, JsonNode)> SearchAsync(string query, string? dosid = "") =>
        RunAsync(query, dosid, SpaceSearchCandidateAction.HandleSearchAsync);

    private Task<(HttpContext, JsonNode)> RewardsAsync(string query, string? dosid = "") =>
        RunAsync(query, dosid, SpaceSearchCandidateAction.HandleRewardsAsync);

    // dosid "" means the signed-in test session; null sends no cookie.
    private async Task<(HttpContext, JsonNode)> RunAsync(string query, string? dosid,
        Func<HttpContext, IGameAuthSessionStore, LocalHangarEquipmentStore, CancellationToken, Task> handler,
        IGameAuthSessionStore? sessions = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={(dosid.Length == 0 ? _session.Dosid : dosid)}";
        context.Response.Body = new MemoryStream();
        await handler(context, sessions ?? _sessions, _store, CancellationToken.None);
        context.Response.Body.Position = 0;
        return (context, JsonNode.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync())!);
    }

    private static string[] Strings(JsonNode? node) =>
        node!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

    private sealed class RejectingSessionStore : IGameAuthSessionStore
    {
        public int LookupCount { get; private set; }

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
        {
            LookupCount++;
            throw new AssertionException("Rejected requests must not resolve a session.");
        }

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account,
            string? keycloakAccessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingSessionStore : IGameAuthSessionStore
    {
        private readonly GameAuthSession _session;

        public CountingSessionStore(GameAuthSession session) => _session = session;

        public int LookupCount { get; private set; }

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult<GameAuthSession?>(_session);
        }

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account,
            string? keycloakAccessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
