using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World.Mesh;
using ThDarkOrbitEmulator.Cluster.Infrastructure;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

public sealed class WorldServerHostedService(IOptions<WorldServerOptions> configured,
    IServiceProvider services, ILogger<WorldServerHostedService> logger) : BackgroundService
{
    private TcpListener? listener;
    private volatile bool stopping;
    public int ActualPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configured.Value;
        options.Validate();
        if (!options.Enabled) return;
        if (options.DynamicTcpPort && options.TryGetPortRange(out var first, out var last))
        {
            for (var port = first; port <= last; port++)
            {
                var candidate = new TcpListener(IPAddress.Loopback, port);
                try { candidate.Start(); listener = candidate; break; }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
                { candidate.Stop(); }
            }
            if (listener is null) throw new InvalidOperationException("No world TCP port is available in the configured range.");
        }
        else
        {
            listener = new TcpListener(IPAddress.Loopback, options.DynamicTcpPort ? 0 : options.Port);
            listener.Start();
        }
        options.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ActualPort = options.Port;
        logger.LogInformation("World TCP listener started on loopback port {Port}", options.Port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = ServeAsync(client, options, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException) when (stopping) { }
        catch (ObjectDisposedException) when (stopping) { }
        finally { listener.Stop(); }
    }

    private async Task ServeAsync(TcpClient client, WorldServerOptions options, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var resolver = services.GetRequiredService<IWorldPilotResolver>();
                var sessions = services.GetRequiredService<WorldSessionStore>();
                await new WorldSessionHandler(resolver, options, sessions,
                    services.GetService<IServerMeshRegistry>(),
                    string.IsNullOrWhiteSpace(services.GetService<IOptions<ClusterOptions>>()?.Value.NodeId)
                        ? $"{Environment.MachineName}-{Environment.ProcessId}"
                        : services.GetRequiredService<IOptions<ClusterOptions>>().Value.NodeId,
                    services.GetService<WorldCollectibles>(),
                    services.GetService<WorldNpcs>(),
                    services.GetService<Admin.IRuntimeVariableRegistry>(),
                    services.GetService<Admin.WorldAdminEvents>(),
                    services.GetService<Admin.IAdminCommandDispatcher>()).RunAsync(client.GetStream(), ct);
            }
            catch (Exception error) when (error is IOException or SocketException)
            {
                logger.LogDebug("World connection ended: {ErrorType}", error.GetType().Name);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping = true;
        listener?.Stop();
        await base.StopAsync(cancellationToken);
    }
}
