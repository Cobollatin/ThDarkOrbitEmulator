using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-122: a successful craft redirects to `crafted={recipe}`; the page confirms it from saved progress only.
public class LocalAssemblyCraftedNoticeTests
{
    private static readonly GameAuthSession Session = new() { Username = "pilot", Dosid = "crafted-dosid" };

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private static readonly EconomyRecipe[] Recipes =
    [
        new("lf3", "LF-3 laser", Json("[{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":150},{\"kind\":\"currency\",\"id\":\"credits\",\"quantity\":500}]"),
            Json("[{\"kind\":\"item\",\"id\":\"laser_lf3\",\"quantity\":1}]"), 0, true),
        new("evil", "<b>Evil</b>", Json("[{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":10}]"),
            Json("[{\"kind\":\"item\",\"id\":\"<x>\",\"quantity\":2}]"), 0, true),
        new("old", "Retired", Json("[{\"kind\":\"resource\",\"id\":\"components\",\"quantity\":1}]"),
            Json("[{\"kind\":\"item\",\"id\":\"x\",\"quantity\":1}]"), 0, false),
        new("free", "Free sample", Json("[]"), Json("[{\"kind\":\"item\",\"id\":\"y\",\"quantity\":3}]"), 0, true)
    ];

    private static readonly EconomyBalance[] Balances =
        [new("components", "resource", 350), new("credits", "currency", 1200)];

    private static string Page(string? crafted, long count, string? detail = null, string status = "all") =>
        LocalAssembly.PageFromDatabase(Session, Recipes, Balances, new Dictionary<string, long>(), status, detail, crafted, count);

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    [TestCase("lf3")]
    [TestCase("sg3n-b02_x")]
    public void CraftedLocation_IsAcceptedPageQuery(string recipe)
    {
        var location = LocalAssemblyAction.CraftedLocation(recipe);
        Assert.Multiple(() =>
        {
            Assert.That(location, Is.EqualTo($"/indexInternal.es?action=internalAssembly&crafted={recipe}"));
            Assert.That(LocalAssemblyAction.ValidPageQuery(Query(location[location.IndexOf('?')..])), Is.True);
        });
    }

    [TestCase("?action=internalAssembly&crafted=lf3&detail=lf3")]
    [TestCase("?action=internalAssembly&crafted=lf3&status=ready&from=internalStart")]
    [TestCase("?action=internalAssembly&crafted=unknown")]
    public void PageQuery_AcceptsCrafted(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.True);

    [TestCase("?action=internalAssembly&crafted=")]
    [TestCase("?action=internalAssembly&crafted=lf3&crafted=lf3")]
    [TestCase("?action=internalAssembly&CRAFTED=lf3")]
    [TestCase("?action=internalAssembly&Crafted=lf3")]
    [TestCase("?action=internalAssembly&crafted=LF3")]
    [TestCase("?action=internalAssembly&crafted=lf3%20")]
    [TestCase("?action=internalAssembly&crafted=%3Cscript%3E")]
    [TestCase("?action=internalAssembly&crafted=a%0Ab")]
    [TestCase("?action=internalAssembly&crafted=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PageQuery_RejectsMalformedCrafted(string query) =>
        Assert.That(LocalAssemblyAction.ValidPageQuery(Query(query)), Is.False);

    [Test]
    public void Notice_ShowsRecipeOutputsTotalAndRemainingCrafts()
    {
        var page = Page("lf3", 1234);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<p id=\"assembly-crafted\" role=\"status\" data-recipe=\"lf3\">"));
            Assert.That(page, Does.Contain("Crafted LF-3 laser. Received: 1 laser_lf3. Total crafted: 1,234. Can craft again: 2.</p>"));
            Assert.That(page.IndexOf("assembly-crafted", StringComparison.Ordinal),
                Is.LessThan(page.IndexOf("assembly-notice", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void Notice_UnlimitedWhenRecipeHasNoInputs() =>
        Assert.That(Page("free", 1), Does.Contain("Crafted Free sample. Received: 3 y. Total crafted: 1. Can craft again: Unlimited."));

    [Test]
    public void Notice_DisabledRecipeStillConfirmsWithZeroRemaining() =>
        Assert.That(Page("old", 2), Does.Contain("Crafted Retired. Received: 1 x. Total crafted: 2. Can craft again: 0."));

    [Test]
    public void Notice_EncodesNameAndOutputs()
    {
        var page = Page("evil", 1);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Crafted &lt;b&gt;Evil&lt;/b&gt;. Received: 2 &lt;x&gt;."));
            Assert.That(page, Does.Not.Contain("<b>Evil</b>"));
            Assert.That(page, Does.Not.Contain("<x>"));
        });
    }

    [TestCase(null, 5)]
    [TestCase("lf3", 0)]
    [TestCase("lf3", -1)]
    [TestCase("unknown", 3)]
    [TestCase("LF3", 3)]
    public void Notice_HiddenWithoutSavedCraftOrKnownRecipe(string? crafted, long count) =>
        Assert.That(Page(crafted, count), Does.Not.Contain("assembly-crafted"));

    [Test]
    public void Notice_WorksWithDetailAndStatusFilter()
    {
        var detail = Page("lf3", 1, detail: "free");
        var filtered = Page("lf3", 1, status: "unavailable");
        Assert.Multiple(() =>
        {
            Assert.That(detail, Does.Contain("Crafted LF-3 laser."));
            Assert.That(detail, Does.Contain("data-recipe=\"free\"><h2>Free sample</h2>"));
            Assert.That(filtered, Does.Contain("Crafted LF-3 laser."));
            Assert.That(filtered, Does.Not.Contain("<tr data-recipe=\"lf3\""));
        });
    }

    [Test]
    public void Notice_AbsentByDefault() =>
        Assert.That(LocalAssembly.PageFromDatabase(Session, Recipes, Balances, new Dictionary<string, long>()),
            Does.Not.Contain("assembly-crafted"));

    [TestCase(EconomyCraftResult.NotFound, 404)]
    [TestCase(EconomyCraftResult.Duplicate, 409)]
    [TestCase(EconomyCraftResult.InsufficientInputs, 409)]
    [TestCase(EconomyCraftResult.Unavailable, 409)]
    [TestCase(EconomyCraftResult.Invalid, 400)]
    public async Task FailedCraft_DoesNotRedirect(EconomyCraftResult result, int status)
    {
        var (context, _) = await CraftAsync(result);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task AppliedCraft_RedirectsToCraftedRecipe()
    {
        var (context, recipes) = await CraftAsync(EconomyCraftResult.Applied, "free");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalAssembly&crafted=free"));
            Assert.That(recipes, Is.EqualTo(new[] { "free" }));
        });
    }

    private static async Task<(DefaultHttpContext Context, List<string> Recipes)> CraftAsync(EconomyCraftResult result, string recipe = "lf3")
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider().GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalAssemblyCraft");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={session.Dosid}";
        var bytes = System.Text.Encoding.UTF8.GetBytes($"recipe={recipe}&key={LocalAssemblyAction.CreateFormKey(session.Dosid, recipe)}");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        var crafted = new List<string>();
        await LocalAssemblyAction.HandleAsync(context, sessions, (_, id, _, _) =>
        {
            crafted.Add(id);
            return Task.FromResult(result);
        }, TimeProvider.System, CancellationToken.None);
        return (context, crafted);
    }
}
