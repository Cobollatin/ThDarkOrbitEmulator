using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Cluster.Application;

namespace ThDarkOrbitEmulator.Cluster.Infrastructure;

public sealed class MasterOrchestratorBackgroundService(
    IEnumerable<IWorkerSpawner> spawners,
    IOptions<ClusterOptions> options,
    ILogger<MasterOrchestratorBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsMaster)
        {
            logger.LogInformation("Node started in worker mode.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var spawner in spawners)
            {
                await spawner.SpawnAsync(stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
