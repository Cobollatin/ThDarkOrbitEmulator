using System.Globalization;
using System.Text;
using Npgsql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Admin;

public sealed record AdminCommand(string? Text, string? Verb, string[]? Arguments);
public sealed record AdminCommandActor(string Surface, string Actor, string NodeId);
public sealed record AdminCommandResult(string Command, object? Data, long? AuditId = null);

public interface IAdminCommandDispatcher
{
    Task<AdminCommandResult> ExecuteAsync(AdminCommand command, AdminCommandActor actor, CancellationToken ct = default);
}

public sealed class AdminCommandDispatcher(IRuntimeVariableRegistry variables, IServerMeshRegistry mesh,
    WorldSessionStore sessions, EconomyStore economy, IConfiguration configuration,
    WorldAdminEvents worldEvents) : IAdminCommandDispatcher
{
    public static readonly string[] Commands =
    [
        "help", "var list [category|search]", "var get <key>", "var set <key> <value> [reason]",
        "var reset <key> [reason]", "var history [key] [limit]", "var rollback <changeId> [reason]",
        "mesh status", "mesh scale <desiredReplicas> [maxMapsPerNode]", "mesh drain <nodeId>",
        "mesh activate <nodeId>", "mesh rebalance", "world status [mapId]",
        "world tp <userIdOrName> <mapId> <x> <y>", "world heal <userIdOrName>",
        "world spawn <box|ore|streuner|lordakia|saimon> <mapId> <x> <y>",
        "world grant <userIdOrName> <credits|uridium|xp|honor> <amount>", "world announce <message>"
    ];

    public async Task<AdminCommandResult> ExecuteAsync(AdminCommand command, AdminCommandActor actor,
        CancellationToken ct = default)
    {
        try
        {
            var result = await ExecuteCoreAsync(command, actor, ct);
            var auditId = await RecordAsync(command, actor, true, null, ct);
            return result with { AuditId = auditId };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await RecordAsync(command, actor, false, error.Message, ct);
            throw;
        }
    }

    private async Task<long> RecordAsync(AdminCommand command, AdminCommandActor actor, bool success,
        string? error, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(configuration.GetConnectionString("identity-write")
            ?? throw new InvalidOperationException("identity-write connection is required."));
        await db.OpenAsync(ct);
        await using var statement = new NpgsqlCommand("INSERT INTO identity.admin_command_history(surface,actor,node_id,command_text,succeeded,error) VALUES(@surface,@actor,@node,@text,@success,@error) RETURNING command_id", db);
        statement.Parameters.AddWithValue("surface", actor.Surface);
        statement.Parameters.AddWithValue("actor", actor.Actor);
        statement.Parameters.AddWithValue("node", actor.NodeId);
        statement.Parameters.AddWithValue("text", command.Text ?? string.Join(' ', new[] { command.Verb ?? "" }.Concat(command.Arguments ?? [])));
        statement.Parameters.AddWithValue("success", success);
        statement.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        return (long)(await statement.ExecuteScalarAsync(ct))!;
    }

    private async Task<AdminCommandResult> ExecuteCoreAsync(AdminCommand command, AdminCommandActor actor,
        CancellationToken ct = default)
    {
        if (actor.Surface is not ("ServerManagement" or "AdminCms" or "InGameAdmin")
            || string.IsNullOrWhiteSpace(actor.Actor) || string.IsNullOrWhiteSpace(actor.NodeId))
            throw new ArgumentException("A valid surface, actor and node ID are required.");
        var words = command.Text is not null ? Parse(command.Text) :
            [.. Parse(command.Verb ?? ""), .. command.Arguments ?? []];
        if (words.Length == 0) throw new ArgumentException("Command is empty.");
        string Arg(int index) => index < words.Length ? words[index] : throw new ArgumentException("Missing command argument.");
        int Integer(int index) => int.TryParse(Arg(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : throw new ArgumentException($"Argument {index} must be an integer.");
        long Long(int index) => long.TryParse(Arg(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : throw new ArgumentException($"Argument {index} must be an integer.");
        string Tail(int index, string fallback = "Admin command") => index < words.Length
            ? string.Join(' ', words[index..]) : fallback;
        AdminCommandResult Result(object? data) => new(string.Join(' ', words.Take(Math.Min(2, words.Length))), data);
        RuntimeVariableActor Attribution(string reason) => new(actor.Surface, actor.Actor, actor.NodeId,
            reason, command.Text);

        switch (words[0].ToLowerInvariant(), words.Length > 1 ? words[1].ToLowerInvariant() : "")
        {
            case ("help", _): return Result(Commands);
            case ("var", "list"):
                var list = await variables.ListAsync(ct);
                return Result(words.Length <= 2 ? list : list.Where(v =>
                    v.Category.Contains(Arg(2), StringComparison.OrdinalIgnoreCase)
                    || v.Key.Contains(Arg(2), StringComparison.OrdinalIgnoreCase)).ToArray());
            case ("var", "get"): return Result(await variables.GetAsync(Arg(2), ct));
            case ("var", "set"):
                return Result(await variables.SetAsync(Arg(2), Arg(3), Attribution(Tail(4)), ct));
            case ("var", "reset"):
                return Result(await variables.ResetToDefaultAsync(Arg(2), Attribution(Tail(3)), ct));
            case ("var", "history"):
                string? key = null;
                var limit = 100;
                if (words.Length > 2)
                {
                    if (int.TryParse(words[2], out var numeric)) limit = numeric;
                    else key = words[2];
                }
                if (words.Length > 3) limit = Integer(3);
                return Result(await variables.HistoryAsync(key, limit, ct: ct));
            case ("var", "rollback"):
                return Result(await variables.RollbackAsync(Long(2), Attribution(Tail(3)), ct));
            case ("mesh", "status"): return Result(await mesh.GetSnapshotAsync(ct));
            case ("mesh", "scale"):
                var desired = Integer(2);
                var maxMaps = words.Length > 3 ? Integer(3) : (await mesh.GetSnapshotAsync(ct)).MaxMapsPerNode;
                if (desired is < 1 or > 16 || maxMaps is < 1 or > 256)
                    throw new ArgumentException("Desired replicas must be 1..16 and max maps 1..256.");
                await variables.SetAsync("mesh.desired_replicas", desired.ToString(CultureInfo.InvariantCulture),
                    Attribution("Mesh scale command"), ct);
                await variables.SetAsync("mesh.max_maps_per_node", maxMaps.ToString(CultureInfo.InvariantCulture),
                    Attribution("Mesh scale command"), ct);
                return Result(await mesh.ScaleAsync(desired, maxMaps, ct));
            case ("mesh", "drain"):
            case ("mesh", "activate"):
                var snapshot = await mesh.GetSnapshotAsync(ct);
                var node = snapshot.Nodes.FirstOrDefault(n => n.NodeId == Arg(2))
                    ?? throw new KeyNotFoundException("Mesh node was not found.");
                if (words[1].Equals("drain", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.Status != ServerMeshNodeStatus.Active
                        || snapshot.Nodes.Count(n => n.Status == ServerMeshNodeStatus.Active) < 2
                        || (!node.Simulated && node.ActivePilots > 0))
                        throw new InvalidOperationException("Node cannot be drained while pilots are connected or no healthy target exists.");
                    return Result(await mesh.SetDrainingAsync(node.NodeId, ct));
                }
                if (node.Status != ServerMeshNodeStatus.Draining)
                    throw new InvalidOperationException("Only a draining node can be activated.");
                return Result(await mesh.SetActiveAsync(node.NodeId, ct));
            case ("mesh", "rebalance"): return Result(await mesh.RebalanceAsync(ct));
            case ("world", "status"):
                var maps = words.Length > 2 ? [Integer(2)] : ServerMeshCoordinator.GameMapIds;
                var world = await mesh.GetSnapshotAsync(ct);
                var pilots = (await Task.WhenAll(maps.Select(id => sessions.ConnectedAsync(id, ct))))
                    .SelectMany(p => p).ToArray();
                return Result(new { Maps = maps.Select(id => new { MapId = id,
                    OwnerNodeId = world.Leases.FirstOrDefault(l => l.MapId == id)?.NodeId,
                    OnlinePilots = pilots.Count(p => p.MapId == id) }).ToArray(), Pilots = pilots });
            case ("world", "tp"):
                var target = await ResolvePilotAsync(Arg(2), ct);
                var mapId = Integer(3); var x = Integer(4); var y = Integer(5);
                if (mapId is < 1 or > 16 || x is < 0 or > 21000 || y is < 0 or > 13100)
                    throw new ArgumentException("Invalid map or coordinates.");
                return Result(await sessions.TeleportAsync(target.UserId, mapId, x, y, false, ct)
                    ?? throw new KeyNotFoundException("Pilot is not online."));
            case ("world", "heal"):
                var patient = await ResolvePilotAsync(Arg(2), ct);
                var online = (await MeshWorldOverview.ReadPilotsAsync(sessions, ct))
                    .FirstOrDefault(p => p.UserId == patient.UserId)
                    ?? throw new KeyNotFoundException("Pilot is not online.");
                return Result(await sessions.TeleportAsync(patient.UserId, online.MapId, online.X, online.Y, true, ct));
            case ("world", "grant"):
                var recipient = await ResolvePilotAsync(Arg(2), ct);
                var asset = Arg(3).ToLowerInvariant() switch
                { "xp" => "experience", "honor" => "honor", "credits" => "credits", "uridium" => "uridium",
                    _ => throw new ArgumentException("Unsupported grant asset.") };
                var amount = Long(4);
                if (amount <= 0) throw new ArgumentException("Grant amount must be positive.");
                var applied = await economy.AdjustAsync(recipient.AccountId, asset, amount,
                    $"admin_command:{actor.Surface}", "admin", Guid.NewGuid().ToString("N"), ct, actor.Actor);
                if (!applied) throw new InvalidOperationException("Grant was not applied.");
                return Result(new { recipient.UserId, Asset = asset, Amount = amount });
            case ("world", "spawn"):
                var kind = Arg(2).ToLowerInvariant();
                if (kind is not ("box" or "ore" or "streuner" or "lordakia" or "saimon"))
                    throw new ArgumentException("Unsupported spawn kind.");
                var spawnMap = Integer(3); var spawnX = Integer(4); var spawnY = Integer(5);
                if (spawnMap is < 1 or > 16 || spawnX is < 0 or > 21000 || spawnY is < 0 or > 13100)
                    throw new ArgumentException("Invalid map or coordinates.");
                return Result(await worldEvents.SpawnAsync(kind, spawnMap, spawnX, spawnY, ct));
            case ("world", "announce"):
                var message = Tail(2, "");
                if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
                    throw new ArgumentException("Announcement must contain 1..500 characters.");
                return Result(await worldEvents.AnnounceAsync(message, actor, ct));
            default: throw new ArgumentException("Unknown admin command. Run help.");
        }
    }

    private async Task<(long UserId, Guid AccountId)> ResolvePilotAsync(string userIdOrName, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(configuration.GetConnectionString("identity-write")
            ?? throw new InvalidOperationException("identity-write connection is required."));
        await db.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT game_user_id,id FROM identity.accounts WHERE username=@name OR game_user_id=@id LIMIT 1", db);
        command.Parameters.AddWithValue("name", userIdOrName);
        command.Parameters.AddWithValue("id", long.TryParse(userIdOrName, out var id) ? id : -1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Pilot was not found.");
        return (reader.GetInt64(0), reader.GetGuid(1));
    }

    public static string[] Parse(string input)
    {
        if (input.Length > 2048) throw new ArgumentException("Command is too long.");
        var words = new List<string>(); var token = new StringBuilder();
        char quote = '\0'; bool escaped = false;
        foreach (var c in input)
        {
            if (escaped) { token.Append(c); escaped = false; continue; }
            if (c == '\\' && quote != '\0') { escaped = true; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; else token.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (token.Length > 0) { words.Add(token.ToString()); token.Clear(); } }
            else token.Append(c);
        }
        if (quote != '\0') throw new ArgumentException("Unterminated quote.");
        if (token.Length > 0) words.Add(token.ToString());
        return words.ToArray();
    }
}
