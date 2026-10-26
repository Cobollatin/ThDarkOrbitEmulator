using System.Xml.Linq;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalCommandCenterTests
{
    [Test]
    public void Page_UsesCapturedNewsIdentifiersAndProvidesUsefulFilters()
    {
        var xmlNames = XDocument.Parse(ClientStubResponses.BreakingNews).Root!
            .Elements("layer").Select(layer => (string)layer.Attribute("name")!).ToArray();
        var catalogNames = CapturedNewsCatalog.MenuEntries.Select(entry => entry.Name).ToArray();
        Assert.That(catalogNames, Is.EquivalentTo(xmlNames));
        Assert.That(catalogNames.Select(name => "BE_" + name),
            Is.EquivalentTo(ClientStubResponses.News().Data.Select(item => item.NewsId)));

        var all = LocalCommandCenter.Page(null, null);
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("id=\"commandCenter\""));
            Assert.That(all, Does.Contain("19 notices"));
            Assert.That(all, Does.Contain("category=events"));
            Assert.That(all, Does.Contain("category=deals"));
            Assert.That(all, Does.Contain("category=news"));
            Assert.That(all, Does.Contain("category=sales"));
            Assert.That(all, Does.Contain("category=upselling"));
            Assert.That(all, Does.Contain("<img width=\"303\" height=\"168\""));
            Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("action=internalCommandCenter"));
        });
        foreach (var name in catalogNames)
            Assert.That(all, Does.Contain("item=" + name), name);

        var events = LocalCommandCenter.Page("events", null);
        Assert.That(events, Does.Contain("Voyagers2607"));
        Assert.That(events, Does.Not.Contain("50cent_deal</a>"));
        var detail = LocalCommandCenter.Page(null, "Voyagers2607");
        Assert.That(detail, Does.Contain("1 notices"));
        Assert.That(detail, Does.Contain("breaking_news_Voyagers__Ascent_202607_303x168.jpg"));
        Assert.That(detail, Does.Contain("src=\"/do_img/new/bg/breaking_news_Voyagers__Ascent_202607_303x168.jpg\""));
    }

    [Test]
    public void VisualAssets_CoverCapturedNewsAndManifestImages()
    {
        var manifest = XDocument.Parse(CapturedCommandCenterResources.Xml);
        var locations = manifest.Root!.Elements("location").ToDictionary(
            e => (string)e.Attribute("id")!, e => (string)e.Attribute("path")!);
        var imagePaths = manifest.Root.Elements("file")
            .Where(e => (string?)e.Attribute("type") is "jpg" or "png")
            .Select(e => "/" + locations[(string)e.Attribute("location")!]
                + (string)e.Attribute("name")! + "." + (string)e.Attribute("type")!)
            .ToArray();
        Assert.That(imagePaths.Length, Is.GreaterThan(500));
        foreach (var entry in CapturedNewsCatalog.MenuEntries)
            Assert.That(CommandCenterVisualAssets.GetImagePath(entry.Background), Is.AnyOf(imagePaths), entry.Name);

        var first = CommandCenterVisualAssets.GetArtwork(imagePaths[0]);
        var second = CommandCenterVisualAssets.GetArtwork(imagePaths[1]);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(16, 4)), Is.EqualTo(303));
            Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(20, 4)), Is.EqualTo(168));
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(CommandCenterVisualAssets.GetArtwork("/do_img/new/bg/not-in-manifest.jpg"), Is.Null);
            Assert.That(CommandCenterVisualAssets.GetArtwork("/resources/command-center/categories.xml"), Is.Null);
        });
    }

    [Test]
    public void UnknownFiltersAreRejectedBeforePageRendering()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalCommandCenter.IsCategory("payments"), Is.False);
            Assert.That(LocalCommandCenter.IsItem("unknown"), Is.False);
            Assert.That(LocalCommandCenter.IsCategory("events"), Is.True);
            Assert.That(LocalCommandCenter.IsItem("Voyagers2607"), Is.True);
            Assert.That(LocalCommandCenter.IsSelection("deals", "Voyagers2607"), Is.False);
            Assert.That(LocalCommandCenter.IsSelection("events", "Voyagers2607"), Is.True);
        });
    }
}
