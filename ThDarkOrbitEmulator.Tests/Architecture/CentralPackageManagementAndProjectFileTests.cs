using System.Xml.Linq;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class CentralPackageManagementAndProjectFileTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void DirectoryPackagesProps_EnablesCentralPackageManagementAndTransitivePinning()
    {
        var propsPath = Path.Combine(RepositoryRoot, "Directory.Packages.props");
        var doc = XDocument.Load(propsPath);

        var cpm = doc.Descendants("ManagePackageVersionsCentrally").SingleOrDefault()?.Value;
        var transitive = doc.Descendants("CentralPackageTransitivePinningEnabled").SingleOrDefault()?.Value;
        var versions = doc.Descendants("PackageVersion").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(cpm, Is.EqualTo("true"));
            Assert.That(transitive, Is.EqualTo("true"));
            Assert.That(versions, Is.Not.Empty);
            Assert.That(versions.Select(v => v.Attribute("Include")?.Value), Is.Unique);
            Assert.That(versions.All(v => !string.IsNullOrWhiteSpace(v.Attribute("Version")?.Value)), Is.True);
            Assert.That(versions.Select(v => v.Attribute("Include")?.Value)
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key),
                Is.Empty, "PackageVersion entries must be unique regardless of casing.");
        });
    }

    [Test]
    public void AllCsprojFolderIncludes_PointToExistingDirectories()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.slnx"));
        var projectFiles = solution.Descendants("Project")
            .Select(project => Path.GetFullPath(Path.Combine(RepositoryRoot,
                project.Attribute("Path")!.Value.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar))));

        foreach (var projectFile in projectFiles)
        {
            var project = XDocument.Load(projectFile);
            foreach (var folder in project.Descendants("Folder"))
            {
                var include = folder.Attribute("Include")?.Value;
                Assert.That(include, Is.Not.Null.And.Not.Empty, $"{projectFile} has a Folder without Include.");
                var folderPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectFile)!,
                    include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                Assert.That(Directory.Exists(folderPath), Is.True,
                    $"{projectFile} includes missing folder {include}.");
            }
        }
    }

    [Test]
    public void AllCsprojFiles_DoNotInlinePackageReferenceVersions()
    {
        var slnx = XDocument.Load(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.slnx"));
        var csprojFiles = slnx.Descendants("Project")
            .Select(p => Path.Combine(RepositoryRoot, p.Attribute("Path")!.Value.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();

        Assert.That(csprojFiles, Has.Length.EqualTo(11));

        foreach (var csproj in csprojFiles)
        {
            var doc = XDocument.Load(csproj);
            var inlineVersions = doc.Descendants("PackageReference")
                .Where(r => r.Attribute("Version") is not null || r.Element("Version") is not null)
                .Select(r => r.Attribute("Include")?.Value ?? "(unknown)")
                .ToArray();

            Assert.That(
                inlineVersions,
                Is.Empty,
                $"{Path.GetFileName(csproj)} must not define inline Version on PackageReference; manage versions in Directory.Packages.props.");
        }
    }

    [Test]
    public void CmsProject_IsIncludedInSolutionAndHasNoOpCompileTarget()
    {
        var cmsCsprojPath = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Cms", "ThDarkOrbitEmulator.Cms.csproj");
        Assert.That(File.Exists(cmsCsprojPath), Is.True);

        var doc = XDocument.Load(cmsCsprojPath);
        var coreCompileTarget = doc.Descendants("Target")
            .FirstOrDefault(t => (string?)t.Attribute("Name") == "CoreCompile");
        Assert.That(coreCompileTarget, Is.Not.Null);
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
