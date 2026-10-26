using System.Diagnostics;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Cluster.Infrastructure;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Mesh;

public sealed class ServerMeshHostedService(IServerMeshRegistry registry,
    WorldServerHostedService world, IOptions<WorldServerOptions> worldOptions,
    WorldSessionStore sessions, IOptions<ClusterOptions> clusterOptions, TimeProvider clock,
    ILogger<ServerMeshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = clusterOptions.Value;
        options.Validate();
        var id = string.IsNullOrWhiteSpace(options.NodeId)
            ? $"{Environment.MachineName}-{Environment.ProcessId}" : options.NodeId;
        var started = clock.GetUtcNow();
        using var process = Process.GetCurrentProcess();
        var previousCpu = process.TotalProcessorTime;
        var previousAt = started;
        try
        {
            if (worldOptions.Value.Enabled)
                while (world.ActualPort == 0 && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(20, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var seconds = (now - previousAt).TotalSeconds;
                var percentage = seconds > 0 ? Math.Clamp(
                    (cpu - previousCpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100, 0, 100) : 0;
                previousAt = now;
                previousCpu = cpu;
                var tcp = worldOptions.Value;
                var node = new ServerMeshNode(id!, Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "api-game",
                    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://api-game",
                    tcp.Host, world.ActualPort, ServerMeshNodeStatus.Active,
                    started, now, sessions.ActivePilotCount, [], percentage, process.WorkingSet64);
                var snapshot = await registry.HeartbeatAsync(node, stoppingToken);
                await sessions.RefreshLocalPresenceAsync(stoppingToken);
                logger.LogInformation("Mesh heartbeat: node {NodeId}, world TCP {Port}, maps {Maps}",
                    id, world.ActualPort, string.Join(',', snapshot.Nodes.First(n => n.NodeId == id).LeasedMapIds));
                await Task.Delay(TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await registry.SetDrainingAsync(id!, timeout.Token); }
            catch (Exception error) { logger.LogWarning(error, "Could not drain mesh node {NodeId}", id); }
        }
    }
}
