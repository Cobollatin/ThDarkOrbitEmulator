using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarEquipmentValidationTests
{
    private const string ValidBody = "slot=1&configuration=1&item=drone_flax&quantity=1";

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task NonPost_Returns405WithAllowHeader(string method)
    {
        var (sessions, equipment, session, account) = await CreateAsync();
        var context = Request(session.Dosid, ValidBody, method: method);
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(405));
        Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Is.Empty);
    }

    [TestCase("")]
    [TestCase("?action=INTERNALDOCKEQUIPMENTSET")]
    [TestCase("?action=internalDockEquipmentSet&action=internalDockEquipmentSet")]
    [TestCase("?action=internalDockEquipmentSet&slot=2")]
    [TestCase("?action=internalDockEquipmentSet&from=dock")]
    public async Task NonCanonicalQuery_Returns400(string query)
    {
        var (sessions, equipment, session, account) = await CreateAsync();
        var context = Request(session.Dosid, ValidBody, query);
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Is.Empty);
    }

    [TestCase("slot=01&configuration=1&item=drone_flax&quantity=1")]
    [TestCase("slot=%2B1&configuration=1&item=drone_flax&quantity=1")]
    [TestCase("slot=%201&configuration=1&item=drone_flax&quantity=1")]
    [TestCase("slot=1&configuration=02&item=drone_flax&quantity=1")]
    [TestCase("slot=1&configuration=3&item=drone_flax&quantity=1")]
    [TestCase("slot=1&configuration=1&item=drone_flax&quantity=01")]
    [TestCase("slot=1&configuration=1&item=drone_flax&quantity=%2B1")]
    [TestCase("slot=1&configuration=1&item=drone_flax&quantity=-0")]
    [TestCase("slot=1&configuration=1&item=drone_flax&quantity=1.0")]
    [TestCase("slot=1&configuration=1&item=drone_flax&quantity=1000")]
    [TestCase("slot=1&configuration=1&item=drone_flax&quantity=%EF%BC%91")]
    [TestCase("slot=1&configuration=1&item=drone%20flax&quantity=1")]
    [TestCase("slot=1&configuration=1&item=%3Cscript%3E&quantity=1")]
    public async Task NonCanonicalFields_Return400WithoutChange(string body)
    {
        var (sessions, equipment, session, account) = await CreateAsync();
        var context = Request(session.Dosid, body);
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Is.Empty);
    }

    [Test]
    public async Task OversizedItemId_Returns400()
    {
        var (sessions, equipment, session, _) = await CreateAsync();
        var context = Request(session.Dosid, $"slot=1&configuration=1&item={new string('a', 65)}&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task NonFormBody_Returns400()
    {
        var (sessions, equipment, session, _) = await CreateAsync();
        var context = Request(session.Dosid, "{\"slot\":1}", contentType: "application/json");
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [Test]
    public async Task QuantityZero_ClearsAssignment_AndSecondClearConflicts()
    {
        var (sessions, equipment, session, account) = await CreateAsync();
        var set = Request(session.Dosid, ValidBody);
        await LocalHangarEquipmentSelection.HandleAsync(set, sessions, equipment, CancellationToken.None);
        Assert.That(set.Response.StatusCode, Is.EqualTo(303));
        Assert.That(set.Response.Headers.Location.ToString(),
            Is.EqualTo("/indexInternal.es?action=internalDockEquipment&slot=1&configuration=1&saved=drone_flax"));

        var clear = Request(session.Dosid, "slot=1&configuration=1&item=drone_flax&quantity=0");
        await LocalHangarEquipmentSelection.HandleAsync(clear, sessions, equipment, CancellationToken.None);
        Assert.That(clear.Response.StatusCode, Is.EqualTo(303));
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Is.Empty);

        var again = Request(session.Dosid, "slot=1&configuration=1&item=drone_flax&quantity=0");
        await LocalHangarEquipmentSelection.HandleAsync(again, sessions, equipment, CancellationToken.None);
        Assert.That(again.Response.StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task SecondShipConfiguration_IsSeparateLoadout()
    {
        var (sessions, equipment, session, account) = await CreateAsync();
        var context = Request(session.Dosid, "slot=2&configuration=2&item=laser_lf2&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(context, sessions, equipment, CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
        var assignment = (await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments.Single();
        Assert.That((assignment.HangarSlot, assignment.Configuration, assignment.ItemId, assignment.Quantity),
            Is.EqualTo((2, 2, "laser_lf2", 1)));
    }

    [TestCase("?action=internalDockEquipment&slot=1&configuration=1", true, 1, 1)]
    [TestCase("?action=internalDockEquipment&slot=2&configuration=2&from=dock&prc=100&lang=en", true, 2, 2)]
    [TestCase("?action=internalDockEquipment&slot=01&configuration=1", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=%2B2", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&slot=3&configuration=1", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&slot=1", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=1&slot=2", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=1&account=other", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&slot=1&configuration=1&lang=en&lang=de", false, 0, 0)]
    [TestCase("?action=INTERNALDOCKEQUIPMENT&slot=1&configuration=1", false, 0, 0)]
    [TestCase("?action=internalDockEquipment&Slot=1&configuration=1", false, 0, 0)]
    public void PageQuery_AcceptsOnlyCanonicalParameters(string query, bool expected, int slot, int configuration)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        var ok = LocalHangarEquipmentSelection.TryParsePageQuery(context.Request.Query, out var parsedSlot, out var parsedConfiguration);
        Assert.That(ok, Is.EqualTo(expected));
        if (expected)
            Assert.That((parsedSlot, parsedConfiguration), Is.EqualTo((slot, configuration)));
    }

    [Test]
    public void PageQuery_RejectsOversizedOptionalParameter()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(
            $"?action=internalDockEquipment&slot=1&configuration=1&from={new string('x', 65)}");
        Assert.That(LocalHangarEquipmentSelection.TryParsePageQuery(context.Request.Query, out _, out _), Is.False);
    }

    [TestCase("from")]
    [TestCase("prc")]
    [TestCase("lang")]
    public void PageQuery_RejectsControlCharactersInNavigationParameters(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(
            $"?action=internalDockEquipment&slot=1&configuration=1&{key}=dock%0Aother");
        Assert.That(LocalHangarEquipmentSelection.TryParsePageQuery(context.Request.Query, out _, out _), Is.False);
    }

    [Test]
    public void Page_OffersOnlyQuantitiesAcceptedByPostAfterLargeInventoryGrant()
    {
        var state = new LocalEquipmentState(
            [new LocalEquipmentItem("custom_module", "Custom module", "Misc", 1500, "misc")], []);
        var page = LocalHangarEquipment.Page(new GameAuthSession { Company = "mmo", GameUserId = 7 }, 1, 1, state);
        Assert.That(page, Does.Contain("name=\"quantity\" min=\"0\" max=\"999\" value=\"0\""));
    }

    [Test]
    public void LegacyAssignmentAboveFormLimit_CanBeLowered()
    {
        var inventory = new List<LocalEquipmentItem>
        {
            new("custom_module", "Custom module", "Misc", 1500, "misc")
        };
        var assignments = new List<LocalEquipmentAssignment>
        {
            new(1, 1, "custom_module", 1200)
        };
        var page = LocalHangarEquipment.Page(new GameAuthSession { Company = "mmo", GameUserId = 7 }, 1, 1,
            new LocalEquipmentState(inventory, assignments));
        var change = LocalHangarEquipmentStore.ApplyAssignment(inventory, assignments, 1, 1,
            "custom_module", 999);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("name=\"quantity\" min=\"0\" max=\"999\" value=\"999\""));
            Assert.That(change.Result, Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(change.Assignments.Single().Quantity, Is.EqualTo(999));
        });
    }

    [TestCase("item\"><script>alert(1)</script>")]
    [TestCase("item with spaces")]
    [TestCase("cañon")]
    public void Page_ShowsCatalogItemWithoutAnUnsubmittableEquipForm(string itemId)
    {
        var state = new LocalEquipmentState(
            [new LocalEquipmentItem(itemId, "Custom laser", "Equipment", 2, "laser")], []);
        var page = LocalHangarEquipment.Page(new GameAuthSession { Company = "mmo", GameUserId = 7 }, 1, 1, state);

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Custom laser"));
            Assert.That(page, Does.Contain("data-equipment-unavailable"));
            Assert.That(page, Does.Not.Contain("name=\"item\""));
            Assert.That(page, Does.Not.Contain("<script>"));
            Assert.That(page, Does.Not.Contain("<form method=\"post\""));
        });
    }

    [Test]
    public void Page_StillOffersCanonicalCatalogIdsForEquip()
    {
        var state = new LocalEquipmentState(
            [new LocalEquipmentItem("custom-laser.v2", "Custom laser", "Equipment", 2, "laser")], []);
        var page = LocalHangarEquipment.Page(new GameAuthSession { Company = "mmo", GameUserId = 7 }, 1, 1, state);

        Assert.That(page, Does.Contain("name=\"item\" value=\"custom-laser.v2\""));
        Assert.That(page, Does.Not.Contain("data-equipment-unavailable"));
    }

    [Test]
    public async Task CustomCatalogIds_OnlyCanonicalItemCanBeEquipped()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var equipment = new LocalHangarEquipmentStore(cache);
        var state = new LocalEquipmentState(
            [new("custom-laser.v2", "Custom laser", "Equipment", 2, "laser"),
                new("bad item", "Broken catalog item", "Equipment", 2, "laser")], []);
        await cache.SetStringAsync($"localhangar:equipment:{account.Id:N}", JsonSerializer.Serialize(state));

        var page = LocalHangarEquipment.Page(session, 1, 1, await equipment.GetAsync(account.Id, CancellationToken.None));
        Assert.That(page, Does.Contain("name=\"item\" value=\"custom-laser.v2\""));
        Assert.That(page, Does.Contain("Broken catalog item"));
        Assert.That(page, Does.Contain("data-equipment-unavailable"));

        var valid = Request(session.Dosid, "slot=1&configuration=1&item=custom-laser.v2&quantity=2");
        await LocalHangarEquipmentSelection.HandleAsync(valid, sessions, equipment, CancellationToken.None);
        var invalid = Request(session.Dosid, "slot=1&configuration=1&item=bad+item&quantity=1");
        await LocalHangarEquipmentSelection.HandleAsync(invalid, sessions, equipment, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(valid.Response.StatusCode, Is.EqualTo(303));
            Assert.That(invalid.Response.StatusCode, Is.EqualTo(400));
        });
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments,
            Is.EqualTo(new[] { new LocalEquipmentAssignment(1, 1, "custom-laser.v2", 2) }));
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalHangarEquipmentStore Equipment, GameAuthSession Session, AccountSummary Account)> CreateAsync()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7);
        var session = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session, account);
    }

    private static DefaultHttpContext Request(string? dosid, string body,
        string query = "?action=internalDockEquipmentSet", string method = "POST",
        string contentType = "application/x-www-form-urlencoded")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = contentType;
        if (dosid is not null)
            context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }
}
