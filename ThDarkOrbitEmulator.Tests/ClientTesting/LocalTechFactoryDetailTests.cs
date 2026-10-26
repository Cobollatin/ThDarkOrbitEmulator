using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalTechFactoryDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "local-session" };
    private static readonly IReadOnlyList<LocalTechRecipe> Recipes =
    [
        new("targeting", "Targeting boost", 100, 2),
        new("shield", "Shield boost", 150, 3),
        new("repair", "Repair charge", 200, 4)
    ];

    private static bool Valid(string suffix) => LocalTechFactoryAction.ValidPageQuery(
        new QueryCollection(QueryHelpers.ParseQuery("?action=internalTechFactory" + suffix)));

    [TestCase("&detail=targeting")]
    [TestCase("&detail=shield&lang=es")]
    [TestCase("&detail=unknown")]
    public void Query_AcceptsWellFormedDetail(string suffix) => Assert.That(Valid(suffix), Is.True);

    [TestCase("&detail=")]
    [TestCase("&Detail=shield")]
    [TestCase("&detail=Shield")]
    [TestCase("&detail=shield&detail=repair")]
    [TestCase("&detail=shield%00")]
    [TestCase("&recipe=shield")]
    public void Query_RejectsMalformedDetail(string suffix) => Assert.That(Valid(suffix), Is.False);

    [Test]
    public void Detail_RequiresEnabledCatalogRecipe()
    {
        Assert.That(LocalTechFactory.HasRecipe(Recipes, "shield"), Is.True);
        Assert.That(LocalTechFactory.HasRecipe(Recipes, "unknown"), Is.False);
        Assert.That(LocalTechFactory.HasRecipe(Recipes, "Shield"), Is.False);
    }

    [Test]
    public void Overview_LinksRecipesAndSummarizesAffordableChoices()
    {
        var page = LocalTechFactory.Page(Session, new(175, new(), null), Now, Recipes);
        Assert.That(page, Does.Contain("2 of 3 recipe(s) affordable"));
        Assert.That(page, Does.Contain("action=internalTechFactory&amp;detail=shield"));
        Assert.That(page, Does.Not.Contain("id=\"techFactoryDetail\""));
    }

    [Test]
    public void Detail_ShowsCostsOwnershipAndNavigation()
    {
        var page = LocalTechFactory.Page(Session, new(175, new() { ["shield"] = 4 }, null), Now, Recipes, "shield");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("data-recipe=\"shield\""));
            Assert.That(page, Does.Contain("Recipe 2 of 3"));
            Assert.That(page, Does.Contain("Cost: 150 components"));
            Assert.That(page, Does.Contain("Owned resource: 4"));
            Assert.That(page, Does.Contain("Components after start: 25"));
            Assert.That(page, Does.Contain("Ready to start."));
            Assert.That(page, Does.Contain("Produce Shield boost"));
            Assert.That(page, Does.Contain("detail=targeting"));
            Assert.That(page, Does.Contain("detail=repair"));
        });
    }

    [Test]
    public void Detail_ExplainsShortfallWithoutOfferingStart()
    {
        var page = LocalTechFactory.Page(Session, new(125, new(), null), Now, Recipes, "repair");
        Assert.That(page, Does.Contain("Need 75 more components to start."));
        Assert.That(page, Does.Not.Contain("Produce Repair charge"));
        Assert.That(page, Does.Not.Contain("id=\"techDetailNext\""));
    }

    [Test]
    public void Detail_ShowsCurrentJobState()
    {
        var job = new LocalTechJob("shield", Now.AddMinutes(3), Guid.NewGuid());
        var running = LocalTechFactory.Page(Session, new(500, new(), job), Now, Recipes, "shield");
        Assert.That(running, Does.Contain("This job is running; 3 min left."));
        Assert.That(running, Does.Not.Contain("Produce Shield boost"));
        var ready = LocalTechFactory.Page(Session, new(500, new(), job), Now.AddMinutes(3), Recipes, "shield");
        Assert.That(ready, Does.Contain("This job is ready to claim."));
        var other = LocalTechFactory.Page(Session, new(500, new(), job), Now, Recipes, "repair");
        Assert.That(other, Does.Contain("production slot is busy with another recipe"));
    }

    [Test]
    public void Detail_EncodesRecipeTextAndOmitsUnknownSelection()
    {
        var catalog = new[] { new LocalTechRecipe("tech_x", "<script>x</script>", 1, 1) };
        var page = LocalTechFactory.Page(Session, new(10, new(), null), Now, catalog, "tech_x");
        Assert.That(page, Does.Not.Contain("<script>"));
        Assert.That(LocalTechFactory.Page(Session, new(10, new(), null), Now, catalog, "missing"),
            Does.Not.Contain("id=\"techFactoryDetail\""));
    }
}
