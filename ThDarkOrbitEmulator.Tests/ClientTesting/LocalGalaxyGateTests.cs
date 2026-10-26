using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalGalaxyGateTests
{
    [Test]
    public async Task SpinsAndActivation_PersistPerAccount()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalGalaxyGateStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.ActivateAsync(account, "alpha", CancellationToken.None), Is.False);
        Assert.That(await store.SpinAsync(account, "alpha", 0, CancellationToken.None), Is.False);
        Assert.That(await store.SpinAsync(account, "unknown", 1, CancellationToken.None), Is.False);
        for (var i = 0; i < 3; i++)
            Assert.That(await store.SpinAsync(account, "alpha", 10, CancellationToken.None), Is.True);
        Assert.That(await store.SpinAsync(account, "alpha", 5, CancellationToken.None), Is.False);
        Assert.That(await store.SpinAsync(account, "alpha", 4, CancellationToken.None), Is.True);
        Assert.That(await store.ActivateAsync(account, "alpha", CancellationToken.None), Is.True);
        Assert.That(await store.ActivateAsync(account, "alpha", CancellationToken.None), Is.False);
        Assert.That(await store.SpinAsync(account, "alpha", 1, CancellationToken.None), Is.False);
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Energy, Is.EqualTo(216));
            Assert.That(state.Gates.Single(gate => gate.Id == "alpha").PartsOwned, Is.EqualTo(34));
            Assert.That(state.Gates.Single(gate => gate.Id == "alpha").Activated, Is.True);
        });
        Assert.That((await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Gates[0].PartsOwned, Is.Zero);
    }

    [Test]
    public async Task ConcurrentSpins_CannotOverspendEnergy()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalGalaxyGateStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            store.SpinAsync(account, "gamma", 10, CancellationToken.None)));
        Assert.That(results.Count(success => success), Is.EqualTo(8));
        var state = await store.GetAsync(account, CancellationToken.None);
        Assert.That(state.Energy, Is.EqualTo(170));
        Assert.That(state.Gates.Single(gate => gate.Id == "gamma").PartsOwned, Is.EqualTo(80));
    }

    [Test]
    public async Task Post_RequiresSessionAndCompany()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var gates = new LocalGalaxyGateStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        var anonymous = Request(null, "internalGalaxyGateSpin", "gate=alpha&amount=1");
        await LocalGalaxyGateAction.HandleAsync(anonymous, sessions, gates, CancellationToken.None);
        Assert.That(anonymous.Response.StatusCode, Is.EqualTo(401));
        var noCompany = Request(session.Dosid, "internalGalaxyGateSpin", "gate=alpha&amount=1");
        await LocalGalaxyGateAction.HandleAsync(noCompany, sessions, gates, CancellationToken.None);
        Assert.That(noCompany.Response.StatusCode, Is.EqualTo(403));
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var bad = Request(session.Dosid, "internalGalaxyGateSpin", "gate=alpha&amount=11");
        await LocalGalaxyGateAction.HandleAsync(bad, sessions, gates, CancellationToken.None);
        Assert.That(bad.Response.StatusCode, Is.EqualTo(400));
        var valid = Request(session.Dosid, "internalGalaxyGateSpin", Form(session.Dosid, "alpha", "1"));
        await LocalGalaxyGateAction.HandleAsync(valid, sessions, gates, CancellationToken.None);
        Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
        Assert.That(valid.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&spun=alpha"));
        var replay = Request(session.Dosid, "internalGalaxyGateSpin", Form(session.Dosid, "alpha", "1",
            valid.Request.Form["key"].ToString()));
        await LocalGalaxyGateAction.HandleAsync(replay, sessions, gates, CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await gates.GetAsync(session.AccountId, CancellationToken.None)).Gates[0].PartsOwned, Is.EqualTo(1));
    }

    [TestCase("gate=alpha&amount=1", 400)]
    [TestCase("gate=alpha&amount=0&key=x", 400)]
    [TestCase("gate=alpha&amount=-1&key=x", 400)]
    [TestCase("gate=alpha&amount=2147483648&key=x", 400)]
    [TestCase("gate=alpha&amount=1&key=x&extra=1", 400)]
    [TestCase("gate=alpha&gate=beta&amount=1&key=x", 400)]
    public async Task MalformedSpin_ReturnsHtmlError(string body, int status)
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var request = Request(session.Dosid, "internalGalaxyGateSpin", body);
        request.Response.Body = new MemoryStream();
        await LocalGalaxyGateAction.HandleAsync(request, sessions, new LocalGalaxyGateStore(cache), CancellationToken.None);
        Assert.That(request.Response.StatusCode, Is.EqualTo(status));
        Assert.That(request.Response.ContentType, Does.StartWith("text/html"));
        Assert.That(request.Response.Body.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task SignedForms_RejectUnknownGateForeignSessionAndExpiredSession()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var first = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "first", "mmo", 7), null, CancellationToken.None)).Session;
        var second = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "second", "eic", 8), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(first.Dosid, "mmo", CancellationToken.None), Is.True);
        Assert.That(await sessions.SelectCompanyAsync(second.Dosid, "eic", CancellationToken.None), Is.True);
        var gates = new LocalGalaxyGateStore(cache);
        var unknown = Request(first.Dosid, "internalGalaxyGateSpin", Form(first.Dosid, "absent", "1"));
        await LocalGalaxyGateAction.HandleAsync(unknown, sessions, gates, CancellationToken.None);
        Assert.That(unknown.Response.StatusCode, Is.EqualTo(404));
        var foreign = Request(second.Dosid, "internalGalaxyGateSpin", Form(first.Dosid, "alpha", "1"));
        await LocalGalaxyGateAction.HandleAsync(foreign, sessions, gates, CancellationToken.None);
        Assert.That(foreign.Response.StatusCode, Is.EqualTo(400));
        await sessions.InvalidateAsync(first.Dosid, CancellationToken.None);
        var expired = Request(first.Dosid, "internalGalaxyGateSpin", Form(first.Dosid, "alpha", "1"));
        await LocalGalaxyGateAction.HandleAsync(expired, sessions, gates, CancellationToken.None);
        Assert.That(expired.Response.StatusCode, Is.EqualTo(401));
        Assert.That((await gates.GetAsync(first.AccountId, CancellationToken.None)).Energy, Is.EqualTo(250));
    }

    [Test]
    public async Task ParallelSubmissionsOfOneForm_SpendOnce()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var gates = new LocalGalaxyGateStore(cache);
        var body = Form(session.Dosid, "alpha", "10");
        var requests = Enumerable.Range(0, 12).Select(_ => Request(session.Dosid, "internalGalaxyGateSpin", body)).ToArray();
        await Task.WhenAll(requests.Select(r => LocalGalaxyGateAction.HandleAsync(r, sessions, gates, CancellationToken.None)));
        Assert.That(requests.Count(r => r.Response.StatusCode == 303), Is.EqualTo(1));
        Assert.That(requests.Count(r => r.Response.StatusCode == 409), Is.EqualTo(11));
        Assert.That((await gates.GetAsync(session.AccountId, CancellationToken.None)).Energy, Is.EqualTo(240));
    }

    [Test]
    public async Task SignedForm_RejectsChangedQuantityAndActivationFields()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var gates = new LocalGalaxyGateStore(cache);
        var key = LocalGalaxyGateAction.CreateFormKey(session.Dosid, "alpha");
        foreach (var amount in new[] { "-1", "0", "11", "999999999999999999999" })
        {
            var request = Request(session.Dosid, "internalGalaxyGateSpin",
                $"gate=alpha&amount={amount}&key={key}");
            await LocalGalaxyGateAction.HandleAsync(request, sessions, gates, CancellationToken.None);
            Assert.That(request.Response.StatusCode, Is.EqualTo(400), amount);
        }
        var tampered = Request(session.Dosid, "internalGalaxyGateSpin",
            $"gate=beta&amount=1&key={key}");
        await LocalGalaxyGateAction.HandleAsync(tampered, sessions, gates, CancellationToken.None);
        Assert.That(tampered.Response.StatusCode, Is.EqualTo(400));
        var extra = Request(session.Dosid, "internalGalaxyGateActivate",
            $"gate=alpha&key={key}&amount=0");
        await LocalGalaxyGateAction.HandleAsync(extra, sessions, gates, CancellationToken.None);
        Assert.That(extra.Response.StatusCode, Is.EqualTo(400));
        var premature = Request(session.Dosid, "internalGalaxyGateActivate",
            $"gate=alpha&key={LocalGalaxyGateAction.CreateFormKey(session.Dosid, "alpha", true)}");
        await LocalGalaxyGateAction.HandleAsync(premature, sessions, gates, CancellationToken.None);
        Assert.That(premature.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await gates.GetAsync(session.AccountId, CancellationToken.None)).Energy, Is.EqualTo(250));
    }

    [Test]
    public async Task InvalidCompany_RejectsSignedForm()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        session.Company = "foreign-company";
        await cache.SetStringAsync($"gameauth:dosid:{session.Dosid}",
            JsonSerializer.Serialize(session, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var request = Request(session.Dosid, "internalGalaxyGateSpin", Form(session.Dosid, "alpha", "1"));
        await LocalGalaxyGateAction.HandleAsync(request, sessions, new LocalGalaxyGateStore(cache), CancellationToken.None);
        Assert.That(request.Response.StatusCode, Is.EqualTo(403));
    }

    private static string Form(string dosid, string gate, string amount, string? key = null) =>
        $"gate={gate}&amount={amount}&key={key ?? LocalGalaxyGateAction.CreateFormKey(dosid, gate)}";

    private static DefaultHttpContext Request(string? dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
