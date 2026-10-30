namespace ThDarkOrbitEmulator.ApiService.Features.World;

// All distances are map units and speeds are map units per second.
public sealed record WorldTravel(int MapId, double StartX, double StartY, int TargetX, int TargetY,
    int Speed, long StartedAtUnixMilliseconds, Guid MoveId = default)
{
    public (int X, int Y) PositionAt(DateTimeOffset now)
    {
        var elapsedMilliseconds = Math.Max(0, now.ToUnixTimeMilliseconds() - StartedAtUnixMilliseconds);
        var distance = Math.Sqrt(Math.Pow(TargetX - StartX, 2) + Math.Pow(TargetY - StartY, 2));
        var durationMilliseconds = WorldMovement.DurationMilliseconds(distance, Speed);
        var fraction = Speed <= 0 ? 0 : durationMilliseconds == 0 ? 1
            : Math.Clamp((double)elapsedMilliseconds / durationMilliseconds, 0, 1);
        return ((int)Math.Round(StartX + (TargetX - StartX) * fraction),
            (int)Math.Round(StartY + (TargetY - StartY) * fraction));
    }
}

public sealed record WorldMoveResult(long UserId, int X, int Y, int TimeToTargetMs, bool RadiationWarning);

public static class WorldMovement
{
    public static int DurationMilliseconds(double distance, int speed) => speed <= 0 ? 0
        : (int)Math.Min(Math.Round(distance * 1000d / speed), int.MaxValue);

    public static bool IsInsideBoundary((int X, int Y) position, WorldMapInstance map) =>
        position.X > 0 && position.X < map.Width && position.Y > 0 && position.Y < map.Height;

    public static WorldMoveResult Begin(WorldPilot pilot, WorldTravel? previous, int targetX, int targetY,
        int speed, DateTimeOffset now)
    {
        var map = WorldMapInstance.Starter(pilot.MapId);
        var start = previous?.MapId == pilot.MapId ? previous.PositionAt(now) : (pilot.X, pilot.Y);
        var warning = targetX < 0 || targetX > map.Width || targetY < 0 || targetY > map.Height;
        targetX = Math.Clamp(targetX, 0, map.Width);
        targetY = Math.Clamp(targetY, 0, map.Height);
        var distance = Math.Sqrt(Math.Pow((double)targetX - start.Item1, 2) +
            Math.Pow((double)targetY - start.Item2, 2));
        var duration = DurationMilliseconds(distance, speed);
        return new WorldMoveResult(pilot.UserId, targetX, targetY, duration, warning);
    }

    public static WorldTravel Travel(WorldPilot pilot, WorldTravel? previous, WorldMoveResult move,
        int speed, DateTimeOffset now)
    {
        var start = previous?.MapId == pilot.MapId ? previous.PositionAt(now) : (pilot.X, pilot.Y);
        return new WorldTravel(pilot.MapId, start.Item1, start.Item2, move.X, move.Y, speed,
            now.ToUnixTimeMilliseconds(), Guid.NewGuid());
    }
}

public sealed class WorldConnection(WorldPilot pilot, WorldPacketWriter writer)
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    internal SemaphoreSlim RepairBotDeliveryGate { get; } = new(1, 1);
    public WorldPilot Pilot { get; set; } = pilot;
    // NPC ids this socket has been shown: true = created, false = destroy already announced.
    public System.Collections.Concurrent.ConcurrentDictionary<int, bool> KnownNpcs { get; set; } = new();
    // Last HP/shield this socket was told per NPC, so idle sync only sends real changes.
    public System.Collections.Concurrent.ConcurrentDictionary<int, (int Hp, int Shield)> NpcVitals { get; set; } = new();

    public async Task SendAsync(WorldCommandId command, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try { await writer.WriteAsync(command, payload, ct); }
        finally { writeGate.Release(); }
    }
}
