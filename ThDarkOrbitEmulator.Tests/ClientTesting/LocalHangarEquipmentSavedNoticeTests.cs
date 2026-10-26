using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-118: post-save redirect, saved-notice query, and per-category loadout summary.
public class LocalHangarEquipmentSavedNoticeTests
{
    private const string Base = "?action=internalDockEquipment&slot=1&configuration=1";

    [TestCase("", true, null)]
    [TestCase("&saved=drone_flax", true, "drone_flax")]
    [TestCase("&saved=laser_lf3&from=internalDock&lang=es", true, "laser_lf3")]
    [TestCase("&saved=unknown_item", true, "unknown_item")]
    [TestCase("&saved=", false, null)]
    [TestCase("&saved=drone%20flax", false, null)]
    [TestCase("&saved=%3Cscript%3E", false, null)]
    [TestCase("&saved=drone_flax&saved=drone_flax", false, null)]
    [TestCase("&saved=drone_flax&saved=pet_10", false, null)]
    [TestCase("&SAVED=drone_flax", false, null)]
    [TestCase("&Saved=drone_flax", false, null)]
    [TestCase("&savedItem=drone_flax", false, null)]
    [TestCase("&saved=%EF%BC%A1", false, null)]
    public void PageQuery_AcceptsOnlyOneCanonicalSavedItem(string extra, bool valid, string? saved)
    {
        var ok = LocalHangarEquipmentSelection.TryParsePageQuery(Query(Base + extra), out var slot, out var configuration, out var parsed);
        Assert.That(ok, Is.EqualTo(valid));
        Assert.That(parsed, Is.EqualTo(saved));
        if (valid) Assert.That((slot, configuration), Is.EqualTo((1, 1)));
    }

    [Test]
    public void PageQuery_RejectsOversizedSavedItem()
    {
        Assert.That(LocalHangarEquipmentSelection.TryParsePageQuery(Query(Base + "&saved=" + new string('a', 64)), out _, out _, out _), Is.True);
        Assert.That(LocalHangarEquipmentSelection.TryParsePageQuery(Query(Base + "&saved=" + new string('a', 65)), out _, out _, out var saved), Is.False);
        Assert.That(saved, Is.Null);
    }

    [Test]
    public void PageQuery_InvalidSlotDiscardsSavedItem()
    {
        var ok = LocalHangarEquipmentSelection.TryParsePageQuery(
            Query("?action=internalDockEquipment&slot=3&configuration=1&saved=drone_flax"), out _, out _, out var saved);
        Assert.That(ok, Is.False);
        Assert.That(saved, Is.Null);
    }

    [TestCase(1, 1, "drone_flax")]
    [TestCase(2, 2, "shield_sg3n_a03")]
    public void SavedLocation_ProducesAQueryThePageAccepts(int slot, int configuration, string item)
    {
        var location = LocalHangarEquipmentSelection.SavedLocation(slot, configuration, item);
        Assert.That(location, Is.EqualTo($"/indexInternal.es?action=internalDockEquipment&slot={slot}&configuration={configuration}&saved={item}"));
        Assert.That(LocalHangarEquipmentSelection.TryParsePageQuery(Query(location[location.IndexOf('?')..]),
            out var parsedSlot, out var parsedConfiguration, out var saved), Is.True);
        Assert.That((parsedSlot, parsedConfiguration, saved), Is.EqualTo((slot, configuration, item)));
    }

    [Test]
    public async Task Save_RedirectsToTheNotice_ThenRendersTheCurrentQuantity()
    {
        var (sessions, equipment, session) = await CreateAsync();
        var set = await PostAsync(sessions, equipment, session.Dosid, "slot=1&configuration=2&item=laser_lf3&quantity=3");
        Assert.That(set.Response.StatusCode, Is.EqualTo(303));
        var location = set.Response.Headers.Location.ToString();
        Assert.That(location, Is.EqualTo("/indexInternal.es?action=internalDockEquipment&slot=1&configuration=2&saved=laser_lf3"));

        Assert.That(LocalHangarEquipmentSelection.TryParsePageQuery(Query(location[location.IndexOf('?')..]),
            out var slot, out var configuration, out var saved), Is.True);
        var page = LocalHangarEquipment.Page(session, slot, configuration,
            await equipment.GetAsync(session.AccountId, CancellationToken.None), saved);
        Assert.That(page, Does.Contain(
            "<p id=\"equipment-notice\" role=\"status\" data-item=\"laser_lf3\" data-quantity=\"3\">Saved: 3 × LF-3 laser equipped in configuration 2.</p>"));
        Assert.That(page.IndexOf("equipment-notice", StringComparison.Ordinal),
            Is.LessThan(page.IndexOf("<table>", StringComparison.Ordinal)));

        var clear = await PostAsync(sessions, equipment, session.Dosid, "slot=1&configuration=2&item=laser_lf3&quantity=0");
        Assert.That(clear.Response.Headers.Location.ToString(), Does.EndWith("&saved=laser_lf3"));
        page = LocalHangarEquipment.Page(session, 1, 2, await equipment.GetAsync(session.AccountId, CancellationToken.None), "laser_lf3");
        Assert.That(page, Does.Contain("data-quantity=\"0\">LF-3 laser is no longer equipped in configuration 2.</p>"));
    }

    [Test]
    public async Task Save_FailuresDoNotRedirectToANotice()
    {
        var (sessions, equipment, session) = await CreateAsync();
        var body = "slot=1&configuration=1&item=drone_flax&quantity=1";
        Assert.That((await PostAsync(sessions, equipment, session.Dosid, body)).Response.StatusCode, Is.EqualTo(303));
        foreach (var (form, status) in new[]
                 {
                     (body, 409),
                     ("slot=2&configuration=1&item=drone_flax&quantity=1", 409),
                     ("slot=1&configuration=1&item=unknown&quantity=1", 400),
                     ("slot=1&configuration=1&item=%3Cb%3E&quantity=1", 400)
                 })
        {
            var context = await PostAsync(sessions, equipment, session.Dosid, form);
            Assert.That(context.Response.StatusCode, Is.EqualTo(status), form);
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty, form);
        }
    }

    [TestCase(null)]
    [TestCase("unknown_item")]
    [TestCase("Drone_Flax")]
    public void Page_ShowsNoNoticeForAMissingOrUnownedItem(string? saved)
    {
        var page = LocalHangarEquipment.Page(Session, 1, 1, Starter(), saved);
        Assert.That(page, Does.Not.Contain("equipment-notice"));
        if (saved is not null) Assert.That(page, Does.Not.Contain(saved));
    }

    [Test]
    public void Page_NoticeReflectsTheRequestedConfigurationOnly()
    {
        var state = Starter([new LocalEquipmentAssignment(1, 1, "pet_10", 1)]);
        Assert.That(LocalHangarEquipment.Page(Session, 1, 1, state, "pet_10"),
            Does.Contain("data-quantity=\"1\">Saved: 1 × P.E.T. 10 equipped in configuration 1.</p>"));
        Assert.That(LocalHangarEquipment.Page(Session, 1, 2, state, "pet_10"),
            Does.Contain("data-quantity=\"0\">P.E.T. 10 is no longer equipped in configuration 2.</p>"));
        Assert.That(LocalHangarEquipment.Page(Session, 2, 1, state, "pet_10"),
            Does.Contain("is no longer equipped in configuration 1."));
    }

    [Test]
    public void Page_EncodesItemNamesInTheNotice()
    {
        var state = new LocalEquipmentState([new LocalEquipmentItem("odd_item", "<b>Odd</b>", "Equipment", 2)],
            [new LocalEquipmentAssignment(1, 1, "odd_item", 2)]);
        var page = LocalHangarEquipment.Page(Session, 1, 1, state, "odd_item");
        Assert.That(page, Does.Contain("Saved: 2 × &lt;b&gt;Odd&lt;/b&gt; equipped"));
        Assert.That(page, Does.Not.Contain("<b>Odd</b>"));
    }

    [Test]
    public void Page_SummarizesEquippedUnitsPerCategoryForThisConfiguration()
    {
        var state = Starter(
        [
            new LocalEquipmentAssignment(1, 1, "laser_lf3", 3),
            new LocalEquipmentAssignment(1, 1, "shield_sg3n_b02", 2),
            new LocalEquipmentAssignment(1, 1, "drone_iris", 1),
            new LocalEquipmentAssignment(1, 2, "pet_10", 1),
            new LocalEquipmentAssignment(2, 1, "drone_flax", 1)
        ]);
        Assert.That(LocalHangarEquipment.Page(Session, 1, 1, state), Does.Contain(
            "<p id=\"loadout-summary\"><span data-category=\"Equipment\">Equipment: 5</span> · " +
            "<span data-category=\"Drones\">Drones: 1</span> · <span data-category=\"PET\">PET: 0</span></p>"));
        Assert.That(LocalHangarEquipment.Page(Session, 1, 2, state), Does.Contain(
            "<span data-category=\"Equipment\">Equipment: 0</span> · <span data-category=\"Drones\">Drones: 0</span> · <span data-category=\"PET\">PET: 1</span>"));
        Assert.That(LocalHangarEquipment.Page(Session, 2, 1, state), Does.Contain(
            "<span data-category=\"Drones\">Drones: 1</span>"));
    }

    [Test]
    public void Page_SummaryIsEmptyWithoutInventory()
    {
        var page = LocalHangarEquipment.Page(Session, 1, 1, new LocalEquipmentState([], []));
        Assert.That(page, Does.Contain("<p id=\"loadout-summary\"></p>"));
    }

    private static readonly GameAuthSession Session =
        new() { AccountId = Guid.NewGuid(), GameUserId = 7, Dosid = "equipment-dosid", Company = "mmo" };

    private static LocalEquipmentState Starter(List<LocalEquipmentAssignment>? assignments = null) => new(
    [
        new("laser_lf3", "LF-3 laser", "Equipment", 4),
        new("laser_lf2", "LF-2 laser", "Equipment", 2),
        new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 4),
        new("drone_flax", "Flax drone", "Drones", 1),
        new("drone_iris", "Iris drone", "Drones", 1),
        new("pet_10", "P.E.T. 10", "PET", 1)
    ], assignments ?? []);

    private static IQueryCollection Query(string queryString) =>
        new QueryCollection(QueryHelpers.ParseQuery(queryString));

    private static async Task<(GameAuthSessionStore, LocalHangarEquipmentStore, GameAuthSession)> CreateAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!);
    }

    private static async Task<DefaultHttpContext> PostAsync(IGameAuthSessionStore sessions, LocalHangarEquipmentStore equipment,
        string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalDockEquipmentSet");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        return context;
    }
}
