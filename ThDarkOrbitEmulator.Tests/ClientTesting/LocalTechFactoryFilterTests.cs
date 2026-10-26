using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalTechFactoryFilterTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "local-session" };
    private static readonly IReadOnlyList<LocalTechRecipe> Recipes =
    [
        new("targeting", "Targeting boost", 100, 2),
        new("shield", "Shield boost", 150, 3),
        new("repair", "Repair charge", 200, 4)
    ];

    private static bool Valid(string query) => LocalTechFactoryAction.ValidPageQuery(
        new QueryCollection(QueryHelpers.ParseQuery(query)));

    [TestCase("?action=internalTechFactory", true)]
    [TestCase("?action=internalTechFactory&filter=all", true)]
    [TestCase("?action=internalTechFactory&filter=affordable&detail=shield", true)]
    [TestCase("?action=internalTechFactory&filter=shortfall", true)]
    [TestCase("?action=internalTechFactory&filter=", false)]
    [TestCase("?action=internalTechFactory&filter=Affordable", false)]
    [TestCase("?action=internalTechFactory&filter=unknown", false)]
    [TestCase("?action=internalTechFactory&filter=all&filter=shortfall", false)]
    [TestCase("?action=internalTechFactory&Filter=all", false)]
    [TestCase("?ACTION=internalTechFactory", false)]
    [TestCase("?action=internaltechfactory", false)]
    [TestCase("?action=internalTechFactory&Lang=es", false)]
    public void Query_UsesCanonicalKeysAndFilter(string query, bool valid) =>
        Assert.That(Valid(query), Is.EqualTo(valid));

    [Test]
    public void Filters_UseComponentBalanceAndKeepCatalogOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalTechFactory.VisibleRecipes(Recipes, 150, "affordable").Select(r => r.Id),
                Is.EqualTo(new[] { "targeting", "shield" }));
            Assert.That(LocalTechFactory.VisibleRecipes(Recipes, 150, "shortfall").Select(r => r.Id),
                Is.EqualTo(new[] { "repair" }));
            Assert.That(LocalTechFactory.VisibleRecipes(Recipes, 150, "all"), Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void FilteredPage_ShowsCountsRowsAndNavigationWithinFilter()
    {
        var page = LocalTechFactory.Page(Session, new(150, new(), null), DateTimeOffset.UtcNow,
            Recipes, "shield", "affordable");
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Affordable (2)</a>"));
            Assert.That(page, Does.Contain("Shortfall (1)</a>"));
            Assert.That(page, Does.Contain("filter=affordable\" aria-current=\"page\""));
            Assert.That(page, Does.Contain("Recipe 2 of 2"));
            Assert.That(page, Does.Contain("detail=targeting&amp;filter=affordable"));
            Assert.That(page, Does.Not.Contain("data-recipe=\"repair\""));
            Assert.That(page, Does.Not.Contain("id=\"techDetailNext\""));
            Assert.That(page, Does.Contain("id=\"techDetailClose\" href=\"/indexInternal.es?action=internalTechFactory&amp;filter=affordable\""));
        });
    }

    [Test]
    public void EmptyFilter_ShowsNoticeAndNoRecipeRows()
    {
        var page = LocalTechFactory.Page(Session, new(500, new(), null), DateTimeOffset.UtcNow,
            Recipes, filter: "shortfall");
        Assert.That(page, Does.Contain("No recipes match this filter."));
        Assert.That(page, Does.Not.Contain("<tr data-recipe="));
    }
}
