using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using System.Text.Json.Serialization;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only inventory. No post-company equipment contract was captured.
// Type is the catalog item type ("laser", "shield", ...); null for cache states saved before Menu-134.
public sealed record LocalEquipmentItem(string Id, string Name, string Category, int Quantity, string? Type = null);
public sealed record LocalEquipmentAssignment(int HangarSlot, int Configuration, string ItemId, int Quantity);
public sealed record LocalAuctionLot(string ItemId, DateTimeOffset EndsAt, int Bid, bool Claimed);
public enum LocalHangarChange { Applied, Invalid, Conflict }
public sealed record LocalEquipmentState(List<LocalEquipmentItem> Inventory,
    List<LocalEquipmentAssignment> Assignments)
{
    // A local-only balance; the initializer also supplies it for older cached states.
    public int Credits { get; set; } = 10000;
    public List<LocalAuctionLot> AuctionLots { get; set; } = [];
    public int AssemblyComponents { get; set; } = 1000;
    public int PurchasedItems { get; set; }
    public int CraftedItems { get; set; }
    public int NpcKills { get; set; }
    public int StreunerKills { get; set; }
    public List<string> ClaimedQuestIds { get; set; } = [];
    public List<string> ClaimedPaymentPackages { get; set; } = [];
    public List<string> RedeemedVoucherCodes { get; set; } = [];
    public List<string> SearchedPlanetIds { get; set; } = [];
    public List<string> ClaimedBattlePassTiers { get; set; } = [];
    public DateTimeOffset? ExpeditionReadyAt { get; set; }
    public bool ExpeditionClaimed { get; set; }
    [JsonIgnore] public long BaseCredits { get; set; }
    [JsonIgnore] public long BaseComponents { get; set; }
    [JsonIgnore] public Dictionary<string, long> BaseInventory { get; set; } = [];
}

public enum VoucherRedeemResult { Applied, Invalid, NotFound, AlreadyRedeemed, Unavailable, BalanceLimit }
public enum PaymentClaimResult { Applied, Invalid, NotFound, AlreadyClaimed, Unavailable, BalanceLimit }

public sealed class LocalHangarEquipmentStore(IDistributedCache cache, EconomyStore? economy = null)
{
    // The API uses one cache across scopes; separate store instances must serialize
    // read/modify/write operations against that cache within this process.
    private static readonly SemaphoreSlim _mutationLock = new(1, 1);
    private static readonly LocalEquipmentItem[] StarterInventory =
    [
        new("laser_lf3", "LF-3 laser", "Equipment", 4, "laser"),
        new("laser_lf2", "LF-2 laser", "Equipment", 2, "laser"),
        new("shield_sg3n_b02", "SG3N-B02 shield", "Equipment", 4, "shield"),
        new("shield_sg3n_a03", "SG3N-A03 shield", "Equipment", 2, "shield"),
        new("drone_flax", "Flax drone", "Drones", 1, "drone"),
        new("drone_iris", "Iris drone", "Drones", 1, "drone"),
        new("pet_10", "P.E.T. 10", "PET", 1, "pet"),
        new("extra_cpu", "Slot CPU", "Extras", 0, "extra")
    ];

    private static string Key(Guid accountId) => $"localhangar:equipment:{accountId:N}";

    public async Task<LocalEquipmentState> GetAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        var state = json is null
            ? new LocalEquipmentState([.. StarterInventory], [])
            : JsonSerializer.Deserialize<LocalEquipmentState>(json)!;
        if (economy is not null)
        {
            await economy.ImportAsync(accountId, state, ct);
            var balances = await economy.BalancesAsync(accountId, ct);
            state.Credits = checked((int)balances.Single(b => b.AssetId == "credits").Amount);
            state.AssemblyComponents = checked((int)balances.Single(b => b.AssetId == "assembly_components").Amount);
            var owned = await economy.OwnedItemsAsync(accountId, ct);
            state = state with { Inventory = owned.Select(item => new LocalEquipmentItem(item.Id, item.Name,
                item.Type switch { "laser" or "shield" or "generator" => "Equipment", "drone" => "Drones", "pet" => "PET", "extra" => "Extras", _ => item.Type },
                checked((int)item.Quantity), item.Type)).ToList() };
            state.PurchasedItems = checked((int)(await economy.PurchasesAsync(accountId, ct)).Values.Sum());
            state.CraftedItems = checked((int)await economy.CraftCountAsync(accountId, ct));
            var claims = await economy.ClaimStateAsync(accountId, ct);
            foreach (var key in claims.Keys)
            {
                if (key.StartsWith("payment:", StringComparison.Ordinal)) AddOnce(state.ClaimedPaymentPackages, key[8..]);
                else if (key.StartsWith("voucher:", StringComparison.Ordinal)) AddOnce(state.RedeemedVoucherCodes, key[8..]);
                else if (key.StartsWith("quest:", StringComparison.Ordinal)) AddOnce(state.ClaimedQuestIds, key[6..]);
                else if (key.StartsWith("battlepass:", StringComparison.Ordinal)) AddOnce(state.ClaimedBattlePassTiers, key[11..]);
                else if (key.StartsWith("space-search:", StringComparison.Ordinal)) AddOnce(state.SearchedPlanetIds, key[13..]);
            }
            if (claims.ExpeditionStartedAt is { } started && state.ExpeditionReadyAt is null)
                state.ExpeditionReadyAt = started.AddMinutes(LocalEvents.ExpeditionMinutes);
            if (claims.Keys.Contains("expedition:reward")) state.ExpeditionClaimed = true;
            // economy.hangar_equipment_assignments is authoritative; cache-era assignments are copied once.
            if (json is not null)
                await economy.ImportHangarEquipmentAsync(accountId, state.Assignments, ct);
            state = state with { Assignments = [.. await economy.HangarEquipmentAsync(accountId, ct)] };
        }
        // Inventory can change outside this cached menu (for example in Assembly
        // or through an admin grant). Drop unavailable items and cap older saved
        // loadouts before rendering or accepting another assignment.
        state = state with { Assignments = ReconcileAssignments(state.Inventory, state.Assignments) };
        state.BaseCredits = state.Credits;
        state.BaseComponents = state.AssemblyComponents;
        state.BaseInventory = state.Inventory.ToDictionary(item => item.Id, item => (long)item.Quantity);
        return state;
    }

    public static List<LocalEquipmentAssignment> ReconcileAssignments(
        List<LocalEquipmentItem> inventory, List<LocalEquipmentAssignment> assignments)
    {
        var result = new List<LocalEquipmentAssignment>();
        foreach (var item in inventory.Where(item => item.Quantity > 0))
        {
            var remaining = item.Quantity;
            foreach (var slot in new[] { 1, 2 })
            {
                var kind = CapacityKind(item);
                var requested = assignments.Where(a => a.ItemId == item.Id && a.HangarSlot == slot
                    && (a.Configuration is 1 or 2) && a.Quantity > 0)
                    .GroupBy(a => a.Configuration)
                    .Select(group => new { Configuration = group.Key, Quantity = group.Max(a => a.Quantity) })
                    .OrderBy(a => a.Configuration).ToArray();
                var reserved = 0;
                foreach (var assignment in requested)
                {
                    var quantity = Math.Min(remaining, assignment.Quantity);
                    if (kind is not null)
                    {
                        var used = result.Where(a => a.HangarSlot == slot && a.Configuration == assignment.Configuration
                                && inventory.First(candidate => candidate.Id == a.ItemId) is { } equipped
                                && CapacityKind(equipped) == kind)
                            .Sum(a => (long)a.Quantity);
                        quantity = (int)Math.Min(quantity, Math.Max(0, SlotCapacity(slot, kind) - used));
                    }
                    if (quantity > 0)
                        result.Add(new LocalEquipmentAssignment(slot, assignment.Configuration, item.Id, quantity));
                    reserved = Math.Max(reserved, quantity);
                }
                remaining -= reserved;
            }
        }
        return result;
    }

    public async Task<bool> AssignAsync(Guid accountId, int hangarSlot, int configuration,
        string itemId, int quantity, CancellationToken ct) =>
        await SetAssignmentAsync(accountId, hangarSlot, configuration, itemId, quantity, ct)
            == LocalHangarChange.Applied;

    public async Task<LocalHangarChange> SetAssignmentAsync(Guid accountId, int hangarSlot, int configuration,
        string itemId, int quantity, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !LocalHangars.IsValidSlot(hangarSlot)
            || configuration is not (1 or 2) || quantity < 0)
            return LocalHangarChange.Invalid;

        await _mutationLock.WaitAsync(ct);
        try
        {
        var state = await GetAsync(accountId, ct);
        // With Postgres, the change is checked again against owned items under the account lock.
        if (economy is not null)
            return await economy.ChangeHangarEquipmentAsync(accountId, hangarSlot, configuration, itemId, quantity, ct);
        var (result, assignments) = ApplyAssignment(state.Inventory, state.Assignments,
            hangarSlot, configuration, itemId, quantity);
        if (result != LocalHangarChange.Applied)
            return result;
        await SaveAsync(accountId, state with { Assignments = assignments }, ct);
        return LocalHangarChange.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    // Pure assignment rule shared by the cache and Postgres paths. Assignments must already be reconciled.
    public static (LocalHangarChange Result, List<LocalEquipmentAssignment> Assignments) ApplyAssignment(
        IReadOnlyList<LocalEquipmentItem> inventory, IReadOnlyList<LocalEquipmentAssignment> assignments,
        int hangarSlot, int configuration, string itemId, int quantity)
    {
        var current = assignments.ToList();
        if (!LocalHangars.IsValidSlot(hangarSlot) || configuration is not (1 or 2)
            || quantity is < 0 or > LocalHangarEquipmentSelection.MaxQuantity)
            return (LocalHangarChange.Invalid, current);
        var item = inventory.SingleOrDefault(candidate => candidate.Id == itemId);
        if (item is null || quantity > item.Quantity)
            return (LocalHangarChange.Invalid, current);

        var existing = current.SingleOrDefault(assignment => assignment.HangarSlot == hangarSlot
            && assignment.Configuration == configuration && assignment.ItemId == itemId);
        if ((existing?.Quantity ?? 0) == quantity)
            return (LocalHangarChange.Conflict, current);

        // One physical item cannot be assigned to both ships. Configurations
        // on the same ship are alternatives and may reuse its equipment.
        var usedOnOtherShip = current
            .Where(assignment => assignment.ItemId == itemId && assignment.HangarSlot != hangarSlot)
            .GroupBy(assignment => assignment.HangarSlot)
            .Sum(group => (long)group.Max(assignment => assignment.Quantity));
        var alternateConfigurationQuantity = current
            .Where(assignment => assignment.ItemId == itemId && assignment.HangarSlot == hangarSlot
                && assignment.Configuration != configuration)
            .Select(assignment => assignment.Quantity).DefaultIfEmpty(0).Max();
        if (Math.Max(quantity, alternateConfigurationQuantity) + usedOnOtherShip > item.Quantity)
            return (LocalHangarChange.Conflict, current);

        // Menu-134: lasers fill laser slots and shields fill generator slots of this configuration.
        // Menu-135/136: drones, P.E.T.s and extras fill their respective slots.
        // Lowering an assignment is always allowed, so an older over-capacity loadout can be repaired.
        if (CapacityKind(item) is { } kind && quantity > (existing?.Quantity ?? 0))
        {
            var used = current.Where(a => a.HangarSlot == hangarSlot && a.Configuration == configuration
                    && a.ItemId != itemId
                    && inventory.SingleOrDefault(candidate => candidate.Id == a.ItemId) is { } other && CapacityKind(other) == kind)
                .Sum(a => (long)a.Quantity);
            if (used + quantity > SlotCapacity(hangarSlot, kind))
                return (LocalHangarChange.Conflict, current);
        }

        current.RemoveAll(assignment => assignment.HangarSlot == hangarSlot
            && assignment.Configuration == configuration && assignment.ItemId == itemId);
        if (quantity > 0)
            current.Add(new LocalEquipmentAssignment(hangarSlot, configuration, itemId, quantity));
        return (LocalHangarChange.Applied, current);
    }

    public const string LaserKind = "laser";
    public const string GeneratorKind = "shield";
    public const string DroneKind = "drone";
    public const string PetKind = "pet";
    public const string ExtraKind = "extra";
    // Emulator-defined, ship-independent: ten drone bays (the classic drone cap) and one P.E.T.
    public const int DroneSlots = 10;
    public const int PetSlots = 1;

    // An explicit catalog type is authoritative, including types that use no ship slot.
    // Only legacy items without a type fall back to category and then the seeded id prefix.
    public static string? SlotKind(LocalEquipmentItem item)
    {
        var kind = CapacityKind(item);
        return kind is LaserKind or GeneratorKind ? kind : null;
    }

    // Slot kind including drone bays, the P.E.T. slot and ship extras.
    public static string? CapacityKind(LocalEquipmentItem item)
    {
        if (item.Type is { } type)
            return KnownCapacityKind(type);

        var category = item.Category.ToLowerInvariant();
        var categoryKind = category switch
        {
            LaserKind or GeneratorKind or DroneKind or PetKind or ExtraKind => category,
            "drones" => DroneKind,
            "extras" => ExtraKind,
            _ => null
        };
        if (categoryKind is not null)
            return categoryKind;

        return item.Id.StartsWith("laser_", StringComparison.Ordinal) ? LaserKind
            : item.Id.StartsWith("shield_", StringComparison.Ordinal) ? GeneratorKind
            : item.Id.StartsWith("generator_", StringComparison.Ordinal) ? GeneratorKind
            : item.Id.StartsWith("drone_", StringComparison.Ordinal) ? DroneKind
            : item.Id.StartsWith("pet_", StringComparison.Ordinal) ? PetKind
            : item.Id.StartsWith("extra_", StringComparison.Ordinal) ? ExtraKind : null;
    }

    private static string? KnownCapacityKind(string type) => type.ToLowerInvariant() switch
    {
        LaserKind => LaserKind,
        GeneratorKind => GeneratorKind,
        "generator" => GeneratorKind,
        DroneKind => DroneKind,
        PetKind => PetKind,
        ExtraKind => ExtraKind,
        _ => null
    };

    public static int SlotCapacity(int hangarSlot, string kind)
    {
        var spec = LocalHangars.Spec(hangarSlot);
        return kind switch
        {
            LaserKind => spec.LaserSlots,
            GeneratorKind => spec.GeneratorSlots,
            DroneKind => DroneSlots,
            PetKind => PetSlots,
            ExtraKind => spec.ExtraSlots,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    public async Task<bool> PurchaseAsync(Guid accountId, string itemId, int quantity, CancellationToken ct)
    {
        if (accountId == Guid.Empty || quantity is < 1 or > 100
            || LocalShop.Catalog.SingleOrDefault(item => item.Id == itemId) is not { } listing)
            return false;

        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var cost = checked(listing.Price * quantity);
            if (state.Credits < cost)
                return false;
            var index = state.Inventory.FindIndex(item => item.Id == itemId);
            if (index < 0 || state.Inventory[index].Quantity > int.MaxValue - quantity)
                return false;
            state.Inventory[index] = state.Inventory[index] with
            {
                Quantity = state.Inventory[index].Quantity + quantity
            };
            state.Credits -= cost;
            state.PurchasedItems = (int)Math.Min(int.MaxValue, (long)state.PurchasedItems + quantity);
            await SaveAsync(accountId, state, ct);
            return true;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> CraftAsync(Guid accountId, string recipeId, CancellationToken ct)
    {
        var recipe = LocalAssembly.Recipes.SingleOrDefault(item => item.Id == recipeId);
        if (accountId == Guid.Empty || recipe is null) return false;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var index = state.Inventory.FindIndex(item => item.Id == recipe.ItemId);
            if (index < 0 || state.Inventory[index].Quantity == int.MaxValue
                || state.AssemblyComponents < recipe.ComponentCost || state.Credits < recipe.CreditCost)
                return false;
            state.AssemblyComponents -= recipe.ComponentCost;
            state.Credits -= recipe.CreditCost;
            state.Inventory[index] = state.Inventory[index] with { Quantity = state.Inventory[index].Quantity + 1 };
            if (state.CraftedItems < int.MaxValue) state.CraftedItems++;
            await SaveAsync(accountId, state, ct);
            return true;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> ClaimQuestAsync(Guid accountId, string questId, CancellationToken ct) =>
        await ClaimQuestResultAsync(accountId, questId, ct) == QuestClaimResult.Applied;

    public async Task<QuestClaimResult> ClaimQuestResultAsync(Guid accountId, string questId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !LocalQuests.IsWellFormedId(questId)) return QuestClaimResult.NotFound;
        var quest = LocalQuests.Catalog.SingleOrDefault(item => item.Id == questId);
        if (economy is null && quest is null) return QuestClaimResult.NotFound;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var offer = economy is null ? null : (await economy.RewardOffersAsync("quest", ct)).SingleOrDefault(item => item.Id == questId);
            if (offer?.ProgressKind == "npc_kills")
            {
                state.NpcKills = await economy!.NpcKillsAsync(accountId, ct);
                state.StreunerKills = await economy.NpcKillsAsync(accountId, ct, "streuner");
            }
            if (economy is not null && offer is null) return QuestClaimResult.NotFound;
            if (offer is { Enabled: false }) return QuestClaimResult.Unavailable;
            if (state.ClaimedQuestIds.Contains(questId)) return QuestClaimResult.AlreadyClaimed;
            if (offer is null ? quest!.Progress(state) < quest.Target : OfferProgress(offer, state) < offer.Target)
                return QuestClaimResult.Incomplete;
            // An existing durable ledger key (claimed by another process) or a reward that
            // would overflow the local balance is reported as a conflict, never a second grant.
            if (!await GrantAssetAsync(accountId, state, offer?.AssetId ?? "credits",
                offer?.Amount ?? quest!.RewardCredits, "quest_reward", $"quest:{questId}", ct)) return QuestClaimResult.AlreadyClaimed;
            state.ClaimedQuestIds.Add(questId);
            await SaveAsync(accountId, state, ct);
            return QuestClaimResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> ClaimPaymentPackageAsync(Guid accountId, string packageId, CancellationToken ct) =>
        await ClaimPaymentPackageResultAsync(accountId, packageId, ct) == PaymentClaimResult.Applied;

    public async Task<PaymentClaimResult> ClaimPaymentPackageResultAsync(Guid accountId, string packageId, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !LocalPayment.IsWellFormed(packageId)) return PaymentClaimResult.Invalid;
        var package = LocalPayment.Catalog.SingleOrDefault(item => item.Id == packageId);
        if (economy is null && package is null) return PaymentClaimResult.NotFound;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var offer = economy is null ? null : (await economy.RewardOffersAsync("payment", ct)).SingleOrDefault(item => item.Id == packageId);
            if (economy is not null && offer is null) return PaymentClaimResult.NotFound;
            if (state.ClaimedPaymentPackages.Contains(packageId)) return PaymentClaimResult.AlreadyClaimed;
            if (offer is { Enabled: false }) return PaymentClaimResult.Unavailable;
            var assetId = offer?.AssetId ?? "credits";
            var amount = offer?.Amount ?? package!.Credits;
            if (amount is <= 0 or > LocalPayment.MaxGrant) return PaymentClaimResult.Unavailable;
            if (assetId == "credits" && amount > int.MaxValue - (long)state.Credits) return PaymentClaimResult.BalanceLimit;
            // The ledger key is durable, so a claim after cache expiry is still rejected.
            if (!await GrantAssetAsync(accountId, state, assetId, amount, "practice_payment",
                $"payment:{packageId}", ct)) return PaymentClaimResult.AlreadyClaimed;
            state.ClaimedPaymentPackages.Add(packageId);
            await SaveAsync(accountId, state, ct);
            return PaymentClaimResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> RedeemVoucherAsync(Guid accountId, string code, CancellationToken ct) =>
        await RedeemVoucherResultAsync(accountId, code, ct) == VoucherRedeemResult.Applied;

    public async Task<VoucherRedeemResult> RedeemVoucherResultAsync(Guid accountId, string code, CancellationToken ct) =>
        (await RedeemVoucherWithIdAsync(accountId, code, ct)).Result;

    // Menu-114: also returns the canonical code id (catalog or offer spelling) so the redirect can name it.
    public async Task<(VoucherRedeemResult Result, string? Id)> RedeemVoucherWithIdAsync(Guid accountId, string code, CancellationToken ct)
    {
        var voucher = LocalVouchers.Catalog.SingleOrDefault(item =>
            string.Equals(item.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (accountId == Guid.Empty) return (VoucherRedeemResult.Invalid, null);
        if (economy is null && voucher is null) return (VoucherRedeemResult.NotFound, null);
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var offer = economy is null ? null : (await economy.RewardOffersAsync("voucher", ct)).SingleOrDefault(item =>
                string.Equals(item.Id, code?.Trim(), StringComparison.OrdinalIgnoreCase));
            var id = offer?.Id ?? voucher?.Code;
            if (id is null || (economy is not null && offer is null)) return (VoucherRedeemResult.NotFound, null);
            if (state.RedeemedVoucherCodes.Contains(id)) return (VoucherRedeemResult.AlreadyRedeemed, id);
            if (offer is { Enabled: false }) return (VoucherRedeemResult.Unavailable, id);
            var amount = offer?.Amount ?? voucher!.Credits;
            if (amount is <= 0 or > LocalVouchers.MaxGrant) return (VoucherRedeemResult.Unavailable, id);
            if ((offer?.AssetId ?? "credits") == "credits" && amount > int.MaxValue - (long)state.Credits)
                return (VoucherRedeemResult.BalanceLimit, id);
            // The ledger key is durable, so a grant after cache expiry is still rejected.
            if (!await GrantAssetAsync(accountId, state, offer?.AssetId ?? "credits",
                amount, "practice_voucher", $"voucher:{id}", ct)) return (VoucherRedeemResult.AlreadyRedeemed, id);
            state.RedeemedVoucherCodes.Add(id);
            await SaveAsync(accountId, state, ct);
            return (VoucherRedeemResult.Applied, id);
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> SearchPlanetAsync(Guid accountId, string planetId, CancellationToken ct) =>
        await SearchPlanetResultAsync(accountId, planetId, ct) == LocalSpaceSearchResult.Applied;

    public async Task<LocalSpaceSearchResult> SearchPlanetResultAsync(Guid accountId, string planetId, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalSpaceSearchResult.Rejected;
        if (LocalSpaceSearch.Find(planetId) is not { } planet) return LocalSpaceSearchResult.UnknownPlanet;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            if (state.SearchedPlanetIds.Contains(planetId)) return LocalSpaceSearchResult.AlreadySearched;
            if (state.Credits < LocalSpaceSearch.SearchCost) return LocalSpaceSearchResult.InsufficientCredits;
            // Postgres inventory omits unowned items; the grant creates the row.
            var index = state.Inventory.FindIndex(item => item.Id == planet.RewardItemId);
            if (index >= 0 && state.Inventory[index].Quantity == int.MaxValue) return LocalSpaceSearchResult.InventoryFull;
            if (economy is not null && !await economy.GrantItemAsync(accountId, planet.RewardItemId, 1,
                $"space-search:{planetId}", LocalSpaceSearch.SearchCost, "space_search", ct))
                return LocalSpaceSearchResult.Unavailable;
            if (index < 0)
            {
                var starter = StarterInventory.FirstOrDefault(item => item.Id == planet.RewardItemId);
                state.Inventory.Add(new LocalEquipmentItem(planet.RewardItemId, planet.RewardName,
                    starter?.Category ?? "Equipment", 0));
                index = state.Inventory.Count - 1;
            }
            state.Credits -= LocalSpaceSearch.SearchCost;
            state.Inventory[index] = state.Inventory[index] with { Quantity = state.Inventory[index].Quantity + 1 };
            if (economy is not null)
            {
                state.BaseCredits = state.Credits;
                state.BaseInventory[planet.RewardItemId] = state.Inventory[index].Quantity;
            }
            state.SearchedPlanetIds.Add(planetId);
            await SaveAsync(accountId, state, ct);
            return LocalSpaceSearchResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> ClaimBattlePassAsync(Guid accountId, string tierId, CancellationToken ct) =>
        await ClaimBattlePassResultAsync(accountId, tierId, ct) == LocalEventResult.Applied;

    public async Task<LocalEventResult> ClaimBattlePassResultAsync(Guid accountId, string tierId, CancellationToken ct)
    {
        var tier = LocalEvents.Tiers.SingleOrDefault(item => item.Id == tierId);
        if (accountId == Guid.Empty) return LocalEventResult.Rejected;
        if (economy is null && tier is null) return LocalEventResult.UnknownTier;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var offer = economy is null ? null : (await economy.RewardOffersAsync("battle_pass", ct)).SingleOrDefault(item => item.Id == tierId);
            if (economy is not null && offer is null) return LocalEventResult.UnknownTier;
            if (offer is { Enabled: false }) return LocalEventResult.Unavailable;
            if (state.ClaimedBattlePassTiers.Contains(tierId)) return LocalEventResult.AlreadyClaimed;
            if (offer is null ? tier!.Progress(state) < tier.Target : OfferProgress(offer, state) < offer.Target)
                return LocalEventResult.NotReady;
            // The ledger key is durable, so a replay after cache expiry is still rejected.
            if (!await GrantAssetAsync(accountId, state, offer?.AssetId ?? "credits",
                offer?.Amount ?? tier!.RewardCredits, "battle_pass_reward", $"battlepass:{tierId}", ct)) return LocalEventResult.AlreadyClaimed;
            state.ClaimedBattlePassTiers.Add(tierId);
            await SaveAsync(accountId, state, ct);
            return LocalEventResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> StartExpeditionAsync(Guid accountId, DateTimeOffset now, CancellationToken ct) =>
        await StartExpeditionResultAsync(accountId, now, ct) == LocalEventResult.Applied;

    public async Task<LocalEventResult> StartExpeditionResultAsync(Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalEventResult.Rejected;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            if (state.ExpeditionReadyAt is not null || state.ExpeditionClaimed) return LocalEventResult.AlreadyClaimed;
            if (state.Credits < LocalEvents.ExpeditionCost) return LocalEventResult.InsufficientCredits;
            if (economy is not null && !await economy.AdjustAsync(accountId, "credits", -LocalEvents.ExpeditionCost,
                "expedition_start", "events", "expedition:start", ct)) return LocalEventResult.AlreadyClaimed;
            state.Credits -= LocalEvents.ExpeditionCost;
            if (economy is not null) state.BaseCredits = state.Credits;
            state.ExpeditionReadyAt = now.AddMinutes(LocalEvents.ExpeditionMinutes);
            await SaveAsync(accountId, state, ct);
            return LocalEventResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> ClaimExpeditionAsync(Guid accountId, DateTimeOffset now, CancellationToken ct) =>
        await ClaimExpeditionResultAsync(accountId, now, ct) == LocalEventResult.Applied;

    public async Task<LocalEventResult> ClaimExpeditionResultAsync(Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalEventResult.Rejected;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var index = state.Inventory.FindIndex(item => item.Id == LocalEvents.ExpeditionRewardItemId);
            if (state.ExpeditionClaimed) return LocalEventResult.AlreadyClaimed;
            if (state.ExpeditionReadyAt is null || state.ExpeditionReadyAt > now) return LocalEventResult.NotReady;
            if (index < 0 || state.Inventory[index].Quantity == int.MaxValue) return LocalEventResult.InventoryFull;
            if (economy is not null && !await economy.GrantItemAsync(accountId, LocalEvents.ExpeditionRewardItemId,
                1, "expedition:reward", 0, "events", ct)) return LocalEventResult.AlreadyClaimed;
            state.Inventory[index] = state.Inventory[index] with { Quantity = state.Inventory[index].Quantity + 1 };
            if (economy is not null) state.BaseInventory[LocalEvents.ExpeditionRewardItemId] = state.Inventory[index].Quantity;
            state.ExpeditionClaimed = true;
            await SaveAsync(accountId, state, ct);
            return LocalEventResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> BidAsync(Guid accountId, string itemId, int amount, DateTimeOffset now, CancellationToken ct) =>
        await BidResultAsync(accountId, itemId, amount, now, ct) == LocalAuctionResult.Applied;

    public async Task<LocalAuctionResult> BidResultAsync(Guid accountId, string itemId, long amount, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty || amount <= 0) return LocalAuctionResult.Rejected;
        if (LocalAuction.Catalog.All(item => item.ItemId != itemId)) return LocalAuctionResult.UnknownLot;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var existing = state.AuctionLots.SingleOrDefault(item => item.ItemId == itemId);
            if (existing is { Claimed: true }) return LocalAuctionResult.AlreadyClaimed;
            if (existing is not null && now >= existing.EndsAt) return LocalAuctionResult.Ended;
            var prior = existing?.Bid ?? 0;
            var minimum = prior == 0 ? LocalAuction.Catalog.Single(item => item.ItemId == itemId).OpeningBid
                : prior + Math.Max(1, prior / 10);
            if (amount < minimum) return LocalAuctionResult.BidTooLow;
            if (amount > int.MaxValue || amount - prior > state.Credits) return LocalAuctionResult.InsufficientCredits;
            var lot = GetOrCreateLot(state, itemId, now);
            state.Credits -= (int)amount - lot.Bid;
            state.AuctionLots[state.AuctionLots.FindIndex(item => item.ItemId == itemId)] = lot with { Bid = (int)amount };
            await SaveAsync(accountId, state, ct);
            return LocalAuctionResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    public async Task<bool> ClaimAuctionAsync(Guid accountId, string itemId, DateTimeOffset now, CancellationToken ct) =>
        await ClaimAuctionResultAsync(accountId, itemId, now, ct) == LocalAuctionResult.Applied;

    public async Task<LocalAuctionResult> ClaimAuctionResultAsync(Guid accountId, string itemId, DateTimeOffset now, CancellationToken ct)
    {
        if (accountId == Guid.Empty) return LocalAuctionResult.Rejected;
        if (LocalAuction.Catalog.All(item => item.ItemId != itemId)) return LocalAuctionResult.UnknownLot;
        await _mutationLock.WaitAsync(ct);
        try
        {
            var state = await GetAsync(accountId, ct);
            var lot = state.AuctionLots.SingleOrDefault(item => item.ItemId == itemId);
            if (lot is null || lot.Bid == 0) return LocalAuctionResult.NoBid;
            if (lot.Claimed) return LocalAuctionResult.AlreadyClaimed;
            if (now < lot.EndsAt) return LocalAuctionResult.StillRunning;
            var index = state.Inventory.FindIndex(item => item.Id == itemId);
            if (index < 0) return LocalAuctionResult.Unavailable;
            if (state.Inventory[index].Quantity == int.MaxValue) return LocalAuctionResult.InventoryFull;
            state.Inventory[index] = state.Inventory[index] with { Quantity = state.Inventory[index].Quantity + 1 };
            state.AuctionLots[state.AuctionLots.FindIndex(item => item.ItemId == itemId)] = lot with { Claimed = true };
            await SaveAsync(accountId, state, ct);
            return LocalAuctionResult.Applied;
        }
        finally { _mutationLock.Release(); }
    }

    private static LocalAuctionLot GetOrCreateLot(LocalEquipmentState state, string itemId, DateTimeOffset now)
    {
        var lot = state.AuctionLots.SingleOrDefault(item => item.ItemId == itemId);
        if (lot is not null) return lot;
        lot = new LocalAuctionLot(itemId, now.AddMinutes(5), 0, false);
        state.AuctionLots.Add(lot);
        return lot;
    }

    internal static long OfferProgress(EconomyRewardOffer offer, LocalEquipmentState state) => offer.ProgressKind switch
    {
        "purchases" => state.PurchasedItems,
        "crafts" => state.CraftedItems,
        "npc_kills" => offer.Id == "first_streuner" ? state.StreunerKills : state.NpcKills,
        _ => 0
    };

    private static void AddOnce(List<string> values, string value)
    {
        if (!values.Contains(value)) values.Add(value);
    }

    private async Task<bool> GrantAssetAsync(Guid accountId, LocalEquipmentState state, string assetId, long amount,
        string reason, string key, CancellationToken ct)
    {
        if (amount <= 0 || (assetId == "credits" && amount > int.MaxValue - state.Credits)) return false;
        if (economy is not null && !await economy.AdjustAsync(accountId, assetId, amount, reason,
            reason, key, ct)) return false;
        if (assetId == "credits") state.Credits += checked((int)amount);
        if (economy is not null) state.BaseCredits = state.Credits;
        return true;
    }

    private async Task SaveAsync(Guid accountId, LocalEquipmentState state, CancellationToken ct)
    {
        if (economy is not null)
        {
            var deltas = state.Inventory.ToDictionary(item => item.Id,
                item => (long)item.Quantity - state.BaseInventory.GetValueOrDefault(item.Id));
            await economy.ReconcileLegacyAsync(accountId, state.Credits - state.BaseCredits,
                state.AssemblyComponents - state.BaseComponents, deltas, ct);
        }
        // Postgres owns assignments, so the cached state never carries a copy that could be imported later.
        var cached = economy is null ? state : state with { Assignments = [] };
        await cache.SetStringAsync(Key(accountId), JsonSerializer.Serialize(cached),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
    }
}

public static class LocalHangarEquipment
{
    public static string Page(GameAuthSession session, int hangarSlot, int configuration,
        LocalEquipmentState state, string? saved = null,
        ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts.UnityHangar? assignedShip = null)
    {
        var ship = assignedShip ?? LocalHangars.For(session, hangarSlot).Single(h => h.SlotNumber == hangarSlot);
        var equipped = state.Assignments.Where(a => a.HangarSlot == hangarSlot
            && a.Configuration == configuration).Sum(a => (long)a.Quantity);
        var notice = equipped == 0
            ? "This configuration has no equipment assigned."
            : $"This configuration has {equipped} equipped item{(equipped == 1 ? "" : "s")}.";
        // Menu-118: equipped units per category in this configuration, in inventory order.
        var summary = string.Join(" · ", state.Inventory.Select(item => item.Category).Distinct().Select(category =>
        {
            var count = state.Assignments.Where(a => a.HangarSlot == hangarSlot && a.Configuration == configuration
                && state.Inventory.Any(item => item.Id == a.ItemId && item.Category == category)).Sum(a => (long)a.Quantity);
            return $"<span data-category=\"{HtmlEncoder.Default.Encode(category)}\">{HtmlEncoder.Default.Encode(category)}: {count}</span>";
        }));
        var savedNotice = SavedNotice(state, hangarSlot, configuration, saved);
        // Menu-134: laser and generator slot usage of this configuration against the ship's spec.
        long Used(string kind) => state.Assignments.Where(a => a.HangarSlot == hangarSlot && a.Configuration == configuration
            && state.Inventory.SingleOrDefault(item => item.Id == a.ItemId) is { } owned
            && LocalHangarEquipmentStore.CapacityKind(owned) == kind).Sum(a => (long)a.Quantity);
        var usedSlots = new Dictionary<string, long>
        {
            [LocalHangarEquipmentStore.LaserKind] = Used(LocalHangarEquipmentStore.LaserKind),
            [LocalHangarEquipmentStore.GeneratorKind] = Used(LocalHangarEquipmentStore.GeneratorKind),
            [LocalHangarEquipmentStore.DroneKind] = Used(LocalHangarEquipmentStore.DroneKind),
            [LocalHangarEquipmentStore.PetKind] = Used(LocalHangarEquipmentStore.PetKind),
            [LocalHangarEquipmentStore.ExtraKind] = Used(LocalHangarEquipmentStore.ExtraKind)
        };
        string SlotText(string kind, string label)
        {
            var capacity = LocalHangarEquipmentStore.SlotCapacity(hangarSlot, kind);
            return $"<span data-slot-kind=\"{kind}\" data-used=\"{usedSlots[kind]}\" data-capacity=\"{capacity}\">{label}: {usedSlots[kind]}/{capacity}</span>";
        }
        var overCapacity = usedSlots.Any(pair => pair.Value > LocalHangarEquipmentStore.SlotCapacity(hangarSlot, pair.Key));
        var slotCapacity = $"<p id=\"slot-capacity\">{SlotText(LocalHangarEquipmentStore.LaserKind, "Laser slots")} · " +
            $"{SlotText(LocalHangarEquipmentStore.GeneratorKind, "Generator slots")} · " +
            $"{SlotText(LocalHangarEquipmentStore.DroneKind, "Drone slots")} · " +
            $"{SlotText(LocalHangarEquipmentStore.PetKind, "P.E.T. slots")} · " +
            $"{SlotText(LocalHangarEquipmentStore.ExtraKind, "Extra slots")}</p>" +
            (overCapacity ? "<p id=\"slot-warning\" role=\"alert\">This configuration uses more slots than the ship has. Lower an assignment to fix it.</p>" : "");
        var rows = string.Join("", state.Inventory.Select(item =>
        {
            var assigned = state.Assignments.SingleOrDefault(assignment =>
                assignment.HangarSlot == hangarSlot && assignment.Configuration == configuration
                && assignment.ItemId == item.Id)?.Quantity ?? 0;
            var reservedByOtherShip = state.Assignments
                .Where(assignment => assignment.HangarSlot != hangarSlot && assignment.ItemId == item.Id)
                .GroupBy(assignment => assignment.HangarSlot)
                .Sum(group => (long)group.Max(assignment => assignment.Quantity));
            var available = Math.Max(0L, (long)item.Quantity - reservedByOtherShip);
            // The input never offers more than the free slots of this item's kind (current value kept).
            var max = LocalHangarEquipmentStore.CapacityKind(item) is { } kind
                ? Math.Min(available, Math.Max(assigned, assigned + LocalHangarEquipmentStore.SlotCapacity(hangarSlot, kind) - usedSlots[kind]))
                : available;
            max = Math.Min(max, LocalHangarEquipmentSelection.MaxQuantity);
            var form = LocalHangarEquipmentSelection.IsWellFormedItemId(item.Id)
                ? "<form method=\"post\" action=\"/indexInternal.es?action=internalDockEquipmentSet\">" +
                $"<input type=\"hidden\" name=\"slot\" value=\"{hangarSlot}\"><input type=\"hidden\" name=\"configuration\" value=\"{configuration}\">" +
                $"<input type=\"hidden\" name=\"item\" value=\"{HtmlEncoder.Default.Encode(item.Id)}\"><input type=\"number\" name=\"quantity\" min=\"0\" max=\"{max}\" value=\"{Math.Min(assigned, LocalHangarEquipmentSelection.MaxQuantity)}\">" +
                "<button type=\"submit\">Save</button></form>"
                : "<span data-equipment-unavailable>Item ID cannot be equipped.</span>";
            return $"<tr><td>{HtmlEncoder.Default.Encode(item.Category)}</td><td>{HtmlEncoder.Default.Encode(item.Name)}</td>" +
                $"<td>{item.Quantity}</td><td>{available}</td><td>{form}</td></tr>";
        }));
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Equipment - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff;margin-right:20px}td,th{padding:12px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:8px;border:0}</style></head><body><main id="hangarEquipment"><h1>{{HtmlEncoder.Default.Encode(ship.ShipName)}} equipment</h1><p>Hangar slot {{hangarSlot}} · Configuration {{configuration}}</p>{{savedNotice}}<p role="status">{{notice}}</p><p id="loadout-summary">{{summary}}</p>{{slotCapacity}}<p>Inventory is shared across ships. The two configurations on one ship are alternatives. Available stock excludes the other ship's maximum assignment. Lasers use laser slots, shields use generator slots, drones use drone slots, a P.E.T. uses the P.E.T. slot, and extras use the ship's extra slots.</p><nav><a href="/indexInternal.es?action=internalDockEquipment&amp;slot={{hangarSlot}}&amp;configuration=1">Configuration 1</a><a href="/indexInternal.es?action=internalDockEquipment&amp;slot={{hangarSlot}}&amp;configuration=2">Configuration 2</a></nav><table><thead><tr><th>Category</th><th>Item</th><th>Owned</th><th>Available</th><th>Equipped</th></tr></thead><tbody>{{rows}}</tbody></table><p><a href="/indexInternal.es?action=internalDock">Back to Hangar</a></p></main></body></html>
            """;
    }

    // Confirms the item named by the post-save redirect with its current quantity; an id that is not
    // owned (removed since, or a hand-edited link) shows no notice and is never echoed.
    private static string SavedNotice(LocalEquipmentState state, int hangarSlot, int configuration, string? saved)
    {
        if (saved is null || state.Inventory.SingleOrDefault(item => item.Id == saved) is not { } item) return "";
        var quantity = state.Assignments.SingleOrDefault(a => a.HangarSlot == hangarSlot
            && a.Configuration == configuration && a.ItemId == item.Id)?.Quantity ?? 0;
        var name = HtmlEncoder.Default.Encode(item.Name);
        var text = quantity == 0
            ? $"{name} is no longer equipped in configuration {configuration}."
            : $"Saved: {quantity} × {name} equipped in configuration {configuration}.";
        return $"<p id=\"equipment-notice\" role=\"status\" data-item=\"{HtmlEncoder.Default.Encode(item.Id)}\" data-quantity=\"{quantity}\">{text}</p>";
    }
}
