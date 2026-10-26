using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class WorldServerOptionsSpecificationTests
{
    [Test]
    public async Task DynamicListenerWithoutRange_PublishesAssignedLoopbackPortAndStops()
    {
        var options = new WorldServerOptions { Enabled = true, DynamicTcpPort = true, Port = 0 };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            await WaitForPortAsync(server);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.ActualPort);
            Assert.Multiple(() =>
            {
                Assert.That(server.ActualPort, Is.InRange(1, 65535));
                Assert.That(options.Port, Is.EqualTo(server.ActualPort));
                Assert.That(client.Connected, Is.True);
            });
        }
        finally { await server.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task DynamicListenerWithoutRange_IgnoresOccupiedConfiguredPort()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var configuredPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var options = new WorldServerOptions
            {
                Enabled = true, DynamicTcpPort = true, Port = configuredPort
            };
            using var services = new ServiceCollection().BuildServiceProvider();
            using var server = new WorldServerHostedService(Options.Create(options), services,
                NullLogger<WorldServerHostedService>.Instance);

            await server.StartAsync(CancellationToken.None);
            try
            {
                await WaitForPortAsync(server);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, server.ActualPort);
                Assert.Multiple(() =>
                {
                    Assert.That(server.ActualPort, Is.Not.EqualTo(configuredPort));
                    Assert.That(options.Port, Is.EqualTo(server.ActualPort));
                    Assert.That(client.Connected, Is.True);
                });
            }
            finally { await server.StopAsync(CancellationToken.None); }
        }
        finally { occupied.Stop(); }
    }

    [Test]
    public async Task DynamicSinglePortRange_IgnoresConfiguredPortOutsideRange()
    {
        var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var selectedPort = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        var options = Dynamic($"{selectedPort}-{selectedPort}");
        options.Enabled = true;
        options.Port = 1;
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            await WaitForPortAsync(server);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, selectedPort);
            Assert.Multiple(() =>
            {
                Assert.That(server.ActualPort, Is.EqualTo(selectedPort));
                Assert.That(options.Port, Is.EqualTo(selectedPort));
                Assert.That(client.Connected, Is.True);
            });
        }
        finally { await server.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task ExhaustedSinglePortRange_FailsWithoutPublishingPort()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var options = Dynamic($"{port}-{port}");
            options.Enabled = true;
            using var services = new ServiceCollection().BuildServiceProvider();
            using var server = new WorldServerHostedService(Options.Create(options), services,
                NullLogger<WorldServerHostedService>.Instance);

            await server.StartAsync(CancellationToken.None);
            var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Multiple(() =>
            {
                Assert.That(error!.Message, Is.EqualTo("No world TCP port is available in the configured range."));
                Assert.That(server.ActualPort, Is.Zero);
                Assert.That(options.Port, Is.Zero);
            });
        }
        finally { occupied.Stop(); }
    }

    [Test]
    public async Task ExhaustedTwoPortRange_FailsWithoutPublishingPortOrFallingBack()
    {
        var (first, second) = ReserveAdjacentPorts();
        try
        {
            var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
            var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
            var options = Dynamic($"{firstPort}-{secondPort}");
            options.Enabled = true;
            using var services = new ServiceCollection().BuildServiceProvider();
            using var server = new WorldServerHostedService(Options.Create(options), services,
                NullLogger<WorldServerHostedService>.Instance);

            await server.StartAsync(CancellationToken.None);
            var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Multiple(() =>
            {
                Assert.That(error!.Message, Is.EqualTo("No world TCP port is available in the configured range."));
                Assert.That(server.ActualPort, Is.Zero);
                Assert.That(options.Port, Is.Zero);
            });
        }
        finally
        {
            first.Stop();
            second.Stop();
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task DynamicRange_BindsLowestAvailablePortAndPublishesIt(int occupiedPrefixLength)
    {
        var (first, second) = ReserveAdjacentPorts();
        var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
        var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
        var selected = occupiedPrefixLength == 0 ? first : second;
        var blocked = occupiedPrefixLength == 0 ? second : first;
        selected.Stop();
        try
        {
            var options = Dynamic($"{firstPort}-{secondPort}");
            options.Enabled = true;
            using var services = new ServiceCollection().BuildServiceProvider();
            using var server = new WorldServerHostedService(Options.Create(options), services,
                NullLogger<WorldServerHostedService>.Instance);

            await server.StartAsync(CancellationToken.None);
            try
            {
                await WaitForPortAsync(server);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, selected == first ? firstPort : secondPort);
                Assert.Multiple(() =>
                {
                    Assert.That(server.ActualPort, Is.EqualTo(selected == first ? firstPort : secondPort));
                    Assert.That(options.Port, Is.EqualTo(server.ActualPort));
                    Assert.That(client.Connected, Is.True);
                });
            }
            finally { await server.StopAsync(CancellationToken.None); }
        }
        finally { blocked.Stop(); }
    }

    [Test]
    public async Task DisabledListener_CompletesWithoutBindingOrPublishingPort()
    {
        var options = new WorldServerOptions { Enabled = false, Port = 4321 };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);

        await server.StartAsync(CancellationToken.None);
        await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(server.ExecuteTask.IsCompletedSuccessfully, Is.True);
            Assert.That(server.ActualPort, Is.Zero);
            Assert.That(options.Port, Is.EqualTo(4321));
        });
    }

    [Test]
    public async Task InvalidDisabledListener_FaultsBeforeEnabledGateWithoutPublishingPort()
    {
        var options = new WorldServerOptions { Enabled = false, Host = "localhost", Port = 4321 };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);

        await server.StartAsync(CancellationToken.None);
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Is.EqualTo("Invalid loopback WorldServer configuration."));
            Assert.That(server.ActualPort, Is.Zero);
            Assert.That(options.Port, Is.EqualTo(4321));
        });
    }

    [Test]
    public async Task FixedListener_BindsExactlyConfiguredLoopbackPort()
    {
        var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        var options = new WorldServerOptions { Enabled = true, Port = port };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            await WaitForPortAsync(server);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            Assert.Multiple(() =>
            {
                Assert.That(server.ActualPort, Is.EqualTo(port));
                Assert.That(options.Port, Is.EqualTo(port));
                Assert.That(client.Connected, Is.True);
            });
        }
        finally { await server.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task OccupiedFixedPort_PropagatesSocketErrorWithoutFallbackOrPublishing()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var options = new WorldServerOptions { Enabled = true, Port = port };
            using var services = new ServiceCollection().BuildServiceProvider();
            using var server = new WorldServerHostedService(Options.Create(options), services,
                NullLogger<WorldServerHostedService>.Instance);

            await server.StartAsync(CancellationToken.None);
            var error = Assert.ThrowsAsync<SocketException>(async () =>
                await server.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Multiple(() =>
            {
                Assert.That(error!.SocketErrorCode, Is.EqualTo(SocketError.AddressAlreadyInUse));
                Assert.That(server.ActualPort, Is.Zero);
                Assert.That(options.Port, Is.EqualTo(port));
            });
        }
        finally { occupied.Stop(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StoppingIdleListener_CompletesPendingAcceptAndReleasesBoundPort(bool ranged)
    {
        var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        var options = ranged
            ? Dynamic($"{port}-{port}")
            : new WorldServerOptions { Port = port };
        options.Enabled = true;
        using var services = new ServiceCollection().BuildServiceProvider();
        using var server = new WorldServerHostedService(Options.Create(options), services,
            NullLogger<WorldServerHostedService>.Instance);

        await server.StartAsync(CancellationToken.None);
        await WaitForPortAsync(server);
        Assert.That(server.ActualPort, Is.EqualTo(port));
        await server.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var rebound = new TcpListener(IPAddress.Loopback, port);
        try
        {
            rebound.Start();
            Assert.Multiple(() =>
            {
                Assert.That(server.ExecuteTask, Is.Not.Null);
                Assert.That(server.ExecuteTask!.IsCompletedSuccessfully, Is.True);
                Assert.That(((IPEndPoint)rebound.LocalEndpoint).Port, Is.EqualTo(port));
            });
        }
        finally { rebound.Stop(); }
    }

    private static (TcpListener First, TcpListener Second) ReserveAdjacentPorts()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = new TcpListener(IPAddress.Loopback, 0);
            first.Start();
            var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;
            if (firstPort is < 1024 or >= 65535)
            {
                first.Stop();
                continue;
            }

            var second = new TcpListener(IPAddress.Loopback, firstPort + 1);
            try
            {
                second.Start();
                return (first, second);
            }
            catch (SocketException)
            {
                second.Stop();
                first.Stop();
            }
        }

        throw new InvalidOperationException("Could not reserve adjacent loopback ports for the listener test.");
    }

    private static async Task WaitForPortAsync(WorldServerHostedService server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ActualPort == 0)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private static WorldServerOptions Dynamic(string? range) =>
        new() { DynamicTcpPort = true, Port = 0, DynamicTcpPortRange = range };

    [Test]
    public void Defaults_AreDisabledLoopbackFixedPortAndValidate()
    {
        var options = new WorldServerOptions();
        Assert.Multiple(() =>
        {
            Assert.That(WorldServerOptions.SectionName, Is.EqualTo("WorldServer"));
            Assert.That(options.Enabled, Is.False);
            Assert.That(options.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(options.Port, Is.EqualTo(60954));
            Assert.That(options.DynamicTcpPort, Is.False);
            Assert.That(options.DynamicTcpPortRange, Is.Null);
            Assert.That(options.Version, Is.EqualTo("local-1"));
            Assert.That(options.MaximumPacketBytes, Is.EqualTo(65536));
            Assert.DoesNotThrow(options.Validate);
        });
    }

    [TestCase("localhost")]
    [TestCase("0.0.0.0")]
    [TestCase("::1")]
    [TestCase(" 127.0.0.1")]
    [TestCase("127.0.0.1 ")]
    [TestCase("127.0.0.2")]
    [TestCase("")]
    public void Host_MustBeExactIpv4Loopback(string host)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new WorldServerOptions { Host = host }.Validate());
        Assert.That(error!.Message, Is.EqualTo("Invalid loopback WorldServer configuration."));
    }

    [Test]
    public void Validate_RunsEvenWhenListenerIsDisabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WorldServerOptions { Enabled = false, Host = "localhost" }.Validate());
    }

    [TestCase(1)]
    [TestCase(1023)]
    [TestCase(60954)]
    [TestCase(65535)]
    public void FixedPort_AcceptsOneThroughMaximum(int port)
    {
        Assert.DoesNotThrow(() => new WorldServerOptions { Port = port }.Validate());
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(65536)]
    [TestCase(int.MinValue)]
    [TestCase(int.MaxValue)]
    public void FixedPort_RejectsZeroNegativeAndAboveMaximum(int port)
    {
        Assert.Throws<InvalidOperationException>(() => new WorldServerOptions { Port = port }.Validate());
    }

    [TestCase(0)]
    [TestCase(60954)]
    [TestCase(65535)]
    public void DynamicPort_AllowsZeroOrAnyInRangePortWithoutRange(int port)
    {
        Assert.DoesNotThrow(() => new WorldServerOptions { DynamicTcpPort = true, Port = port }.Validate());
    }

    [TestCase(-1)]
    [TestCase(65536)]
    public void DynamicPort_StillRejectsOutOfRangePort(int port)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WorldServerOptions { DynamicTcpPort = true, Port = port }.Validate());
    }

    [TestCase("61000-61003")]
    [TestCase("")]
    [TestCase("   ")]
    public void Range_IsRejectedWhenDynamicPortIsOff(string range)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WorldServerOptions { DynamicTcpPortRange = range }.Validate());
    }

    [TestCase("1024-2047", 1024, 2047)]
    [TestCase("1024-1024", 1024, 1024)]
    [TestCase("65535-65535", 65535, 65535)]
    [TestCase("64512-65535", 64512, 65535)]
    [TestCase(" 61000 - 61003 ", 61000, 61003)]
    [TestCase("+61000-61003", 61000, 61003)]
    public void Range_AcceptsInclusiveBoundsUpToOneThousandTwentyFourPorts(string range, int first, int last)
    {
        var options = Dynamic(range);
        Assert.Multiple(() =>
        {
            Assert.That(options.TryGetPortRange(out var parsedFirst, out var parsedLast), Is.True);
            Assert.That((parsedFirst, parsedLast), Is.EqualTo((first, last)));
            Assert.DoesNotThrow(options.Validate);
        });
    }

    [TestCase("1023-1100")]
    [TestCase("1024-2048")]
    [TestCase("65000-65536")]
    [TestCase("61003-61000")]
    [TestCase("61000")]
    [TestCase("61000-61001-61002")]
    [TestCase("61000-")]
    [TestCase("-61000")]
    [TestCase("a-b")]
    [TestCase("61000..61003")]
    [TestCase("")]
    [TestCase("   ")]
    public void Range_RejectsMalformedReversedOversizedAndPrivilegedRanges(string range)
    {
        var options = Dynamic(range);
        Assert.Multiple(() =>
        {
            Assert.That(options.TryGetPortRange(out var first, out var last), Is.False);
            Assert.That(options.Validate, Throws.InstanceOf<InvalidOperationException>());
            if (string.IsNullOrWhiteSpace(range)) Assert.That((first, last), Is.EqualTo((0, 0)));
        });
    }

    [Test]
    public void Range_NullMeansNoRangeAndIsValidForDynamicListener()
    {
        var options = Dynamic(null);
        Assert.Multiple(() =>
        {
            Assert.That(options.TryGetPortRange(out var first, out var last), Is.False);
            Assert.That((first, last), Is.EqualTo((0, 0)));
            Assert.DoesNotThrow(options.Validate);
        });
    }

    [Test]
    public void Range_UsesInvariantAsciiSignsRegardlessOfProcessCulture()
    {
        var original = CultureInfo.CurrentCulture;
        var custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        custom.NumberFormat.PositiveSign = "p";
        try
        {
            CultureInfo.CurrentCulture = custom;
            var accepted = Dynamic("+1024-+1025");
            var rejected = Dynamic("p1024-p1025");
            Assert.Multiple(() =>
            {
                Assert.That(accepted.TryGetPortRange(out var first, out var last), Is.True);
                Assert.That((first, last), Is.EqualTo((1024, 1025)));
                Assert.DoesNotThrow(accepted.Validate);
                Assert.That(rejected.TryGetPortRange(out var rejectedFirst, out var rejectedLast), Is.False);
                Assert.That((rejectedFirst, rejectedLast), Is.EqualTo((0, 0)));
                Assert.That(rejected.Validate, Throws.InstanceOf<InvalidOperationException>());
            });
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [TestCase("1024-1023")]
    [TestCase("1024-2048")]
    [TestCase("1024-abc")]
    [TestCase("abc-1024")]
    [TestCase("1024-1025-extra")]
    [TestCase("1,024-1,025")]
    [TestCase("１０２４-１０２５")]
    public void Range_FailureDoesNotPublishPartiallyParsedBounds(string range)
    {
        var options = Dynamic(range);
        Assert.Multiple(() =>
        {
            Assert.That(options.TryGetPortRange(out var first, out var last), Is.False);
            Assert.That((first, last), Is.EqualTo((0, 0)));
            Assert.That(options.Validate, Throws.InstanceOf<InvalidOperationException>());
        });
    }

    [TestCase(64)]
    [TestCase(65536)]
    [TestCase(1048576)]
    public void MaximumPacketBytes_AcceptsInclusiveBounds(int bytes)
    {
        Assert.DoesNotThrow(() => new WorldServerOptions { MaximumPacketBytes = bytes }.Validate());
    }

    [TestCase(63)]
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1048577)]
    public void MaximumPacketBytes_RejectsOutsideBounds(int bytes)
    {
        Assert.Throws<InvalidOperationException>(() => new WorldServerOptions { MaximumPacketBytes = bytes }.Validate());
    }

    [Test]
    public void Version_AcceptsOneThroughSixtyFourCharacters()
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => new WorldServerOptions { Version = "v" }.Validate());
            Assert.DoesNotThrow(() => new WorldServerOptions { Version = new string('v', 64) }.Validate());
            Assert.DoesNotThrow(() => new WorldServerOptions { Version = " padded " }.Validate());
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void Version_RejectsBlank(string version)
    {
        Assert.Throws<InvalidOperationException>(() => new WorldServerOptions { Version = version }.Validate());
    }

    [Test]
    public void Version_RejectsNullAndSixtyFiveCharacters()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => new WorldServerOptions { Version = null! }.Validate());
            Assert.Throws<InvalidOperationException>(() =>
                new WorldServerOptions { Version = new string('v', 65) }.Validate());
        });
    }

    [Test]
    public void Validate_DoesNotMutateOptions()
    {
        var options = new WorldServerOptions
        {
            DynamicTcpPort = true, Port = 4321, DynamicTcpPortRange = " 61000 - 61003 ", Version = " v "
        };
        options.Validate();
        Assert.Multiple(() =>
        {
            Assert.That(options.Port, Is.EqualTo(4321));
            Assert.That(options.DynamicTcpPortRange, Is.EqualTo(" 61000 - 61003 "));
            Assert.That(options.Version, Is.EqualTo(" v "));
        });
    }
}
