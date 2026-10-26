using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalHangarExtraSlotTests
{
    private static readonly List<LocalEquipmentItem> Items =
    [
        new("extra_cpu", "Slot CPU", "Extras", 4, "extra"),
        new("extra_repair", "Repair CPU", "Extras", 4, "extra"),
        new("laser_lf3", "LF-3", "Equipment", 4, "laser")
    ];

    [TestCase(1, 2, LocalHangarChange.Applied)]
    [TestCase(1, 3, LocalHangarChange.Conflict)]
    [TestCase(2, 1, LocalHangarChange.Applied)]
    [TestCase(2, 2, LocalHangarChange.Conflict)]
    public void ExtraCapacity_MatchesDockShipSpec(int slot, int quantity, LocalHangarChange expected)
    {
        var result = LocalHangarEquipmentStore.ApplyAssignment(Items, [], slot, 1, "extra_cpu", quantity);
        Assert.That(result.Result, Is.EqualTo(expected));
    }

    [Test]
    public void ExtrasShareSlots_ButConfigurationsAreAlternatives()
    {
        List<LocalEquipmentAssignment> current = [new(1, 1, "extra_cpu", 1)];
        Assert.Multiple(() =>
        {
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, current, 1, 1, "extra_repair", 1).Result,
                Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, current, 1, 1, "extra_repair", 2).Result,
                Is.EqualTo(LocalHangarChange.Conflict));
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, current, 1, 2, "extra_repair", 2).Result,
                Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, current, 2, 1, "extra_repair", 1).Result,
                Is.EqualTo(LocalHangarChange.Applied));
        });
    }

    [Test]
    public void OldOverCapacityAssignment_CanBeReduced()
    {
        List<LocalEquipmentAssignment> old = [new(2, 1, "extra_cpu", 3)];
        Assert.Multiple(() =>
        {
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, old, 2, 1, "extra_cpu", 2).Result,
                Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, old, 2, 1, "extra_cpu", 4).Result,
                Is.EqualTo(LocalHangarChange.Conflict));
            Assert.That(LocalHangarEquipmentStore.ApplyAssignment(Items, old, 2, 1, "extra_cpu", 0).Result,
                Is.EqualTo(LocalHangarChange.Applied));
        });
    }

    [TestCase("custom", "Extras", null, "extra")]
    [TestCase("extra_old", "Equipment", null, "extra")]
    [TestCase("custom", "Equipment", "extra", "extra")]
    [TestCase("extra_named_laser", "Extras", "laser", "laser")]
    public void CapacityKind_PrefersTypeThenCategoryThenLegacyId(string id, string category, string? type, string expected) =>
        Assert.That(LocalHangarEquipmentStore.CapacityKind(new(id, id, category, 1, type)), Is.EqualTo(expected));

    [Test]
    public void Page_ShowsExtraSlotsAndClampsQuantity()
    {
        var state = new LocalEquipmentState(Items, [new(2, 1, "extra_cpu", 1)]);
        var page = LocalHangarEquipment.Page(Session(), 2, 1, state);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-slot-kind=\"extra\" data-used=\"1\" data-capacity=\"1\">Extra slots: 1/1"));
            Assert.That(page, Does.Contain("value=\"extra_repair\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"0\" value=\"0\""));
        });
    }

    [Test]
    public async Task ShopPurchase_DebitsLocalCredits_AndExtraCanBeEquipped()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new LocalHangarEquipmentStore(services.GetRequiredService<IDistributedCache>());
        var account = Guid.NewGuid();
        Assert.That(await store.PurchaseAsync(account, "extra_cpu", 2, CancellationToken.None), Is.True);
        var purchased = await store.GetAsync(account, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(purchased.Credits, Is.EqualTo(8500));
            Assert.That(purchased.Inventory.Single(item => item.Id == "extra_cpu").Quantity, Is.EqualTo(2));
        });
        Assert.That(await store.SetAssignmentAsync(account, 1, 1, "extra_cpu", 2, CancellationToken.None),
            Is.EqualTo(LocalHangarChange.Applied));
        Assert.That(await store.SetAssignmentAsync(account, 2, 1, "extra_cpu", 1, CancellationToken.None),
            Is.EqualTo(LocalHangarChange.Conflict));
    }

    private static GameAuthSession Session() => new()
    {
        AccountId = Guid.NewGuid(), Username = "pilot", GameUserId = 7, Company = "mmo"
    };
}
