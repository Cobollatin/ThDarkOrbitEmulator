using NetArchTest.Rules;
using ThDarkOrbitEmulator.Cluster.Application;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.ApiService.Features.Identity.Login;

namespace ThDarkOrbitEmulator.Tests;

public class ArchitectureTests
{
    [Test]
    public void DomainLayerShouldNotDependOnApplicationOrInfrastructure()
    {
        var result = Types
            .InAssembly(typeof(Account).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure",
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
    public void ClusterApplicationLayerShouldNotDependOnInfrastructure()
    {
        var result = Types
            .InAssembly(typeof(IWorkerSpawner).Assembly)
            .ShouldNot()
            .HaveDependencyOn("ThDarkOrbitEmulator.Cluster.Infrastructure")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void IdentityInfrastructureLayerCanDependOnApplicationAndDomainOnly()
    {
        var result = Types
            .InAssembly(typeof(IdentityCommandService).Assembly)
            .ShouldNot()
            .HaveDependencyOn("ThDarkOrbitEmulator.ApiService.Features")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }
}
