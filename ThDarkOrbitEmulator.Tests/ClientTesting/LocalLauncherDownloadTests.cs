using ThDarkOrbitEmulator.ApiService.Features.IndexEs;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalLauncherDownloadTests
{
    [Test]
    public void DownloadActionProvidesNavigationForExistingClientWithoutInventingInstaller()
    {
        var page = LocalLauncherDownload.Page;
        Assert.That(page, Does.Contain("id=\"localLauncherDownload\""));
        Assert.That(page, Does.Contain("separately installed DarkOrbit Unity client"));
        Assert.That(page, Does.Contain("does not distribute an installer"));
        Assert.That(page, Does.Contain("action=internalWebGL&amp;from=internalStart"));
        Assert.That(page, Does.Contain("action=internalHelp"));
        Assert.That(page, Does.Contain("href=\"/\""));
        Assert.That(page, Does.Not.Contain(".exe\""));
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=externalDownload"));
    }
}
