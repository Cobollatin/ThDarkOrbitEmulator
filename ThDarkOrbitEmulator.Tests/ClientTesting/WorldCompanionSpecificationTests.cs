using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

[TestFixture]
public sealed class WorldCompanionSpecificationTests
{
    private static readonly LocalPet FueledPet = new(true, 10);

    private static LocalEquipmentState Equipment(List<LocalEquipmentItem> inventory,
        params LocalEquipmentAssignment[] assignments) => new(inventory, [.. assignments]);

    private static LocalEquipmentItem Item(string id, int quantity) => new(id, id, "Drones", quantity);

    private static IReadOnlyList<WorldCompanion> Drones(int count, string formation = "standard") =>
        Enumerable.Range(0, count).Select(i => new WorldCompanion("drone", "drone_flax", i, formation)).ToArray();

    [Test]
    public void FromEquipment_ExpandsOwnedDroneAssignmentsInAssignmentOrderWithRunningIndexes()
    {
        var equipment = Equipment([Item("drone_iris", 1), Item("drone_flax", 2)],
            new LocalEquipmentAssignment(1, 1, "drone_iris", 1), new LocalEquipmentAssignment(1, 1, "drone_flax", 2));
        var companions = WorldCompanions.FromEquipment(equipment, 1, new LocalPet(Formation: "line"));
        Assert.That(companions, Is.EqualTo(new[]
        {
            new WorldCompanion("drone", "drone_iris", 0, "line"),
            new WorldCompanion("drone", "drone_flax", 1, "line"),
            new WorldCompanion("drone", "drone_flax", 2, "line")
        }));
    }

    [Test]
    public void FromEquipment_FiltersBySlotAndConfiguration()
    {
        var equipment = Equipment([Item("drone_flax", 5)],
            new LocalEquipmentAssignment(2, 1, "drone_flax", 1), new LocalEquipmentAssignment(1, 2, "drone_flax", 2), new LocalEquipmentAssignment(1, 1, "drone_flax", 3));
        Assert.Multiple(() =>
        {
            Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet()), Has.Count.EqualTo(3));
            Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet(), 2), Has.Count.EqualTo(2));
            Assert.That(WorldCompanions.FromEquipment(equipment, 2, new LocalPet()), Has.Count.EqualTo(1));
            Assert.That(WorldCompanions.FromEquipment(equipment, 3, new LocalPet()), Is.Empty);
        });
    }

    [TestCase("drone_apis")]
    [TestCase("drone_zeus")]
    [TestCase("DRONE_FLAX")]
    [TestCase("pet_10")]
    public void FromEquipment_IgnoresItemsOutsideTheExactFlaxIrisAllowList(string itemId)
    {
        var equipment = Equipment([Item(itemId, 3)], new LocalEquipmentAssignment(1, 1, itemId, 3));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet()), Is.Empty);
    }

    [TestCase(0)]
    [TestCase(-2)]
    public void FromEquipment_SkipsNonPositiveDroneAssignments(int quantity)
    {
        var equipment = Equipment([Item("drone_flax", 4)], new LocalEquipmentAssignment(1, 1, "drone_flax", quantity));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet()), Is.Empty);
    }

    [Test]
    public void FromEquipment_DropsUnderOwnedAssignmentWholeInsteadOfCapping()
    {
        var equipment = Equipment([Item("drone_flax", 2), Item("drone_iris", 1)],
            new LocalEquipmentAssignment(1, 1, "drone_flax", 3), new LocalEquipmentAssignment(1, 1, "drone_iris", 1));
        var companions = WorldCompanions.FromEquipment(equipment, 1, new LocalPet());
        Assert.That(companions.Select(c => (c.ItemId, c.Index)), Is.EqualTo(new[] { ("drone_iris", 0) }));
    }

    [Test]
    public void FromEquipment_DoesNotCombineSplitInventoryRowsToSatisfyDroneAssignment()
    {
        var equipment = Equipment([Item("drone_flax", 1), Item("drone_flax", 1)],
            new LocalEquipmentAssignment(1, 1, "drone_flax", 2));

        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet()), Is.Empty);
    }

    [Test]
    public void FromEquipment_UsesAnySingleSufficientInventoryRowRegardlessOfRowOrder()
    {
        var equipment = Equipment([Item("drone_flax", 1), Item("DRONE_FLAX", 9), Item("drone_flax", 3)],
            new LocalEquipmentAssignment(1, 1, "drone_flax", 2));

        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet()).Select(c => (c.ItemId, c.Index)),
            Is.EqualTo(new[] { ("drone_flax", 0), ("drone_flax", 1) }));
    }

    [Test]
    public void FromEquipment_ChecksOwnershipPerAssignmentNotCumulatively()
    {
        var equipment = Equipment([Item("drone_flax", 2)],
            new LocalEquipmentAssignment(1, 1, "drone_flax", 2), new LocalEquipmentAssignment(1, 1, "drone_flax", 2));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, new LocalPet()), Has.Count.EqualTo(4));
    }

    [Test]
    public void FromEquipment_CapsDronesAtHangarDroneSlotLimitAndStillAppendsPet()
    {
        var equipment = Equipment([Item("drone_flax", 8), Item("drone_iris", 8), Item("pet_10", 1)],
            new LocalEquipmentAssignment(1, 1, "drone_flax", 8), new LocalEquipmentAssignment(1, 1, "drone_iris", 8), new LocalEquipmentAssignment(1, 1, "pet_10", 1));
        var companions = WorldCompanions.FromEquipment(equipment, 1, FueledPet);
        var drones = companions.Where(c => c.Kind == "drone").ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(drones, Has.Length.EqualTo(LocalHangarEquipmentStore.DroneSlots));
            Assert.That(drones.Count(d => d.ItemId == "drone_iris"), Is.EqualTo(2));
            Assert.That(drones.Select(d => d.Index), Is.EqualTo(Enumerable.Range(0, 10)));
            Assert.That(companions[^1], Is.EqualTo(new WorldCompanion("pet", "pet_10", 0, "standard")));
        });
    }

    [Test]
    public void FromEquipment_AppendsPetLastWithIndexZeroAndSharedFormation()
    {
        var equipment = Equipment([Item("pet_10", 1), Item("drone_flax", 1)],
            new LocalEquipmentAssignment(1, 1, "pet_10", 1), new LocalEquipmentAssignment(1, 1, "drone_flax", 1));
        var companions = WorldCompanions.FromEquipment(equipment, 1, new LocalPet(true, 1, "arrow"));
        Assert.That(companions, Is.EqualTo(new[]
        {
            new WorldCompanion("drone", "drone_flax", 0, "arrow"),
            new WorldCompanion("pet", "pet_10", 0, "arrow")
        }));
    }

    [TestCase(false, 10, 1, 1, 1, false)]
    [TestCase(true, 0, 1, 1, 1, false)]
    [TestCase(true, -5, 1, 1, 1, false)]
    [TestCase(true, 1, 0, 1, 1, false)]
    [TestCase(true, 1, 1, 0, 1, false)]
    [TestCase(true, 1, 1, 1, 2, false)]
    [TestCase(true, 1, 1, 1, 1, true)]
    public void FromEquipment_PetRequiresEnabledFuelAssignmentOwnershipAndActiveConfiguration(bool enabled, int fuel,
        int assignedQuantity, int ownedQuantity, int assignedConfiguration, bool expected)
    {
        var equipment = Equipment([Item("pet_10", ownedQuantity)], new LocalEquipmentAssignment(1, assignedConfiguration, "pet_10", assignedQuantity));
        var companions = WorldCompanions.FromEquipment(equipment, 1, new LocalPet(enabled, fuel));
        Assert.That(companions.Any(c => c.Kind == "pet"), Is.EqualTo(expected));
    }

    [Test]
    public void FromEquipment_PetOwnershipNeedsOnlyOnePositiveInventoryUnit()
    {
        var equipment = Equipment([Item("pet_10", 1)], new LocalEquipmentAssignment(1, 1, "pet_10", 3));
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, FueledPet).Single().Kind, Is.EqualTo("pet"));
    }

    [Test]
    public void FromEquipment_DuplicatePetAssignmentsAndInventoryRowsStillYieldExactlyOnePet()
    {
        var equipment = Equipment([Item("pet_10", 0), Item("PET_10", 10), Item("pet_10", 1)],
            new LocalEquipmentAssignment(1, 1, "pet_10", 2),
            new LocalEquipmentAssignment(1, 1, "pet_10", 1));

        Assert.That(WorldCompanions.FromEquipment(equipment, 1, FueledPet),
            Is.EqualTo(new[] { new WorldCompanion("pet", "pet_10", 0, "standard") }));
    }

    [Test]
    public void FromEquipment_PetRequiresExactPositiveInventoryRowAndAssignment()
    {
        var noExactInventory = Equipment([Item("PET_10", 3), Item("pet_10", 0)],
            new LocalEquipmentAssignment(1, 1, "pet_10", 1));
        var noExactAssignment = Equipment([Item("pet_10", 3)],
            new LocalEquipmentAssignment(1, 1, "PET_10", 1));
        var nonPositiveAssignments = Equipment([Item("pet_10", 3)],
            new LocalEquipmentAssignment(1, 1, "pet_10", 0),
            new LocalEquipmentAssignment(1, 1, "pet_10", -1));

        Assert.Multiple(() =>
        {
            Assert.That(WorldCompanions.FromEquipment(noExactInventory, 1, FueledPet), Is.Empty);
            Assert.That(WorldCompanions.FromEquipment(noExactAssignment, 1, FueledPet), Is.Empty);
            Assert.That(WorldCompanions.FromEquipment(nonPositiveAssignments, 1, FueledPet), Is.Empty);
        });
    }

    [Test]
    public void At_EmptyCompanionList_ReturnsEmpty() =>
        Assert.That(WorldCompanions.At([], 500, 500), Is.Empty);

    [Test]
    public void At_StandardFormation_SpreadsDronesOnLowerSemicircleOfRadius65()
    {
        var positions = WorldCompanions.At(Drones(3), 1000, 1000);
        Assert.That(positions.Select(p => (p.X, p.Y)), Is.EqualTo(new[] { (1046, 1046), (1000, 1065), (954, 1046) }));
        var single = WorldCompanions.At(Drones(1), 1000, 1000);
        Assert.That((single[0].X, single[0].Y), Is.EqualTo((1000, 1065)));
    }

    [TestCase("standard")]
    [TestCase("Arrow")]
    [TestCase("")]
    [TestCase("diamond")]
    public void At_UnknownOrDefaultFormation_FallsBackToStandardSemicircle(string formation)
    {
        var position = WorldCompanions.At(Drones(1, formation), 1000, 1000).Single();
        Assert.That((position.X, position.Y), Is.EqualTo((1000, 1065)));
    }

    [Test]
    public void At_ArrowFormation_AlternatesSidesAndWidensEveryPair()
    {
        var positions = WorldCompanions.At(Drones(4, "arrow"), 1000, 1000);
        Assert.That(positions.Select(p => (p.X - 1000, p.Y - 1000)),
            Is.EqualTo(new[] { (-45, -28), (-45, 28), (-67, -48), (-67, 48) }));
    }

    [Test]
    public void At_LineFormation_CentersWithIntegerDivisionSoEvenCountsSkewDown()
    {
        var odd = WorldCompanions.At(Drones(3, "line"), 1000, 1000);
        var even = WorldCompanions.At(Drones(4, "line"), 1000, 1000);
        Assert.Multiple(() =>
        {
            Assert.That(odd.Select(p => (p.X - 1000, p.Y - 1000)), Is.EqualTo(new[] { (-60, -30), (-60, 0), (-60, 30) }));
            Assert.That(even.Select(p => p.Y - 1000), Is.EqualTo(new[] { -30, 0, 30, 60 }));
        });
    }

    [TestCase("standard")]
    [TestCase("arrow")]
    [TestCase("line")]
    public void At_PetUsesFixedOffsetAndIsExcludedFromDroneCount(string formation)
    {
        var companions = new List<WorldCompanion>(Drones(1, formation)) { new("pet", "pet_10", 0, formation) };
        var positions = WorldCompanions.At(companions, 1000, 1000);
        var expectedDrone = WorldCompanions.At(Drones(1, formation), 1000, 1000).Single();
        Assert.Multiple(() =>
        {
            Assert.That((positions[0].X, positions[0].Y), Is.EqualTo((expectedDrone.X, expectedDrone.Y)));
            Assert.That((positions[1].X, positions[1].Y), Is.EqualTo((925, 1055)));
        });
    }

    [Test]
    public void At_UsesStoredDroneIndexesAndEachCompanionsOwnFormation()
    {
        var companions = new[]
        {
            new WorldCompanion("drone", "drone_flax", 2, "standard"),
            new WorldCompanion("pet", "pet_10", 99, "line"),
            new WorldCompanion("drone", "drone_iris", 0, "arrow")
        };

        var positions = WorldCompanions.At(companions, 1000, 1000);

        Assert.That(positions.Select(p => (p.X, p.Y)),
            Is.EqualTo(new[] { (935, 1000), (925, 1055), (955, 972) }));
        Assert.That(positions.Select(p => (p.Kind, p.ItemId, p.Index, p.Formation)),
            Is.EqualTo(companions.Select(c => (c.Kind, c.ItemId, c.Index, c.Formation))));
    }

    [Test]
    public void At_DuplicateDroneIndexesProduceIdenticalOffsetsWithoutRenumbering()
    {
        var companions = new[]
        {
            new WorldCompanion("drone", "drone_flax", 0, "line"),
            new WorldCompanion("drone", "drone_iris", 0, "line")
        };

        var positions = WorldCompanions.At(companions, 1000, 1000);

        Assert.That(positions.Select(p => (p.X, p.Y, p.Index)),
            Is.EqualTo(new[] { (940, 1000, 0), (940, 1000, 0) }));
    }

    [Test]
    public void At_ClampsEveryCompanionToInclusiveMapBounds()
    {
        var companions = new List<WorldCompanion>(Drones(3, "arrow")) { new("pet", "pet_10", 0, "arrow") };
        var low = WorldCompanions.At(companions, 0, 0);
        var high = WorldCompanions.At(companions, 21000, 13100);
        Assert.Multiple(() =>
        {
            Assert.That(low.Select(p => (p.X, p.Y)), Is.EqualTo(new[] { (0, 0), (0, 28), (0, 0), (0, 55) }));
            Assert.That(high.Select(p => (p.X, p.Y)),
                Is.EqualTo(new[] { (20955, 13072), (20955, 13100), (20933, 13052), (20925, 13100) }));
        });
    }

    [TestCase(int.MinValue, int.MinValue, 0, 0)]
    [TestCase(int.MinValue, int.MaxValue, 0, 13100)]
    [TestCase(int.MaxValue, int.MinValue, 21000, 0)]
    [TestCase(int.MaxValue, int.MaxValue, 21000, 13100)]
    public void At_ExtremeShipCoordinates_ClampAfterWidenedOffsetAddition(int x, int y, int expectedX, int expectedY)
    {
        var companions = new List<WorldCompanion>(Drones(2)) { new("pet", "pet_10", 0, "standard") };
        var positions = WorldCompanions.At(companions, x, y);
        Assert.That(positions.Select(p => (p.X, p.Y)),
            Is.EqualTo(Enumerable.Repeat((expectedX, expectedY), companions.Count)));
    }

    [Test]
    public void At_PreservesOrderAndCopiesCompanionIdentity()
    {
        var companions = new[]
        {
            new WorldCompanion("drone", "drone_iris", 0, "line"), new WorldCompanion("pet", "pet_10", 0, "line")
        };
        var positions = WorldCompanions.At(companions, 300, 300);
        Assert.That(positions.Select(p => (p.Kind, p.ItemId, p.Index, p.Formation)),
            Is.EqualTo(companions.Select(c => (c.Kind, c.ItemId, c.Index, c.Formation))));
    }

    [Test]
    public async Task PetStore_MissingRecord_ReturnsInactiveUnfueledStandardPet()
    {
        var (store, _) = CreatePetStore();
        Assert.That(await store.GetAsync(Guid.NewGuid(), default), Is.EqualTo(new LocalPet(false, 0, "standard")));
    }

    [Test]
    public async Task PetStore_RoundTripsPerAccountUnderDocumentedKey()
    {
        var (store, cache) = CreatePetStore();
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        await store.SetAsync(account, new LocalPet(true, 42, "arrow"), default);
        Assert.That(await store.GetAsync(account, default), Is.EqualTo(new LocalPet(true, 42, "arrow")));
        Assert.That(await store.GetAsync(other, default), Is.EqualTo(new LocalPet()));
        Assert.That(await cache.GetStringAsync($"world:pet:{account:N}"), Does.Contain("\"Fuel\":42"));
    }

    [Test]
    public async Task PetStore_JsonNullRecord_FallsBackToDefaultPet()
    {
        var (store, cache) = CreatePetStore();
        var account = Guid.NewGuid();
        await cache.SetStringAsync($"world:pet:{account:N}", "null");
        Assert.That(await store.GetAsync(account, default), Is.EqualTo(new LocalPet()));
    }

    [TestCase("{}", false, 0, "standard")]
    [TestCase("{\"Enabled\":true}", true, 0, "standard")]
    [TestCase("{\"Fuel\":5,\"Formation\":\"arrow\"}", false, 5, "arrow")]
    [TestCase("{\"enabled\":true,\"fuel\":5,\"formation\":\"line\"}", false, 0, "standard")]
    [TestCase("{\"Enabled\":true,\"Fuel\":5,\"Formation\":\"line\",\"FutureField\":42}", true, 5, "line")]
    public async Task PetStore_ValidSparseOrExtendedJson_UsesDeclaredDefaultsAndExactPropertyNames(
        string json, bool enabled, int fuel, string formation)
    {
        var (store, cache) = CreatePetStore();
        var account = Guid.NewGuid();
        await cache.SetStringAsync($"world:pet:{account:N}", json);

        Assert.That(await store.GetAsync(account, default),
            Is.EqualTo(new LocalPet(enabled, fuel, formation)));
    }

    [Test]
    public async Task PetStore_EnabledWithoutFuel_DoesNotProjectPetUntilPositiveFuelIsSaved()
    {
        var (store, cache) = CreatePetStore();
        var account = Guid.NewGuid();
        var equipment = Equipment([Item("pet_10", 1)], new LocalEquipmentAssignment(1, 1, "pet_10", 1));
        await cache.SetStringAsync($"world:pet:{account:N}", "{\"Enabled\":true}");

        Assert.That(WorldCompanions.FromEquipment(equipment, 1, await store.GetAsync(account, default)), Is.Empty);
        await store.SetAsync(account, new LocalPet(true, 1, "arrow"), default);
        Assert.That(WorldCompanions.FromEquipment(equipment, 1, await store.GetAsync(account, default)),
            Is.EqualTo(new[] { new WorldCompanion("pet", "pet_10", 0, "arrow") }));
    }

    [TestCase("")]
    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("{\"Fuel\":\"not-a-number\"}")]
    public async Task PetStore_DamagedJsonRecord_FallsBackToInactivePetWithoutAffectingOtherAccounts(string damagedJson)
    {
        var (store, cache) = CreatePetStore();
        var damaged = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        await store.SetAsync(healthy, new LocalPet(true, 7, "arrow"), default);
        await cache.SetStringAsync($"world:pet:{damaged:N}", damagedJson);
        var damagedPet = await store.GetAsync(damaged, default);
        var healthyPet = await store.GetAsync(healthy, default);

        Assert.Multiple(() =>
        {
            Assert.That(damagedPet, Is.EqualTo(new LocalPet()));
            Assert.That(healthyPet, Is.EqualTo(new LocalPet(true, 7, "arrow")));
        });
    }

    [Test]
    public async Task PetStore_WriteUsesAccountKeyJsonAndSlidingExpiryWithoutAbsoluteExpiry()
    {
        var cache = new RecordingPetCache();
        var account = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await new LocalPetStore(cache).SetAsync(account, new LocalPet(true, 17, "line"), cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(cache.LastKey, Is.EqualTo($"world:pet:{account:N}"));
            Assert.That(System.Text.Encoding.UTF8.GetString(cache.LastValue!),
                Is.EqualTo("{\"Enabled\":true,\"Fuel\":17,\"Formation\":\"line\"}"));
            Assert.That(cache.LastOptions?.SlidingExpiration, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(cache.LastOptions?.AbsoluteExpiration, Is.Null);
            Assert.That(cache.LastOptions?.AbsoluteExpirationRelativeToNow, Is.Null);
            Assert.That(cache.LastToken, Is.EqualTo(cancellation.Token));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PetStore_CacheReadFailurePropagatesWithoutDefaulting(bool cancelled)
    {
        var cache = new RecordingPetCache
        {
            Failure = cancelled ? new OperationCanceledException() : new IOException("cache unavailable")
        };
        using var cancellation = new CancellationTokenSource();
        var read = new LocalPetStore(cache).GetAsync(Guid.NewGuid(), cancellation.Token);
        if (cancelled)
            Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
        else
            Assert.ThrowsAsync<IOException>(async () => await read);
        Assert.That(cache.LastToken, Is.EqualTo(cancellation.Token));
    }

    [Test]
    public void PetStore_CacheWriteFailurePropagates()
    {
        var cache = new RecordingPetCache { Failure = new IOException("cache unavailable") };
        var write = new LocalPetStore(cache).SetAsync(Guid.NewGuid(), new LocalPet(true, 1), default);
        Assert.ThrowsAsync<IOException>(async () => await write);
    }

    private sealed class RecordingPetCache : IDistributedCache
    {
        public Exception? Failure { get; init; }
        public string? LastKey { get; private set; }
        public byte[]? LastValue { get; private set; }
        public DistributedCacheEntryOptions? LastOptions { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            LastKey = key;
            LastToken = token;
            return Failure is null ? Task.FromResult<byte[]?>(null) : Task.FromException<byte[]?>(Failure);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            LastKey = key;
            LastValue = value;
            LastOptions = options;
            LastToken = token;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private static (LocalPetStore Store, IDistributedCache Cache) CreatePetStore()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        return (new LocalPetStore(cache), cache);
    }
}
