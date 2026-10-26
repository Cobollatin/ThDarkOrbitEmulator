using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-70: drives the real GET /indexInternal.es endpoint for each menu detail view, so the
// 400 -> 401 -> 403 (-> 404) routing order is checked end to end rather than only in page helpers.
// Only the error paths are exercised; the 200 pages need Postgres balances (integration tests).
[TestFixture]
public sealed class LocalMenuDetailRoutingTests
{
    // (action, detail key, a well-formed id for that key) for pages that check the query before the session.
    private static readonly object[] DetailViews =
    [
        new object[] { "internalGalaxyGates", "gate", "alpha" },
        new object[] { "internalAuction", "lot", "lot-1" },
        new object[] { "internalVouchers", "voucher", "welcome" },
        new object[] { "internalAssembly", "detail", "lf3" },
        new object[] { "internalQuests", "detail", "first_purchase" },
        new object[] { "internalSkylab", "detail", "prometium" },
        // Menu-71: Map overview now validates its query before the session as well.
        new object[] { "internalMapOverview", "map", "1" },
        // Menu-72: Command Center now validates its query before the session as well.
        new object[] { "internalCommandCenter", "item", "Voyagers2607" },
        // Menu-73: Hall of Fame and Logbook now validate their query before the session as well.
        new object[] { "internalHallofFame", "pilot", "Draco-EIC" },
        new object[] { "internalLogbook", "entry", "7-1" },
        // Menu-75: Messages now validates its query before the session as well; Clan and Tech Factory already did.
        new object[] { "internalMessages", "message", "2" },
        new object[] { "internalClan", "detail", "nova-wing" },
        new object[] { "internalTechFactory", "detail", "lf3_core" },
        new object[] { "internalEvents", "detail", "supplier" },
        new object[] { "internalSpaceSearch", "planet", "mars" }
    ];


    private const string Hostile = "%3Cscript%3Eboom%3C%2Fscript%3E";

    [TestCaseSource(nameof(DetailViews))]
    public async Task MalformedDetailId_Returns400BeforeSessionCheck(string action, string key, string _)
    {
        var context = await SendAsync($"?action={action}&{key}={Hostile}", dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Not.Contain("<script>boom"));
    }

    [TestCaseSource(nameof(DetailViews))]
    public async Task RepeatedDetailKey_Returns400(string action, string key, string id)
    {
        var context = await SendAsync($"?action={action}&{key}={id}&{key}={id}", dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCaseSource(nameof(DetailViews))]
    public async Task UpperCaseDetailKey_Returns400(string action, string key, string id)
    {
        var context = await SendAsync($"?action={action}&{key.ToUpperInvariant()}={id}", dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCaseSource(nameof(DetailViews))]
    public async Task WellFormedDetailId_WithoutSession_Returns401(string action, string key, string id)
    {
        var context = await SendAsync($"?action={action}&{key}={id}", dosid: "missing-session");

        AssertError(context, StatusCodes.Status401Unauthorized);
    }

    [TestCaseSource(nameof(DetailViews))]
    public async Task WellFormedDetailId_WithoutCompany_RedirectsToCompanyChoice(string action, string key, string id)
    {
        // Menu-86: every menu page now sends pilots without a company to company selection, as Live does.
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync($"?action={action}&{key}={id}", harness.Session.Dosid, harness);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
        });
    }

    [TestCase("omega")]
    [TestCase("zeta")]
    public async Task GalaxyGates_UnknownWellFormedGate_Returns404AfterCompanyCheck(string gate)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync($"?action=internalGalaxyGates&gate={gate}", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Not.Contain(gate));
    }

    // Menu-145: an unknown filter or a gate outside the selected filter is 404 without echoing input;
    // a malformed filter is 400 before the session check.
    [TestCase("filter=claimed")]
    [TestCase("filter=activated&gate=beta")]
    public async Task GalaxyGates_UnknownFilterOrGateOutsideFilter_Returns404(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync($"?action=internalGalaxyGates&{query}", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Not.Contain("claimed").And.Not.Contain("data-gate"));
    }

    [Test]
    public async Task GalaxyGates_MalformedFilter_Returns400BeforeSession()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync("?action=internalGalaxyGates&filter=Ready", null, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    // Menu-211: a malformed Events status filter is 400 before the session check.
    [TestCase("filter=Ready")]
    [TestCase("filter=open")]
    [TestCase("Filter=ready")]
    public async Task Events_MalformedFilter_Returns400BeforeSession(string query)
    {
        var context = await SendAsync($"?action=internalEvents&{query}", dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("0")]
    [TestCase("01")]
    [TestCase("12345")]
    [TestCase("-1")]
    [TestCase("1.5")]
    [TestCase("%EF%BC%91")] // full-width digit one
    public async Task MapOverview_MalformedMapId_Returns400WithoutSession(string map)
    {
        var context = await SendAsync($"?action=internalMapOverview&map={map}", dosid: "missing-session");

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task MapOverview_UpperCaseMapKey_IsRejectedBeforeSession()
    {
        var malformed = await SendAsync($"?action=internalMapOverview&MAP={Hostile}", dosid: null);
        var wellFormed = await SendAsync("?action=internalMapOverview&MAP=5", dosid: "missing-session");

        AssertError(malformed, StatusCodes.Status400BadRequest);
        // Menu-189: keys match exactly, so a well-formed value under MAP= is 400 before the session check.
        AssertError(wellFormed, StatusCodes.Status400BadRequest);
    }

    [TestCase("?ACTION=internalMapOverview")]
    [TestCase("?action=INTERNALMAPOVERVIEW")]
    [TestCase("?action=internalMapOverview&Company=eic")]
    public async Task MapOverview_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    // Menu-191: the Dock rejects non-canonical keys and action values before the company-choice redirect.
    [TestCase("?ACTION=internalDock")]
    [TestCase("?action=INTERNALDOCK")]
    [TestCase("?action=internalDock&From=internalStart")]
    [TestCase("?action=internalDock&lang=a%0Db")]
    [TestCase("?action=internalDock&debug=1")]
    public async Task Dock_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(Body(context), Does.Contain("Hangar request failed (400)"));
            Assert.That(Body(context), Does.Not.Contain("From"));
            Assert.That(Body(context), Does.Not.Contain("INTERNALDOCK"));
        });
    }

    [TestCase("?ACTION=internalDock")]
    [TestCase("?action=internalDock&Lang=en")]
    public async Task Dock_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?ACTION=internalDock")]
    [TestCase("?action=internalDock&PRC=100")]
    public async Task Dock_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    // Menu-199: Payment rejects non-canonical keys and action values before the company-choice redirect.
    [TestCase("?ACTION=internalPayment")]
    [TestCase("?action=INTERNALPAYMENT")]
    [TestCase("?action=internalPayment&From=internalStart")]
    [TestCase("?action=internalPayment&Detail=starter")]
    [TestCase("?action=internalPayment&lang=a%0Db")]
    [TestCase("?action=internalPayment&debug=1")]
    public async Task Payment_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(Body(context), Does.Contain("Payment request failed (400)"));
            Assert.That(Body(context), Does.Not.Contain("INTERNALPAYMENT").And.Not.Contain("From").And.Not.Contain("debug"));
        });
    }

    [TestCase("?ACTION=internalPayment")]
    [TestCase("?action=internalPayment&Lang=en")]
    public async Task Payment_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?ACTION=internalPayment")]
    [TestCase("?action=internalPayment&PRC=100")]
    [TestCase("?action=internalPayment&Filter=claimed")]
    public async Task Payment_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Contain("Payment request failed (400)"));
    }

    [TestCase("?action=internalPayment")]
    [TestCase("?action=internalPayment&from=internalStart&lang=es")]
    public async Task Payment_CanonicalQuery_WithoutCompany_StillRedirectsToCompanyChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
    }

    [TestCase("?ACTION=internalSpaceSearch")]
    [TestCase("?action=INTERNALSPACESEARCH")]
    [TestCase("?action=internalSpaceSearch&From=internalStart")]
    [TestCase("?action=internalSpaceSearch&Filter=available")]
    [TestCase("?action=internalSpaceSearch&Planet=mars")]
    [TestCase("?action=internalSpaceSearch&lang=a%0Db")]
    public async Task SpaceSearch_NonCanonicalQuery_Returns400BeforeSessionAndCompany(string query)
    {
        var withoutSession = await SendAsync(query, dosid: null);
        var harness = await HarnessAsync(selectCompany: false);
        var withoutCompany = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(withoutSession, StatusCodes.Status400BadRequest);
        AssertError(withoutCompany, StatusCodes.Status400BadRequest);
        Assert.That(withoutCompany.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(withoutCompany), Does.Not.Contain("INTERNALSPACESEARCH").And.Not.Contain("From"));
    }

    [TestCase("?action=internalSpaceSearch")]
    [TestCase("?action=internalSpaceSearch&from=internalStart&filter=available")]
    public async Task SpaceSearch_CanonicalQuery_WithoutCompany_StillRedirects(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
    }

    // Menu-213: Space Search planet search is validated before the session; a planet outside the search is 404.
    [TestCase("?action=internalSpaceSearch&q=x")]
    [TestCase("?action=internalSpaceSearch&q=%3Cscript%3E")]
    [TestCase("?action=internalSpaceSearch&q=mars&q=venus")]
    [TestCase("?action=internalSpaceSearch&Q=mars")]
    public async Task SpaceSearch_MalformedSearch_Returns400BeforeSession(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Not.Contain("<script>"));
    }

    [Test]
    public async Task SpaceSearch_ValidSearch_WithoutSession_Returns401() =>
        AssertError(await SendAsync("?action=internalSpaceSearch&q=laser", dosid: "missing-session"),
            StatusCodes.Status401Unauthorized);

    [Test]
    public async Task SpaceSearch_PlanetOutsideSearch_Returns404()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync("?action=internalSpaceSearch&q=laser&planet=venus", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
    }

    [TestCase("?ACTION=internalPilotSheet")]
    [TestCase("?action=INTERNALPILOTSHEET")]
    [TestCase("?action=internalPilotSheet&From=internalStart")]
    [TestCase("?action=internalPilotSheet&Detail=hull")]
    [TestCase("?action=internalPilotSheet&Filter=locked")]
    [TestCase("?action=internalPilotSheet&lang=a%0Db")]
    public async Task PilotSheet_NonCanonicalQuery_Returns400BeforeSessionAndCompany(string query)
    {
        var withoutSession = await SendAsync(query, dosid: null);
        var harness = await HarnessAsync(selectCompany: false);
        var withoutCompany = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(withoutSession, StatusCodes.Status400BadRequest);
        AssertError(withoutCompany, StatusCodes.Status400BadRequest);
        Assert.That(withoutCompany.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(withoutCompany), Does.Not.Contain("INTERNALPILOTSHEET").And.Not.Contain("From"));
    }

    [TestCase("?action=internalPilotSheet")]
    [TestCase("?action=internalPilotSheet&from=internalStart&filter=locked")]
    public async Task PilotSheet_CanonicalQuery_WithoutCompany_StillRedirects(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
    }

    [TestCase("?ACTION=internalEvents")]
    [TestCase("?action=INTERNALEVENTS")]
    [TestCase("?action=internalEvents&From=internalStart")]
    [TestCase("?action=internalEvents&Claimed=supplier")]
    public async Task Events_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(context), Does.Not.Contain("INTERNALEVENTS").And.Not.Contain("From"));
    }

    [TestCase("?action=internalDock")]
    [TestCase("?action=internalDock&from=internalStart&prc=100&lang=en&ship=2")]
    public async Task Dock_CanonicalQuery_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("?ACTION=internalSkylab")]
    [TestCase("?action=INTERNALSKYLAB")]
    [TestCase("?action=internalSkylab&From=internalStart")]
    [TestCase("?action=internalSkylab&lang=a%0Db")]
    public async Task Skylab_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(context), Does.Not.Contain("INTERNALSKYLAB").And.Not.Contain("From"));
    }

    [TestCase("?ACTION=internalSkylab")]
    [TestCase("?action=internalSkylab&PRC=100")]
    public async Task Skylab_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?ACTION=internalSkylab")]
    [TestCase("?action=internalSkylab&Lang=en")]
    public async Task Skylab_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("?action=internalSkylab")]
    [TestCase("?action=internalSkylab&from=internalStart&prc=100&lang=en&detail=prometium")]
    public async Task Skylab_CanonicalQuery_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("?ACTION=internalAssembly")]
    [TestCase("?action=INTERNALASSEMBLY")]
    [TestCase("?action=internalAssembly&From=internalStart")]
    [TestCase("?action=internalAssembly&lang=a%0Db")]
    public async Task Assembly_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(context), Does.Not.Contain("INTERNALASSEMBLY").And.Not.Contain("From"));
    }

    [TestCase("?ACTION=internalAssembly")]
    [TestCase("?action=internalAssembly&PRC=100")]
    public async Task Assembly_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?action=internalassembly")]
    [TestCase("?action=internalAssembly&Lang=en")]
    public async Task Assembly_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("?action=internalAssembly")]
    [TestCase("?action=internalAssembly&from=internalStart&prc=100&lang=en&status=ready")]
    public async Task Assembly_CanonicalQuery_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("?ACTION=internalAuction")]
    [TestCase("?action=INTERNALAUCTION")]
    [TestCase("?action=internalAuction&From=internalStart")]
    [TestCase("?action=internalAuction&lang=a%0Db")]
    public async Task Auction_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(context), Does.Not.Contain("INTERNALAUCTION").And.Not.Contain("From"));
    }

    [TestCase("?ACTION=internalAuction")]
    [TestCase("?action=internalAuction&PRC=100")]
    public async Task Auction_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?action=internalauction")]
    [TestCase("?action=internalAuction&Lang=en")]
    public async Task Auction_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("?action=internalAuction")]
    [TestCase("?action=internalAuction&from=internalStart&prc=100&lang=en&filter=open&lot=laser_lf3")]
    public async Task Auction_CanonicalQuery_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("?ACTION=internalVouchers")]
    [TestCase("?action=INTERNALVOUCHERS")]
    [TestCase("?action=internalVouchers&From=internalStart")]
    [TestCase("?action=internalVouchers&lang=a%0Db")]
    public async Task Vouchers_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(context), Does.Not.Contain("INTERNALVOUCHERS").And.Not.Contain("From"));
    }

    [TestCase("?ACTION=internalVouchers")]
    [TestCase("?action=internalVouchers&PRC=100")]
    public async Task Vouchers_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?action=internalvouchers")]
    [TestCase("?action=internalVouchers&Lang=en")]
    public async Task Vouchers_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("?action=internalVouchers")]
    [TestCase("?action=internalVouchers&from=internalStart&prc=100&lang=en&filter=available&voucher=TRAINING-500")]
    public async Task Vouchers_CanonicalQuery_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("?ACTION=internalQuests")]
    [TestCase("?action=INTERNALQUESTS")]
    [TestCase("?action=internalQuests&From=internalStart")]
    [TestCase("?action=internalQuests&lang=a%0Db")]
    public async Task Quests_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        Assert.That(Body(context), Does.Not.Contain("INTERNALQUESTS").And.Not.Contain("From"));
    }

    [TestCase("?ACTION=internalQuests")]
    [TestCase("?action=internalQuests&PRC=100")]
    public async Task Quests_NonCanonicalQuery_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?action=internalquests")]
    [TestCase("?action=internalQuests&Lang=en")]
    public async Task Quests_NonCanonicalQuery_WithCompany_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("?action=internalQuests")]
    [TestCase("?action=internalQuests&from=internalStart&prc=100&lang=en&filter=ready&detail=first_purchase")]
    public async Task Quests_CanonicalQuery_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    // Menu-209: a malformed search is 400 before the session; a detail outside the search is 404.
    [TestCase("?action=internalQuests&q=f")]
    [TestCase("?action=internalQuests&q=first&q=bulk")]
    [TestCase("?action=internalQuests&q=%3Cb%3E")]
    public async Task Quests_InvalidSearch_WithoutSession_Returns400(string query)
    {
        var context = await SendAsync(query, dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Not.Contain("<b>"));
    }

    [TestCase("?action=internalQuests&q=first")]
    [TestCase("?action=internalQuests&filter=ready&q=%20first%20&detail=first_craft")]
    public async Task Quests_ValidSearch_WithoutCompany_RedirectsToChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [Test]
    public async Task MapOverview_CanonicalQuery_WithoutCompany_RedirectsToChoice()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalMapOverview&company=eic", harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("from")]
    [TestCase("prc")]
    [TestCase("lang")]
    public async Task MapOverview_InvalidAuxiliaryParameter_Returns400BeforeSessionCheck(string key)
    {
        var tooLong = await SendAsync($"?action=internalMapOverview&{key}={new string('a', 65)}", dosid: null);
        var control = await SendAsync($"?action=internalMapOverview&{key}=a%0Db", dosid: null);
        var repeated = await SendAsync($"?action=internalMapOverview&{key}=a&{key}=b", dosid: null);

        AssertError(tooLong, StatusCodes.Status400BadRequest);
        AssertError(control, StatusCodes.Status400BadRequest);
        AssertError(repeated, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task MapOverview_UnknownParameter_Returns400BeforeCompanyRedirect()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync($"?action=internalMapOverview&debug={Hostile}", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Not.Contain("<script>boom"));
    }

    [TestCase("999")]
    [TestCase("4")]
    public async Task MapOverview_UnknownWellFormedMap_Returns404AfterCompanyCheck(string map)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync($"?action=internalMapOverview&map={map}", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Not.Contain($"map={map}"));
    }

    [Test]
    public async Task MapOverview_UnknownWellFormedMap_WithoutCompany_RedirectsBefore404()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalMapOverview&map=999", harness.Session.Dosid, harness);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
    }

    [TestCase("from=a%0Db")]
    [TestCase("lang=en%00")]
    [TestCase("prc=" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("sort=countdown")]
    [TestCase("sort=Ending")]
    [TestCase("category=..%2Fevents")]
    [TestCase("debug=1")]
    [TestCase("category=events&category=deals")]
    public async Task CommandCenter_InvalidQuery_Returns400BeforeSessionCheck(string query)
    {
        var withoutCookie = await SendAsync($"?action=internalCommandCenter&{query}", dosid: null);
        var expired = await SendAsync($"?action=internalCommandCenter&{query}", dosid: "missing-session");

        AssertError(withoutCookie, StatusCodes.Status400BadRequest);
        AssertError(expired, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CommandCenter_InvalidQuery_Returns400BeforeCompanyRedirect()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync($"?action=internalCommandCenter&from=a%0Db", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
    }

    [TestCase("?ACTION=internalCommandCenter")]
    [TestCase("?action=INTERNALCOMMANDCENTER")]
    [TestCase("?action=internalCommandCenter&Category=events")]
    public async Task CommandCenter_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [Test]
    public async Task CommandCenter_WellFormedQuery_WithoutSession_Returns401WithSignIn()
    {
        var context = await SendAsync("?action=internalCommandCenter&category=events&sort=ending", dosid: null);

        AssertError(context, StatusCodes.Status401Unauthorized);
        Assert.That(Body(context), Does.Contain("<a href=\"/\">Sign in</a>"));
    }

    [TestCase("category=payments")]
    [TestCase("item=unknownNotice")]
    [TestCase("category=deals&item=Voyagers2607")]
    public async Task CommandCenter_UnknownSelection_Returns404AfterCompanyCheck(string query)
    {
        var withCompany = await HarnessAsync(selectCompany: true);
        var withoutCompany = await HarnessAsync(selectCompany: false);

        var found = await SendAsync($"?action=internalCommandCenter&{query}", withCompany.Session.Dosid, withCompany);
        var redirected = await SendAsync($"?action=internalCommandCenter&{query}", withoutCompany.Session.Dosid, withoutCompany);

        AssertError(found, StatusCodes.Status404NotFound);
        Assert.That(Body(found), Does.Not.Contain("unknownNotice"));
        Assert.Multiple(() =>
        {
            Assert.That(redirected.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(redirected.Response.Headers.Location.ToString(), Does.Contain("internalCompanyChoose"));
        });
    }

    [TestCase("internalHallofFame", "company=xyz")]
    [TestCase("internalHallofFame", "page=0")]
    [TestCase("internalHallofFame", "page=")]
    [TestCase("internalHallofFame", "page=01")]
    [TestCase("internalHallofFame", "page=99999")]
    [TestCase("internalHallofFame", "category=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("internalHallofFame", "category=")]
    [TestCase("internalHallofFame", "pilot=")]
    [TestCase("internalHallofFame", "from=a%0Db")]
    [TestCase("internalHallofFame", "debug=1")]
    [TestCase("internalLogbook", "category=")]
    [TestCase("internalLogbook", "category=aaaaaaaaaaaaaaaaa")]
    [TestCase("internalLogbook", "page=")]
    [TestCase("internalLogbook", "page=-1")]
    [TestCase("internalLogbook", "entry=7")]
    [TestCase("internalLogbook", "entry=07-1")]
    [TestCase("internalLogbook", "entry=7-0")]
    [TestCase("internalLogbook", "debug=1")]
    public async Task RankingsAndLogbook_InvalidQuery_Returns400BeforeSessionAndCompany(string action, string query)
    {
        var withoutCookie = await SendAsync($"?action={action}&{query}", dosid: null);
        var expired = await SendAsync($"?action={action}&{query}", dosid: "missing-session");
        var harness = await HarnessAsync(selectCompany: false);
        var withoutCompany = await SendAsync($"?action={action}&{query}", harness.Session.Dosid, harness);

        AssertError(withoutCookie, StatusCodes.Status400BadRequest);
        AssertError(expired, StatusCodes.Status400BadRequest);
        AssertError(withoutCompany, StatusCodes.Status400BadRequest);
    }

    [TestCase("internalHallofFame", "category=honour&page=2")]
    [TestCase("internalLogbook", "category=combat&page=1")]
    public async Task RankingsAndLogbook_WellFormedQuery_WithoutSession_Returns401(string action, string query)
    {
        var context = await SendAsync($"?action={action}&{query}", dosid: null);

        AssertError(context, StatusCodes.Status401Unauthorized);
    }

    [TestCase("category=bogus")]
    [TestCase("page=999")]
    [TestCase("pilot=Nobody-XYZ")]
    public async Task HallOfFame_UnknownSelection_Returns404AfterCompanyCheck(string query)
    {
        var withCompany = await HarnessAsync(selectCompany: true);
        var withoutCompany = await HarnessAsync(selectCompany: false);

        var found = await SendAsync($"?action=internalHallofFame&{query}", withCompany.Session.Dosid, withCompany);
        var redirected = await SendAsync($"?action=internalHallofFame&{query}", withoutCompany.Session.Dosid, withoutCompany);

        AssertError(found, StatusCodes.Status404NotFound);
        Assert.That(Body(found), Does.Not.Contain("Nobody-XYZ"));
        Assert.Multiple(() =>
        {
            Assert.That(redirected.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(redirected.Response.Headers.Location.ToString(), Does.Contain("internalCompanyChoose"));
        });
    }

    // Menu-74: the economy store is null in this harness, so these pass only if no ledger read happens.
    [TestCase("entry=8-1")]
    [TestCase("entry=70-1")]
    [TestCase("entry=8-1&page=1")]
    [TestCase("entry=8-1&category=combat")]
    public async Task Logbook_OtherAccountEntry_Returns403BeforeLedgerRead(string query)
    {
        var withCompany = await HarnessAsync(selectCompany: true);
        var withoutCompany = await HarnessAsync(selectCompany: false);

        var refused = await SendAsync($"?action=internalLogbook&{query}", withCompany.Session.Dosid, withCompany);
        var redirected = await SendAsync($"?action=internalLogbook&{query}", withoutCompany.Session.Dosid, withoutCompany);
        var anonymous = await SendAsync($"?action=internalLogbook&{query}", dosid: "missing-session");

        AssertError(refused, StatusCodes.Status403Forbidden);
        AssertError(anonymous, StatusCodes.Status401Unauthorized);
        Assert.Multiple(() =>
        {
            Assert.That(Body(refused), Does.Contain("another account"));
            Assert.That(redirected.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(redirected.Response.Headers.Location.ToString(), Does.Contain("internalCompanyChoose"));
        });
    }

    [TestCase("category=bogus")]
    [TestCase("category=Combat")]
    [TestCase("category=bogus&entry=8-1")]
    [TestCase("category=bogus&page=2")]
    public async Task Logbook_UnknownCategory_Returns404BeforeLedgerRead(string query)
    {
        var withCompany = await HarnessAsync(selectCompany: true);

        var context = await SendAsync($"?action=internalLogbook&{query}", withCompany.Session.Dosid, withCompany);

        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Not.Contain("bogus"));
    }

    [Test]
    public async Task Logbook_ExpiredSession_ForeignEntry_Returns401()
    {
        var harness = await HarnessAsync(selectCompany: true);
        await harness.Sessions.InvalidateAsync(harness.Session.Dosid, CancellationToken.None);

        var context = await SendAsync("?action=internalLogbook&entry=8-1", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task GalaxyGates_UnknownGate_DoesNotChangeGateState()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var before = await harness.Gates.GetAsync(harness.Session.AccountId, CancellationToken.None);

        await SendAsync("?action=internalGalaxyGates&gate=omega", harness.Session.Dosid, harness);

        var after = await harness.Gates.GetAsync(harness.Session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(after.Energy, Is.EqualTo(before.Energy));
            Assert.That(after.Gates.Select(g => (g.Id, g.PartsOwned, g.Activated)),
                Is.EqualTo(before.Gates.Select(g => (g.Id, g.PartsOwned, g.Activated))));
        });
    }

    [TestCase("folder=archive")]
    [TestCase("folder=Inbox")]
    [TestCase("folder=")]
    [TestCase("message=01")]
    [TestCase("message=-1")]
    [TestCase("message=1234567890")]
    [TestCase("message=1&message=2")]
    [TestCase("Folder=inbox")]
    [TestCase("lang=%0A")]
    [TestCase("pilot=1001")]
    public async Task Messages_InvalidQuery_Returns400BeforeSessionAndCompany(string query)
    {
        var withoutCookie = await SendAsync($"?action=internalMessages&{query}", dosid: null);
        var expired = await SendAsync($"?action=internalMessages&{query}", dosid: "missing-session");
        var harness = await HarnessAsync(selectCompany: false);
        var withoutCompany = await SendAsync($"?action=internalMessages&{query}", harness.Session.Dosid, harness);

        AssertError(withoutCookie, StatusCodes.Status400BadRequest);
        AssertError(expired, StatusCodes.Status400BadRequest);
        AssertError(withoutCompany, StatusCodes.Status400BadRequest);
    }

    [TestCase("folder=inbox")]
    [TestCase("folder=sent&message=1")]
    public async Task Messages_WellFormedQuery_WithoutSession_Returns401(string query)
    {
        var context = await SendAsync($"?action=internalMessages&{query}", dosid: "missing-session");

        AssertError(context, StatusCodes.Status401Unauthorized);
    }

    // The seeded mailbox holds three inbox notices (ids 1-3) and nothing in Sent.
    [TestCase("message=999")]
    [TestCase("message=4")]
    [TestCase("folder=sent&message=1")]
    [TestCase("folder=all&message=999999999")]
    public async Task Messages_UnknownMessage_Returns404AfterCompanyCheck(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync($"?action=internalMessages&{query}", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task Messages_UnknownMessage_WithoutCompany_RedirectsBefore404()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalMessages&message=999", harness.Session.Dosid, harness);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Does.Contain("internalCompanyChoose"));
        });
    }

    [TestCase("q=")]
    [TestCase("q=a")]
    [TestCase("q=%20Welcome")]
    [TestCase("q=Welcome%20")]
    [TestCase("q=Welcome&q=Gates")]
    [TestCase("Q=Welcome")]
    [TestCase("q=%0AWelcome")]
    public async Task Messages_InvalidSearch_Returns400BeforeSession(string query)
    {
        AssertError(await SendAsync($"?action=internalMessages&{query}", dosid: null),
            StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Messages_SearchExcludesDetailWithoutMarkingItRead()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var excluded = await SendAsync("?action=internalMessages&folder=inbox&q=Galaxy&message=1",
            harness.Session.Dosid, harness);
        AssertError(excluded, StatusCodes.Status404NotFound);
        var state = await harness.Messages.GetAsync(harness.Session.AccountId, CancellationToken.None);
        Assert.That(state.Messages.Single(message => message.Id == 1).Read, Is.False);
    }

    // Menu-143: Clan search is validated before the session; a clan outside the search is 404.
    [TestCase("q=")]
    [TestCase("q=%20Mars")]
    [TestCase("q=Mars%20")]
    [TestCase("q=Mars&q=Guard")]
    [TestCase("Q=Mars")]
    [TestCase("q=Mars_Guard")]
    [TestCase("q=%0AMars")]
    [TestCase("q=" + Hostile)]
    [TestCase("q=abcdefghijklmnopqrstuvwxyzabcdefg")]
    public async Task Clan_InvalidSearch_Returns400BeforeSession(string query)
    {
        AssertError(await SendAsync($"?action=internalClan&{query}", dosid: null),
            StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Clan_ValidSearch_WithoutSession_Returns401()
    {
        AssertError(await SendAsync("?action=internalClan&q=Mars%20Guard", dosid: "missing-session"),
            StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task Clan_DetailOutsideSearch_Returns404()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync("?action=internalClan&q=Elite&detail=mmo-guard", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
    }

    // Menu-197: Clan page keys and the action value are exact; wrong casing is 400 before the company redirect.
    [TestCase("?ACTION=internalClan")]
    [TestCase("?action=INTERNALCLAN")]
    [TestCase("?action=internalClan&From=internalStart")]
    [TestCase("?action=internalClan&Lang=xMarkerX")]
    public async Task Clan_NonCanonicalQueryKeys_Return400BeforeCompanyRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
            Assert.That(Body(context), Does.Not.Contain("xMarkerX").And.Not.Contain("INTERNALCLAN"));
        });
    }

    [Test]
    public async Task Clan_CanonicalShellKeys_PassToCompanyRedirect()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalClan&from=internalStart&prc=100&lang=en",
            harness.Session.Dosid, harness);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
        });
    }

    // Menu-144: Auction filter shape is validated before the session; an unknown filter is 404 afterwards.
    [TestCase("filter=")]
    [TestCase("filter=open&filter=open")]
    [TestCase("Filter=open")]
    [TestCase("filter=Open")]
    [TestCase("filter=open_now")]
    [TestCase("filter=%0Aopen")]
    [TestCase("filter=" + Hostile)]
    [TestCase("filter=abcdefghijklmnopq")]
    public async Task Auction_InvalidFilter_Returns400BeforeSession(string query)
    {
        var context = await SendAsync($"?action=internalAuction&{query}", dosid: null);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Not.Contain("<script>boom"));
    }

    [Test]
    public async Task Auction_ValidFilter_WithoutSession_Returns401()
    {
        AssertError(await SendAsync("?action=internalAuction&filter=claimable", dosid: "missing-session"),
            StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task Auction_UnknownFilter_Returns404WithoutEcho()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync("?action=internalAuction&filter=bidding", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Not.Contain("bidding"));
    }

    // Menu-146: a malformed Payment filter is 400 and an unknown one is 404, neither echoing input.
    [TestCase("filter=Claimed")]
    [TestCase("filter=claimed&filter=claimed")]
    [TestCase("Filter=claimed")]
    [TestCase("filter=" + Hostile)]
    public async Task Payment_MalformedFilter_Returns400(string query)
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync($"?action=internalPayment&{query}", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(Body(context), Does.Not.Contain("<script>boom"));
    }

    [Test]
    public async Task Payment_UnknownFilter_Returns404WithoutEcho()
    {
        var harness = await HarnessAsync(selectCompany: true);
        var context = await SendAsync("?action=internalPayment&filter=pending", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status404NotFound);
        Assert.That(Body(context), Does.Not.Contain("pending"));
    }

    // Menu-76: Hangar equipment checks its query before the session (400 → 401 → 403).
    [TestCase("?action=internalDockEquipment")]
    [TestCase("?action=internalDockEquipment&slot=1")]
    [TestCase("?action=internalDockEquipment&slot=3&configuration=1")]
    [TestCase("?action=internalDockEquipment&slot=01&configuration=1")]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=%2B2")]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=1&slot=2")]
    [TestCase("?action=internalDockEquipment&Slot=1&configuration=1")]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=1&account=other")]
    [TestCase("?action=INTERNALDOCKEQUIPMENT&slot=1&configuration=1")]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=" + Hostile)]
    public async Task DockEquipment_MalformedQuery_Returns400BeforeSessionAndCompany(string query)
    {
        var withoutCompany = await HarnessAsync(selectCompany: false);
        foreach (var (dosid, harness) in new (string?, Harness?)[]
                 { (null, null), ("missing-session", null), (withoutCompany.Session.Dosid, withoutCompany) })
        {
            var context = await SendAsync(query, dosid, harness);

            AssertError(context, StatusCodes.Status400BadRequest);
            Assert.That(Body(context), Does.Not.Contain("<script>boom"));
        }
    }

    [TestCase("?action=internalDockEquipment&slot=1&configuration=1")]
    [TestCase("?action=internalDockEquipment&slot=2&configuration=2&from=dock&lang=en")]
    public async Task DockEquipment_WellFormedQuery_WithoutSession_Returns401(string query)
    {
        AssertError(await SendAsync(query, dosid: null), StatusCodes.Status401Unauthorized);
        AssertError(await SendAsync(query, dosid: "missing-session"), StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task DockEquipment_WellFormedQuery_ExpiredSession_Returns401()
    {
        var harness = await HarnessAsync(selectCompany: true);
        await harness.Sessions.InvalidateAsync(harness.Session.Dosid, CancellationToken.None);
        var context = await SendAsync("?action=internalDockEquipment&slot=1&configuration=1", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status401Unauthorized);
    }

    [TestCase("?action=internalDockEquipment&slot=1&configuration=1")]
    [TestCase("?action=internalDockEquipment&slot=2&configuration=1")]
    public async Task DockEquipment_WellFormedQuery_WithoutCompany_RedirectsToCompanyChoice(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    // Menu-86: Live answers menu pages before company selection with 302 to company choice
    // (PreCompanyMenuRedirect, captured for internalGalaxyGates). These six pages used to answer 403.
    // The other stores are null in this harness, so the redirect must come before any store read.
    [TestCase("?action=internalShop")]
    [TestCase("?action=internalShop&category=drones&item=drone_flax")]
    [TestCase("?action=internalGalaxyGates")]
    [TestCase("?action=internalSkylab")]
    [TestCase("?action=internalTechFactory")]
    [TestCase("?action=internalClan&filter=recruiting")]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=2")]
    public async Task MenuPage_WithoutCompany_RedirectsToCompanyChoiceWithoutBody(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
        Assert.That(Body(context), Is.Empty);
    }

    [Test]
    public async Task ShopPage_NonCanonicalAction_WithoutCompany_Returns400BeforeRedirect()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=INTERNALSHOP", harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [TestCase("?action=INTERNALACCOUNT")]
    [TestCase("?ACTION=internalAccount")]
    [TestCase("?action=internalAccount&FROM=internalStart")]
    public async Task AccountPage_NonCanonicalQuery_WithoutCompany_Returns400BeforeRedirect(string query)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync(query, harness.Session.Dosid, harness);

        AssertError(context, StatusCodes.Status400BadRequest);
        Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
    }

    [Test]
    public async Task AccountPage_CanonicalQuery_WithoutCompany_RedirectsToChoice()
    {
        var harness = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalAccount&from=internalStart", harness.Session.Dosid, harness);

        AssertCompanyChoiceRedirect(context);
    }

    [TestCase("internalShop")]
    [TestCase("internalGalaxyGates")]
    [TestCase("internalSkylab")]
    [TestCase("internalTechFactory")]
    [TestCase("internalClan")]
    public async Task MenuPage_WithoutCompany_StillChecksQueryAndSessionFirst(string action)
    {
        var harness = await HarnessAsync(selectCompany: false);
        var malformed = await SendAsync($"?action={action}&detail={Hostile}&item={Hostile}&gate={Hostile}", harness.Session.Dosid, harness);
        var noSession = await SendAsync($"?action={action}", "missing-session", harness);

        AssertError(malformed, StatusCodes.Status400BadRequest);
        AssertError(noSession, StatusCodes.Status401Unauthorized);
    }

    private static void AssertCompanyChoiceRedirect(DefaultHttpContext context)
    {
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found), Body(context));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
        });
    }

    private static void AssertError(DefaultHttpContext context, int status)
    {
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), Body(context));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
        });
    }

    private sealed record Harness(IDistributedCache Cache, GameAuthSessionStore Sessions, GameAuthSession Session,
        LocalGalaxyGateStore Gates, LocalMessagingStore Messages);

    private static async Task<Harness> HarnessAsync(bool selectCompany)
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        if (selectCompany)
            Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return new Harness(cache, sessions, session, new LocalGalaxyGateStore(cache), new LocalMessagingStore(cache));
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, string? dosid, Harness? harness = null)
    {
        harness ??= await HarnessAsync(selectCompany: false);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();

        // Space Search checks saved inventory before deciding whether a planet matches a search.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, harness.Sessions, null!, null!, new LocalHangarEquipmentStore(harness.Cache), null!,
            harness.Gates, null!, null!, null!, null!, harness.Messages, null!, TimeProvider.System);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
