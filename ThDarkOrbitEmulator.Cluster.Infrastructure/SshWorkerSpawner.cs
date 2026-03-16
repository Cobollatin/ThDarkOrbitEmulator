using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Cluster.Application;

namespace ThDarkOrbitEmulator.Cluster.Infrastructure;

public sealed class SshWorkerSpawner(ILogger<SshWorkerSpawner> logger, IOptions<ClusterOptions> options) : IWorkerSpawner
{
    public Task SpawnAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnableSshSpawner)
        {
            return Task.CompletedTask;
        }

        logger.LogInformation("SSH worker orchestration cycle executed.");
        return Task.CompletedTask;
    }
}
