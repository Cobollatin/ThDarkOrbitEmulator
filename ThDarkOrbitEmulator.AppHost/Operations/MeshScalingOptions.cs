using Microsoft.Extensions.Configuration;

namespace ThDarkOrbitEmulator.AppHost;

internal static class MeshScalingOptions
{
    public static int DesiredReplicas(IConfiguration configuration)
    {
        var raw = configuration["Emulator:Mesh:ApiReplicas"]
            ?? configuration["Cluster:DesiredWorkers"] ?? "1";
        if (!int.TryParse(raw, out var count) || count is < 1 or > 16)
            throw new InvalidOperationException("Api replicas must be between 1 and 16.");
        return count;
    }
}
