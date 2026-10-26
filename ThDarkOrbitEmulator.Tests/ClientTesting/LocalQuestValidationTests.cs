using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalQuestValidationTests
{
    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("HEAD")]
    public async Task ClaimAction_RejectsNonPostWithoutClaiming(string method)
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var context = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now));
        context.Request.Method = method;
        await LocalQuestAction.HandleAsync(context, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(Body(context), Does.Contain("must be submitted with POST"));
        });
        await AssertUnclaimedAsync(store, session);
    }

    [TestCase("?action=internalquestclaim")]
    [TestCase("?action=INTERNALQUESTCLAIM")]
    [TestCase("?action=internalQuestClaim&action=internalQuestClaim")]
    [TestCase("?action=internalQuestClaim&from=internalStart")]
    [TestCase("?action=internalQuests")]
    [TestCase("")]
    public async Task ClaimAction_RequiresExactSingleActionQuery(string query)
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var context = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now), query);
        await LocalQuestAction.HandleAsync(context, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400), query);
        Assert.That(Body(context), Does.Contain("Invalid Quests action."));
        await AssertUnclaimedAsync(store, session);
    }

    [Test]
    public async Task ClaimAction_UnreadableOrNonFormBodiesReturn400()
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var broken = Request(session.Dosid, "--x\r\nContent-Disposition: form-data; name=\"quest\"\r\n\r\nfirst_pur");
        broken.Request.ContentType = "multipart/form-data; boundary=x";
        await LocalQuestAction.HandleAsync(broken, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(broken.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(broken), Does.Contain("could not be read"));

        var json = Request(session.Dosid, "{\"quest\":\"first_purchase\"}");
        json.Request.ContentType = "application/json";
        await LocalQuestAction.HandleAsync(json, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(json.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(json), Does.Contain("Submit a quest claim form."));
        await AssertUnclaimedAsync(store, session);
    }

    [Test]
    public async Task ClaimAction_ErrorPagesEncodeNothingFromInput()
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var context = Request(session.Dosid, "quest=%3Cscript%3E&key=x");
        await LocalQuestAction.HandleAsync(context, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        Assert.That(Body(context), Does.Not.Contain("script"));
        Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
    }

    [Test]
    public async Task ClaimAction_ValidClaimCreditsRewardOnceThenConflicts()
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var before = (await store.GetAsync(session.AccountId, CancellationToken.None)).Credits;
        var first = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now));
        await LocalQuestAction.HandleAsync(first, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(first.Response.StatusCode, Is.EqualTo(303));
        Assert.That(first.Response.Headers.Location.ToString(), Is.EqualTo("/indexInternal.es?action=internalQuests&claimed=first_purchase"));
        var after = await store.GetAsync(session.AccountId, CancellationToken.None);
        Assert.That(after.ClaimedQuestIds, Is.EquivalentTo(new[] { "first_purchase" }));
        Assert.That(after.Credits, Is.EqualTo(before + 250));

        var replay = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now));
        await LocalQuestAction.HandleAsync(replay, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).Credits, Is.EqualTo(after.Credits));
    }

    [Test]
    public async Task ClaimAction_KeyAtLifetimeBoundaryStillWorksAndSkewedKeyIsRejected()
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var skewed = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now.AddMinutes(6)));
        await LocalQuestAction.HandleAsync(skewed, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(skewed.Response.StatusCode, Is.EqualTo(400));
        // Keys store whole seconds, so pin the clock to a whole second for the exact boundary.
        now = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var boundary = Request(session.Dosid, Signed(session.Dosid, "first_purchase", now - LocalQuestAction.FormLifetime));
        await LocalQuestAction.HandleAsync(boundary, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.That(boundary.Response.StatusCode, Is.EqualTo(303));
    }

    [TestCase("253402300799")]
    [TestCase("253402300800")]
    [TestCase("999999999999")]
    public async Task ClaimAction_SignedFarFutureTimestampReturns400WithoutClaim(string seconds)
    {
        var (sessions, store, session, now) = await ReadyToClaimAsync();
        var key = SignedKeyWithSeconds(session.Dosid, "first_purchase", seconds);
        var context = Request(session.Dosid, $"quest=first_purchase&key={Uri.EscapeDataString(key)}");
        await LocalQuestAction.HandleAsync(context, sessions, store, new FixedClock(now), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(Body(context), Does.Contain("The form is invalid"));
            Assert.That(Body(context), Does.Not.Contain(seconds));
        });
        await AssertUnclaimedAsync(store, session);
    }

    [TestCase("action=internalQuests", true)]
    [TestCase("action=internalQuests&from=internalStart", true)]
    [TestCase("action=internalQuests&from=internalStart&prc=100&lang=es", true)]
    [TestCase("action=internalQuests&FROM=internalStart", false)]
    [TestCase("from=internalStart", false)]
    [TestCase("action=internalQuests&quest=first_purchase", false)]
    [TestCase("action=internalQuests&from=a&from=b", false)]
    [TestCase("action=internalQuests&action=internalQuests", false)]
    [TestCase("action=internalQuests&lang=%0Aes", false)]
    public void PageQuery_AcceptsOnlyShellNavigationParameters(string query, bool expected)
    {
        var parsed = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
        Assert.That(LocalQuestAction.ValidPageQuery(parsed), Is.EqualTo(expected), query);
    }

    [Test]
    public void PageQuery_RejectsOverlongValues()
    {
        var parsed = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            $"action=internalQuests&prc={new string('1', 65)}"));
        Assert.That(LocalQuestAction.ValidPageQuery(parsed), Is.False);
    }

    [Test]
    public void Page_ShowsRemainingProgressForUnfinishedQuests()
    {
        var session = new GameAuthSession { Username = "pilot", Dosid = "dosid-1" };
        var page = LocalQuests.Page(session, new LocalEquipmentState([], []) { PurchasedItems = 2, CraftedItems = 0 });
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("<td>2 / 3</td>"));
            Assert.That(page, Does.Contain("In progress (1 more needed)"));
            Assert.That(page, Does.Contain("In progress (8 more needed)"));
            Assert.That(page, Does.Contain("In progress (5 more needed)"));
            Assert.That(page, Does.Not.Contain("In progress (0 more needed)"));
        });
    }

    private static async Task<(GameAuthSessionStore, LocalHangarEquipmentStore, GameAuthSession, DateTimeOffset)> ReadyToClaimAsync()
    {
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", CancellationToken.None), Is.True);
        var store = new LocalHangarEquipmentStore(cache);
        Assert.That(await store.PurchaseAsync(session.AccountId, "drone_flax", 1, CancellationToken.None), Is.True);
        return (sessions, store, session, DateTimeOffset.UtcNow);
    }

    private static async Task AssertUnclaimedAsync(LocalHangarEquipmentStore store, GameAuthSession session) =>
        Assert.That((await store.GetAsync(session.AccountId, CancellationToken.None)).ClaimedQuestIds, Is.Empty);

    private static string Signed(string dosid, string quest, DateTimeOffset issued) =>
        $"quest={quest}&key={Uri.EscapeDataString(LocalQuestAction.CreateFormKey(dosid, quest, issued))}";

    private static string SignedKeyWithSeconds(string dosid, string quest, string seconds)
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dosid),
            Encoding.UTF8.GetBytes($"quest:claim:{quest}:{nonce}:{seconds}"))).ToLowerInvariant();
        return $"{nonce}.{seconds}.{signature}";
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DefaultHttpContext Request(string dosid, string body, string query = "?action=internalQuestClaim")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
