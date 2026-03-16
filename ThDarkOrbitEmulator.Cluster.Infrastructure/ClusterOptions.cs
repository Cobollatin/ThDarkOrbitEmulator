namespace ThDarkOrbitEmulator.Cluster.Infrastructure;

public sealed class ClusterOptions
{
    public const string SectionName = "Cluster";

    public bool IsMaster { get; set; } = true;

    public bool EnableSshSpawner { get; set; }

    public int DesiredWorkers { get; set; } = 1;
}
