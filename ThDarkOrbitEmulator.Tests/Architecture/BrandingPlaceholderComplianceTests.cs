using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.World;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class BrandingPlaceholderComplianceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void LocalSpacemapRoute_ServesGenericPlaceholderPage()
    {
        var endpoint = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "IndexInternal", "InternalStart", "IndexInternalStartEndpoint.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Does.Contain("WriteClientHtmlAsync(Features.World.LocalSpacemapPage.Html, ct)"));
            Assert.That(LocalSpacemapPage.Html, Does.Contain("<title>Spacemap - Space Emulator (Placeholder)</title>"));
            Assert.That(LocalSpacemapPage.Html, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(LocalSpacemapPage.Html, Does.Not.Contain("Bigpoint").IgnoreCase);
        });
    }

    [Test]
    public void HomePageFallback_UsesGenericPlaceholderWithoutExternalAssets()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.ApiService",
            "Features", "Mocks", "WwwDarkorbitCom", "HomeEndpoint.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private const string FallbackHtml ="));
            Assert.That(source, Does.Contain("? FallbackHtml"));
            Assert.That(source, Does.Contain("GetBody(HttpContext.Request, HomeHtml)"));
            Assert.That(source, Does.Not.Contain("<title>DarkOrbit</title>"));
        });
    }

    [Test]
    public void CmsIndexAndTranslations_UseGenericPlaceholderBranding()
    {
        var cmsRoot = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Cms", "src");
        var indexHtml = File.ReadAllText(Path.Combine(cmsRoot, "index.html"));
        var shellHtml = File.ReadAllText(Path.Combine(cmsRoot, "app", "core", "layout", "app-shell.component.html"));
        var enJson = File.ReadAllText(Path.Combine(cmsRoot, "assets", "i18n", "en.json"));
        var esJson = File.ReadAllText(Path.Combine(cmsRoot, "assets", "i18n", "es.json"));

        Assert.Multiple(() =>
        {
            Assert.That(indexHtml, Does.Contain("Space Emulator CMS (Placeholder)"));
            Assert.That(indexHtml, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(indexHtml, Does.Not.Contain("Bigpoint").IgnoreCase);

            Assert.That(shellHtml, Does.Contain(">LOGO<"));
            Assert.That(shellHtml, Does.Contain("PLACEHOLDER UI"));
            Assert.That(shellHtml, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(shellHtml, Does.Not.Contain("Bigpoint").IgnoreCase);

            Assert.That(enJson, Does.Contain("Space Emulator CMS"));
            Assert.That(enJson, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(enJson, Does.Not.Contain("Bigpoint").IgnoreCase);

            Assert.That(esJson, Does.Contain("CMS del Emulador Espacial"));
            Assert.That(esJson, Does.Not.Contain("DarkOrbit").IgnoreCase);
            Assert.That(esJson, Does.Not.Contain("Bigpoint").IgnoreCase);
        });
    }

    [Test]
    public void EmulatorLoginPageRewrite_ReplacesOfficialBrandingAndExternalFormAction()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);

        var rewritten = EmulatorRedirectHelper.RewriteLoginPageHtml(context.Request, File.ReadAllText(Path.Combine(RepositoryRoot, "_reversing", "Requests", "home_html_raw.log")));

        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Does.Contain("<title>Space Emulator Portal | Local Login (Placeholder)</title>"));
            Assert.That(rewritten, Does.Contain("SPACE EMULATOR"));
            Assert.That(rewritten, Does.Contain("Space Emulator Placeholder"));
            Assert.That(rewritten, Does.Contain("action=\"/Sas/Authentication/Bigpoint?"));
            Assert.That(rewritten, Does.Not.Contain("action=\"https://sas.bpsecure.com/Sas/Authentication/Bigpoint"));
        });
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
