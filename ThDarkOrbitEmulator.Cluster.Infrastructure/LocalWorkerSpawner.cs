using Microsoft.Extensions.Logging;
using ThDarkOrbitEmulator.Cluster.Application;

namespace ThDarkOrbitEmulator.Cluster.Infrastructure;

public sealed class LocalWorkerSpawner(ILogger<LocalWorkerSpawner> logger) : IWorkerSpawner
{
    public Task SpawnAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Local worker orchestration cycle executed.");
        return Task.CompletedTask;
    }
}
