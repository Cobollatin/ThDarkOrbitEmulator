namespace ThDarkOrbitEmulator.ApiService.Features.World;

// The map owner tracks what it announced so a respawn cycle replaces old boxes on idle sockets.
public static class WorldCollectibleVisibility
{
    public static (IReadOnlyList<int> Removed, IReadOnlyList<WorldCollectible> Created) Diff(
        HashSet<int> announced, IReadOnlyList<WorldCollectible> visible)
    {
        var current = visible.Select(box => box.Id).ToHashSet();
        var removed = announced.Where(id => !current.Contains(id)).Order().ToArray();
        var created = visible.Where(box => !announced.Contains(box.Id)).ToArray();
        announced.Clear();
        announced.UnionWith(current);
        return (removed, created);
    }
}
