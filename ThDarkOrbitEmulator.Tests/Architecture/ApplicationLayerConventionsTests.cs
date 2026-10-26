using NetArchTest.Rules;
using ThDarkOrbitEmulator.Cluster.Application;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class ApplicationLayerConventionsTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void IdentityApplication_DoesNotDependOnInfrastructureOrWebFrameworks()
    {
        var result = Types
            .InAssembly(typeof(IIdentityCommandService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "FastEndpoints",
                "Npgsql",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.Cluster.Application",
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.ApiService")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ClusterApplication_DoesNotDependOnInfrastructureOrIdentity()
    {
        var result = Types
            .InAssembly(typeof(IWorkerSpawner).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "FastEndpoints",
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.Identity.Domain",
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.ApiService")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ApplicationInterfaces_StartWithPrefixI()
    {
        var identityResult = Types
            .InAssembly(typeof(IIdentityCommandService).Assembly)
            .That()
            .AreInterfaces()
            .Should()
            .HaveNameStartingWith("I")
            .GetResult();

        var clusterResult = Types
            .InAssembly(typeof(IWorkerSpawner).Assembly)
            .That()
            .AreInterfaces()
            .Should()
            .HaveNameStartingWith("I")
            .GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(identityResult.IsSuccessful, Is.True);
            Assert.That(clusterResult.IsSuccessful, Is.True);
        });
    }

    [Test]
    public void IdentityApplication_IsOrganizedIntoScreamingFeatureFolders()
    {
        var appDir = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Identity.Application");
        var folders = Directory.GetDirectories(appDir)
            .Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(folders, Is.EqualTo(new[] { "Accounts", "Backoffice", "Companies", "Keycloak" }));
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