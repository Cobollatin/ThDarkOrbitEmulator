using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using ThDarkOrbitEmulator.AppHost;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.Cluster.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Operations;

public class MeshScalingTests
{
    [TestCase(1)]
    [TestCase(16)]
    public void ClusterOptions_AcceptsBoundedWorkers(int count) =>
        Assert.DoesNotThrow(() => new ClusterOptions { DesiredWorkers = count }.Validate());

    [TestCase(0)]
    [TestCase(17)]
    public void ClusterOptions_RejectsOutOfBoundsWorkers(int count) =>
        Assert.Throws<InvalidOperationException>(() => new ClusterOptions { DesiredWorkers = count }.Validate());

    [Test]
    public void ClusterOptions_RequiresLeaseLongerThanTwoHeartbeats() =>
        Assert.Throws<InvalidOperationException>(() => new ClusterOptions
        { HeartbeatIntervalSeconds = 10, LeaseTtlSeconds = 20 }.Validate());

    [TestCase("Emulator:Mesh:ApiReplicas", "16", 16)]
    [TestCase("Cluster:DesiredWorkers", "3", 3)]
    public void AppHost_ResolvesReplicaCount(string key, string value, int expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        Assert.That(MeshScalingOptions.DesiredReplicas(configuration), Is.EqualTo(expected));
    }

    [TestCase("0")]
    [TestCase("17")]
    [TestCase("abc")]
    public void AppHost_RejectsInvalidReplicaCount(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Emulator:Mesh:ApiReplicas"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => MeshScalingOptions.DesiredReplicas(configuration));
    }

    [Test]
    public void WorldServerOptions_ValidatesDynamicRange()
    {
        Assert.DoesNotThrow(() => new WorldServerOptions
        { DynamicTcpPort = true, Port = 0, DynamicTcpPortRange = "61000-61003" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new WorldServerOptions
        { DynamicTcpPort = true, Port = 0, DynamicTcpPortRange = "61003-61000" }.Validate());
    }

    [Test]
    public async Task WorldListeners_AllocateDistinctDynamicPorts()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var firstOptions = new WorldServerOptions { Enabled = true, DynamicTcpPort = true, Port = 0 };
        var secondOptions = new WorldServerOptions { Enabled = true, DynamicTcpPort = true, Port = 0 };
        using var first = new WorldServerHostedService(Options.Create(firstOptions), services,
            NullLogger<WorldServerHostedService>.Instance);
        using var second = new WorldServerHostedService(Options.Create(secondOptions), services,
            NullLogger<WorldServerHostedService>.Instance);
        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 50 && (firstOptions.Port == 0 || secondOptions.Port == 0); attempt++)
                await Task.Delay(20);
            Assert.Multiple(() =>
            {
                Assert.That(firstOptions.Port, Is.GreaterThan(0));
                Assert.That(secondOptions.Port, Is.GreaterThan(0));
                Assert.That(secondOptions.Port, Is.Not.EqualTo(firstOptions.Port));
                Assert.That(first.ActualPort, Is.EqualTo(firstOptions.Port));
                Assert.That(second.ActualPort, Is.EqualTo(secondOptions.Port));
            });
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
        }
    }
}
