namespace ThDarkOrbitEmulator.ApiService.Features.World.Admin;

public static class InGameAdmin
{
    public static AdminCommandActor Actor(WorldPilot pilot, string? nodeId) => new(
        "InGameAdmin", $"{pilot.UserName}:{pilot.UserId}",
        string.IsNullOrWhiteSpace(nodeId) ? $"{Environment.MachineName}-{Environment.ProcessId}" : nodeId);

    public static string Expand(string text, WorldPilot pilot)
    {
        if (!text.StartsWith('/')) return text;
        var words = AdminCommandDispatcher.Parse(text[1..]);
        if (words.Length == 0) return "help";
        return words[0].ToLowerInvariant() switch
        {
            "tp" => $"world tp {pilot.UserId} {string.Join(' ', words.Skip(1))}",
            "heal" => $"world heal {pilot.UserId}",
            "spawn" => $"world {text[1..]}",
            "grant" => $"world grant {pilot.UserId} {string.Join(' ', words.Skip(1))}",
            _ => text[1..]
        };
    }
}
