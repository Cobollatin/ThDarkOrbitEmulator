using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalSpaceSearchActionHardeningTests
{
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _store = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _store = new LocalHangarEquipmentStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task TruncatedMultipartBody_Returns400_InsteadOfThrowing()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"planet\"\r\n\r\nmars");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("could not be read"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task BodyStreamFailure_Returns400_InsteadOfThrowing()
    {
        var context = Build("planet=mars");
        context.Request.Body = new FailingStream();
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task ValidMultipartBody_StillSearches()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"planet\"\r\n\r\nmars\r\n--b--\r\n");
        context.Request.ContentType = "multipart/form-data; boundary=b";
        await Handle(context);
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(303));
            Assert.That(state.Credits, Is.EqualTo(10000 - LocalSpaceSearch.SearchCost));
            Assert.That(state.SearchedPlanetIds, Is.EquivalentTo(new[] { "mars" }));
        });
    }

    [Test]
    public async Task MultipartBody_WithFileAlongsidePlanet_IsRejectedWithoutMutation()
    {
        var context = Build("--b\r\nContent-Disposition: form-data; name=\"planet\"\r\n\r\nmars\r\n" +
            "--b\r\nContent-Disposition: form-data; name=\"attachment\"; filename=\"note.txt\"\r\n" +
            "Content-Type: text/plain\r\n\r\nignored\r\n--b--\r\n");
        context.Request.ContentType = "multipart/form-data; boundary=b";

        await Handle(context);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(context.Response.Headers.Location.ToString(), Is.Empty);
        });
        await AssertUnchangedAsync();
    }

    [TestCase("PUT")]
    [TestCase("DELETE")]
    [TestCase("PATCH")]
    public async Task NonPostMethods_Return405_BeforeSessionCheck(string method)
    {
        var context = Build("planet=mars", dosid: "not-a-session");
        context.Request.Method = method;
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
        });
        await AssertUnchangedAsync();
    }

    [TestCase("?action=INTERNALSPACESEARCHEXPLORE")]
    [TestCase("?action=internalspacesearchexplore")]
    [TestCase("?action=internalSpaceSearchExplore&action=internalSpaceSearchExplore")]
    [TestCase("?action=internalSpaceSearchExplore&from=internalSpaceSearch")]
    [TestCase("?action=internalSpaceSearchExplore&planet=mars")]
    [TestCase("?action=internalVoucherRedeem")]
    [TestCase("?planet=mars")]
    public async Task InvalidQuery_Returns400_WithoutChangingState(string query)
    {
        var context = Build("planet=mars");
        context.Request.QueryString = new QueryString(query);
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task EmptyQuery_StillSearches()
    {
        var context = Build("planet=mars");
        context.Request.QueryString = QueryString.Empty;
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(303));
    }

    [TestCase("planet=mars&%70lanet=venus")]
    [TestCase("planet=mars&Planet=venus")]
    [TestCase("%50lanet=mars")]
    [TestCase("planet=mars&%63redits=99999")]
    [TestCase("planet=mars%0A")]
    public async Task DecodedOrNonCanonicalFormKeysAndValues_AreRejectedWithoutMutation(string body)
    {
        var context = Build(body);
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
        await AssertUnchangedAsync();
    }

    [TestCase("application/json")]
    [TestCase("text/plain")]
    [TestCase("")]
    public async Task UnsupportedOrMissingFormMediaType_Returns400WithoutReadingBody(string contentType)
    {
        var context = Build("planet=mars");
        context.Request.ContentType = contentType;
        context.Request.Body = new FailingStream();
        await Handle(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
        await AssertUnchangedAsync();
    }

    [Test]
    public async Task InvalidQuery_IsRejected_BeforeSessionCheck()
    {
        var context = Build("planet=mars", dosid: "not-a-session");
        context.Request.QueryString = new QueryString("?action=internalspacesearchexplore");
        await Handle(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [TestCase("planet=<script>alert(1)</script>")]
    [TestCase("planet=<b>mars</b>")]
    public async Task RejectedPlanet_IsNotEchoedIntoErrorPage(string body)
    {
        var context = Build(body);
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(html, Does.Not.Contain("<script>").And.Not.Contain("<b>"));
        });
        await AssertUnchangedAsync();
    }

    [TestCase("action=internalSpaceSearch", true)]
    [TestCase("action=internalSpaceSearch&from=internalStart&prc=1&lang=en", true)]
    [TestCase("action=internalSpaceSearch&LANG=en", false)]
    [TestCase("from=internalStart", false)]
    [TestCase("action=internalSpaceSearch&action=internalSpaceSearch", false)]
    [TestCase("action=internalSpaceSearch&lang=en&lang=de", false)]
    [TestCase("action=internalSpaceSearch&planet=Mars", false)]
    [TestCase("action=internalSpaceSearch&credits=99999", false)]
    [TestCase("action=internalSpaceSearch&lang=e%0An", false)]
    public void PageQuery_AllowsOnlyShellNavigationParameters(string query, bool expected) =>
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query(query)), Is.EqualTo(expected));

    [Test]
    public void PageQuery_LimitsValueLength()
    {
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query("action=internalSpaceSearch&from=" + new string('a', 64))), Is.True);
        Assert.That(LocalSpaceSearchAction.ValidPageQuery(Query("action=internalSpaceSearch&from=" + new string('a', 65))), Is.False);
    }

    [Test]
    public async Task ExpiredSession_GetsHtmlErrorPage_ThatLinksBack()
    {
        var context = Build("planet=mars", dosid: "not-a-session");
        await Handle(context);
        var html = await Body(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(html, Does.Contain("Sign in again").And.Contain("action=internalSpaceSearch\""));
        });
        await AssertUnchangedAsync();
    }

    [TestCase("", 0)]
    [TestCase("   ", 0)]
    [TestCase(129, 0)]
    [TestCase(128, 1)]
    public async Task Explore_BoundsDosidBeforeSessionAndFormAccess(object token, int expectedLookups)
    {
        var dosid = token is int length ? new string('a', length) : (string)token;
        var selected = await _sessions.TryGetByDosidAsync(_session.Dosid, CancellationToken.None);
        Assert.That(selected, Is.Not.Null);
        var sessions = new CountingSessionStore(selected!);
        var context = Build("planet=mars", dosid);
        if (expectedLookups == 0) context.Request.Body = new FailingStream();

        await LocalSpaceSearchAction.HandleAsync(context, sessions, _store, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(sessions.LookupCount, Is.EqualTo(expectedLookups));
            Assert.That(context.Response.StatusCode, Is.EqualTo(expectedLookups == 0 ? 401 : 303));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
        if (expectedLookups == 0)
        {
            var html = await Body(context);
            Assert.That(html, Does.Contain("Sign in again"));
            if (dosid.Length > 0) Assert.That(html, Does.Not.Contain(dosid));
            await AssertUnchangedAsync();
        }
        else
        {
            var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
            Assert.That(state.SearchedPlanetIds, Is.EquivalentTo(new[] { "mars" }));
        }
    }

    private static IQueryCollection Query(string query) => new QueryCollection(QueryHelpers.ParseQuery(query));

    private DefaultHttpContext Build(string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?action=internalSpaceSearchExplore");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private Task Handle(HttpContext context) =>
        LocalSpaceSearchAction.HandleAsync(context, _sessions, _store, CancellationToken.None);

    private static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private async Task AssertUnchangedAsync()
    {
        var state = await _store.GetAsync(_session.AccountId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(10000));
            Assert.That(state.SearchedPlanetIds, Is.Empty);
        });
    }

    private sealed class FailingStream : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("Client disconnected.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("Client disconnected.");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Client disconnected.");
    }

    private sealed class CountingSessionStore(GameAuthSession session) : IGameAuthSessionStore
    {
        public int LookupCount { get; private set; }

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult<GameAuthSession?>(session);
        }

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account,
            string? keycloakAccessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
