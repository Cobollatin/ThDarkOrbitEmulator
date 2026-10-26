using System.Xml.Linq;
using NetArchTest.Rules;
using ThDarkOrbitEmulator.Identity.Domain;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class DomainLayerPurityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void IdentityDomain_HasZeroProjectAndPackageDependencies()
    {
        var csprojPath = Path.Combine(
            RepositoryRoot,
            "ThDarkOrbitEmulator.Identity.Domain",
            "ThDarkOrbitEmulator.Identity.Domain.csproj");
        var doc = XDocument.Load(csprojPath);

        Assert.Multiple(() =>
        {
            Assert.That(doc.Descendants("ProjectReference"), Is.Empty);
            Assert.That(doc.Descendants("PackageReference"), Is.Empty);
        });
    }

    [Test]
    public void IdentityDomain_DoesNotReferenceExternalFrameworksOrInfrastructure()
    {
        var result = Types
            .InAssembly(typeof(Account).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "FastEndpoints",
                "Npgsql",
                "StackExchange.Redis",
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.Cluster.Application",
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.ApiService")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void IdentityDomain_AccountEntityIsPublicAndResidesInDomainNamespace()
    {
        var domainTypes = Types
            .InAssembly(typeof(Account).Assembly)
            .That()
            .AreClasses()
            .GetTypes()
            .Where(t => !t.Name.StartsWith('<'))
            .ToArray();

        Assert.That(domainTypes, Is.Not.Empty);
        foreach (var type in domainTypes)
        {
            Assert.That(type.Namespace, Does.StartWith("ThDarkOrbitEmulator.Identity.Domain"));
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