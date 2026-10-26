using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;
using ThDarkOrbitEmulator.ApiService.Common;
using ThDarkOrbitEmulator.ApiService.Features.Identity.Login;
using ThDarkOrbitEmulator.ApiService.Features.Identity.Profile;
using ThDarkOrbitEmulator.ApiService.Features.Identity.Register;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.Identity;

/// <summary>
/// SPEC-03 §8: HTTP contract of the <c>identity/register</c>, <c>identity/login</c> and <c>identity/me</c> slices.
/// </summary>
public class IdentityHttpEndpointSpecificationTests
{
    [Test]
    public async Task Register_FeatureDisabled_Returns404BeforeAnyDependencyCall()
    {
        var env = new Env { FeatureEnabled = false };

        var context = await env.RegisterAsync(new RegisterRequest { Username = "pilot", Password = "pw" });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status404NotFound, ApiErrorCode.IdentityFeatureDisabled);
            Assert.That(env.Features.Requested, Is.EqualTo(new[] { "Identity.Register" }));
            Assert.That(env.Keycloak.CreatedUsers, Is.Empty);
            Assert.That(env.Commands.Commands, Is.Empty);
        });
    }

    [TestCase("", "pw")]
    [TestCase("   ", "pw")]
    [TestCase(null, "pw")]
    [TestCase("pilot", "")]
    [TestCase("pilot", "   ")]
    [TestCase("pilot", null)]
    public async Task Register_BlankUsernameOrPassword_Returns400WithoutKeycloakOrStoreCall(string? username, string? password)
    {
        var env = new Env();

        var context = await env.RegisterAsync(new RegisterRequest { Username = username!, Password = password! });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status400BadRequest, ApiErrorCode.IdentityInvalidInput);
            Assert.That(env.Keycloak.CreatedUsers, Is.Empty);
            Assert.That(env.Commands.Commands, Is.Empty);
        });
    }

    [Test]
    public async Task Register_Success_TrimsUsernameOnlyAndCreatesKeycloakUserBeforeLocalAccount()
    {
        var env = new Env();

        var context = await env.RegisterAsync(new RegisterRequest
            { Username = "  pilot  ", Password = " secret ", Email = " pilot@example.test " });

        using var body = Parse(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(body.RootElement, "created").GetBoolean(), Is.True);
            Assert.That(env.Calls, Is.EqualTo(new[] { "keycloak.create", "store.register" }));
            Assert.That(env.Keycloak.CreatedUsers.Single(),
                Is.EqualTo(("pilot", " secret ", (string?)" pilot@example.test ", (string?)null)));
            Assert.That(env.Commands.Commands.Single(),
                Is.EqualTo(new RegisterAccountCommand("pilot", " pilot@example.test ", " secret ")));
        });
    }

    [Test]
    public async Task Register_NullEmail_IsForwardedAsNull()
    {
        var env = new Env();

        await env.RegisterAsync(new RegisterRequest { Username = "pilot", Password = "pw", Email = null });

        Assert.Multiple(() =>
        {
            Assert.That(env.Keycloak.CreatedUsers.Single().Email, Is.Null);
            Assert.That(env.Commands.Commands.Single().Email, Is.Null);
        });
    }

    [Test]
    public async Task Register_KeycloakRejects_Returns503AndNeverWritesLocalAccount()
    {
        var env = new Env();
        env.Keycloak.CreateResult = false;

        var context = await env.RegisterAsync(new RegisterRequest { Username = "pilot", Password = "pw" });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status503ServiceUnavailable, ApiErrorCode.IdentityKeycloakUnavailable);
            Assert.That(env.Commands.Commands, Is.Empty);
        });
    }

    [Test]
    public async Task Register_LocalDuplicate_Returns409AfterKeycloakCreation()
    {
        var env = new Env();
        env.Commands.RegisterResult = false;

        var context = await env.RegisterAsync(new RegisterRequest { Username = "pilot", Password = "pw" });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status409Conflict, ApiErrorCode.IdentityUsernameTaken);
            Assert.That(env.Calls, Is.EqualTo(new[] { "keycloak.create", "store.register" }));
        });
    }

    [Test]
    public async Task Register_ForwardsCancellationTokenToKeycloakAndStore()
    {
        using var cancellation = new CancellationTokenSource();
        var env = new Env();

        await env.RegisterAsync(new RegisterRequest { Username = "pilot", Password = "pw" }, cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(env.Keycloak.Tokens, Is.EqualTo(new[] { cancellation.Token }));
            Assert.That(env.Commands.Tokens, Is.EqualTo(new[] { cancellation.Token }));
        });
    }

    [Test]
    public async Task Login_FeatureDisabled_Returns404BeforeAnyDependencyCall()
    {
        var env = new Env { FeatureEnabled = false };

        var context = await env.LoginAsync(new LoginRequest { Username = "pilot", Password = "pw" });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status404NotFound, ApiErrorCode.IdentityFeatureDisabled);
            Assert.That(env.Features.Requested, Is.EqualTo(new[] { "Identity.Login" }));
            Assert.That(env.Calls, Is.Empty);
        });
    }

    [TestCase("", "pw")]
    [TestCase("   ", "pw")]
    [TestCase(null, "pw")]
    [TestCase("pilot", "")]
    [TestCase("pilot", "   ")]
    [TestCase("pilot", null)]
    public async Task Login_BlankUsernameOrPassword_Returns400WithoutLookup(string? username, string? password)
    {
        var env = new Env();

        var context = await env.LoginAsync(new LoginRequest { Username = username!, Password = password! });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status400BadRequest, ApiErrorCode.IdentityInvalidInput);
            Assert.That(env.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task Login_UnknownLocalAccount_Returns401WithoutKeycloakTokenRequest()
    {
        var env = new Env();
        env.Queries.CredentialsResult = false;

        var context = await env.LoginAsync(new LoginRequest { Username = "ghost", Password = "pw" });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status401Unauthorized, ApiErrorCode.IdentityAccountNotRegistered);
            Assert.That(env.Calls, Is.EqualTo(new[] { "store.validate" }));
        });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task Login_KeycloakReturnsNoToken_Returns401AuthenticationFailed(string? token)
    {
        var env = new Env();
        env.Keycloak.Token = token;

        var context = await env.LoginAsync(new LoginRequest { Username = "pilot", Password = "pw" });

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status401Unauthorized, ApiErrorCode.IdentityAuthenticationFailed);
            Assert.That(env.Calls, Is.EqualTo(new[] { "store.validate", "keycloak.token" }));
        });
    }

    [Test]
    public async Task Login_Success_ReturnsTokenVerbatimAndTrimsOnlyTheUsername()
    {
        var env = new Env();
        env.Keycloak.Token = "header.payload.signature";

        var context = await env.LoginAsync(new LoginRequest { Username = "  pilot ", Password = " pw " });

        using var body = Parse(context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(Property(body.RootElement, "authenticated").GetBoolean(), Is.True);
            Assert.That(Property(body.RootElement, "token").GetString(), Is.EqualTo("header.payload.signature"));
            Assert.That(env.Queries.Validations.Single(), Is.EqualTo(new ValidateCredentialsQuery("pilot")));
            Assert.That(env.Keycloak.TokenRequests.Single(), Is.EqualTo(("pilot", " pw ")));
        });
    }

    [Test]
    public async Task Profile_PrincipalWithoutUsername_Returns401WithoutLookup()
    {
        var env = new Env();

        var context = await env.ProfileAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status401Unauthorized, ApiErrorCode.IdentityUnauthorized);
            Assert.That(env.Queries.Lookups, Is.Empty);
        });
    }

    [Test]
    public async Task Profile_WhitespacePreferredUsername_IsRejectedWithoutFallingBack()
    {
        var env = new Env();
        var principal = Principal(("preferred_username", "   "), (ClaimTypes.Name, "fallback"));

        var context = await env.ProfileAsync(principal);

        Assert.Multiple(() =>
        {
            AssertError(context, StatusCodes.Status401Unauthorized, ApiErrorCode.IdentityUnauthorized);
            Assert.That(env.Queries.Lookups, Is.Empty);
        });
    }

    [Test]
    public async Task Profile_PreferredUsernameWinsOverNameClaim()
    {
        var env = new Env();

        await env.ProfileAsync(Principal((ClaimTypes.Name, "name-claim"), ("preferred_username", "preferred")));

        Assert.That(env.Queries.Lookups, Is.EqualTo(new[] { new GetAccountByUsernameQuery("preferred") }));
    }

    [Test]
    public async Task Profile_FallsBackToNameClaimAndForwardsItUntrimmed()
    {
        var env = new Env();

        await env.ProfileAsync(Principal((ClaimTypes.Name, " pilot ")));

        Assert.That(env.Queries.Lookups, Is.EqualTo(new[] { new GetAccountByUsernameQuery(" pilot ") }));
    }

    [Test]
    public async Task Profile_UnknownAccount_Returns404()
    {
        var env = new Env();

        var context = await env.ProfileAsync(Principal(("preferred_username", "ghost")));

        AssertError(context, StatusCodes.Status404NotFound, ApiErrorCode.IdentityAccountNotFound);
    }

    [Test]
    public async Task Profile_KnownAccount_ProjectsUsernameEmailAndGameUserIdOnly()
    {
        var env = new Env();
        env.Queries.Account = new AccountSummary(Guid.NewGuid(), "Pilot", null, 100000042, "mmo");

        var context = await env.ProfileAsync(Principal(("preferred_username", "pilot")));

        using var body = Parse(context);
        var names = body.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Order().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(names, Is.EqualTo(new[] { "email", "gameuserid", "username" }));
            Assert.That(Property(body.RootElement, "username").GetString(), Is.EqualTo("Pilot"));
            Assert.That(Property(body.RootElement, "email").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(Property(body.RootElement, "gameUserId").GetInt64(), Is.EqualTo(100000042));
        });
    }

    [Test]
    public void Endpoints_PinRoutesVersionAndAnonymousAccess()
    {
        var register = Factory.Create<RegisterEndpoint>(new Env().Deps<RegisterEndpoint>());
        var login = Factory.Create<LoginEndpoint>(new Env().Deps<LoginEndpoint>());
        var profile = Factory.Create<ProfileEndpoint>(new Env().Deps<ProfileEndpoint>());
        register.Configure();
        login.Configure();
        profile.Configure();

        Assert.Multiple(() =>
        {
            Assert.That(register.Definition.Routes, Is.EqualTo(new[] { "identity/register" }));
            Assert.That(register.Definition.Verbs, Is.EqualTo(new[] { "POST" }));
            Assert.That(register.Definition.AnonymousVerbs, Is.Not.Null.And.Not.Empty);
            Assert.That(login.Definition.Routes, Is.EqualTo(new[] { "identity/login" }));
            Assert.That(login.Definition.Verbs, Is.EqualTo(new[] { "POST" }));
            Assert.That(login.Definition.AnonymousVerbs, Is.Not.Null.And.Not.Empty);
            Assert.That(profile.Definition.Routes, Is.EqualTo(new[] { "identity/me" }));
            Assert.That(profile.Definition.Verbs, Is.EqualTo(new[] { "GET" }));
            Assert.That(profile.Definition.AnonymousVerbs, Is.Null.Or.Empty);
        });
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static void AssertError(HttpContext context, int status, string code)
    {
        using var body = Parse(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(status));
        Assert.That(Property(body.RootElement, "code").GetString(), Is.EqualTo(code));
        Assert.That(Property(body.RootElement, "message").GetString(), Is.EqualTo(ApiErrorCatalog.GetMessage(code)));
    }

    private static JsonDocument Parse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body);
    }

    private static JsonElement Property(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value
            : element.GetProperty(char.ToUpperInvariant(name[0]) + name[1..]);

    private sealed class Env
    {
        public Env()
        {
            Keycloak = new FakeKeycloak(Calls);
            Commands = new FakeCommands(Calls);
            Queries = new FakeQueries(Calls);
        }

        public bool FeatureEnabled { init => Features.Enabled = value; }

        public List<string> Calls { get; } = [];

        public FakeFeatures Features { get; } = new();

        public FakeKeycloak Keycloak { get; }

        public FakeCommands Commands { get; }

        public FakeQueries Queries { get; }

        public object[] Deps<TEndpoint>()
            => typeof(TEndpoint) == typeof(RegisterEndpoint)
                ? [Commands, Keycloak, Features, NullLogger<RegisterEndpoint>.Instance]
                : typeof(TEndpoint) == typeof(LoginEndpoint)
                    ? [Queries, Features, Keycloak, NullLogger<LoginEndpoint>.Instance]
                    : [Queries, NullLogger<ProfileEndpoint>.Instance];

        public async Task<HttpContext> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
        {
            var context = NewContext();
            await Factory.Create<RegisterEndpoint>(context, Deps<RegisterEndpoint>()).HandleAsync(request, ct);
            return context;
        }

        public async Task<HttpContext> LoginAsync(LoginRequest request)
        {
            var context = NewContext();
            await Factory.Create<LoginEndpoint>(context, Deps<LoginEndpoint>()).HandleAsync(request, CancellationToken.None);
            return context;
        }

        public async Task<HttpContext> ProfileAsync(ClaimsPrincipal principal)
        {
            var context = NewContext();
            context.User = principal;
            await Factory.Create<ProfileEndpoint>(context, Deps<ProfileEndpoint>()).HandleAsync(CancellationToken.None);
            return context;
        }

        private static DefaultHttpContext NewContext()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            return context;
        }
    }

    private sealed class FakeFeatures : IFeatureManager
    {
        public bool Enabled { get; set; } = true;

        public List<string> Requested { get; } = [];

        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            await Task.CompletedTask;
            yield return "Identity.Register";
            yield return "Identity.Login";
        }

        public Task<bool> IsEnabledAsync(string feature)
        {
            Requested.Add(feature);
            return Task.FromResult(Enabled);
        }

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) => IsEnabledAsync(feature);
    }

    private sealed class FakeKeycloak(List<string> calls) : IKeycloakService
    {
        public bool CreateResult { get; set; } = true;

        public string? Token { get; set; } = "token";

        public List<(string Username, string Password, string? Email, string? RealmRole)> CreatedUsers { get; } = [];

        public List<(string Username, string Password)> TokenRequests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<string?> RequestAccessTokenAsync(string username, string password, CancellationToken cancellationToken)
        {
            calls.Add("keycloak.token");
            TokenRequests.Add((username, password));
            return Task.FromResult(Token);
        }

        public Task<bool> CreateUserAsync(string username, string password, string? email,
            CancellationToken cancellationToken, string? realmRole = null)
        {
            calls.Add("keycloak.create");
            CreatedUsers.Add((username, password, email, realmRole));
            Tokens.Add(cancellationToken);
            return Task.FromResult(CreateResult);
        }

        public Task<bool> EnsureRealmRoleAsync(string username, string realmRole, CancellationToken cancellationToken)
            => throw new AssertionException("Identity HTTP slices must not change realm roles.");
    }

    private sealed class FakeCommands(List<string> calls) : IIdentityCommandService
    {
        public bool RegisterResult { get; set; } = true;

        public List<RegisterAccountCommand> Commands { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<bool> ExecuteAsync(RegisterAccountCommand command, CancellationToken cancellationToken)
        {
            calls.Add("store.register");
            Commands.Add(command);
            Tokens.Add(cancellationToken);
            return Task.FromResult(RegisterResult);
        }

        public Task<bool> SetCompanyAsync(Guid accountId, string company, CancellationToken cancellationToken)
            => throw new AssertionException("Identity HTTP slices must not choose a company.");
    }

    private sealed class FakeQueries(List<string> calls) : IIdentityQueryService
    {
        public bool CredentialsResult { get; set; } = true;

        public AccountSummary? Account { get; set; }

        public List<ValidateCredentialsQuery> Validations { get; } = [];

        public List<GetAccountByUsernameQuery> Lookups { get; } = [];

        public Task<bool> ExecuteAsync(ValidateCredentialsQuery query, CancellationToken cancellationToken)
        {
            calls.Add("store.validate");
            Validations.Add(query);
            return Task.FromResult(CredentialsResult);
        }

        public Task<AccountSummary?> ExecuteAsync(GetAccountByUsernameQuery query, CancellationToken cancellationToken)
        {
            calls.Add("store.lookup");
            Lookups.Add(query);
            return Task.FromResult(Account);
        }
    }
}
