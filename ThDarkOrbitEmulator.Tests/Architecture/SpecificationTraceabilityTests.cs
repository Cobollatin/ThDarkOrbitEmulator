using System.Text.RegularExpressions;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class SpecificationTraceabilityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly string[] ExpectedSpecs =
    [
        "README.md",
        "01-authentication-and-portal.md",
        "02-client-uri-patching-and-connectivity.md",
        "03-identity-companies-and-onboarding.md",
        "04-out-of-game-menus-1-to-15.md",
        "05-out-of-game-menus-16-to-29-and-economy.md",
        "06-world-server-and-spacemap.md",
        "07-scalable-server-mesh-and-aspire.md",
        "08-admin-commands-and-runtime-variables.md",
        "09-cms-backoffice-and-placeholder-ui.md"
    ];

    [Test]
    public void DocsSpecs_ContainsAllCanonicalSpecificationFilesAndMasterMatrix()
    {
        var specsDir = Path.Combine(RepositoryRoot, "docs", "specs");
        Assert.That(Directory.Exists(specsDir), Is.True, "docs/specs directory must exist.");

        var readme = File.ReadAllText(Path.Combine(specsDir, "README.md"));
        foreach (var specFile in ExpectedSpecs)
        {
            var fullPath = Path.Combine(specsDir, specFile);
            Assert.That(File.Exists(fullPath), Is.True, $"Missing specification file: {specFile}");
            var content = File.ReadAllText(fullPath);
            Assert.That(content.Trim(), Is.Not.Empty, $"Specification file {specFile} must not be empty.");

            if (specFile != "README.md")
            {
                Assert.That(readme, Does.Contain(specFile), $"docs/specs/README.md traceability matrix must reference {specFile}.");
            }
        }
    }

    [Test]
    public void DocsSpecs_EverySpecMapsToExistingTestClasses()
    {
        var readme = File.ReadAllLines(Path.Combine(RepositoryRoot, "docs", "specs", "README.md"));
        var testClassNames = Directory.GetFiles(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\bclass\s+(\w+Tests)\b")
                .Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        for (var number = 1; number <= 9; number++)
        {
            var specId = $"SPEC-{number:D2}";
            var rows = readme.Where(line => line.StartsWith($"| `{specId}` |", StringComparison.Ordinal)).ToArray();
            Assert.That(rows, Has.Length.EqualTo(1), $"README must contain one matrix row for {specId}.");
            var columns = rows[0].Split('|', StringSplitOptions.TrimEntries);
            Assert.That(columns.Length, Is.GreaterThanOrEqualTo(6), $"{specId} needs architecture and feature test columns.");
            foreach (var column in columns.Skip(4).Take(2))
            {
                var mappedClasses = Regex.Matches(column, @"`(\w+Tests)`|\b(\w+Tests)\b")
                    .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                    .ToArray();
                Assert.That(mappedClasses, Is.Not.Empty, $"{specId} must map to test classes in each test column.");
                Assert.That(mappedClasses.Where(name => !testClassNames.Contains(name)), Is.Empty,
                    $"{specId} maps to a missing test class.");
            }
        }
    }

    [Test]
    public void MasterMatrix_AllReferencedFilesAndSourceDirectoriesExistAndFixturesContainTests()
    {
        var readme = File.ReadAllLines(Path.Combine(RepositoryRoot, "docs", "specs", "README.md"));
        var rows = readme.Where(line => Regex.IsMatch(line, @"^\| `SPEC-\d{2}` \|"));
        foreach (var row in rows)
        {
            var columns = row.Split('|', StringSplitOptions.TrimEntries);
            Assert.That(columns, Has.Length.GreaterThanOrEqualTo(7), $"Malformed matrix row: {row}");
            foreach (var columnIndex in new[] { 2, 3, 4, 5 })
            {
                var paths = Regex.Matches(columns[columnIndex], @"`([^`]+)`" )
                    .Select(match => match.Groups[1].Value).ToArray();
                Assert.That(paths, Is.Not.Empty, $"Missing path in column {columnIndex}: {row}");
                foreach (var path in paths)
                {
                    Assert.That(path, Does.Not.Contain(".."), $"Path must stay in repository: {path}");
                    var fullPath = Path.Combine(RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    if (columnIndex == 3)
                    {
                        Assert.That(Directory.Exists(fullPath), Is.True, $"Missing source directory: {path}");
                    }
                    else
                    {
                        Assert.That(File.Exists(fullPath), Is.True, $"Missing referenced file: {path}");
                        if (columnIndex >= 4)
                        {
                            Assert.That(Regex.IsMatch(File.ReadAllText(fullPath), @"\[(?:Test|TestCase)(?:\s*\(|\s*\])"),
                                Is.True, $"Fixture has no [Test] or [TestCase] method: {path}");
                        }
                    }
                }
            }
        }
    }

    [Test]
    public void FixtureInventories_ListEveryArchitectureAndSpecificationFixtureWithCurrentCounts()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specs", "README.md"));
        var matrix = readme.Split("## Specification fixture inventory", StringSplitOptions.None)[0];
        var testsRoot = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Tests");
        var fixtures = Directory.GetFiles(Path.Combine(testsRoot, "Architecture"), "*.cs")
            .Concat(Directory.GetFiles(testsRoot, "*SpecificationTests.cs", SearchOption.AllDirectories))
            .ToArray();
        foreach (var fixture in fixtures)
        {
            var relativePath = Path.GetRelativePath(RepositoryRoot, fixture).Replace('\\', '/');
            Assert.That(matrix, Does.Contain($"`{relativePath}`"), $"Master matrix must map {relativePath}.");
            var rows = readme.Split('\n').Where(line => line.StartsWith($"| `{relativePath}` |", StringComparison.Ordinal)).ToArray();
            Assert.That(rows, Has.Length.EqualTo(1), $"Fixture inventory must list {relativePath} exactly once.");
            var declaredCount = Regex.Matches(File.ReadAllText(fixture), @"^\s*\[(?:Test|TestCase)(?:\s*\(|\s*\])", RegexOptions.Multiline).Count;
            var columns = rows[0].Split('|', StringSplitOptions.TrimEntries);
            Assert.That(int.TryParse(columns[2], out var documentedCount), Is.True, $"Invalid count for {relativePath}");
            Assert.That(documentedCount, Is.EqualTo(declaredCount), $"Stale declared test count for {relativePath}");
            Assert.That(declaredCount, Is.GreaterThan(0), $"Fixture must declare tests: {relativePath}");
        }
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
