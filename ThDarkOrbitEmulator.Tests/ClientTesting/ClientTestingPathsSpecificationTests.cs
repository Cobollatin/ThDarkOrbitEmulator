using ThDarkOrbitEmulator.AppHost.ClientTesting;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// SPEC-02: path resolution for the AppHost client-testing tooling. Paths are computed only; no file is touched.
public class ClientTestingPathsSpecificationTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SyntheticRoot = Path.Combine(Path.GetTempPath(), "spec02-paths-" + Guid.NewGuid().ToString("N"));

    [Test]
    public void DefaultFeaturesPath_ResolvesInsideTheRepositoryRootAndIsDiscoverable()
    {
        var paths = new ClientTestingPaths(RepoRoot, new ClientTestingOptions());

        Assert.Multiple(() =>
        {
            Assert.That(paths.FeaturesDirectory, Is.EqualTo(Path.Combine(RepoRoot, "ThDarkOrbitEmulator.ApiService", "Features")));
            Assert.That(Directory.Exists(paths.FeaturesDirectory), Is.True);
            Assert.That(ClientEndpointCatalog.Discover(paths.FeaturesDirectory), Is.Not.Empty);
        });
    }

    [Test]
    public void DefaultOptions_PlaceGeneratedArtifactsUnderTheOutputDirectory()
    {
        var paths = new ClientTestingPaths(SyntheticRoot, new ClientTestingOptions());
        var output = Path.Combine(SyntheticRoot, "client-testing");

        Assert.Multiple(() =>
        {
            Assert.That(paths.RepoRoot, Is.EqualTo(SyntheticRoot));
            Assert.That(paths.OutputDirectory, Is.EqualTo(output));
            Assert.That(paths.MappingPath, Is.EqualTo(Path.Combine(SyntheticRoot, "client-testing/url-replacements.json")));
            Assert.That(paths.TrafficLogPath, Is.EqualTo(Path.Combine(output, "client-traffic.jsonl")));
            Assert.That(paths.PidLogPath, Is.EqualTo(Path.Combine(output, "darkorbit_pids.log")));
            Assert.That(paths.MitmPidFile, Is.EqualTo(Path.Combine(output, "mitmproxy.pid")));
        });
    }

    [Test]
    public void ScriptPaths_AreFixedRelativeToTheRepositoryRootRegardlessOfOptions()
    {
        var options = new ClientTestingOptions
        {
            OutputDirectory = Path.Combine(SyntheticRoot, "elsewhere"),
            MappingPath = Path.Combine(SyntheticRoot, "elsewhere", "map.json"),
            ApiServiceFeaturesPath = Path.Combine(SyntheticRoot, "features"),
        };
        var paths = new ClientTestingPaths(SyntheticRoot, options);
        var scripts = Path.Combine(SyntheticRoot, "scripts");
        var clientTesting = Path.Combine(scripts, "client-testing");

        Assert.Multiple(() =>
        {
            Assert.That(paths.ScriptsDirectory, Is.EqualTo(scripts));
            Assert.That(paths.ClientTestingScriptsDirectory, Is.EqualTo(clientTesting));
            Assert.That(paths.FindUrlsScript, Is.EqualTo(Path.Combine(scripts, "Find-HardcodedUrls.ps1")));
            Assert.That(paths.ReplaceUrlsScript, Is.EqualTo(Path.Combine(scripts, "Replace-HardcodedUrls.ps1")));
            Assert.That(paths.PatchClientUrisScript, Is.EqualTo(Path.Combine(clientTesting, "Patch-ClientUris.ps1")));
            Assert.That(paths.MappingTemplatePath, Is.EqualTo(Path.Combine(clientTesting, "url-replacements.template.json")));
            Assert.That(paths.LaunchRedirectScript, Is.EqualTo(Path.Combine(SyntheticRoot, "_reversing", "Mitmproxy", "launch_redirect.py")));
        });
    }

    [Test]
    public void RepositoryScriptPaths_ExistInTheRepository()
    {
        var paths = new ClientTestingPaths(RepoRoot, new ClientTestingOptions());

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(paths.PatchClientUrisScript), Is.True, paths.PatchClientUrisScript);
            Assert.That(File.Exists(paths.MappingTemplatePath), Is.True, paths.MappingTemplatePath);
        });
    }

    [Test]
    public void RootedOutputAndMappingPaths_AreUsedVerbatim()
    {
        var output = Path.Combine(SyntheticRoot, "abs-output");
        var mapping = Path.Combine(SyntheticRoot, "abs-mapping", "map.json");
        var paths = new ClientTestingPaths(Path.Combine(SyntheticRoot, "repo"),
            new ClientTestingOptions { OutputDirectory = output, MappingPath = mapping });

        Assert.Multiple(() =>
        {
            Assert.That(paths.OutputDirectory, Is.EqualTo(output));
            Assert.That(paths.MappingPath, Is.EqualTo(mapping));
            Assert.That(paths.TrafficLogPath, Is.EqualTo(Path.Combine(output, "client-traffic.jsonl")));
        });
    }

    [Test]
    public void FeaturesDirectory_IsNormalizedButOutputDirectoryIsNot()
    {
        var paths = new ClientTestingPaths(SyntheticRoot, new ClientTestingOptions
        {
            OutputDirectory = "nested/../out",
            ApiServiceFeaturesPath = "src/../Features",
        });

        Assert.Multiple(() =>
        {
            Assert.That(paths.FeaturesDirectory, Is.EqualTo(Path.GetFullPath(Path.Combine(SyntheticRoot, "Features"))));
            Assert.That(paths.OutputDirectory, Is.EqualTo(Path.Combine(SyntheticRoot, "nested/../out")));
        });
    }

    [TestCase("../ThDarkOrbitEmulator.ApiService/Features")]
    [TestCase("..\\ThDarkOrbitEmulator.ApiService\\Features")]
    public void ParentRelativeFeaturesPath_EscapesTheRepositoryRoot(string featuresPath)
    {
        var paths = new ClientTestingPaths(RepoRoot, new ClientTestingOptions { ApiServiceFeaturesPath = featuresPath });

        Assert.That(paths.FeaturesDirectory, Does.Not.StartWith(RepoRoot + Path.DirectorySeparatorChar));
    }

    [Test]
    public void Construction_DoesNotTouchTheFileSystemAndKeepsTheOptionsInstance()
    {
        var options = new ClientTestingOptions();
        var paths = new ClientTestingPaths(SyntheticRoot, options);

        Assert.Multiple(() =>
        {
            Assert.That(paths.Options, Is.SameAs(options));
            Assert.That(Directory.Exists(SyntheticRoot), Is.False);
            Assert.That(Directory.Exists(paths.OutputDirectory), Is.False);
        });
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
