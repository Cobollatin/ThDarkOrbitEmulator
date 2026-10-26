using System.Xml.Linq;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class ProjectReferenceArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void Solution_ContainsOnlyExpectedProjects()
    {
        var solutionPath = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.slnx");
        var solution = XDocument.Load(solutionPath);
        var projectPaths = solution
            .Descendants("Project")
            .Select(project => NormalizeProjectName(project.Attribute("Path")?.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(projectPaths, Is.EqualTo(new[]
        {
            "ThDarkOrbitEmulator.ApiService",
            "ThDarkOrbitEmulator.AppHost",
            "ThDarkOrbitEmulator.Cluster.Application",
            "ThDarkOrbitEmulator.Cluster.Infrastructure",
            "ThDarkOrbitEmulator.Cms",
            "ThDarkOrbitEmulator.Gateway",
            "ThDarkOrbitEmulator.Identity.Application",
            "ThDarkOrbitEmulator.Identity.Domain",
            "ThDarkOrbitEmulator.Identity.Infrastructure",
            "ThDarkOrbitEmulator.ServiceDefaults",
            "ThDarkOrbitEmulator.Tests"
        }.Order(StringComparer.Ordinal)));
    }

    [TestCase("ThDarkOrbitEmulator.Cms")]
    [TestCase("ThDarkOrbitEmulator.Identity.Domain")]
    [TestCase("ThDarkOrbitEmulator.Cluster.Application")]
    [TestCase("ThDarkOrbitEmulator.Identity.Application", "ThDarkOrbitEmulator.Identity.Domain")]
    [TestCase("ThDarkOrbitEmulator.Identity.Infrastructure", "ThDarkOrbitEmulator.Identity.Application", "ThDarkOrbitEmulator.Identity.Domain")]
    [TestCase("ThDarkOrbitEmulator.Cluster.Infrastructure", "ThDarkOrbitEmulator.Cluster.Application")]
    [TestCase(
        "ThDarkOrbitEmulator.ApiService",
        "ThDarkOrbitEmulator.Cluster.Application",
        "ThDarkOrbitEmulator.Cluster.Infrastructure",
        "ThDarkOrbitEmulator.Identity.Application",
        "ThDarkOrbitEmulator.Identity.Infrastructure",
        "ThDarkOrbitEmulator.ServiceDefaults")]
    [TestCase("ThDarkOrbitEmulator.Gateway", "ThDarkOrbitEmulator.ServiceDefaults")]
    [TestCase("ThDarkOrbitEmulator.AppHost", "ThDarkOrbitEmulator.ApiService", "ThDarkOrbitEmulator.Gateway")]
    public void ProjectReferences_MatchLayeringContract(string projectName, params string[] expectedReferences)
    {
        var references = ReadProjectReferences(projectName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(references, Is.EqualTo(expectedReferences.Order(StringComparer.Ordinal)));
    }

    private static IReadOnlyCollection<string> ReadProjectReferences(string projectName)
    {
        var projectPath = Path.Combine(RepositoryRoot, projectName, $"{projectName}.csproj");
        var project = XDocument.Load(projectPath);

        return project
            .Descendants("ProjectReference")
            .Select(reference => NormalizeProjectName(reference.Attribute("Include")?.Value))
            .ToArray();
    }

    [Test]
    public void AppHostClientTesting_TypesAreInternalAndConfinedToTheClientTestingFolder()
    {
        var types = typeof(ThDarkOrbitEmulator.AppHost.ClientTesting.ClientTestingOptions).Assembly.GetTypes()
            .Where(type => type.Namespace == "ThDarkOrbitEmulator.AppHost.ClientTesting" && !type.IsNested
                && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToArray();

        Assert.That(types.Select(type => type.Name), Does.Contain("ClientTestingTrafficAnalyzer").And.Contain("ClientTestingLocalGuard"));
        Assert.That(types.Where(type => type.IsPublic).Select(type => type.FullName), Is.Empty,
            "AppHost client-testing types are local tooling and must stay internal.");

        var folder = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.AppHost", "ClientTesting");
        var misplaced = Directory.GetFiles(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.AppHost"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("namespace ThDarkOrbitEmulator.AppHost.ClientTesting;", StringComparison.Ordinal))
            .Where(path => !path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.That(misplaced, Is.Empty, "ClientTesting namespace files must live under ThDarkOrbitEmulator.AppHost/ClientTesting/.");
    }

    [Test]
    public void EndpointCatalog_DiscoveredHostsHaveNoSourceCodePunctuation()
    {
        var features = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features");
        var entries = ThDarkOrbitEmulator.AppHost.ClientTesting.ClientEndpointCatalog.Discover(features);

        Assert.That(entries, Is.Not.Empty);
        Assert.That(entries.Where(entry => entry.OriginalHost.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            .Select(entry => $"{entry.OriginalHost} in {entry.SourceFile}"), Is.Empty);
    }

    [Test]
    public void EndpointCatalog_DiscoveryReadsFeatureSourcesWithoutWritingArtifacts()
    {
        var path = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.AppHost", "ClientTesting", "ClientEndpointCatalog.cs");
        var source = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("Directory.EnumerateFiles(featuresDirectory, \"*.cs\", SearchOption.AllDirectories)"));
            Assert.That(source, Does.Contain("File.ReadAllText(file)"));
            Assert.That(source, Does.Not.Contain("File.Write"));
            Assert.That(source, Does.Not.Contain("Directory.Create"));
            Assert.That(source, Does.Not.Contain("Process.Start"));
        });
    }

    [Test]
    public void AppHostClientTestingPaths_ResolveFromTheRepositoryRootWithoutParentSegments()
    {
        var appHost = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.AppHost", "AppHost.cs"));
        Assert.That(appHost, Does.Contain("var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, \"..\"));"),
            "ClientTestingPaths must receive the repository root, not the AppHost directory.");
        Assert.That(appHost, Does.Contain("new ClientTestingPaths(repoRoot, clientTestingOptions)"));

        var defaults = new ThDarkOrbitEmulator.AppHost.ClientTesting.ClientTestingOptions();
        var configured = new[] { "appsettings.json", "appsettings.Development.json" }
            .Select(name => Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.AppHost", name))
            .Where(File.Exists)
            .SelectMany(path => File.ReadAllLines(path))
            .Where(line => line.Contains("\"ApiServiceFeaturesPath\"", StringComparison.Ordinal)
                || line.Contains("\"OutputDirectory\"", StringComparison.Ordinal)
                || line.Contains("\"MappingPath\"", StringComparison.Ordinal));

        Assert.That(new[] { defaults.ApiServiceFeaturesPath, defaults.OutputDirectory, defaults.MappingPath }
                .Concat(configured)
                .Where(value => value.Contains("..", StringComparison.Ordinal)),
            Is.Empty, "Repo-relative client-testing paths must not climb above the repository root.");
    }

    [Test]
    public void ClientUriPatchCommands_RequireConfirmationForApplyAndReset()
    {
        var path = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.AppHost", "ClientTesting", "ClientTestingExtensions.cs");
        var source = File.ReadAllText(path);
        var registrations = source.Split('\n')
            .Where(line => line.Contains("builder.WithCommand(\"client-test-patch-uris-", StringComparison.Ordinal))
            .ToArray();

        Assert.That(registrations, Has.Length.EqualTo(3), "Status, apply and reset must each have one dashboard command.");
        foreach (var action in new[] { "apply", "reset" })
        {
            var registration = registrations.Single(line => line.Contains($"\"client-test-patch-uris-{action}\"", StringComparison.Ordinal));
            Assert.That(registration, Does.Contain("confirm: true"), $"{action} modifies local client files.");
        }

        var status = registrations.Single(line => line.Contains("\"client-test-patch-uris-status\"", StringComparison.Ordinal));
        Assert.That(status, Does.Not.Contain("confirm: true"), "Status is a read-only operation.");
    }

    private static string NormalizeProjectName(string? path)
    {
        Assert.That(path, Is.Not.Null.And.Not.Empty);
        return Path.GetFileNameWithoutExtension(path!.Replace('\\', Path.DirectorySeparatorChar));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
