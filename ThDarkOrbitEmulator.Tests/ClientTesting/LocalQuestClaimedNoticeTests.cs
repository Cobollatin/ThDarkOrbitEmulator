using System.Text;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Menu-119: a successful quest claim redirects to internalQuests&claimed={quest}, and the page
// shows a confirmation only for a quest the pilot has actually claimed.
[TestFixture]
public sealed class LocalQuestClaimedNoticeTests
{
    [Test]
    public async Task Claim_RedirectsWithClaimedQuest_AndPageConfirmsReward()
    {
        var (sessions, store, session) = await HarnessAsync(selectCompany: true);
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 3, CancellationToken.None), Is.True);

        var claim = ClaimRequest(session.Dosid, "field_supplier");
        await LocalQuestAction.HandleAsync(claim, sessions, store, CancellationToken.None);

        Assert.That(claim.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
        var location = claim.Response.Headers.Location.ToString();
        Assert.That(location, Is.EqualTo("/indexInternal.es?action=internalQuests&claimed=field_supplier"));

        var query = new QueryCollection(QueryHelpers.ParseQuery(new Uri("http://local" + location).Query));
        Assert.That(LocalQuestAction.ValidPageQuery(query), Is.True);
        var html = LocalQuests.Page(session, await store.GetAsync(session.AccountId, CancellationToken.None),
            claimed: query["claimed"].ToString());
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("id=\"questClaimed\""));
            Assert.That(html, Does.Contain("data-quest=\"field_supplier\""));
            Assert.That(html, Does.Contain("Reward claimed: Field supplier · 500 local credits added to your account."));
            Assert.That(html.IndexOf("questClaimed", StringComparison.Ordinal),
                Is.LessThan(html.IndexOf("quest-notice", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task FailedClaim_DoesNotRedirect()
    {
        var (sessions, store, session) = await HarnessAsync(selectCompany: true);
        var incomplete = ClaimRequest(session.Dosid, "first_purchase");
        await LocalQuestAction.HandleAsync(incomplete, sessions, store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(incomplete.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(incomplete.Response.Headers.Location.ToString(), Is.Empty);
        });

        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        var first = ClaimRequest(session.Dosid, "first_purchase");
        await LocalQuestAction.HandleAsync(first, sessions, store, CancellationToken.None);
        var replay = ClaimRequest(session.Dosid, "first_purchase");
        await LocalQuestAction.HandleAsync(replay, sessions, store, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(first.Response.StatusCode, Is.EqualTo(StatusCodes.Status303SeeOther));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(replay.Response.Headers.Location.ToString(), Is.Empty);
        });
    }

    [Test]
    public void ClaimedRedirect_EscapesQuestId()
    {
        Assert.That(LocalQuestAction.ClaimedRedirect("first_craft"),
            Is.EqualTo("/indexInternal.es?action=internalQuests&claimed=first_craft"));
        Assert.That(LocalQuestAction.ClaimedRedirect("a b&c"),
            Is.EqualTo("/indexInternal.es?action=internalQuests&claimed=a%20b%26c"));
    }

    [TestCase("first_purchase")]
    [TestCase("unknown_quest")]
    public async Task UnclaimedOrUnknownQuest_ShowsNoNotice(string claimed)
    {
        var (_, store, session) = await HarnessAsync(selectCompany: true);
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        var html = LocalQuests.Page(session, await store.GetAsync(session.AccountId, CancellationToken.None),
            claimed: claimed);
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Not.Contain("questClaimed"));
            Assert.That(html, Does.Not.Contain("Reward claimed"));
            Assert.That(html, Does.Not.Contain("unknown_quest"));
        });
    }

    [Test]
    public async Task OtherAccountsClaim_ShowsNoNotice()
    {
        var (_, store, session) = await HarnessAsync(selectCompany: true);
        var other = Guid.NewGuid();
        Assert.That(await store.PurchaseAsync(other, "drone_flax", 1, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(other, "first_purchase", CancellationToken.None), Is.True);

        var html = LocalQuests.Page(session, await store.GetAsync(session.AccountId, CancellationToken.None),
            claimed: "first_purchase");
        Assert.That(html, Does.Not.Contain("questClaimed"));
    }

    [Test]
    public async Task Notice_UsesPostgresOfferRewardAndWorksWithFilterAndDetail()
    {
        var (_, store, session) = await HarnessAsync(selectCompany: true);
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(session.AccountId, "first_purchase", CancellationToken.None), Is.True);
        var state = await store.GetAsync(session.AccountId, CancellationToken.None);
        var offers = new[]
        {
            new EconomyRewardOffer("quest", "first_purchase", "First supply order", "uridium", 25, "purchases", 1, true)
        };

        var html = LocalQuests.Page(session, state, offers, "claimed", "first_purchase", "first_purchase");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Reward claimed: First supply order · 25 uridium added to your account."));
            Assert.That(html, Does.Contain("id=\"questDetail\""));
            Assert.That(html, Does.Contain("data-filter=\"claimed\""));
        });
    }

    [Test]
    public async Task ClaimedQuestLaterDisabled_StillConfirmsClaim()
    {
        var (_, store, session) = await HarnessAsync(selectCompany: true);
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(session.AccountId, "first_purchase", CancellationToken.None), Is.True);
        var offers = new[]
        {
            new EconomyRewardOffer("quest", "first_purchase", "First supply order", "credits", 250, "purchases", 1, false)
        };
        var html = LocalQuests.Page(session, await store.GetAsync(session.AccountId, CancellationToken.None),
            offers, claimed: "first_purchase");
        Assert.That(html, Does.Contain("Reward claimed: First supply order · 250 local credits"));
    }

    [Test]
    public async Task WithoutClaimed_PageHasNoNotice()
    {
        var (_, store, session) = await HarnessAsync(selectCompany: true);
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        Assert.That(await store.ClaimQuestAsync(session.AccountId, "first_purchase", CancellationToken.None), Is.True);
        var html = LocalQuests.Page(session, await store.GetAsync(session.AccountId, CancellationToken.None));
        Assert.That(html, Does.Not.Contain("questClaimed"));
    }

    [TestCase("?action=internalQuests&claimed=first_purchase", true)]
    [TestCase("?action=internalQuests&filter=claimed&claimed=first_purchase", true)]
    [TestCase("?action=internalQuests&claimed=", false)]
    [TestCase("?action=internalQuests&claimed=first_purchase&claimed=first_craft", false)]
    [TestCase("?action=internalQuests&CLAIMED=first_purchase", false)]
    [TestCase("?action=internalQuests&Claimed=first_purchase", false)]
    [TestCase("?action=internalQuests&claimed=%0Afirst", false)]
    public void ValidPageQuery_ChecksClaimedKey(string query, bool valid)
    {
        var parsed = new QueryCollection(QueryHelpers.ParseQuery(query));
        Assert.That(LocalQuestAction.ValidPageQuery(parsed), Is.EqualTo(valid));
    }

    [TestCase("claimed=")]
    [TestCase("claimed=first_purchase&claimed=first_craft")]
    [TestCase("CLAIMED=first_purchase")]
    [TestCase("claimed=First_Purchase")]
    [TestCase("claimed=first-purchase")]
    [TestCase("claimed=%3Cscript%3E")]
    [TestCase("claimed=first%20purchase")]
    public async Task Endpoint_BadClaimed_Returns400BeforeSessionWithoutEcho(string claimed)
    {
        var (sessions, _, _) = await HarnessAsync(selectCompany: false);
        var context = await SendAsync($"?action=internalQuests&{claimed}", null, sessions);
        var body = Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest), body);
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(body, Does.Not.Contain("<script>"));
            Assert.That(body, Does.Not.Contain("First_Purchase"));
        });
    }

    [Test]
    public async Task Endpoint_WellFormedClaimedWithoutSession_Returns401()
    {
        var (sessions, _, _) = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalQuests&claimed=first_purchase", null, sessions);
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task Endpoint_WellFormedClaimedWithoutCompany_RedirectsToCompanyChoice()
    {
        var (sessions, _, session) = await HarnessAsync(selectCompany: false);
        var context = await SendAsync("?action=internalQuests&claimed=first_purchase", session.Dosid, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/indexInternal.es?action=internalCompanyChoose"));
        });
    }

    private static async Task<(GameAuthSessionStore Sessions, LocalHangarEquipmentStore Store, GameAuthSession Session)>
        HarnessAsync(bool selectCompany)
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        if (selectCompany)
            Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        return (sessions, new LocalHangarEquipmentStore(cache), session);
    }

    private static DefaultHttpContext ClaimRequest(string dosid, string quest)
    {
        var body = $"quest={quest}&key={Uri.EscapeDataString(LocalQuestAction.CreateFormKey(dosid, quest))}";
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalQuestClaim");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        return context;
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, string? dosid, GameAuthSessionStore sessions)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        if (dosid is not null) context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Response.Body = new MemoryStream();

        // Only the session store is reached before the company redirect on these paths.
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, sessions, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);
        await endpoint.HandleAsync(CancellationToken.None);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
