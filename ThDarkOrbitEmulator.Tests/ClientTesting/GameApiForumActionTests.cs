using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class GameApiForumActionTests
{
    private const string Dosid = "forum-dosid";

    private static async Task<(LocalLinksRequest.Result Result, FakeSessions Store)> ResolveAsync(string queryString,
        string? dosid = Dosid, string? company = "mmo")
    {
        var store = new FakeSessions(company);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(queryString);
        if (dosid is not null)
            context.Request.Headers.Cookie = $"dosid={dosid}";
        return (await GameApiForumAction.ResolveAsync(context.Request, store, CancellationToken.None), store);
    }

    [Test]
    public async Task CapturedLink_OpensForumIndexForLiveSession()
    {
        var (result, store) = await ResolveAsync("?action=portal.redirectToBoard");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(result.Html, Does.Contain($"<p id=\"topicCount\">{LocalForumHelp.Topics.Count} topics</p>"));
            Assert.That(result.Html, Is.EqualTo(LocalForumHelp.Page(true)));
            Assert.That(store.Lookups, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CapturedLink_WithTopicAndCategory_MatchesInternalForum()
    {
        var (detail, _) = await ResolveAsync("?action=portal.redirectToBoard&topic=skylab");
        var (filtered, _) = await ResolveAsync("?action=portal.redirectToBoard&category=production");

        Assert.Multiple(() =>
        {
            Assert.That(detail.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(detail.Html, Is.EqualTo(LocalForumHelp.Page(true, "skylab")));
            Assert.That(detail.Html, Does.Contain("action=internalForum&amp;topic=assembly"));
            Assert.That(filtered.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(filtered.Html, Is.EqualTo(LocalForumHelp.Page(true, null, "production")));
        });
    }

    [TestCase("")]
    [TestCase("?action=")]
    [TestCase("?action=portal.redirectToForum")]
    [TestCase("?action=PORTAL.REDIRECTTOBOARD")]
    [TestCase("?action=portal.redirecttoboard")]
    [TestCase("?action=portal.redirectToBoard%20")]
    [TestCase("?action=portal.redirectToBoard&action=portal.redirectToBoard")]
    [TestCase("?ACTION=portal.redirectToBoard")]
    public async Task NonCanonicalAction_Returns404BeforeSessionLookup(string queryString)
    {
        var (result, store) = await ResolveAsync(queryString);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(result.Html, Does.Contain("Unknown GameAPI action"));
            Assert.That(result.Html, Does.Not.Contain("REDIRECTTOBOARD").And.Not.Contain("redirectToForum"));
            Assert.That(store.Lookups, Is.Zero);
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("unknown-session")]
    public async Task MissingOrUnknownSession_Returns401WithSignInLink(string? dosid)
    {
        var (result, _) = await ResolveAsync("?action=portal.redirectToBoard", dosid);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
            Assert.That(result.Html, Does.Contain("<a href=\"/\">Back to sign-in</a>"));
            Assert.That(result.Html, Does.Not.Contain("topicCount"));
        });
    }

    [Test]
    public async Task OversizedCookie_IsRejectedWithoutLookup()
    {
        var (result, store) = await ResolveAsync("?action=portal.redirectToBoard",
            new string('a', LocalLogoutAction.MaxDosidLength + 1));

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(store.Lookups, Is.Zero);
    }

    [Test]
    public async Task CookieAtLengthLimit_IsLookedUp()
    {
        var (result, store) = await ResolveAsync("?action=portal.redirectToBoard",
            new string('a', LocalLogoutAction.MaxDosidLength));

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(store.Lookups, Is.EqualTo(1));
    }

    [Test]
    public async Task SessionWithoutCompany_StillOpensForum_UnknownCompanyIs403()
    {
        var (noCompany, _) = await ResolveAsync("?action=portal.redirectToBoard", company: null);
        var (unknown, _) = await ResolveAsync("?action=portal.redirectToBoard", company: "xyz");

        Assert.That(noCompany.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(unknown.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [TestCase("?action=portal.redirectToBoard&section=connection", StatusCodes.Status400BadRequest)]
    [TestCase("?action=portal.redirectToBoard&Topic=skylab", StatusCodes.Status400BadRequest)]
    [TestCase("?action=portal.redirectToBoard&topic=skylab&topic=clan", StatusCodes.Status400BadRequest)]
    [TestCase("?action=portal.redirectToBoard&topic=Skylab", StatusCodes.Status400BadRequest)]
    [TestCase("?action=portal.redirectToBoard&topic=no-such-topic", StatusCodes.Status404NotFound)]
    [TestCase("?action=portal.redirectToBoard&category=no-such-category", StatusCodes.Status404NotFound)]
    [TestCase("?action=portal.redirectToBoard&category=production&topic=clan", StatusCodes.Status404NotFound)]
    [TestCase("?action=portal.redirectToBoard&from=%0A", StatusCodes.Status400BadRequest)]
    public async Task InvalidForumParameters_AreRejectedLikeInternalForum(string queryString, int status)
    {
        var (result, _) = await ResolveAsync(queryString);

        Assert.That(result.Status, Is.EqualTo(status));
        Assert.That(result.Html, Does.Contain("id=\"localLinksError\""));
        Assert.That(result.Html, Does.Contain("<a href=\"/indexInternal.es?action=internalForum\">Back to Forum</a>"));
    }

    [Test]
    public async Task NavigationParameters_AreTolerated()
    {
        var (result, _) = await ResolveAsync("?action=portal.redirectToBoard&from=menu&prc=1&lang=es");

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
    }

    [TestCase("?action=portal.redirectToBoard&ACTION=portal.redirectToBoard")]
    [TestCase("?action=portal.redirectToBoard&Action=portal.redirectToBoard")]
    public async Task MixedCaseDuplicateAction_IsRejectedBeforeSessionLookup(string queryString)
    {
        var (result, store) = await ResolveAsync(queryString);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(store.Lookups, Is.Zero);
    }

    [Test]
    public async Task UnknownAction_TakesPrecedenceOverMissingSessionAndInvalidForumParameters()
    {
        var (result, store) = await ResolveAsync("?action=unknown&topic=Not-Valid", dosid: null);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        Assert.That(result.Html, Does.Contain("Unknown GameAPI action"));
        Assert.That(store.Lookups, Is.Zero);
    }

    [Test]
    public async Task MissingSession_TakesPrecedenceOverInvalidForumParameters()
    {
        var (result, store) = await ResolveAsync("?action=portal.redirectToBoard&topic=Not-Valid", dosid: null);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(result.Html, Does.Contain("Back to sign-in"));
        Assert.That(store.Lookups, Is.Zero);
    }

    [Test]
    public async Task InvalidCompany_TakesPrecedenceOverInvalidForumParameters()
    {
        var (result, store) = await ResolveAsync("?action=portal.redirectToBoard&topic=Not-Valid", company: "unknown");

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(result.Html, Does.Contain("unknown company"));
        Assert.That(store.Lookups, Is.EqualTo(1));
    }

    [TestCase("?action=portal.redirectToBoard&topic=")]
    [TestCase("?action=portal.redirectToBoard&q=%20%20")]
    public async Task EmptyTopicOrBlankSearch_OpensForumIndex(string queryString)
    {
        var (result, _) = await ResolveAsync(queryString);

        Assert.That(result.Status, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(result.Html, Is.EqualTo(LocalForumHelp.Page(true)));
    }

    [TestCase("?action=portal.redirectToBoard&topic=skylab&q=laser", StatusCodes.Status400BadRequest)]
    [TestCase("?action=portal.redirectToBoard&q=x", StatusCodes.Status400BadRequest)]
    [TestCase("?action=portal.redirectToBoard&category=Production", StatusCodes.Status400BadRequest)]
    public async Task ForumSearchAndCategoryBounds_AreApplied(string queryString, int status)
    {
        var (result, _) = await ResolveAsync(queryString);

        Assert.That(result.Status, Is.EqualTo(status));
        Assert.That(result.Html, Does.Contain("id=\"localLinksError\""));
    }

    [Test]
    public async Task WrittenResponse_IsNoStoreHtmlWithoutCookie()
    {
        var (result, _) = await ResolveAsync("?action=portal.redirectToBoard");
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await LocalLinksRequest.WriteAsync(context.Response, result, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(body.Length, Is.GreaterThan(0));
        });
    }

    private sealed class FakeSessions(string? company) : IGameAuthSessionStore
    {
        public int Lookups { get; private set; }

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult<GameAuthSession?>(dosid == Dosid
                ? new GameAuthSession
                {
                    AccountId = Guid.NewGuid(),
                    Username = "pilot",
                    GameUserId = 42,
                    Company = company,
                    Dosid = Dosid,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                }
                : null);
        }

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account, string? keycloakAccessToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
