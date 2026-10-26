using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalGalaxyGateValidationTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        public TestClock Clock { get; } = new();
        public GameAuthSessionStore Sessions { get; }
        public LocalGalaxyGateStore Store { get; }
        public GameAuthSession Session { get; private set; } = null!;

        public Fixture()
        {
            var cache = _services.GetRequiredService<IDistributedCache>();
            Sessions = new GameAuthSessionStore(cache);
            Store = new LocalGalaxyGateStore(cache);
        }

        public async Task<Fixture> InitAsync()
        {
            Session = (await Sessions.CreateSessionAsync(
                new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
            Assert.That(await Sessions.SelectCompanyAsync(Session.Dosid, "mmo", CancellationToken.None), Is.True);
            return this;
        }

        public string SpinForm(string gate, string amount, DateTimeOffset? issued = null) =>
            $"gate={Uri.EscapeDataString(gate)}&amount={Uri.EscapeDataString(amount)}&key={LocalGalaxyGateAction.CreateFormKey(Session.Dosid, gate, false, issued ?? Clock.GetUtcNow())}";

        public string ActivateForm(string gate, DateTimeOffset? issued = null) =>
            $"gate={Uri.EscapeDataString(gate)}&key={LocalGalaxyGateAction.CreateFormKey(Session.Dosid, gate, true, issued ?? Clock.GetUtcNow())}";

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
            await LocalGalaxyGateAction.HandleAsync(context, Sessions, Store, CancellationToken.None, Clock);
            return context;
        }

        public async Task<LocalGalaxyGateState> StateAsync() =>
            await Store.GetAsync(Session.AccountId, CancellationToken.None);

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

    [Test]
    public async Task FilteredSpin_KeepsFilterAfterSavedChange()
    {
        using var fixture = await new Fixture().InitAsync();
        var response = await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "1") + "&filter=incomplete");
        Assert.Multiple(() =>
        {
            Assert.That(response.Response.StatusCode, Is.EqualTo(303));
            Assert.That(response.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&spun=alpha&filter=incomplete"));
        });
        Assert.That((await fixture.StateAsync()).Gates.Single(g => g.Id == "alpha").PartsOwned, Is.EqualTo(1));
    }

    [Test]
    public async Task FilteredActivation_RedirectsToReadyListWithSavedNotice()
    {
        using var fixture = await new Fixture().InitAsync();
        foreach (var amount in new[] { 10, 10, 10, 4 })
            Assert.That((await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", amount.ToString())))
                .Response.StatusCode, Is.EqualTo(303));

        var response = await fixture.PostAsync("internalGalaxyGateActivate",
            fixture.ActivateForm("alpha") + "&filter=ready");
        Assert.Multiple(() =>
        {
            Assert.That(response.Response.StatusCode, Is.EqualTo(303));
            Assert.That(response.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&activated=alpha&filter=ready"));
        });
        Assert.That((await fixture.StateAsync()).Gates.Single(g => g.Id == "alpha").Activated, Is.True);
    }

    [TestCase("filter=", 400)]
    [TestCase("filter=Ready", 400)]
    [TestCase("filter=unknown", 400)]
    [TestCase("Filter=incomplete", 400)]
    [TestCase("filter=incomplete&filter=ready", 400)]
    public async Task InvalidFilter_DoesNotSpendEnergy(string filter, int status)
    {
        using var fixture = await new Fixture().InitAsync();
        var response = await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "1") + "&" + filter);
        AssertError(response, status);
        var state = await fixture.StateAsync();
        Assert.Multiple(() =>
        {
            Assert.That(state.Energy, Is.EqualTo(250));
            Assert.That(state.Gates.Single(g => g.Id == "alpha").PartsOwned, Is.Zero);
        });
    }

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task NonPost_Returns405WithoutSpending(string method)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "1"), method), 405, "POST");
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(250));
    }

    [TestCase("?action=internalGalaxyGateSpin&x=1")]
    [TestCase("?action=internalGalaxyGateSpin&action=internalGalaxyGateSpin")]
    [TestCase("?action=internalGalaxyGateOther")]
    [TestCase("?action=INTERNALGALAXYGATESPIN")]
    public async Task BadQuery_Returns400(string query)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "1"), query: query), 400);
    }

    [TestCase("+5")]
    [TestCase("05")]
    [TestCase(" 5")]
    [TestCase("5 ")]
    [TestCase("1.0")]
    [TestCase("١")]
    [TestCase("0")]
    [TestCase("11")]
    [TestCase("100")]
    public async Task MalformedAmount_Returns400(string amount)
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", amount)), 400, "from 1 to 10");
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(250));
    }

    [TestCase("Alpha")]
    [TestCase("alpha ")]
    [TestCase("al-pha")]
    [TestCase("<script>")]
    public async Task MalformedGate_Returns400WithoutEcho(string gate)
    {
        using var fixture = await new Fixture().InitAsync();
        var context = await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm(gate, "1"));
        AssertError(context, 400, "Invalid gate ID.");
        Assert.That(Body(context), Does.Not.Contain("<script>"));
    }

    [Test]
    public async Task OverlongGate_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm(new string('a', 33), "1")), 400);
    }

    [Test]
    public async Task UnreadableOrNonFormBody_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", "{}", contentType: "application/json"), 400, "Submit");
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", "--x\r\nbroken",
            contentType: "multipart/form-data; boundary=x"), 400);
    }

    [Test]
    public async Task ExpiredForm_Returns409BeforeSpending()
    {
        using var fixture = await new Fixture().InitAsync();
        var form = fixture.SpinForm("alpha", "1", fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(LocalGalaxyGateAction.FormLifetime + TimeSpan.FromSeconds(1));
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", form), 409, "expired");
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(250));
    }

    [Test]
    public async Task FormAtLifetimeBoundary_IsAccepted()
    {
        using var fixture = await new Fixture().InitAsync();
        var form = fixture.SpinForm("alpha", "2", fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(LocalGalaxyGateAction.FormLifetime);
        Assert.That((await fixture.PostAsync("internalGalaxyGateSpin", form)).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(248));
    }

    [Test]
    public async Task FutureDatedKey_Returns400()
    {
        using var fixture = await new Fixture().InitAsync();
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin",
            fixture.SpinForm("alpha", "1", fixture.Clock.GetUtcNow().AddMinutes(6))), 400);
        Assert.That((await fixture.PostAsync("internalGalaxyGateSpin",
            fixture.SpinForm("alpha", "1", fixture.Clock.GetUtcNow().AddMinutes(4)))).Response.StatusCode, Is.EqualTo(303));
    }

    [Test]
    public async Task LegacyTamperedAndOverlongKeys_Return400()
    {
        using var fixture = await new Fixture().InitAsync();
        var key = LocalGalaxyGateAction.CreateFormKey(fixture.Session.Dosid, "alpha", false, fixture.Clock.GetUtcNow());
        var parts = key.Split('.');
        var legacy = $"{parts[0]}.{parts[2]}";
        var shifted = $"{parts[0]}.{long.Parse(parts[1]) + 1}.{parts[2]}";
        var padded = $"{parts[0]}.0{parts[1]}.{parts[2]}";
        foreach (var bad in new[] { legacy, shifted, padded, key + new string('a', 100), key.ToUpperInvariant() })
            AssertError(await fixture.PostAsync("internalGalaxyGateSpin", $"gate=alpha&amount=1&key={bad}"), 400);
        // An activation key cannot be used for a spin.
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin",
            $"gate=alpha&amount=1&key={LocalGalaxyGateAction.CreateFormKey(fixture.Session.Dosid, "alpha", true, fixture.Clock.GetUtcNow())}"), 400);
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(250));
    }

    [Test]
    public async Task SpinBeyondMissingParts_Returns409WithRemainingCount()
    {
        using var fixture = await new Fixture().InitAsync();
        for (var i = 0; i < 3; i++)
            Assert.That((await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "10"))).Response.StatusCode,
                Is.EqualTo(303));
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "5")), 409, "Only 4 part(s)");
        AssertError(await fixture.PostAsync("internalGalaxyGateActivate", fixture.ActivateForm("alpha")), 409, "still needs 4 part(s)");
        Assert.That((await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "4"))).Response.StatusCode,
            Is.EqualTo(303));
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "1")), 409, "Activate it instead");
        Assert.That((await fixture.PostAsync("internalGalaxyGateActivate", fixture.ActivateForm("alpha"))).Response.StatusCode,
            Is.EqualTo(303));
        AssertError(await fixture.PostAsync("internalGalaxyGateActivate", fixture.ActivateForm("alpha")), 409, "already activated");
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("alpha", "1")), 409, "already activated");
        var state = await fixture.StateAsync();
        Assert.Multiple(() =>
        {
            Assert.That(state.Energy, Is.EqualTo(250 - 34));
            Assert.That(state.Gates.Single(g => g.Id == "alpha").Activated, Is.True);
        });
    }

    [Test]
    public async Task InsufficientEnergy_Returns400WithAmounts()
    {
        using var fixture = await new Fixture().InitAsync();
        // Drain energy to 5 by completing alpha (34), beta (48), gamma (82) and 81 parts of delta.
        foreach (var (gate, parts) in new[] { ("alpha", 34), ("beta", 48), ("gamma", 82), ("delta", 81) })
            for (var left = parts; left > 0; left -= 10)
                Assert.That((await fixture.PostAsync("internalGalaxyGateSpin",
                    fixture.SpinForm(gate, Math.Min(10, left).ToString()))).Response.StatusCode, Is.EqualTo(303));
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(5));
        AssertError(await fixture.PostAsync("internalGalaxyGateSpin", fixture.SpinForm("epsilon", "6")), 400,
            "6 needed, 5 available");
        Assert.That((await fixture.StateAsync()).Energy, Is.EqualTo(5));
    }

    [Test]
    public void PageQuery_AllowsOnlyShellParameters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query("?action=internalGalaxyGates")), Is.True);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query("?action=internalGalaxyGates&from=internalStart&prc=1&lang=es")), Is.True);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query("?action=internalGalaxyGates&GATE=alpha")), Is.False);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query("?action=internalGalaxyGates&lang=es&lang=en")), Is.False);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query($"?action=internalGalaxyGates&from={new string('a', 65)}")), Is.False);
            Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query("?from=internalStart")), Is.False);
        });
    }

    [Test]
    public void Page_ShowsStatusPerGateAndActivatedCount()
    {
        var session = new GameAuthSession { Username = "<pilot>", Dosid = "dosid" };
        var gates = LocalGalaxyGates.Catalog.Select(g => g.Id switch
        {
            "alpha" => g with { PartsOwned = 34, Activated = true },
            "beta" => g with { PartsOwned = 48 },
            "gamma" => g with { PartsOwned = 80 },
            _ => g
        }).ToList();
        var page = LocalGalaxyGates.Page(session, new LocalGalaxyGateState(3, gates));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Activated gates: 1 / 5"));
            Assert.That(page, Does.Contain("Status: Activated"));
            Assert.That(page, Does.Contain("Status: Ready to activate"));
            Assert.That(page, Does.Contain("Status: 2 parts missing"));
            Assert.That(page, Does.Contain("max=\"2\""));
            Assert.That(page, Does.Contain("max=\"3\""));
            Assert.That(page, Does.Contain("&lt;pilot&gt;"));
            Assert.That(page, Does.Not.Contain("<pilot>"));
        });
        var empty = LocalGalaxyGates.Page(session, new LocalGalaxyGateState(0, [.. LocalGalaxyGates.Catalog]));
        Assert.Multiple(() =>
        {
            Assert.That(empty, Does.Contain("Status: No energy (34 parts missing)"));
            Assert.That(empty, Does.Contain("disabled>Spin"));
        });
    }

    private static IQueryCollection Query(string query) =>
        new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query;

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
