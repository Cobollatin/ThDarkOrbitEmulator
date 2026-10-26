using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.Sas.Authentication;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

/// <summary>
/// SPEC-01 §5.2: endpoint-level contract of <c>POST /Sas/Authentication/Bigpoint</c> with identity,
/// Keycloak and game-auth session fakes.
/// </summary>
public class SasAuthenticationEndpointSpecificationTests
{
    private const string LocalFailure = "/?loginError=94";
    private const string PublicFailure = "https://www.darkorbit.com/?loginError=94";

    [TestCase(null, "pw")]
    [TestCase("", "pw")]
    [TestCase("   ", "pw")]
    [TestCase("pilot", null)]
    [TestCase("pilot", "")]
    [TestCase("pilot", "   ")]
    public async Task BlankUsernameOrPassword_RedirectsToLoginErrorWithoutAnyDependencyCall(string? username, string? password)
    {
        var env = new Env();

        var context = await env.PostAsync(Form(username, password));

        Assert.Multiple(() =>
        {
            AssertFailure(context, LocalFailure);
            Assert.That(env.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task NonFormBody_IsTreatedAsBlankCredentials()
    {
        var env = new Env();
        var context = Env.NewContext("localhost");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("{\"username\":\"pilot\",\"password\":\"pw\"}"u8.ToArray());

        await env.HandleAsync(context);

        Assert.Multiple(() =>
        {
            AssertFailure(context, LocalFailure);
            Assert.That(env.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task UnknownAccount_RedirectsToLoginErrorBeforeKeycloak()
    {
        var env = new Env { Account = null };

        var context = await env.PostAsync(Form("pilot", "pw"));

        Assert.Multiple(() =>
        {
            AssertFailure(context, LocalFailure);
            Assert.That(env.Calls, Is.EqualTo(new[] { "store.lookup" }));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task KeycloakRejection_RedirectsToLoginErrorWithoutSession(string? token)
    {
        var env = new Env { AccessToken = token };

        var context = await env.PostAsync(Form("pilot", "pw"));

        Assert.Multiple(() =>
        {
            AssertFailure(context, LocalFailure);
            Assert.That(env.Calls, Is.EqualTo(new[] { "store.lookup", "keycloak.token" }));
            Assert.That(env.CreatedSessions, Is.Empty);
        });
    }

    [Test]
    public async Task Success_LooksUpThenAuthenticatesThenCreatesSessionWithTrimmedUsernameOnly()
    {
        var env = new Env();

        var context = await env.PostAsync(Form("  pilot  ", " secret "));

        Assert.Multiple(() =>
        {
            Assert.That(env.Calls, Is.EqualTo(new[] { "store.lookup", "keycloak.token", "session.create" }));
            Assert.That(env.Lookups.Single(), Is.EqualTo(new GetAccountByUsernameQuery("pilot")));
            Assert.That(env.TokenRequests.Single(), Is.EqualTo(("pilot", " secret ")));
            Assert.That(env.CreatedSessions.Single(), Is.EqualTo((env.Account, (string?)"kc-token")));
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        });
    }

    [Test]
    public async Task Success_OnLoopbackRedirectsToRelativeWwwHopWithEscapedToken()
    {
        var env = new Env { RedirectToken = "a b+/=?&" };

        var context = await env.PostAsync(Form("pilot", "pw"));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/ProjectApi/Authentication?authUser=291&token=a%20b%2B%2F%3D%3F%26"));
        });
    }

    [Test]
    public async Task Success_SetsFreshEdgeCookieAndSecurityHeadersExactlyOnce()
    {
        var env = new Env();

        var first = await env.PostAsync(Form("pilot", "pw"));
        var second = await env.PostAsync(Form("pilot", "pw"));

        var cookies = new[] { first, second }.Select(c => c.Response.Headers.SetCookie.ToArray()).ToArray();
        Assert.Multiple(() =>
        {
            foreach (var context in new[] { first, second })
            {
                var setCookie = context.Response.Headers.SetCookie.ToArray();
                Assert.That(setCookie, Has.Length.EqualTo(1));
                Assert.That(setCookie[0], Does.Match("^aliyungf_tc=[0-9a-f]{32}; Path=/; HttpOnly$"));
                Assert.That(context.Response.Headers.ContentSecurityPolicy.ToArray(),
                    Is.EqualTo(new[] { "frame-ancestors 'none';" }));
                Assert.That(context.Response.Headers.StrictTransportSecurity.ToArray(),
                    Is.EqualTo(new[] { "max-age=31536000" }));
            }

            Assert.That(cookies[0][0], Is.Not.EqualTo(cookies[1][0]));
        });
    }

    [Test]
    public async Task Failure_SetsNoCookieAndNoSecurityHeaders()
    {
        var env = new Env { Account = null };

        var context = await env.PostAsync(Form("pilot", "pw"));

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
            Assert.That(context.Response.Headers.ContainsKey("Content-Security-Policy"), Is.False);
            Assert.That(context.Response.Headers.ContainsKey("Strict-Transport-Security"), Is.False);
        });
    }

    [TestCase("www.darkorbit.com", null, null)]
    [TestCase("sas.bpsecure.com", null, null)]
    [TestCase("localhost.evil.example", null, null)]
    [TestCase("localhost", null, "www.darkorbit.com")]
    [TestCase("api-game", null, "www.darkorbit.com")]
    public async Task PublicOrReplayHost_UsesAbsoluteProductionLocations(string host, string? forwardedHost, string? originalHost)
    {
        var env = new Env { RedirectToken = "tok" };

        var success = await env.PostAsync(Form("pilot", "pw"), host, forwardedHost, originalHost);
        var failure = await new Env { Account = null }.PostAsync(Form("pilot", "pw"), host, forwardedHost, originalHost);

        Assert.Multiple(() =>
        {
            Assert.That(success.Response.Headers.Location.ToString(),
                Is.EqualTo("https://www.darkorbit.com/ProjectApi/Authentication?authUser=291&token=tok"));
            Assert.That(failure.Response.Headers.Location.ToString(), Is.EqualTo(PublicFailure));
        });
    }

    [TestCase("localhost", null)]
    [TestCase("127.0.0.1", null)]
    [TestCase("[::1]", null)]
    [TestCase("api-game", null)]
    [TestCase("gateway.internal", "localhost:5173")]
    public async Task EmulatorHost_UsesRelativeLocations(string host, string? forwardedHost)
    {
        var env = new Env { RedirectToken = "tok" };

        var success = await env.PostAsync(Form("pilot", "pw"), host, forwardedHost);
        var failure = await new Env { Account = null }.PostAsync(Form("pilot", "pw"), host, forwardedHost);

        Assert.Multiple(() =>
        {
            Assert.That(success.Response.Headers.Location.ToString(),
                Is.EqualTo("/ProjectApi/Authentication?authUser=291&token=tok"));
            Assert.That(failure.Response.Headers.Location.ToString(), Is.EqualTo(LocalFailure));
        });
    }

    [Test]
    public async Task QueryCaptchaAndExtraFields_DoNotChangeTheOutcome()
    {
        var env = new Env { RedirectToken = "tok" };
        var form = Form("pilot", "pw");
        form["g-recaptcha-response"] = "r";
        form["h-captcha-response"] = "h";
        form["loginForm_default_login_submit"] = "Login";

        var context = await env.PostAsync(form, query: "?authUser=22&token=abc");

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.Headers.Location.ToString(),
                Is.EqualTo("/ProjectApi/Authentication?authUser=291&token=tok"));
            Assert.That(env.Calls, Is.EqualTo(new[] { "store.lookup", "keycloak.token", "session.create" }));
        });
    }

    [Test]
    public async Task CancellationToken_IsForwardedToEveryDependency()
    {
        var env = new Env();
        using var cts = new CancellationTokenSource();

        await env.PostAsync(Form("pilot", "pw"), ct: cts.Token);

        Assert.That(env.Tokens, Has.Count.EqualTo(3).And.All.EqualTo(cts.Token));
    }

    [Test]
    public async Task RequestContext_SeparatesKnownAndAdditionalFormFieldsAndCookiesCaseInsensitively()
    {
        var context = Env.NewContext("localhost");
        var form = Form("pilot", "pw");
        form["H-Captcha-Response"] = "h";
        form["extra"] = "x";
        Env.WriteForm(context, form);
        context.Request.Headers.Cookie = "dosid=d1; aliyungf_tc=a1; _ga=g1";
        context.Request.QueryString = new QueryString("?authUser=22&token=abc");
        context.Request.Headers.UserAgent = "UA";

        var parsed = await SasAuthenticationRequestContext.ParseAsync(context.Request, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Payload.Username, Is.EqualTo("pilot"));
            Assert.That(parsed.Payload.Password, Is.EqualTo("pw"));
            Assert.That(parsed.Payload.HCaptchaResponse, Is.EqualTo("h"));
            Assert.That(parsed.Payload.Additional, Is.EqualTo(new Dictionary<string, string> { ["extra"] = "x" }));
            Assert.That(parsed.Cookies.Dosid, Is.EqualTo("d1"));
            Assert.That(parsed.Cookies.AliYun, Is.EqualTo("a1"));
            Assert.That(parsed.Cookies.Additional, Is.EqualTo(new Dictionary<string, string> { ["_ga"] = "g1" }));
            Assert.That(parsed.Query.AuthUser, Is.EqualTo("22"));
            Assert.That(parsed.Query.Token, Is.EqualTo("abc"));
            Assert.That(parsed.Metadata.UserAgent, Is.EqualTo("UA"));
            Assert.That(parsed.Metadata.Host, Is.EqualTo("localhost"));
        });
    }

    [Test]
    public async Task RequestContext_NonFormRequestHasNullCredentialsAndNullMissingCookies()
    {
        var context = Env.NewContext("localhost");

        var parsed = await SasAuthenticationRequestContext.ParseAsync(context.Request, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Payload.Username, Is.Null);
            Assert.That(parsed.Payload.Password, Is.Null);
            Assert.That(parsed.Payload.Additional, Is.Empty);
            Assert.That(parsed.Cookies.Dosid, Is.Null);
            Assert.That(parsed.Cookies.AliYun, Is.Null);
            Assert.That(parsed.Query.AuthUser, Is.Empty);
            Assert.That(parsed.Metadata.Origin, Is.Empty);
        });
    }

    private static Dictionary<string, string?> Form(string? username, string? password)
    {
        var form = new Dictionary<string, string?>();
        if (username is not null)
        {
            form["username"] = username;
        }

        if (password is not null)
        {
            form["password"] = password;
        }

        return form;
    }

    private static void AssertFailure(HttpContext context, string location)
    {
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo(location));
        Assert.That(context.Response.Headers.SetCookie.Count, Is.Zero);
    }

    private sealed class Env : IKeycloakService, IIdentityQueryService, IGameAuthSessionStore
    {
        public AccountSummary? Account { get; init; } = new(Guid.NewGuid(), "pilot", null, 42, "eic");

        public string? AccessToken { get; init; } = "kc-token";

        public string RedirectToken { get; init; } = "redirect-token";

        public List<string> Calls { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public List<GetAccountByUsernameQuery> Lookups { get; } = [];

        public List<(string Username, string Password)> TokenRequests { get; } = [];

        public List<(AccountSummary? Account, string? Token)> CreatedSessions { get; } = [];

        public async Task<DefaultHttpContext> PostAsync(
            Dictionary<string, string?> form,
            string host = "localhost",
            string? forwardedHost = null,
            string? originalHost = null,
            string query = "",
            CancellationToken ct = default)
        {
            var context = NewContext(host);
            if (forwardedHost is not null)
            {
                context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            }

            if (originalHost is not null)
            {
                context.Request.Headers["X-Original-Host"] = originalHost;
            }

            context.Request.QueryString = new QueryString(query);
            WriteForm(context, form);
            await HandleAsync(context, ct);
            return context;
        }

        public Task HandleAsync(DefaultHttpContext context, CancellationToken ct = default)
            => Factory.Create<SasAuthenticationEndpoint>(
                    context,
                    NullLogger<SasAuthenticationEndpoint>.Instance,
                    (IKeycloakService)this,
                    (IIdentityQueryService)this,
                    (IGameAuthSessionStore)this)
                .HandleAsync(ct);

        public static DefaultHttpContext NewContext(string host)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Host = new HostString(host);
            context.Response.Body = new MemoryStream();
            return context;
        }

        public static void WriteForm(HttpContext context, Dictionary<string, string?> form)
        {
            var body = string.Join("&", form.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        }

        public Task<AccountSummary?> ExecuteAsync(GetAccountByUsernameQuery query, CancellationToken cancellationToken)
        {
            Calls.Add("store.lookup");
            Lookups.Add(query);
            Tokens.Add(cancellationToken);
            return Task.FromResult(Account);
        }

        public Task<bool> ExecuteAsync(ValidateCredentialsQuery query, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not validate local credentials.");

        public Task<string?> RequestAccessTokenAsync(string username, string password, CancellationToken cancellationToken)
        {
            Calls.Add("keycloak.token");
            TokenRequests.Add((username, password));
            Tokens.Add(cancellationToken);
            return Task.FromResult(AccessToken);
        }

        public Task<bool> CreateUserAsync(string username, string password, string? email,
            CancellationToken cancellationToken, string? realmRole = null)
            => throw new AssertionException("SAS authentication must not create users.");

        public Task<bool> EnsureRealmRoleAsync(string username, string realmRole, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not change realm roles.");

        public Task<GameAuthSessionCreateResult> CreateSessionAsync(
            AccountSummary account, string? keycloakAccessToken, CancellationToken cancellationToken)
        {
            Calls.Add("session.create");
            CreatedSessions.Add((account, keycloakAccessToken));
            Tokens.Add(cancellationToken);
            return Task.FromResult(new GameAuthSessionCreateResult
            {
                Session = new GameAuthSession { AccountId = account.Id, Username = account.Username, Dosid = "dosid" },
                RedirectToken = RedirectToken
            });
        }

        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not rotate tokens.");

        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not read redirect tokens.");

        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not read existing sessions.");

        public Task<bool> SelectCompanyAsync(string dosid, string company, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not choose a company.");

        public Task InvalidateAsync(string dosid, CancellationToken cancellationToken)
            => throw new AssertionException("SAS authentication must not invalidate sessions.");
    }
}
