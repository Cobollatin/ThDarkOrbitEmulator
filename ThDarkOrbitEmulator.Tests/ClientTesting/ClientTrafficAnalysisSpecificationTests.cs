using System.Text.Json;
using ThDarkOrbitEmulator.AppHost.ClientTesting;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

/// <summary>
/// SPEC-02 traffic-analysis contract for <see cref="ClientTestingTrafficAnalyzer"/> (mitmproxy JSONL log summaries).
/// </summary>
public class ClientTrafficAnalysisSpecificationTests
{
    private static readonly ClientTestingOptions Options = new() { LocalGatewayHttpsUrl = "https://gateway.test:60952" };

    private string _logPath = null!;

    [SetUp]
    public void SetUp() =>
        _logPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "spec02-traffic-" + Guid.NewGuid().ToString("N") + ".jsonl");

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    [Test]
    public void MissingLog_ThrowsFileNotFoundWithStartMitmHint()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => ClientTestingTrafficAnalyzer.Analyze(_logPath, Options, []));
        Assert.That(ex!.Message, Does.Contain(_logPath).And.Contain("client-test-start-mitm"));
    }

    [Test]
    public void EmptyLog_ProducesZeroCountsAndEmptyLists()
    {
        File.WriteAllText(_logPath, "");
        var analysis = ClientTestingTrafficAnalyzer.Analyze(_logPath, Options, []);

        Assert.Multiple(() =>
        {
            Assert.That(analysis.TotalRequests, Is.Zero);
            Assert.That(analysis.LocalGatewayHits, Is.Zero);
            Assert.That(analysis.ProductionHits, Is.Zero);
            Assert.That(analysis.RedirectedHits, Is.Zero);
            Assert.That(analysis.ByHost, Is.Empty);
            Assert.That(analysis.UnmatchedProductionPaths, Is.Empty);
        });
    }

    [Test]
    public void BlankAndWhitespaceLines_AreSkippedAndNotCounted()
    {
        WriteLines("", "   ", Entry("www.example.test", "GET", "/a"), "\t");
        var analysis = Analyze();
        Assert.That(analysis.TotalRequests, Is.EqualTo(1));
    }

    [Test]
    public void MalformedJsonLine_PropagatesJsonException()
    {
        WriteLines(Entry("www.example.test", "GET", "/a"), "{not json");
        Assert.That(() => Analyze(), Throws.InstanceOf<JsonException>());
    }

    [Test]
    public void NonBooleanLocalFlag_PropagatesInvalidOperationException()
    {
        WriteLines("""{"host":"www.example.test","is_local_gateway":"yes"}""");
        Assert.Throws<InvalidOperationException>(() => Analyze());
    }

    [TestCase("is_local_gateway")]
    [TestCase("redirected")]
    public void NullBooleanFlag_IsRejectedRatherThanTreatedAsFalse(string flag)
    {
        WriteLines($$"""{"host":"www.example.test","{{flag}}":null}""");
        Assert.Throws<InvalidOperationException>(() => Analyze());
    }

    [TestCase("host")]
    [TestCase("method")]
    [TestCase("path")]
    public void NonStringRequestField_IsRejectedRatherThanDefaulted(string field)
    {
        WriteLines($$"""{"{{field}}":42}""");
        Assert.Throws<InvalidOperationException>(() => Analyze());
    }

    [TestCase("[]")]
    [TestCase("null")]
    public void NonObjectJsonRoot_IsRejected(string json)
    {
        WriteLines(json);
        Assert.Throws<InvalidOperationException>(() => Analyze());
    }

    [Test]
    public void MissingFields_DefaultToEmptyHostGetMethodAndRootPath()
    {
        WriteLines("{}");
        var analysis = Analyze();

        Assert.That(analysis.ProductionHits, Is.EqualTo(1));
        Assert.That(analysis.ByHost.Single(), Is.EqualTo(new TrafficHostSummary("", 1, false)));
        Assert.That(analysis.UnmatchedProductionPaths.Single(), Is.EqualTo(new TrafficPathSummary("", "GET", "/", 1)));
    }

    [Test]
    public void NullStringFields_FallBackToTheSameDefaults()
    {
        WriteLines("""{"host":null,"method":null,"path":null}""");
        var analysis = Analyze();
        Assert.That(analysis.UnmatchedProductionPaths.Single(), Is.EqualTo(new TrafficPathSummary("", "GET", "/", 1)));
    }

    [TestCase("gateway.test")]
    [TestCase("GATEWAY.test")]
    [TestCase("localhost")]
    [TestCase("LocalHost")]
    public void GatewayHostAndLocalhost_AreLocalCaseInsensitively(string host)
    {
        WriteLines(Entry(host, "GET", "/x"));
        var analysis = Analyze();

        Assert.Multiple(() =>
        {
            Assert.That(analysis.LocalGatewayHits, Is.EqualTo(1));
            Assert.That(analysis.ProductionHits, Is.Zero);
            Assert.That(analysis.UnmatchedProductionPaths, Is.Empty);
            Assert.That(analysis.ByHost.Single().IsLocalGateway, Is.True);
        });
    }

    [TestCase("127.0.0.1")]
    [TestCase("gateway.test.evil")]
    [TestCase("localhost.example")]
    public void OtherHosts_AreProductionUnlessFlagged(string host)
    {
        WriteLines(Entry(host, "GET", "/x"));
        var analysis = Analyze();
        Assert.That(analysis.ProductionHits, Is.EqualTo(1));
        Assert.That(analysis.LocalGatewayHits, Is.Zero);
    }

    [Test]
    public void GatewayPortIsIgnored_OnlyTheHostnameIsCompared()
    {
        var options = new ClientTestingOptions { LocalGatewayHttpsUrl = "https://gateway.test:1/ignored" };
        WriteLines(Entry("gateway.test", "GET", "/x"));
        var analysis = ClientTestingTrafficAnalyzer.Analyze(_logPath, options, []);
        Assert.That(analysis.LocalGatewayHits, Is.EqualTo(1));
    }

    [Test]
    public void LocalGatewayFlag_CountsAsLocalButHostSummaryUsesHostnameOnly()
    {
        WriteLines(Entry("www.example.test", "GET", "/x", isLocal: true));
        var analysis = Analyze();

        Assert.Multiple(() =>
        {
            Assert.That(analysis.LocalGatewayHits, Is.EqualTo(1));
            Assert.That(analysis.ProductionHits, Is.Zero);
            Assert.That(analysis.UnmatchedProductionPaths, Is.Empty);
            Assert.That(analysis.ByHost.Single(), Is.EqualTo(new TrafficHostSummary("www.example.test", 1, false)));
        });
    }

    [Test]
    public void RedirectedFlag_IsCountedIndependentlyOfLocality()
    {
        WriteLines(
            Entry("gateway.test", "GET", "/a", redirected: true),
            Entry("www.example.test", "GET", "/b", redirected: true),
            Entry("www.example.test", "GET", "/c", redirected: false),
            Entry("www.example.test", "GET", "/d"));
        var analysis = Analyze();

        Assert.That(analysis.RedirectedHits, Is.EqualTo(2));
        Assert.That(analysis.LocalGatewayHits + analysis.ProductionHits, Is.EqualTo(analysis.TotalRequests));
    }

    [Test]
    public void ExplicitFalseFlags_DoNotChangeProductionOrRedirectCounts()
    {
        WriteLines(Entry("www.example.test", "GET", "/a", isLocal: false, redirected: false));
        var analysis = Analyze();

        Assert.Multiple(() =>
        {
            Assert.That(analysis.TotalRequests, Is.EqualTo(1));
            Assert.That(analysis.LocalGatewayHits, Is.Zero);
            Assert.That(analysis.ProductionHits, Is.EqualTo(1));
            Assert.That(analysis.RedirectedHits, Is.Zero);
        });
    }

    [Test]
    public void ByHost_MergesHostsCaseInsensitivelyKeepingFirstSeenSpelling()
    {
        WriteLines(
            Entry("WWW.Example.test", "GET", "/a"),
            Entry("www.example.test", "GET", "/b"),
            Entry("gateway.test", "GET", "/c"));
        var analysis = Analyze();

        Assert.That(analysis.ByHost, Is.EqualTo(new[]
        {
            new TrafficHostSummary("WWW.Example.test", 2, false),
            new TrafficHostSummary("gateway.test", 1, true)
        }));
    }

    [Test]
    public void ByHost_IsOrderedByDescendingCount()
    {
        WriteLines(
            Entry("a.test", "GET", "/"),
            Entry("b.test", "GET", "/"),
            Entry("b.test", "GET", "/"),
            Entry("c.test", "GET", "/"),
            Entry("c.test", "GET", "/"),
            Entry("c.test", "GET", "/"));
        var analysis = Analyze();
        Assert.That(analysis.ByHost.Select(h => h.Host), Is.EqualTo(new[] { "c.test", "b.test", "a.test" }));
    }

    [Test]
    public void EqualCountHosts_KeepFirstSeenOrder()
    {
        WriteLines(Entry("z.test", "GET", "/"), Entry("a.test", "GET", "/"));
        Assert.That(Analyze().ByHost.Select(host => host.Host), Is.EqualTo(new[] { "z.test", "a.test" }));
    }

    [Test]
    public void ProductionPaths_GroupByHostMethodAndPathCaseInsensitively()
    {
        WriteLines(
            Entry("www.example.test", "GET", "/Index.es"),
            Entry("WWW.EXAMPLE.TEST", "get", "/index.ES"),
            Entry("www.example.test", "POST", "/Index.es"),
            Entry("www.example.test", "GET", "/Index.es?action=x"));
        var paths = Analyze().UnmatchedProductionPaths;

        Assert.That(paths, Has.Count.EqualTo(3));
        Assert.That(paths[0], Is.EqualTo(new TrafficPathSummary("www.example.test", "GET", "/Index.es", 2)));
        Assert.That(paths.Select(p => p.Path), Does.Contain("/Index.es?action=x"));
    }

    [Test]
    public void EqualCountProductionPaths_KeepFirstSeenOrderAndSpelling()
    {
        WriteLines(
            Entry("Z.test", "GET", "/First"),
            Entry("a.test", "POST", "/second"),
            Entry("z.TEST", "get", "/first"),
            Entry("A.TEST", "post", "/SECOND"));

        Assert.That(Analyze().UnmatchedProductionPaths, Is.EqualTo(new[]
        {
            new TrafficPathSummary("Z.test", "GET", "/First", 2),
            new TrafficPathSummary("a.test", "POST", "/second", 2)
        }));
    }

    [Test]
    public void ProductionPaths_AreListedEvenWhenTheCatalogMapsThem()
    {
        var catalog = new[] { CatalogEntry("www.example.test", "GET", "/mapped") };
        WriteLines(
            Entry("www.example.test", "GET", "/mapped"),
            Entry("www.example.test", "GET", "/unmapped"));
        var paths = ClientTestingTrafficAnalyzer.Analyze(_logPath, Options, catalog).UnmatchedProductionPaths;

        Assert.That(paths.Select(p => p.Path), Is.EquivalentTo(new[] { "/mapped", "/unmapped" }));
        Assert.That(paths.Select(p => p.Host), Has.None.StartsWith("__unmapped__"));
    }

    [Test]
    public void ProductionPaths_PreservePipeCharactersInsideThePath()
    {
        WriteLines(Entry("www.example.test", "GET", "/a|b|c"));
        var path = Analyze().UnmatchedProductionPaths.Single();
        Assert.That(path, Is.EqualTo(new TrafficPathSummary("www.example.test", "GET", "/a|b|c", 1)));
    }

    [Test]
    public void ProductionPaths_AreCappedAtFortyMostFrequentEntries()
    {
        var lines = new List<string>();
        for (var i = 0; i < 45; i++)
        {
            for (var repeat = 0; repeat <= i; repeat++)
            {
                lines.Add(Entry("www.example.test", "GET", $"/p{i}"));
            }
        }

        WriteLines([.. lines]);
        var paths = Analyze().UnmatchedProductionPaths;

        Assert.That(paths, Has.Count.EqualTo(40));
        Assert.That(paths[0], Is.EqualTo(new TrafficPathSummary("www.example.test", "GET", "/p44", 45)));
        Assert.That(paths[^1].Path, Is.EqualTo("/p5"));
        Assert.That(paths.Select(p => p.Count), Is.Ordered.Descending);
    }

    [Test]
    public void HostCounts_IncludeLocalAndProductionTrafficAndSumToTotal()
    {
        WriteLines(
            Entry("gateway.test", "GET", "/a"),
            Entry("localhost", "GET", "/b"),
            Entry("www.example.test", "GET", "/c"),
            Entry("cdn.example.test", "GET", "/d", isLocal: true));
        var analysis = Analyze();

        Assert.That(analysis.TotalRequests, Is.EqualTo(4));
        Assert.That(analysis.LocalGatewayHits, Is.EqualTo(3));
        Assert.That(analysis.ProductionHits, Is.EqualTo(1));
        Assert.That(analysis.ByHost.Sum(h => h.Count), Is.EqualTo(analysis.TotalRequests));
    }

    private TrafficAnalysis Analyze() => ClientTestingTrafficAnalyzer.Analyze(_logPath, Options, []);

    private void WriteLines(params string[] lines) => File.WriteAllLines(_logPath, lines);

    private static string Entry(string host, string method, string path, bool? isLocal = null, bool? redirected = null)
    {
        var entry = new Dictionary<string, object> { ["host"] = host, ["method"] = method, ["path"] = path };
        if (isLocal is { } local)
        {
            entry["is_local_gateway"] = local;
        }

        if (redirected is { } redirect)
        {
            entry["redirected"] = redirect;
        }

        return JsonSerializer.Serialize(entry);
    }

    private static ClientEndpointEntry CatalogEntry(string host, string method, string path) =>
        new(host, method, path, path, "Features/Example.cs", "Mock", false, "");
}
