using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-125: Galaxy Gates spin/activation confirmations. Emulator-defined; no Live Galaxy Gates form was captured.
public class LocalGalaxyGateActionNoticeTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "dosid" };

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private static LocalGalaxyGateState State(int energy = 200, int alphaParts = 9, bool alphaActivated = false,
        bool betaActivated = false) =>
        new(energy, LocalGalaxyGates.Catalog.Select(g => g.Id switch
        {
            "alpha" => g with { PartsOwned = alphaParts, Activated = alphaActivated },
            "beta" when betaActivated => g with { PartsOwned = g.PartsRequired, Activated = true },
            _ => g
        }).ToList());

    [TestCase("?action=internalGalaxyGates&spun=alpha")]
    [TestCase("?action=internalGalaxyGates&activated=epsilon")]
    [TestCase("?action=internalGalaxyGates&gate=beta&spun=alpha")]
    [TestCase("?action=internalGalaxyGates&from=internalStart&activated=gamma")]
    [TestCase("?action=internalGalaxyGates&spun=unknown")]
    public void PageQuery_AcceptsNoticeIds(string query) =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("?action=internalGalaxyGates&spun=alpha&activated=alpha")]
    [TestCase("?action=internalGalaxyGates&spun=alpha&spun=beta")]
    [TestCase("?action=internalGalaxyGates&activated=alpha&activated=alpha")]
    [TestCase("?action=internalGalaxyGates&spun=")]
    [TestCase("?action=internalGalaxyGates&spun=Alpha")]
    [TestCase("?action=internalGalaxyGates&spun=alpha1")]
    [TestCase("?action=internalGalaxyGates&spun=al%20pha")]
    [TestCase("?action=internalGalaxyGates&activated=%3Cscript%3E")]
    [TestCase("?action=internalGalaxyGates&Spun=alpha")]
    [TestCase("?action=internalGalaxyGates&ACTIVATED=alpha")]
    [TestCase("?action=internalGalaxyGates&spun=abcdefghijklmnopqrstuvwxyzabcdefg")]
    [TestCase("?action=internalGalaxyGates&spined=alpha")]
    public void PageQuery_RejectsMalformedNotices(string query) =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void PageQuery_AcceptsMaximumLengthNoticeId() =>
        Assert.That(LocalGalaxyGateAction.ValidPageQuery(
            Query($"?action=internalGalaxyGates&spun={new string('a', 32)}")), Is.True);

    [Test]
    public void RedirectTarget_NamesOperationAndGate()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalGalaxyGateAction.RedirectTarget(true, "alpha"),
                Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&spun=alpha"));
            Assert.That(LocalGalaxyGateAction.RedirectTarget(false, "delta"),
                Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&activated=delta"));
        });
    }

    [Test]
    public void SpunNotice_ShowsSavedPartsAndEnergy()
    {
        var html = LocalGalaxyGates.Page(Session, State(energy: 241, alphaParts: 9), spun: "alpha");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"gateSpun\" role=\"status\" data-gate=\"alpha\""));
            Assert.That(html, Does.Contain("Spin complete on the Alpha gate: 9 / 34 parts. 25 part(s) missing. Energy left: 241."));
            Assert.That(html, Does.Not.Contain("gateActivated"));
        });
    }

    [Test]
    public void SpunNotice_CompleteGatePromptsActivation()
    {
        var html = LocalGalaxyGates.Page(Session, State(energy: 216, alphaParts: 34), spun: "alpha");
        Assert.That(html, Does.Contain("34 / 34 parts. All parts collected. Activate the gate now. Energy left: 216."));
    }

    [Test]
    public void SpunNotice_HiddenOnceGateIsActivated()
    {
        var html = LocalGalaxyGates.Page(Session, State(alphaParts: 34, alphaActivated: true), spun: "alpha");
        Assert.That(html, Does.Not.Contain("gateSpun"));
    }

    [Test]
    public void ActivatedNotice_ShowsActivationCount()
    {
        var html = LocalGalaxyGates.Page(Session, State(alphaParts: 34, alphaActivated: true, betaActivated: true),
            activated: "alpha");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"gateActivated\" role=\"status\" data-gate=\"alpha\""));
            Assert.That(html, Does.Contain("Alpha gate activated. Activated gates: 2 / 5. Activation does not enter the world."));
            Assert.That(html, Does.Not.Contain("gateSpun"));
        });
    }

    [Test]
    public void ActivatedNotice_HiddenWithoutSavedActivation()
    {
        var html = LocalGalaxyGates.Page(Session, State(alphaParts: 34), activated: "alpha");
        Assert.That(html, Does.Not.Contain("gateActivated"));
    }

    [TestCase("unknown")]
    [TestCase("zeta")]
    public void Notices_HiddenForUnknownGates(string gate)
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalGalaxyGates.Page(Session, State(), spun: gate), Does.Not.Contain("role=\"status\""));
            Assert.That(LocalGalaxyGates.Page(Session, State(), activated: gate), Does.Not.Contain("role=\"status\""));
        });
    }

    [Test]
    public void Notice_PrecedesDetailAndKeepsSelection()
    {
        var html = LocalGalaxyGates.Page(Session, State(), "beta", spun: "alpha");
        Assert.Multiple(() =>
        {
            Assert.That(html.IndexOf("id=\"gateSpun\"", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("id=\"gate-detail\"", StringComparison.Ordinal)));
            Assert.That(html, Does.Contain("data-gate=\"beta\" class=\"selected\""));
        });
    }

    [Test]
    public void Notice_EmptyWithoutQueryValues() =>
        Assert.That(LocalGalaxyGates.Notice(State(), null, null), Is.Empty);

    [Test]
    public async Task SpinAndActivate_RedirectToNoticesWithSavedState()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var gates = new LocalGalaxyGateStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);

        foreach (var amount in new[] { 10, 10, 10, 4 })
        {
            var spin = Request(session.Dosid, "internalGalaxyGateSpin",
                $"gate=alpha&amount={amount}&key={LocalGalaxyGateAction.CreateFormKey(session.Dosid, "alpha")}");
            await LocalGalaxyGateAction.HandleAsync(spin, sessions, gates, CancellationToken.None);
            Assert.That(spin.Response.StatusCode, Is.EqualTo(303));
            Assert.That(spin.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&spun=alpha"));
        }
        var afterSpins = await gates.GetAsync(session.AccountId, CancellationToken.None);
        Assert.That(LocalGalaxyGates.Notice(afterSpins, "alpha", null),
            Does.Contain("34 / 34 parts. All parts collected. Activate the gate now. Energy left: 216."));

        var activate = Request(session.Dosid, "internalGalaxyGateActivate",
            $"gate=alpha&key={LocalGalaxyGateAction.CreateFormKey(session.Dosid, "alpha", true)}");
        await LocalGalaxyGateAction.HandleAsync(activate, sessions, gates, CancellationToken.None);
        Assert.That(activate.Response.StatusCode, Is.EqualTo(303));
        Assert.That(activate.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalGalaxyGates&activated=alpha"));
        var activated = await gates.GetAsync(session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(LocalGalaxyGates.Notice(activated, null, "alpha"),
                Does.Contain("Alpha gate activated. Activated gates: 1 / 5."));
            Assert.That(LocalGalaxyGates.Notice(activated, "alpha", null), Is.Empty);
        });
    }

    [Test]
    public async Task FailedSpin_DoesNotRedirect()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var gates = new LocalGalaxyGateStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var early = Request(session.Dosid, "internalGalaxyGateActivate",
            $"gate=alpha&key={LocalGalaxyGateAction.CreateFormKey(session.Dosid, "alpha", true)}");
        await LocalGalaxyGateAction.HandleAsync(early, sessions, gates, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(early.Response.StatusCode, Is.EqualTo(409));
            Assert.That(early.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    private static DefaultHttpContext Request(string dosid, string action, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString($"?action={action}");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
