using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// Local gameplay state. A P.E.T. is inactive until the pilot enables it and has fuel.
public sealed record LocalPet(bool Enabled = false, int Fuel = 0, string Formation = "standard");

public sealed class LocalPetStore(IDistributedCache cache)
{
    private static string Key(Guid accountId) => $"world:pet:{accountId:N}";

    public async Task<LocalPet> GetAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        if (json is null) return new LocalPet();
        try
        {
            return JsonSerializer.Deserialize<LocalPet>(json) ?? new LocalPet();
        }
        catch (JsonException)
        {
            return new LocalPet();
        }
    }

    public Task SetAsync(Guid accountId, LocalPet pet, CancellationToken ct) =>
        cache.SetStringAsync(Key(accountId), JsonSerializer.Serialize(pet),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
}

public sealed record WorldCompanion(string Kind, string ItemId, int Index, string Formation);
public sealed record WorldCompanionPosition(string Kind, string ItemId, int Index, string Formation, int X, int Y);

public static class WorldCompanions
{
    public static IReadOnlyList<WorldCompanion> FromEquipment(LocalEquipmentState equipment, int slot, LocalPet pet,
        int configuration = 1)
    {
        var result = new List<WorldCompanion>();
        foreach (var assignment in equipment.Assignments.Where(a => a.HangarSlot == slot
            && a.Configuration == configuration && (a.ItemId is "drone_flax" or "drone_iris") && a.Quantity > 0))
        {
            if (!equipment.Inventory.Any(i => i.Id == assignment.ItemId && i.Quantity >= assignment.Quantity)) continue;
            for (var i = 0; i < assignment.Quantity && result.Count < LocalHangarEquipmentStore.DroneSlots; i++)
                result.Add(new WorldCompanion("drone", assignment.ItemId, result.Count, pet.Formation));
        }
        if (pet.Enabled && pet.Fuel > 0 && equipment.Assignments.Any(a => a.HangarSlot == slot
            && a.Configuration == configuration && a.ItemId == "pet_10" && a.Quantity > 0)
            && equipment.Inventory.Any(i => i.Id == "pet_10" && i.Quantity > 0))
            result.Add(new WorldCompanion("pet", "pet_10", 0, pet.Formation));
        return result;
    }

    public static IReadOnlyList<WorldCompanionPosition> At(IReadOnlyList<WorldCompanion> companions, int x, int y)
    {
        var drones = companions.Count(c => c.Kind == "drone");
        return companions.Select(c =>
        {
            var angle = Math.PI * (c.Index + 1) / (drones + 1);
            var (dx, dy) = c.Kind == "pet" ? (-75, 55) : c.Formation switch
            {
                "arrow" => (-45 - 22 * (c.Index / 2), (c.Index % 2 == 0 ? -1 : 1) * (28 + 20 * (c.Index / 2))),
                "line" => (-60, (c.Index - (drones - 1) / 2) * 30),
                _ => ((int)Math.Round(65 * Math.Cos(angle)), (int)Math.Round(65 * Math.Sin(angle)))
            };
            return new WorldCompanionPosition(c.Kind, c.ItemId, c.Index, c.Formation,
                (int)Math.Clamp((long)x + dx, 0, 21000), (int)Math.Clamp((long)y + dy, 0, 13100));
        }).ToArray();
    }
}
