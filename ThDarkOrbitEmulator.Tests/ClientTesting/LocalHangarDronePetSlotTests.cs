using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-135: drones fill ten emulator-defined drone slots and a P.E.T. fills one P.E.T. slot,
// per hangar slot and configuration, on both ships.
public class LocalHangarDronePetSlotTests
{
    private static readonly List<LocalEquipmentItem> Inventory =
    [
        new("laser_lf3", "LF-3 laser", "Equipment", 10, "laser"),
        new("drone_flax", "Flax drone", "Drones", 12, "drone"),
        new("drone_iris", "Iris drone", "Drones", 12, "drone"),
        new("pet_10", "P.E.T. 10", "PET", 3, "pet"),
        new("pet_15", "P.E.T. 15", "PET", 1, "pet")
    ];

    private static LocalHangarChange Apply(List<LocalEquipmentAssignment> current, int slot, int configuration,
        string item, int quantity, IReadOnlyList<LocalEquipmentItem>? inventory = null) =>
        LocalHangarEquipmentStore.ApplyAssignment(inventory ?? Inventory, current, slot, configuration, item, quantity).Result;

    [TestCase(1, "drone_flax", 10, LocalHangarChange.Applied)]
    [TestCase(1, "drone_flax", 11, LocalHangarChange.Conflict)]
    [TestCase(2, "drone_flax", 10, LocalHangarChange.Applied)]
    [TestCase(2, "drone_flax", 11, LocalHangarChange.Conflict)]
    [TestCase(1, "pet_10", 1, LocalHangarChange.Applied)]
    [TestCase(1, "pet_10", 2, LocalHangarChange.Conflict)]
    [TestCase(2, "pet_10", 2, LocalHangarChange.Conflict)]
    public void SingleItem_IsBoundedByDroneAndPetSlots(int slot, string item, int quantity, LocalHangarChange expected) =>
        Assert.That(Apply([], slot, 1, item, quantity), Is.EqualTo(expected));

    [Test]
    public void DroneTypes_ShareTheDroneSlots()
    {
        List<LocalEquipmentAssignment> current = [new(1, 1, "drone_flax", 8)];
        Assert.Multiple(() =>
        {
            Assert.That(Apply(current, 1, 1, "drone_iris", 2), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(current, 1, 1, "drone_iris", 3), Is.EqualTo(LocalHangarChange.Conflict));
            // Drones never use laser slots and lasers never use drone slots.
            Assert.That(Apply(current, 1, 1, "laser_lf3", 5), Is.EqualTo(LocalHangarChange.Applied));
        });
    }

    [Test]
    public void SecondPet_IsRejectedButAnotherConfigurationOrShipCanTakeOne()
    {
        List<LocalEquipmentAssignment> current = [new(1, 1, "pet_10", 1)];
        Assert.Multiple(() =>
        {
            Assert.That(Apply(current, 1, 1, "pet_15", 1), Is.EqualTo(LocalHangarChange.Conflict));
            Assert.That(Apply(current, 1, 2, "pet_15", 1), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(current, 2, 1, "pet_15", 1), Is.EqualTo(LocalHangarChange.Applied));
        });
    }

    [Test]
    public void OverCapacityDrones_CanBeLoweredButNotRaised()
    {
        // A loadout saved before Menu-135 can exceed the ten drone slots.
        List<LocalEquipmentAssignment> legacy = [new(2, 1, "drone_flax", 12)];
        Assert.Multiple(() =>
        {
            Assert.That(Apply(legacy, 2, 1, "drone_flax", 11), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(legacy, 2, 1, "drone_flax", 0), Is.EqualTo(LocalHangarChange.Applied));
            Assert.That(Apply(legacy, 2, 1, "drone_iris", 1), Is.EqualTo(LocalHangarChange.Conflict));
        });
    }

    [TestCase("drone_x", "Equipment", null, "drone")]
    [TestCase("pet_x", "Equipment", null, "pet")]
    [TestCase("custom_bot", "drone", null, "drone")]
    [TestCase("custom_bot", "Drones", null, "drone")]
    [TestCase("custom_companion", "PET", null, "pet")]
    [TestCase("custom_companion", "Equipment", "pet", "pet")]
    [TestCase("custom_cannon", "Equipment", "laser", "laser")]
    [TestCase("shield_x", "Equipment", null, "shield")]
    [TestCase("booster_x", "booster", null, null)]
    [TestCase("drone_named_booster", "booster", "booster", null)]
    public void CapacityKind_UsesTypeThenCategoryThenIdPrefix(string id, string category, string? type, string? expected) =>
        Assert.That(LocalHangarEquipmentStore.CapacityKind(new LocalEquipmentItem(id, id, category, 1, type)), Is.EqualTo(expected));

    [Test]
    public void SlotKind_StillCoversOnlyLasersAndGenerators()
    {
        Assert.That(LocalHangarEquipmentStore.SlotKind(new LocalEquipmentItem("drone_flax", "Flax", "Drones", 1, "drone")), Is.Null);
        Assert.That(() => LocalHangarEquipmentStore.SlotCapacity(1, "booster"), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Page_ShowsDroneAndPetUsageAndClampsInputs()
    {
        var state = new LocalEquipmentState(Inventory.ToList(), [new(2, 1, "drone_flax", 7), new(2, 1, "pet_10", 1)]);
        var page = LocalHangarEquipment.Page(Session(), 2, 1, state);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-slot-kind=\"drone\" data-used=\"7\" data-capacity=\"10\">Drone slots: 7/10</span>"));
            Assert.That(page, Does.Contain("data-slot-kind=\"pet\" data-used=\"1\" data-capacity=\"1\">P.E.T. slots: 1/1</span>"));
            Assert.That(page, Does.Not.Contain("slot-warning"));
            Assert.That(page, Does.Contain("value=\"drone_flax\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"10\" value=\"7\">"));
            Assert.That(page, Does.Contain("value=\"drone_iris\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"3\" value=\"0\">"));
            Assert.That(page, Does.Contain("value=\"pet_10\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"1\" value=\"1\">"));
            Assert.That(page, Does.Contain("value=\"pet_15\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"0\" value=\"0\">"));
        });
    }

    [Test]
    public void Page_WarnsAboutTwoPets()
    {
        var state = new LocalEquipmentState(Inventory.ToList(), [new(1, 2, "pet_10", 2)]);
        var page = LocalHangarEquipment.Page(Session(), 1, 2, state);
        Assert.That(page, Does.Contain("P.E.T. slots: 2/1"));
        Assert.That(page, Does.Contain("id=\"slot-warning\" role=\"alert\""));
    }

    private static GameAuthSession Session() => new()
    {
        AccountId = Guid.NewGuid(), Username = "pilot", GameUserId = 7, Company = "mmo"
    };
}
