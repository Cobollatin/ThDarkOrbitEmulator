using NetArchTest.Rules;
using ThDarkOrbitEmulator.ApiService.Features.Identity.Login;
using ThDarkOrbitEmulator.Cluster.Application;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class CleanArchitectureLayerTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void DomainLayerShouldNotDependOnApplicationOrInfrastructure()
    {
        var result = Types
            .InAssembly(typeof(Account).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.Cluster.Application",
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.ApiService.Features")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ApplicationLayerShouldNotDependOnInfrastructureOrFeatures()
    {
        var result = Types
            .InAssembly(typeof(IIdentityCommandService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.Cluster.Application",
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.ApiService.Features")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ApiLayerShouldNotUseControllers()
    {
        var result = Types
            .InAssembly(typeof(LoginEndpoint).Assembly)
            .ShouldNot()
            .Inherit(typeof(Microsoft.AspNetCore.Mvc.ControllerBase))
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ClusterApplicationLayerShouldNotDependOnInfrastructureOrIdentity()
    {
        var result = Types
            .InAssembly(typeof(IWorkerSpawner).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.Identity.Domain",
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.ApiService.Features")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void ClusterInfrastructureLayerShouldNotDependOnIdentityOrApiFeatures()
    {
        var result = Types
            .InAssembly(typeof(SshWorkerSpawner).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.Identity.Domain",
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
                "ThDarkOrbitEmulator.ApiService.Features")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void IdentityInfrastructureLayerCanDependOnApplicationAndDomainOnly()
    {
        var result = Types
            .InAssembly(typeof(IdentityCommandService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.Cluster.Application",
                "ThDarkOrbitEmulator.Cluster.Infrastructure",
                "ThDarkOrbitEmulator.ApiService.Features")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void IdentityAndClusterTypes_DoNotReferenceEachOther()
    {
        var identityAssemblies = new[]
        {
            typeof(Account).Assembly,
            typeof(IIdentityCommandService).Assembly,
            typeof(IdentityCommandService).Assembly
        };
        var clusterAssemblies = new[]
        {
            typeof(IWorkerSpawner).Assembly,
            typeof(SshWorkerSpawner).Assembly
        };

        foreach (var assembly in identityAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot()
                .HaveDependencyOnAny("ThDarkOrbitEmulator.Cluster.Application", "ThDarkOrbitEmulator.Cluster.Infrastructure")
                .GetResult();
            Assert.That(result.IsSuccessful, Is.True, $"{assembly.GetName().Name} references Cluster types.");
        }

        foreach (var assembly in clusterAssemblies)
        {
            var result = Types.InAssembly(assembly).ShouldNot()
                .HaveDependencyOnAny("ThDarkOrbitEmulator.Identity.Domain", "ThDarkOrbitEmulator.Identity.Application", "ThDarkOrbitEmulator.Identity.Infrastructure")
                .GetResult();
            Assert.That(result.IsSuccessful, Is.True, $"{assembly.GetName().Name} references Identity types.");
        }
    }

    [TestCase("ThDarkOrbitEmulator.Identity.Domain")]
    [TestCase("ThDarkOrbitEmulator.Identity.Application")]
    [TestCase("ThDarkOrbitEmulator.Identity.Infrastructure", "DependencyInjection.cs")]
    [TestCase("ThDarkOrbitEmulator.Cluster.Application")]
    [TestCase("ThDarkOrbitEmulator.Cluster.Infrastructure")]
    [TestCase("ThDarkOrbitEmulator.ApiService", "Program.cs")]
    [TestCase("ThDarkOrbitEmulator.AppHost", "AppHost.cs")]
    [TestCase("ThDarkOrbitEmulator.Tests")]
    public void ScreamingArchitecture_ProjectRootsDoNotContainLooseFeatureFiles(string projectName, params string[] allowedRootFiles)
    {
        var projectDir = Path.Combine(RepositoryRoot, projectName);
        var rootCsFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(rootCsFiles, Is.EqualTo(allowedRootFiles.Order(StringComparer.Ordinal)));
    }

    [Test]
    public void ScreamingArchitecture_ApiServiceFeaturesFolderHasNoLooseRootFiles()
    {
        var featuresDir = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService", "Features");
        var looseFiles = Directory.GetFiles(featuresDir, "*.cs", SearchOption.TopDirectoryOnly);

        Assert.That(looseFiles, Is.Empty, "All files under ThDarkOrbitEmulator.ApiService/Features must belong to a feature slice or Common subfolder.");
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



