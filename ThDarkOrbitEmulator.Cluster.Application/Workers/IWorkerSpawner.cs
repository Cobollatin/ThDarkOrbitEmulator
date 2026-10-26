namespace ThDarkOrbitEmulator.Cluster.Application;

public interface IWorkerSpawner
{
    Task SpawnAsync(CancellationToken cancellationToken);
}
