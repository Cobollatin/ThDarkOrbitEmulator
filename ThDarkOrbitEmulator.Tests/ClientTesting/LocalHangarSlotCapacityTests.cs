using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-134: lasers fill laser slots and shields fill generator slots of the Dock ship spec
// (Pusat Plus 5/12, Phoenix 1/1), per hangar slot and configuration.
public class LocalHangarSlotCapacityTests
{
    private static readonly List<LocalEquipmentItem> Inventory =
    [
        new("laser_lf3", "LF-3 laser", "Equipment", 10, "laser"),
        new("laser_lf2", "LF-2 laser", "Equipment", 4, "laser"),
        new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 20, "shield"),
        new("shield_sg3n_a03", "SG3N-A03 shield", "Equipment", 2, "shield"),
        new("drone_flax", "Flax drone", "Drones", 3, "drone"),
        new("pet_10", "P.E.T. 10", "PET", 1, "pet")
    ];

    private static LocalHangarChange Apply(List<LocalEquipmentAssignment> current, int slot, int configuration,
        string item, int quantity, IReadOnlyList<LocalEquipmentItem>? inventory = null) =>
        LocalHangarEquipmentStore.ApplyAssignment(inventory ?? Inventory, current, slot, configuration, item, quantity).Result;

    [TestCase(1, "laser_lf3", 5, LocalHangarChange.Applied)]
    [TestCase(1, "laser_lf3", 6, LocalHangarChange.Conflict)]
    [TestCase(1, "shield_sg3n_b02", 12, LocalHangarChange.Applied)]
    [TestCase(1, "shield_sg3n_b02", 13, LocalHangarChange.Conflict)]
    [TestCase(2, "laser_lf3", 1, LocalHangarChange.Applied)]
    [TestCase(2, "laser_lf3", 2, LocalHangarChange.Conflict)]
    [TestCase(2, "shield_sg3n_b02", 1, LocalHangarChange.Applied)]
    [TestCase(2, "shield_sg3n_b02", 2, LocalHangarChange.Conflict)]
    [TestCase(2, "drone_flax", 3, LocalHangarChange.Applied)]
    [TestCase(2, "pet_10", 1, LocalHangarChange.Applied)]
    public void SingleItem_IsBoundedBySlotsOfItsKind(int slot, string item, int quantity, LocalHangarChange expected) =>
        Assert.That(Apply([], slot, 1, item, quantity), Is.EqualTo(expected));

    [Test]
    public void DifferentItemsOfOneKind_ShareTheSlots()
    {
        List<LocalEquipmentAssignment> pusat = [new(1, 1, "laser_lf3", 4)];
        List<LocalEquipmentAssignment> phoenix = [new(2, 1, "shield_sg3n_b02", 1)];
        Assert.Multiple(() =>
        {
            Assert.That(Apply(pusat, 1, 1, "laser_lf2", 1), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(pusat, 1, 1, "laser_lf2", 2), Is.EqualTo(LocalHangarChange.Conflict));
            Assert.That(Apply(phoenix, 2, 1, "shield_sg3n_a03", 1), Is.EqualTo(LocalHangarChange.Conflict));
            // Lasers never use generator slots and vice versa.
            Assert.That(Apply(phoenix, 2, 1, "laser_lf2", 1), Is.EqualTo(LocalHangarChange.Applied));
        });
    }

    [Test]
    public void SlotsAreCountedPerConfigurationAndShip()
    {
        List<LocalEquipmentAssignment> current = [new(1, 1, "laser_lf3", 5)];
        Assert.Multiple(() =>
        {
            Assert.That(Apply(current, 1, 2, "laser_lf3", 5), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(current, 2, 1, "laser_lf2", 1), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(current, 1, 1, "laser_lf2", 1), Is.EqualTo(LocalHangarChange.Conflict));
        });
    }

    [Test]
    public void OverCapacityLoadout_CanBeLoweredButNotRaised()
    {
        // An assignment saved before Menu-134 can exceed the Phoenix's single laser slot.
        List<LocalEquipmentAssignment> legacy = [new(2, 1, "laser_lf3", 3)];
        Assert.Multiple(() =>
        {
            Assert.That(Apply(legacy, 2, 1, "laser_lf3", 2), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(legacy, 2, 1, "laser_lf3", 0), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(legacy, 2, 1, "laser_lf3", 4), Is.EqualTo(LocalHangarChange.Conflict));
            Assert.That(Apply(legacy, 2, 1, "laser_lf2", 1), Is.EqualTo(LocalHangarChange.Conflict));
        });
    }

    [Test]
    public void RejectedChange_LeavesAssignmentsUnchanged()
    {
        List<LocalEquipmentAssignment> current = [new(2, 1, "laser_lf3", 1)];
        var (result, after) = LocalHangarEquipmentStore.ApplyAssignment(Inventory, current, 2, 1, "laser_lf2", 1);
        Assert.That(result, Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(after, Is.EqualTo(current));
    }

    [Test]
    public void LegacyLargeLoadout_CanRenderAndBeRepairedWithoutOverflow()
    {
        var inventory = new List<LocalEquipmentItem>
        {
            new("laser_a", "Laser A", "Equipment", int.MaxValue, "laser"),
            new("laser_b", "Laser B", "Equipment", int.MaxValue, "laser"),
            new("laser_c", "Laser C", "Equipment", 1, "laser")
        };
        var legacy = new List<LocalEquipmentAssignment>
        {
            new(1, 1, "laser_a", int.MaxValue),
            new(1, 1, "laser_b", int.MaxValue)
        };

        var page = LocalHangarEquipment.Page(Session("mmo"), 1, 1, new LocalEquipmentState(inventory, legacy));
        Assert.That(page, Does.Contain("Laser slots: 4294967294/5"));
        Assert.That(page, Does.Contain("id=\"slot-warning\""));

        var (rejected, unchanged) = LocalHangarEquipmentStore.ApplyAssignment(inventory, legacy, 1, 1, "laser_c", 1);
        Assert.That(rejected, Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(unchanged, Is.EqualTo(legacy));

        var (lowered, repaired) = LocalHangarEquipmentStore.ApplyAssignment(inventory, legacy, 1, 1, "laser_a", 0);
        Assert.That(lowered, Is.EqualTo(LocalHangarChange.Applied));
        Assert.That(repaired, Has.Count.EqualTo(1));
    }

    [Test]
    public void CorruptCrossShipReservations_ReturnConflictWithoutOverflow()
    {
        var inventory = new List<LocalEquipmentItem> { new("laser_a", "Laser A", "Equipment", int.MaxValue, "laser") };
        var legacy = new List<LocalEquipmentAssignment>
        {
            new(2, 1, "laser_a", int.MaxValue),
            new(2, 2, "laser_a", int.MaxValue)
        };
        var (result, unchanged) = LocalHangarEquipmentStore.ApplyAssignment(inventory, legacy, 1, 1, "laser_a", 1);
        Assert.That(result, Is.EqualTo(LocalHangarChange.Conflict));
        Assert.That(unchanged, Is.EqualTo(legacy));
    }

    [Test]
    public async Task Post_LargeLegacyLoadout_Returns409AndKeepsAssignments()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var account = new AccountSummary(Guid.NewGuid(), "pilot", null, 7);
        var created = (await sessions.CreateSessionAsync(account, null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(created.Dosid, "mmo", CancellationToken.None), Is.True);
        var inventory = new List<LocalEquipmentItem>
        {
            new("laser_a", "Laser A", "Equipment", int.MaxValue, "laser"),
            new("laser_b", "Laser B", "Equipment", int.MaxValue, "laser"),
            new("laser_c", "Laser C", "Equipment", 1, "laser")
        };
        var legacy = new List<LocalEquipmentAssignment>
        {
            new(1, 1, "laser_a", int.MaxValue),
            new(1, 1, "laser_b", int.MaxValue)
        };
        await cache.SetStringAsync($"localhangar:equipment:{account.Id:N}",
            JsonSerializer.Serialize(new LocalEquipmentState(inventory, legacy)), CancellationToken.None);
        var equipment = new LocalHangarEquipmentStore(cache);

        var response = await PostAsync(sessions, equipment, created.Dosid,
            "slot=1&configuration=1&item=laser_c&quantity=1");
        Assert.That(response.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await equipment.GetAsync(account.Id, CancellationToken.None)).Assignments, Is.EqualTo(legacy));
    }

    [TestCase("laser_x", "Equipment", null, "laser")]
    [TestCase("shield_x", "Equipment", null, "shield")]
    [TestCase("custom_cannon", "laser", null, "laser")]
    [TestCase("custom_generator", "shield", null, "shield")]
    [TestCase("custom_cannon", "Equipment", "laser", "laser")]
    [TestCase("laser_named_drone", "Drones", "drone", null)]
    [TestCase("drone_flax", "Drones", null, null)]
    [TestCase("pet_10", "PET", "pet", null)]
    [TestCase("booster_x", "booster", null, null)]
    [TestCase("laser_boost", "Equipment", "booster", null)]
    [TestCase("shield_boost", "shield", "booster", null)]
    [TestCase("laser_drone", "Drones", null, null)]
    public void SlotKind_UsesTypeThenCategoryThenIdPrefix(string id, string category, string? type, string? expected) =>
        Assert.That(LocalHangarEquipmentStore.SlotKind(new LocalEquipmentItem(id, id, category, 1, type)), Is.EqualTo(expected));

    [Test]
    public void ExplicitBoosterType_DoesNotConsumeLaserSlotsOrRestrictAssignment()
    {
        List<LocalEquipmentItem> items =
        [
            new("laser_lf3", "LF-3", "Equipment", 1, "laser"),
            new("laser_boost", "Laser booster", "Equipment", 3, "booster")
        ];
        List<LocalEquipmentAssignment> current = [new(2, 1, "laser_lf3", 1)];

        var (result, after) = LocalHangarEquipmentStore.ApplyAssignment(items, current, 2, 1, "laser_boost", 3);
        var page = LocalHangarEquipment.Page(Session("mmo"), 2, 1, new LocalEquipmentState(items, after));
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(LocalHangarEquipmentStore.CapacityKind(items[1]), Is.Null);
            Assert.That(page, Does.Contain("Laser slots: 1/1"));
            Assert.That(page, Does.Not.Contain("id=\"slot-warning\""));
            Assert.That(page, Does.Contain("value=\"laser_boost\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"3\" value=\"3\">"));
        });
    }

    [Test]
    public void LegacyDroneCategory_TakesPrecedenceOverLaserIdPrefix()
    {
        var drone = new LocalEquipmentItem("laser_named_drone", "Old drone", "Drones", 2);
        Assert.Multiple(() =>
        {
            Assert.That(LocalHangarEquipmentStore.CapacityKind(drone), Is.EqualTo("drone"));
            Assert.That(LocalHangarEquipmentStore.SlotKind(drone), Is.Null);
            Assert.That(Apply([], 2, 1, drone.Id, 2, [drone]), Is.EqualTo(LocalHangarChange.Applied));
        });
    }

    [Test]
    public void PostgresShapedItems_AreLimitedByTheirType()
    {
        // ReadOwnedEquipmentAsync puts the raw catalog type in Category; an admin item id need not be prefixed.
        List<LocalEquipmentItem> owned = [new("admin_cannon", "Admin cannon", "laser", 5, "laser")];
        Assert.Multiple(() =>
        {
            Assert.That(Apply([], 2, 1, "admin_cannon", 1, owned), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply([], 2, 1, "admin_cannon", 2, owned), Is.EqualTo(LocalHangarChange.Conflict));
        });
    }

    [Test]
    public void Page_ShowsSlotUsageAndClampsInputs()
    {
        var state = new LocalEquipmentState(Inventory.ToList(),
            [new(1, 1, "laser_lf3", 3), new(1, 1, "shield_sg3n_b02", 2)]);
        var page = LocalHangarEquipment.Page(Session("mmo"), 1, 1, state);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-slot-kind=\"laser\" data-used=\"3\" data-capacity=\"5\">Laser slots: 3/5</span>"));
            Assert.That(page, Does.Contain("data-slot-kind=\"shield\" data-used=\"2\" data-capacity=\"12\">Generator slots: 2/12</span>"));
            Assert.That(page, Does.Not.Contain("slot-warning"));
            // LF-3: 3 assigned + 2 free laser slots; LF-2: 0 + 2 free; SG3N-A03 is bounded by its stock of 2.
            Assert.That(page, Does.Contain("value=\"laser_lf3\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"5\" value=\"3\">"));
            Assert.That(page, Does.Contain("value=\"laser_lf2\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"2\" value=\"0\">"));
            Assert.That(page, Does.Contain("value=\"shield_sg3n_b02\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"12\" value=\"2\">"));
            Assert.That(page, Does.Contain("value=\"shield_sg3n_a03\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"2\" value=\"0\">"));
            Assert.That(page, Does.Contain("value=\"drone_flax\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"3\" value=\"0\">"));
        });
    }

    [Test]
    public void Page_WarnsAboutOverCapacityAndKeepsCurrentValueSelectable()
    {
        var state = new LocalEquipmentState(Inventory.ToList(), [new(2, 1, "laser_lf3", 3)]);
        var page = LocalHangarEquipment.Page(Session("eic"), 2, 1, state);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Laser slots: 3/1"));
            Assert.That(page, Does.Contain("Generator slots: 0/1"));
            Assert.That(page, Does.Contain("id=\"slot-warning\" role=\"alert\""));
            Assert.That(page, Does.Contain("value=\"laser_lf3\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"3\" value=\"3\">"));
            Assert.That(page, Does.Contain("value=\"laser_lf2\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"0\" value=\"0\">"));
        });
    }

    [Test]
    public void Page_LegacyCacheItemsWithoutType_UseIdPrefix()
    {
        List<LocalEquipmentItem> legacy = [new("laser_lf3", "LF-3 laser", "Equipment", 4), new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 4)];
        var page = LocalHangarEquipment.Page(Session("vru"), 2, 2,
            new LocalEquipmentState(legacy, [new(2, 2, "shield_sg3n_b02", 1)]));
        Assert.That(page, Does.Contain("Laser slots: 0/1"));
        Assert.That(page, Does.Contain("Generator slots: 1/1"));
    }

    [Test]
    public async Task Post_OverSlotCapacity_Returns409AndKeepsAssignments()
    {
        var (sessions, equipment, session) = await CreateAsync();
        Assert.That((await PostAsync(sessions, equipment, session.Dosid, "slot=2&configuration=1&item=laser_lf3&quantity=1")).Response.StatusCode, Is.EqualTo(303));
        var context = await PostAsync(sessions, equipment, session.Dosid, "slot=2&configuration=1&item=laser_lf2&quantity=1");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var assignments = (await equipment.GetAsync(session.AccountId, CancellationToken.None)).Assignments;
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(409));
            Assert.That(body, Does.Contain("free slots"));
            Assert.That(assignments.Select(a => (a.HangarSlot, a.Configuration, a.ItemId, a.Quantity)),
                Is.EqualTo(new[] { (2, 1, "laser_lf3", 1) }));
        });
    }

    [Test]
    public async Task Post_StarterLoadoutFitsPusatPlus()
    {
        var (sessions, equipment, session) = await CreateAsync();
        foreach (var body in new[] { "item=laser_lf3&quantity=4", "item=laser_lf2&quantity=1", "item=shield_sg3n_b02&quantity=4", "item=shield_sg3n_a03&quantity=2" })
            Assert.That((await PostAsync(sessions, equipment, session.Dosid, $"slot=1&configuration=1&{body}")).Response.StatusCode, Is.EqualTo(303), body);
        Assert.That((await PostAsync(sessions, equipment, session.Dosid, "slot=1&configuration=1&item=laser_lf2&quantity=2")).Response.StatusCode, Is.EqualTo(409));
    }

    private static GameAuthSession Session(string company) => new()
    {
        AccountId = Guid.NewGuid(), Username = "pilot", GameUserId = 7, Company = company
    };

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
