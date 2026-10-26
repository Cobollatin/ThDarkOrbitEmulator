using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalTechFactoryValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        public TestClock Clock { get; } = new();
        public GameAuthSessionStore Sessions { get; }
        public LocalTechFactoryStore Store { get; }
        public GameAuthSession Session { get; private set; } = null!;

        public Fixture()
        {
            var cache = _services.GetRequiredService<IDistributedCache>();
            Sessions = new GameAuthSessionStore(cache);
            Store = new LocalTechFactoryStore(cache, Clock);
        }

        public async Task<Fixture> InitAsync(string? company = "mmo")
        {
            Session = (await Sessions.CreateSessionAsync(
                new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
            if (company is not null)
                Assert.That(await Sessions.SelectCompanyAsync(Session.Dosid, company, CancellationToken.None), Is.True);
            return this;
        }

        public string StartForm(string recipe, DateTimeOffset? issued = null) =>
            $"recipe={Uri.EscapeDataString(recipe)}&key={LocalTechFactoryAction.CreateFormKey(Session.Dosid, "start", recipe, issued ?? Clock.GetUtcNow())}";

        public string ClaimForm(string job, DateTimeOffset? issued = null) =>
            $"job={Uri.EscapeDataString(job)}&key={LocalTechFactoryAction.CreateFormKey(Session.Dosid, "claim", job, issued ?? Clock.GetUtcNow())}";

        public async Task<DefaultHttpContext> PostAsync(string action, string body, string method = "POST",
            string? query = null, string contentType = "application/x-www-form-urlencoded")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.QueryString = new QueryString(query ?? $"?action={action}");
            context.Request.ContentType = contentType;
            context.Request.Headers.Cookie = $"dosid={Session.Dosid}";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Response.Body = new MemoryStream();
            await LocalTechFactoryAction.HandleAsync(context, Sessions, Store, CancellationToken.None, Clock);
            return context;
        }

        public void Dispose() => _services.Dispose();
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static void AssertError(HttpContext context, int status, string? message = null)
    {
        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        if (message is not null) Assert.That(Body(context), Does.Contain(message));
    }

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task NonPost_Returns405(string method)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("targeting"), method), 405, "POST");
        Assert.That((await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Job, Is.Null);
    }

    [TestCase("?action=internalTechFactoryStart&x=1")]
    [TestCase("?action=internalTechFactoryStart&action=internalTechFactoryStart")]
    [TestCase("?action=internalTechFactoryOther")]
    public async Task BadQuery_Returns400(string query)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("", fixture.StartForm("targeting"), query: query), 400);
    }

    [Test]
    public async Task NonFormBody_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", "{}", contentType: "application/json"), 400, "Submit a Tech Factory form");
    }

    [TestCase("TARGETING")]
    [TestCase("target ing")]
    [TestCase("targeting<script>")]
    [TestCase("")]
    public async Task MalformedRecipe_Returns400WithoutEcho(string recipe)
    {
        using var fixture = await new Fixture().InitAsync();
        var context = await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm(recipe));
        AssertError(context, 400);
        if (recipe.Length > 0) Assert.That(Body(context), Does.Not.Contain(recipe));
    }

    [Test]
    public async Task OverlongRecipe_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm(new string('a', 65))), 400, "Invalid recipe ID");
    }

    [Test]
    public async Task WellFormedUnknownRecipe_Returns404()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("warp_drive")), 404, "does not exist");
    }

    [TestCase("ABCDEF0123456789ABCDEF0123456789")]
    [TestCase("not-a-guid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    [TestCase("0123456789abcdef0123456789abcde")]
    public async Task MalformedJobId_Returns400(string job)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryClaim", fixture.ClaimForm(job)), 400, "Invalid job ID");
    }

    [TestCase("abc")]
    [TestCase("0123456789abcdef0123456789abcdef.0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task MalformedOrOldFormatKey_Returns400(string key)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", $"recipe=targeting&key={key}"), 400, "invalid or has been changed");
    }

    [Test]
    public async Task OverlongKey_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", $"recipe=targeting&key={new string('a', 200)}"), 400);
    }

    [Test]
    public async Task AlteredIssueTime_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        var key = LocalTechFactoryAction.CreateFormKey(fixture.Session.Dosid, "start", "targeting", Start);
        var parts = key.Split('.');
        var altered = $"{parts[0]}.{long.Parse(parts[1]) + 60}.{parts[2]}";
        AssertError(await fixture.PostAsync("internalTechFactoryStart", $"recipe=targeting&key={altered}"), 400);
    }

    [Test]
    public async Task KeySignedForOtherAction_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        var key = LocalTechFactoryAction.CreateFormKey(fixture.Session.Dosid, "claim", "targeting", Start);
        AssertError(await fixture.PostAsync("internalTechFactoryStart", $"recipe=targeting&key={key}"), 400);
    }

    [Test]
    public async Task FutureKey_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("targeting", Start.AddMinutes(10))), 400);
    }

    [Test]
    public async Task SmallClockSkew_IsAccepted()
    {
        using var fixture = await new Fixture().InitAsync();
        Assert.That((await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("targeting", Start.AddMinutes(4)))).Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task ExpiredKey_Returns409AndDoesNotDebit()
    {
        using var fixture = await new Fixture().InitAsync();
        var body = fixture.StartForm("targeting");
        fixture.Clock.Advance(LocalTechFactoryAction.FormLifetime + TimeSpan.FromSeconds(1));
        AssertError(await fixture.PostAsync("internalTechFactoryStart", body), 409, "expired");
        Assert.That((await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Components, Is.EqualTo(1000));
    }

    [Test]
    public async Task KeyAtLifetimeBoundary_IsAccepted()
    {
        using var fixture = await new Fixture().InitAsync();
        var body = fixture.StartForm("targeting");
        fixture.Clock.Advance(LocalTechFactoryAction.FormLifetime);
        Assert.That((await fixture.PostAsync("internalTechFactoryStart", body)).Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task BusySlot_Returns409WithReason()
    {
        using var fixture = await new Fixture().InitAsync();
        Assert.That((await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("targeting"))).Response.StatusCode, Is.EqualTo(303));
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("shield")), 409, "production slot is busy");
        Assert.That((await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Components, Is.EqualTo(900));
    }

    [Test]
    public async Task InsufficientComponents_Returns409WithAmounts()
    {
        using var fixture = await new Fixture().InitAsync();
        // 1000 starting components: energy (300) three times leaves 100, below energy's cost.
        for (var i = 0; i < 3; i++)
        {
            Assert.That((await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("energy"))).Response.StatusCode, Is.EqualTo(303));
            fixture.Clock.Advance(TimeSpan.FromMinutes(6));
            var job = (await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Job!;
            Assert.That((await fixture.PostAsync("internalTechFactoryClaim", fixture.ClaimForm(job.Id.ToString("N")))).Response.StatusCode, Is.EqualTo(303));
        }
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("energy")), 409, "300 needed, 100 available");
        var state = await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Components, Is.EqualTo(100));
            Assert.That(state.Produced["energy"], Is.EqualTo(3));
            Assert.That(state.Job, Is.Null);
        });
    }

    [Test]
    public async Task EarlyClaim_Returns409WithReadyTime()
    {
        using var fixture = await new Fixture().InitAsync();
        Assert.That((await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("speed"))).Response.StatusCode, Is.EqualTo(303));
        var job = (await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Job!;
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        AssertError(await fixture.PostAsync("internalTechFactoryClaim", fixture.ClaimForm(job.Id.ToString("N"))), 409, "not ready until 2026-10-22 00:05 UTC");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That((await fixture.PostAsync("internalTechFactoryClaim", fixture.ClaimForm(job.Id.ToString("N")))).Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task ClaimWithoutJob_Returns409()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalTechFactoryClaim", fixture.ClaimForm(Guid.NewGuid().ToString("N"))), 409, "no matching production job");
    }

    [Test]
    public async Task ClaimReplay_Returns409AndCreditsOnce()
    {
        using var fixture = await new Fixture().InitAsync();
        Assert.That((await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("targeting"))).Response.StatusCode, Is.EqualTo(303));
        var job = (await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Job!;
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var body = fixture.ClaimForm(job.Id.ToString("N"));
        Assert.That((await fixture.PostAsync("internalTechFactoryClaim", body)).Response.StatusCode, Is.EqualTo(303));
        AssertError(await fixture.PostAsync("internalTechFactoryClaim", body), 409);
        Assert.That((await fixture.Store.GetAsync(fixture.Session.AccountId, CancellationToken.None)).Produced["targeting"], Is.EqualTo(1));
    }

    [Test]
    public async Task NoCompany_Returns403()
    {
        using var fixture = await new Fixture().InitAsync(company: null);
        AssertError(await fixture.PostAsync("internalTechFactoryStart", fixture.StartForm("targeting")), 403);
    }

    [Test]
    public void PageQuery_AcceptsShellParametersOnly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?action=internalTechFactory")), Is.True);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?action=internalTechFactory&from=internalStart&prc=1&lang=es")), Is.True);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?action=internalTechFactory&recipe=targeting")), Is.False);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?action=internalTechFactory&lang=es&lang=en")), Is.False);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query($"?action=internalTechFactory&from={new string('a', 65)}")), Is.False);
            Assert.That(LocalTechFactoryAction.ValidPageQuery(Query("?from=internalStart")), Is.False);
        });
    }

    [Test]
    public void Page_ShowsShortfallCountdownAndSignedKeys()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "dosid" };
        var job = new LocalTechJob("targeting", Start.AddMinutes(90).AddSeconds(30), Guid.NewGuid());
        var busy = LocalTechFactory.Page(session, new LocalTechFactoryState(120, new Dictionary<string, int> { ["targeting"] = 2 }, job), Start);
        Assert.Multiple(() =>
        {
            Assert.That(busy, Does.Contain("1 h 31 min left"));
            Assert.That(busy, Does.Contain("Slot busy"));
            Assert.That(busy, Does.Not.Contain("internalTechFactoryClaim"));
            Assert.That(busy, Does.Contain($".{Start.ToUnixTimeSeconds()}."));
        });
        var idle = LocalTechFactory.Page(session, new LocalTechFactoryState(120, new Dictionary<string, int>(), null), Start);
        Assert.Multiple(() =>
        {
            Assert.That(idle, Does.Contain("Need 30 more"));
            Assert.That(idle, Does.Contain("Need 180 more"));
            Assert.That(idle, Does.Contain("Available"));
            Assert.That(idle, Does.Contain("Production slot empty"));
        });
        var ready = LocalTechFactory.Page(session, new LocalTechFactoryState(0, new Dictionary<string, int>(), job), Start.AddHours(2));
        Assert.That(ready, Does.Contain("ready to claim"));
        Assert.That(ready, Does.Contain("action=internalTechFactoryClaim"));
        Assert.That(LocalTechFactory.Page(session, new LocalTechFactoryState(0, new Dictionary<string, int>(), null), Start, []),
            Does.Contain("No production recipes are enabled"));
    }

    [Test]
    public void Page_EncodesRecipeNames()
    {
        var page = LocalTechFactory.Page(new GameAuthSession { Username = "<pilot>", Dosid = "d" },
            new LocalTechFactoryState(1000, new Dictionary<string, int>(), null), Start,
            [new LocalTechRecipe("tech_x", "<b>X</b>", 10, 1)]);
        Assert.That(page, Does.Not.Contain("<b>X</b>"));
        Assert.That(page, Does.Not.Contain("<pilot>"));
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query;

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
