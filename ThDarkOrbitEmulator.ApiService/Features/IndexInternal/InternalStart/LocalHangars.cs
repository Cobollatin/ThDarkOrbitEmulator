using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local menu data. Only the nine-field hangar shape and the first ship's icon
// are supported by the saved login response; the second slot is synthetic.
public static class LocalHangars
{
    public const int DefaultSlot = 1;

    // Emulator-defined form lifetime and limits; no official Dock form was captured.
    public static readonly TimeSpan FormLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private const int MaxKeyLength = 128;
    private static readonly string[] AllowedPageKeys = ["action", "from", "prc", "lang"];
    public const string ActivatedKey = "activated";
    public const string ShipKey = "ship";

    // Emulator-defined ship specs for the Dock detail view; no Live ship stats were captured.
    public sealed record ShipSpec(int HitPoints, int Speed, int Cargo, int LaserSlots, int GeneratorSlots, int ExtraSlots);

    public static ShipSpec Spec(int slot) => slot == 2
        ? new ShipSpec(4000, 320, 100, 1, 1, 1)
        : new ShipSpec(128000, 370, 1500, 5, 12, 2);

    public static bool IsValidSlot(int slot) => slot is 1 or 2;

    // Strict slot parser: only the canonical "1" or "2", no signs, spaces or leading zeros.
    public static bool TryParseSlot(string? value, out int slot)
    {
        slot = value switch { "1" => 1, "2" => 2, _ => 0 };
        return slot != 0;
    }

    // GET page accepts only the shared shell parameters, each once and at most 64 characters,
    // plus the post-activation notice: lowercase "activated", once, as a canonical slot.
    // Menu-191: keys and the action value match exactly (ACTION=, From=, action=INTERNALDOCK are 400),
    // like Map overview and Command Center, and values may not contain control characters.
    public static bool ValidPageQuery(IQueryCollection query) =>
        query["action"].Count == 1 && query["action"].ToString() == "internalDock"
        && query.All(pair => pair.Value.Count == 1 && pair.Value.ToString().Length <= 64
            && !pair.Value.ToString().Any(char.IsControl)
            && (AllowedPageKeys.Contains(pair.Key, StringComparer.Ordinal)
                || (string.Equals(pair.Key, ActivatedKey, StringComparison.Ordinal)
                    || string.Equals(pair.Key, ShipKey, StringComparison.Ordinal)) && TryParseSlot(pair.Value, out _)));

    // Only call after ValidPageQuery; returns the slot named by a successful activation redirect.
    public static int? ActivatedSlot(IQueryCollection query) =>
        TryParseSlot(query[ActivatedKey], out var slot) ? slot : null;

    // Only call after ValidPageQuery; returns the slot whose detail view was requested.
    public static int? ShipSlot(IQueryCollection query) =>
        TryParseSlot(query[ShipKey], out var slot) ? slot : null;

    public static string ActivatedLocation(int slot) => $"/indexInternal.es?action=internalDock&{ActivatedKey}={slot}";

    // The key binds the session, the requested slot and the slot that was active when the page was rendered,
    // so a form is useless once the active hangar changes (stale tab or replay).
    public static string CreateFormKey(string dosid, int slot, int current, DateTimeOffset? issued = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var seconds = (issued ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        return $"{nonce}.{seconds}.{Sign(dosid, slot, current, nonce, seconds)}";
    }

    private static string Sign(string dosid, int slot, int current, string nonce, string issued) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"dock:{slot}:{current}:{nonce}:{issued}"))).ToLowerInvariant();

    // Returns the issue time of a correctly signed key, or null for malformed or tampered keys.
    public static DateTimeOffset? KeyIssued(string dosid, int slot, int current, string key)
    {
        if (key.Length > MaxKeyLength) return null;
        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _)
            || parts[1].Length is < 1 or > 12 || !parts[1].All(char.IsAsciiDigit) || parts[1][0] == '0'
            || parts[2].Length != 64 || !long.TryParse(parts[1], out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[2]),
            Encoding.ASCII.GetBytes(Sign(dosid, slot, current, parts[0], parts[1])))) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    public static IReadOnlyList<UnityHangar> For(GameAuthSession session, int activeSlot)
    {
        // Exactly one hangar is active even if a caller passes a corrupt slot.
        if (!IsValidSlot(activeSlot))
            activeSlot = DefaultSlot;
        var map = LocalCompanySelection.StarterMap(session.Company) ?? "1";
        // The captured hangar IDs are six numeric digits. Keep ordinary local IDs
        // unchanged while bounding large game IDs and avoiding overflow in slot 2.
        var id = Math.Abs(session.GameUserId % 900000);
        return
        [
            new UnityHangar
            {
                SlotNumber = 1, ShipId = "ship_pusat-plus", ModelId = 534,
                HangarId = id.ToString("D6", CultureInfo.InvariantCulture), ShipName = "Pusat Plus", MapId = map,
                ShipDestroyed = false, HangarActive = activeSlot == 1,
                TooltipIcon = $"/do_img/global/items/ship/pusat-plus_100x100.png?__cv={CapturedAssetHashes.ActiveHangarIconVersion}"
            },
            new UnityHangar
            {
                SlotNumber = 2, ShipId = "ship_phoenix", ModelId = 1,
                HangarId = (id + 100000).ToString("D6", CultureInfo.InvariantCulture), ShipName = "Phoenix", MapId = map,
                ShipDestroyed = false, HangarActive = activeSlot == 2,
                TooltipIcon = "/do_img/global/items/ship/phoenix_100x100.png"
            }
        ];
    }

    private static string ActivateForm(GameAuthSession session, int slot, int activeSlot, DateTimeOffset? issued) =>
        $"<form method=\"post\" action=\"/indexInternal.es?action=internalDockSelect\"><input type=\"hidden\" name=\"slot\" value=\"{slot}\"><input type=\"hidden\" name=\"current\" value=\"{activeSlot}\"><input type=\"hidden\" name=\"key\" value=\"{CreateFormKey(session.Dosid, slot, activeSlot, issued)}\"><button type=\"submit\">Activate</button></form>";

    private static string EquipmentLink(int slot) =>
        $"<a href=\"/indexInternal.es?action=internalDockEquipment&amp;slot={slot}&amp;configuration=1\">Equipment, drones and PET</a>";

    // Detail view for one slot: specs, status and the same signed Activate form as the card.
    private static string ShipDetail(GameAuthSession session, UnityHangar hangar, int activeSlot, DateTimeOffset? issued)
    {
        var spec = Spec(hangar.SlotNumber);
        var other = hangar.SlotNumber == 1 ? 2 : 1;
        var company = CompanyCatalog.Find(session.Company);
        var map = company is null ? $"map {hangar.MapId}" : $"{company.NumericFactionId}-1 (map {company.StarterMap})";
        return $"<section id=\"dock-ship\" data-slot=\"{hangar.SlotNumber}\" data-active=\"{(hangar.HangarActive ? "true" : "false")}\">" +
            $"<h2>{HtmlEncoder.Default.Encode(hangar.ShipName)} (slot {hangar.SlotNumber})</h2>" +
            $"<img src=\"{HtmlEncoder.Default.Encode(hangar.TooltipIcon)}\" alt=\"{HtmlEncoder.Default.Encode(hangar.ShipName)}\" width=\"100\" height=\"100\">" +
            "<table id=\"dock-ship-specs\"><tbody>" +
            $"<tr data-spec=\"hit-points\"><th>Hit points</th><td>{spec.HitPoints.ToString("N0", CultureInfo.InvariantCulture)}</td></tr>" +
            $"<tr data-spec=\"speed\"><th>Speed</th><td>{spec.Speed}</td></tr>" +
            $"<tr data-spec=\"cargo\"><th>Cargo</th><td>{spec.Cargo.ToString("N0", CultureInfo.InvariantCulture)}</td></tr>" +
            $"<tr data-spec=\"lasers\"><th>Laser slots</th><td>{spec.LaserSlots}</td></tr>" +
            $"<tr data-spec=\"generators\"><th>Generator slots</th><td>{spec.GeneratorSlots}</td></tr>" +
            $"<tr data-spec=\"extras\"><th>Extra slots</th><td>{spec.ExtraSlots}</td></tr>" +
            $"<tr data-spec=\"hangar\"><th>Hangar</th><td>{hangar.HangarId}</td></tr>" +
            $"<tr data-spec=\"map\"><th>Home map</th><td>{HtmlEncoder.Default.Encode(map)}</td></tr>" +
            $"<tr data-spec=\"model\"><th>Ship</th><td>{HtmlEncoder.Default.Encode(hangar.ShipId)} (model {hangar.ModelId})</td></tr>" +
            "</tbody></table>" +
            (hangar.HangarActive ? "<p data-status=\"active\"><strong>Active ship</strong></p>"
                : $"<p data-status=\"inactive\">Slot {activeSlot} is active. Activate this ship to fly it.</p>{ActivateForm(session, hangar.SlotNumber, activeSlot, issued)}") +
            $"<p>{EquipmentLink(hangar.SlotNumber)} · <a href=\"/indexInternal.es?action=internalDock&amp;{ShipKey}={other}\">Slot {other}</a> · <a href=\"/indexInternal.es?action=internalDock\">All ships</a></p></section>";
    }

    public static string Page(GameAuthSession session, int activeSlot, DateTimeOffset? issued = null, int? activated = null, int? ship = null,
        IReadOnlyList<UnityHangar>? assignedShips = null)
    {
        if (!IsValidSlot(activeSlot))
            activeSlot = DefaultSlot;
        var ships = assignedShips ?? For(session, activeSlot);
        // The notice confirms only while the activated slot is still active; otherwise it says it went stale.
        var notice = activated is not { } done || !IsValidSlot(done) ? ""
            : done == activeSlot
                ? $"<p id=\"dock-notice\" role=\"status\" data-slot=\"{done}\">{HtmlEncoder.Default.Encode(ships.FirstOrDefault(item => item.SlotNumber == done)?.ShipName ?? "Ship")} is now the active ship (slot {done}).</p>"
                : $"<p id=\"dock-notice\" role=\"status\" data-slot=\"{done}\" data-stale=\"true\">The active ship has changed since slot {done} was activated.</p>";
        var cards = string.Join("", ships.Select(hangar =>
            $"<article data-slot=\"{hangar.SlotNumber}\" data-active=\"{(hangar.HangarActive ? "true" : "false")}\"><h2>Slot {hangar.SlotNumber}: {HtmlEncoder.Default.Encode(hangar.ShipName)}</h2>" +
            $"<p>Hangar {hangar.HangarId} · Map {hangar.MapId} · Ship {HtmlEncoder.Default.Encode(hangar.ShipId)} (model {hangar.ModelId})</p>" +
            (hangar.HangarActive ? "<strong>Active</strong>" : ActivateForm(session, hangar.SlotNumber, activeSlot, issued)) +
            $"<p>{EquipmentLink(hangar.SlotNumber)} · <a href=\"/indexInternal.es?action=internalDock&amp;{ShipKey}={hangar.SlotNumber}\">Details</a></p></article>"));
        if (ship is { } detail && ships.FirstOrDefault(item => item.SlotNumber == detail) is { } selected)
            cards = ShipDetail(session, selected, activeSlot, issued);
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Hangar - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:10vh auto}article{border:1px solid #4c7896;padding:16px;margin:16px 0}a{color:#8cdcff}button{padding:10px;background:#1789b5;color:white;border:0;cursor:pointer}</style></head><body><main id="hangar"><h1>Hangar</h1>{{notice}}{{cards}}<a href="/indexInternal.es?action=internalWebGL&from=internalStart">Back to game</a></main></body></html>
            """;
    }
}

public sealed class LocalHangarStore(IDistributedCache cache, EconomyStore? economy = null)
{
    private static readonly SemaphoreSlim SelectionLock = new(1, 1);
    private static string Key(Guid accountId) => $"localhangar:active:{accountId:N}";

    // With Postgres, economy.hangar_selections is authoritative; a cache-era slot is imported once and removed.
    public async Task<int> GetActiveSlotAsync(Guid accountId, CancellationToken ct)
    {
        if (economy is null) return await GetCachedSlotAsync(accountId, ct);
        if (await cache.GetStringAsync(Key(accountId), ct) is not null)
        {
            await economy.ImportHangarSlotAsync(accountId, await GetCachedSlotAsync(accountId, ct), ct);
            await cache.RemoveAsync(Key(accountId), ct);
        }
        return await economy.HangarSlotAsync(accountId, ct) ?? LocalHangars.DefaultSlot;
    }

    private async Task<int> GetCachedSlotAsync(Guid accountId, CancellationToken ct)
    {
        var value = await cache.GetStringAsync(Key(accountId), ct);
        return int.TryParse(value, out var slot) && LocalHangars.IsValidSlot(slot)
            ? slot : LocalHangars.DefaultSlot;
    }

    public async Task<bool> SelectAsync(Guid accountId, int slot, CancellationToken ct) =>
        await ChangeAsync(accountId, slot, ct) == LocalHangarChange.Applied;

    public Task<LocalHangarChange> ChangeAsync(Guid accountId, int slot, CancellationToken ct) =>
        ChangeAsync(accountId, slot, null, ct);

    // With expectedCurrent, the change applies only if that slot is still active (compare-and-set).
    public async Task<LocalHangarChange> ChangeAsync(Guid accountId, int slot, int? expectedCurrent, CancellationToken ct)
    {
        if (accountId == Guid.Empty || !LocalHangars.IsValidSlot(slot)
            || expectedCurrent is { } expected && (!LocalHangars.IsValidSlot(expected) || expected == slot))
            return LocalHangarChange.Invalid;
        if (economy is not null)
        {
            // Import any cache-era slot first so the compare-and-set sees it.
            await GetActiveSlotAsync(accountId, ct);
            return await economy.ChangeHangarSlotAsync(accountId, slot, expectedCurrent, ct);
        }
        await SelectionLock.WaitAsync(ct);
        try
        {
            var active = await GetActiveSlotAsync(accountId, ct);
            if (active == slot || expectedCurrent is { } current && active != current)
                return LocalHangarChange.Conflict;
            await cache.SetStringAsync(Key(accountId), slot.ToString(),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) }, ct);
            return LocalHangarChange.Applied;
        }
        finally { SelectionLock.Release(); }
    }
}
