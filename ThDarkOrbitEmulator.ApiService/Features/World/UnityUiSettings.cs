using System.Collections.Concurrent;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public sealed record UnityWindowState(int Height, int Width, int PosX, bool Maximized, int PosY);

// Per-pilot HUD state the Unity client reports back (bar layout, window placement, ammo selection and
// slot assignments). The client keeps none of this locally, so it is replayed on every login.
public sealed class UnityUiSettings
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UnityWindowState> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<int, string>> _slotBars = new(StringComparer.Ordinal);
    private UnityWindowSettingsRequest? _layout;

    public UnityUiSettings()
    {
        foreach (var (category, items) in UnityItemsMenu.Categories)
            _selected[category] = items.First(item => item.Selected).Id;
        _slotBars[UnityItemsMenu.StandardSlotBar] = new SortedDictionary<int, string>(UnityItemsMenu.Lasers
            .Concat(UnityItemsMenu.Rockets)
            .Select((item, index) => (Slot: index + 1, item.Id))
            .ToDictionary(slot => slot.Slot, slot => slot.Id));
        _slotBars[UnityItemsMenu.PremiumSlotBar] = new SortedDictionary<int, string>();
        _slotBars[UnityItemsMenu.ProActionBar] = new SortedDictionary<int, string>();
    }

    public UnityWindowSettingsRequest? Layout
    {
        get { lock (_gate) return _layout; }
        set { lock (_gate) _layout = value; }
    }

    public void SetWindow(string itemId, UnityWindowState state)
    {
        lock (_gate) _windows[itemId] = state;
    }

    public IReadOnlyList<(string ItemId, UnityWindowState State)> Windows()
    {
        lock (_gate) return _windows.Select(window => (window.Key, window.Value)).ToArray();
    }

    // Selects an ammo item; returns the previously selected item of the same category, or null when
    // the item is unknown or already selected.
    public (string Category, string? Previous)? Select(string menuItemId)
    {
        var category = UnityItemsMenu.CategoryOf(menuItemId);
        if (category is null) return null;
        lock (_gate)
        {
            _selected.TryGetValue(category, out var previous);
            if (previous == menuItemId) return (category, null);
            _selected[category] = menuItemId;
            return (category, previous);
        }
    }

    public bool IsSelected(string menuItemId)
    {
        lock (_gate) return _selected.ContainsValue(menuItemId);
    }

    public void MoveSlot(UnitySlotBarConfigSetRequest request)
    {
        if (UnityItemsMenu.CategoryOf(request.MenuItemId) is null) return;
        lock (_gate)
        {
            _slotBars.TryGetValue(request.SlotBarIdFrom, out var from);
            _slotBars.TryGetValue(request.SlotBarIdTo, out var to);
            if (from is not null && from.TryGetValue(request.SlotIdFrom, out var moved) && moved == request.MenuItemId)
                from.Remove(request.SlotIdFrom);
            if (to is null || request.SlotIdTo < 1) return;
            if (to.TryGetValue(request.SlotIdTo, out var displaced) && displaced != request.MenuItemId
                && from is not null && !from.ContainsKey(request.SlotIdFrom) && request.SlotIdFrom >= 1)
                from[request.SlotIdFrom] = displaced;
            to[request.SlotIdTo] = request.MenuItemId;
        }
    }

    public IReadOnlyList<(int SlotId, string MenuItemId)> Slots(string slotBarId)
    {
        lock (_gate)
            return _slotBars.TryGetValue(slotBarId, out var slots)
                ? slots.Select(slot => (slot.Key, slot.Value)).ToArray()
                : [];
    }
}

public sealed class UnityUiSettingsStore
{
    private readonly ConcurrentDictionary<long, UnityUiSettings> _settings = new();

    public static UnityUiSettingsStore Shared { get; } = new();

    public UnityUiSettings For(long userId) => _settings.GetOrAdd(userId, _ => new UnityUiSettings());
}
