namespace ThDarkOrbitEmulator.Cluster.Infrastructure;

public sealed class ClusterOptions
{
    public const string SectionName = "Cluster";

    public bool IsMaster { get; set; } = true;

    public bool EnableSshSpawner { get; set; }

    public int DesiredWorkers { get; set; } = 1;

    public string? NodeId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";
    public int MaxMapsPerNode { get; set; } = 4;
    public int HeartbeatIntervalSeconds { get; set; } = 10;
    public int LeaseTtlSeconds { get; set; } = 30;

    public void Validate()
    {
        if (DesiredWorkers is < 1 or > 16 || MaxMapsPerNode is < 1 or > 256
            || HeartbeatIntervalSeconds is < 1 or > 300
            || LeaseTtlSeconds <= HeartbeatIntervalSeconds * 2 || LeaseTtlSeconds > 3600
            || (NodeId is { Length: > 0 } && (NodeId.Length > 128 || NodeId.Any(char.IsControl))))
            throw new InvalidOperationException("Invalid cluster mesh configuration.");
    }
}
