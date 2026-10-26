using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarActivatedNoticeTests
{
    private static readonly GameAuthSession Session =
        new() { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "dock-dosid", Company = "mmo" };

    [TestCase("&activated=1", true)]
    [TestCase("&activated=2", true)]
    [TestCase("&from=internalStart&activated=2&lang=es", true)]
    [TestCase("&activated=", false)]
    [TestCase("&activated=0", false)]
    [TestCase("&activated=3", false)]
    [TestCase("&activated=02", false)]
    [TestCase("&activated=+2", false)]
    [TestCase("&activated=%202", false)]
    [TestCase("&activated=2%20", false)]
    [TestCase("&activated=-1", false)]
    [TestCase("&activated=1.0", false)]
    [TestCase("&activated=%D9%A2", false)]
    [TestCase("&activated=2147483649", false)]
    [TestCase("&activated=pusat", false)]
    [TestCase("&activated=1&activated=1", false)]
    [TestCase("&activated=1&activated=2", false)]
    [TestCase("&ACTIVATED=2", false)]
    [TestCase("&Activated=2", false)]
    [TestCase("&activated", false)]
    [TestCase("&activatedSlot=2", false)]
    public void PageQuery_AcceptsOnlyOneCanonicalLowercaseActivatedSlot(string extra, bool valid)
    {
        var query = Query("?action=internalDock" + extra);
        Assert.That(LocalHangars.ValidPageQuery(query), Is.EqualTo(valid));
    }

    [TestCase("?action=internalDock", null)]
    [TestCase("?action=internalDock&activated=1", 1)]
    [TestCase("?action=internalDock&activated=2", 2)]
    public void ActivatedSlot_ReadsTheValidatedSlot(string query, int? expected) =>
        Assert.That(LocalHangars.ActivatedSlot(Query(query)), Is.EqualTo(expected));

    [Test]
    public void ActivatedLocation_ProducesAQueryTheDockPageAccepts()
    {
        foreach (var slot in new[] { 1, 2 })
        {
            var location = LocalHangars.ActivatedLocation(slot);
            Assert.That(location, Is.EqualTo($"/indexInternal.es?action=internalDock&activated={slot}"));
            var query = Query(location[location.IndexOf('?')..]);
            Assert.That(LocalHangars.ValidPageQuery(query), Is.True);
            Assert.That(LocalHangars.ActivatedSlot(query), Is.EqualTo(slot));
        }
    }

    [TestCase(1, "Pusat Plus is now the active ship (slot 1).")]
    [TestCase(2, "Phoenix is now the active ship (slot 2).")]
    public void Page_ConfirmsTheActivatedShipWhileItIsStillActive(int slot, string text)
    {
        var page = LocalHangars.Page(Session, slot, activated: slot);
        Assert.That(page, Does.Contain($"<p id=\"dock-notice\" role=\"status\" data-slot=\"{slot}\">{text}</p>"));
        Assert.That(page, Does.Not.Contain("data-stale"));
        // The notice sits before the cards and the inactive slot keeps its signed form.
        Assert.That(page.IndexOf("dock-notice", StringComparison.Ordinal),
            Is.LessThan(page.IndexOf("<article", StringComparison.Ordinal)));
        Assert.That(Regex.Matches(page, "<form ").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(page, "data-active=\"true\"").Count, Is.EqualTo(1));
    }

    [TestCase(1, 2)]
    [TestCase(2, 1)]
    public void Page_ReportsAStaleNoticeWhenAnotherSlotIsActiveNow(int activated, int active)
    {
        var page = LocalHangars.Page(Session, active, activated: activated);
        Assert.That(page, Does.Contain(
            $"data-slot=\"{activated}\" data-stale=\"true\">The active ship has changed since slot {activated} was activated.</p>"));
        Assert.That(page, Does.Not.Contain("is now the active ship"));
        Assert.That(page, Does.Contain($"data-slot=\"{active}\" data-active=\"true\""));
    }

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(3)]
    [TestCase(-1)]
    public void Page_ShowsNoNoticeWithoutAValidActivatedSlot(int? activated)
    {
        var page = LocalHangars.Page(Session, 1, activated: activated);
        Assert.That(page, Does.Not.Contain("dock-notice"));
        Assert.That(page, Does.Contain("<h1>Hangar</h1><article"));
    }

    [Test]
    public void Page_CorruptActiveSlotFallsBackToDefaultForTheNotice()
    {
        var page = LocalHangars.Page(Session, 9, activated: 1);
        Assert.That(page, Does.Contain("Pusat Plus is now the active ship (slot 1)."));
        var stale = LocalHangars.Page(Session, 9, activated: 2);
        Assert.That(stale, Does.Contain("data-stale=\"true\""));
    }

    [Test]
    public async Task Selection_RedirectsToTheActivatedNotice_ThenRendersIt()
    {
        var (sessions, session, hangars) = await CreateAsync();
        var toPhoenix = await PostAsync(sessions, hangars, session.Dosid,
            $"slot=2&current=1&key={LocalHangars.CreateFormKey(session.Dosid, 2, 1)}");
        Assert.That(toPhoenix.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
        var location = toPhoenix.Response.Headers.Location.ToString();
        Assert.That(location, Is.EqualTo("/indexInternal.es?action=internalDock&activated=2"));

        var query = Query(location[location.IndexOf('?')..]);
        Assert.That(LocalHangars.ValidPageQuery(query), Is.True);
        var active = await hangars.GetActiveSlotAsync(session.AccountId, CancellationToken.None);
        Assert.That(LocalHangars.Page(session, active, activated: LocalHangars.ActivatedSlot(query)),
            Does.Contain("Phoenix is now the active ship (slot 2)."));

        var back = await PostAsync(sessions, hangars, session.Dosid,
            $"slot=1&current=2&key={LocalHangars.CreateFormKey(session.Dosid, 1, 2)}");
        Assert.That(back.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalDock&activated=1"));
        // Reloading the earlier redirect now reports it as stale instead of a false confirmation.
        active = await hangars.GetActiveSlotAsync(session.AccountId, CancellationToken.None);
        Assert.That(LocalHangars.Page(session, active, activated: 2), Does.Contain("data-stale=\"true\""));
    }

    [Test]
    public async Task Selection_FailuresDoNotRedirectToANotice()
    {
        var (sessions, session, hangars) = await CreateAsync();
        var signed = $"slot=2&current=1&key={LocalHangars.CreateFormKey(session.Dosid, 2, 1)}";
        Assert.That((await PostAsync(sessions, hangars, session.Dosid, signed)).Response.StatusCode, Is.EqualTo(303));
        foreach (var (body, status) in new[]
                 {
                     (signed, 409),
                     ("slot=2&current=1&key=bad", 400),
                     ("slot=3&current=1&key=bad", 400)
                 })
        {
            var context = await PostAsync(sessions, hangars, session.Dosid, body);
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), body);
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty, body);
        }
    }

    private static IQueryCollection Query(string queryString) =>
        new QueryCollection(QueryHelpers.ParseQuery(queryString));

    private static async Task<(GameAuthSessionStore, GameAuthSession, LocalHangarStore)> CreateAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!, new LocalHangarStore(cache));
    }

    private static async Task<DefaultHttpContext> PostAsync(IGameAuthSessionStore sessions, LocalHangarStore hangars,
        string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalDockSelect");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalHangarSelection.HandleAsync(context, sessions, hangars, CancellationToken.None);
        return context;
    }
}
