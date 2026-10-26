using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Quest claim, Voucher redeem, Payment claim and Space Search explore share the same action-query and form-read rules.
public class LocalRewardActionQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;
    private GameAuthSessionStore _sessions = null!;
    private LocalHangarEquipmentStore _equipment = null!;
    private GameAuthSession _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        _services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = _services.GetRequiredService<IDistributedCache>();
        _sessions = new GameAuthSessionStore(cache);
        _equipment = new LocalHangarEquipmentStore(cache);
        _session = (await _sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", "mmo", 7), null, CancellationToken.None)).Session;
        Assert.That(await _sessions.SelectCompanyAsync(_session.Dosid, "mmo", CancellationToken.None), Is.True);
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    public static IEnumerable<TestCaseData> NonCanonicalKeys()
    {
        foreach (var action in new[] { "internalQuestClaim", "internalVoucherRedeem", "internalPaymentClaim", "internalSpaceSearchExplore" })
        foreach (var key in new[] { "ACTION", "Action" })
            yield return new TestCaseData(action, key).SetName($"{action}_{key}_Returns400BeforeSessionLookup");
    }

    [TestCaseSource(nameof(NonCanonicalKeys))]
    public async Task NonCanonicalActionKey_Returns400BeforeSessionLookup(string action, string key)
    {
        var context = Build($"?{key}={action}", Body(action), "missing-session");
        await Handle(action, context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(ResponseText(context), Does.Contain(InvalidActionMessage(action)));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    [TestCase("internalQuestClaim")]
    [TestCase("internalVoucherRedeem")]
    [TestCase("internalPaymentClaim")]
    [TestCase("internalSpaceSearchExplore")]
    public async Task RepeatedMixedCaseKey_Returns400(string action)
    {
        var context = Build($"?action={action}&ACTION={action}", Body(action));
        await Handle(action, context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(400));
    }

    [TestCase("internalQuestClaim")]
    [TestCase("internalVoucherRedeem")]
    [TestCase("internalPaymentClaim")]
    [TestCase("internalSpaceSearchExplore")]
    public async Task NonCanonicalActionKey_WithGet_Returns405WithAllow(string action)
    {
        var context = Build($"?ACTION={action}", "");
        context.Request.Method = "GET";
        await Handle(action, context);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(405));
            Assert.That(context.Response.Headers.Allow.ToString(), Is.EqualTo("POST"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        });
    }

    public static IEnumerable<TestCaseData> UnreadableForms()
    {
        foreach (var action in new[] { "internalQuestClaim", "internalVoucherRedeem", "internalPaymentClaim", "internalSpaceSearchExplore" })
        foreach (var failure in new[] { typeof(InvalidOperationException), typeof(IOException), typeof(InvalidDataException) })
            yield return new TestCaseData(action, failure).SetName($"{action}_{failure.Name}_Returns400AndAppliesNothing");
    }

    [TestCaseSource(nameof(UnreadableForms))]
    public async Task UnreadableForm_Returns400WithoutEchoAndAppliesNothing(string action, Type failure)
    {
        var before = await Snapshot();
        var context = Build($"?action={action}", "");
        context.Request.ContentLength = null;
        context.Request.Body = new FailingStream((Exception)Activator.CreateInstance(failure, "Body unavailable.")!);
        await Handle(action, context);
        var after = await Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(ResponseText(context), Does.Contain("could not be read."));
            Assert.That(ResponseText(context), Does.Not.Contain("Body unavailable"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(after, Is.EqualTo(before));
        });
    }

    [TestCase("internalQuestClaim")]
    [TestCase("internalVoucherRedeem")]
    [TestCase("internalPaymentClaim")]
    [TestCase("internalSpaceSearchExplore")]
    public async Task MissingSession_Returns401BeforeReadingForm(string action)
    {
        var context = Build($"?action={action}", "", "missing-session");
        context.Request.Body = new FailingStream(new InvalidOperationException("Body already consumed."));
        await Handle(action, context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public void QueryValidators_AcceptOnlyTheCanonicalKey()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalVoucherAction.ValidRedeemQuery(Query("?action=internalVoucherRedeem")), Is.True);
            Assert.That(LocalVoucherAction.ValidRedeemQuery(Query("")), Is.True);
            Assert.That(LocalVoucherAction.ValidRedeemQuery(Query("?Action=internalVoucherRedeem")), Is.False);
            Assert.That(LocalPaymentAction.ValidClaimQuery(Query("?action=internalPaymentClaim")), Is.True);
            Assert.That(LocalPaymentAction.ValidClaimQuery(Query("?ACTION=internalPaymentClaim")), Is.False);
            Assert.That(LocalSpaceSearchAction.ValidExploreQuery(Query("?action=internalSpaceSearchExplore")), Is.True);
            Assert.That(LocalSpaceSearchAction.ValidExploreQuery(Query("?aCtIoN=internalSpaceSearchExplore")), Is.False);
        });
    }

    private Task Handle(string action, DefaultHttpContext context) => action switch
    {
        "internalQuestClaim" => LocalQuestAction.HandleAsync(context, _sessions, _equipment, new FixedClock(Now), CancellationToken.None),
        "internalVoucherRedeem" => LocalVoucherAction.HandleAsync(context, _sessions, _equipment, CancellationToken.None),
        "internalPaymentClaim" => LocalPaymentAction.HandleAsync(context, _sessions, _equipment, CancellationToken.None),
        "internalSpaceSearchExplore" => LocalSpaceSearchAction.HandleAsync(context, _sessions, _equipment, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private string Body(string action) => action switch
    {
        "internalQuestClaim" => $"quest=first_purchase&key={Uri.EscapeDataString(LocalQuestAction.CreateFormKey(_session.Dosid, "first_purchase", Now))}",
        "internalVoucherRedeem" => "code=PRACTICE-500",
        "internalPaymentClaim" => "package=starter",
        _ => "planet=mars",
    };

    private static string InvalidActionMessage(string action) => action switch
    {
        "internalQuestClaim" => "Invalid Quests action.",
        "internalVoucherRedeem" => "Invalid voucher action.",
        "internalPaymentClaim" => "Invalid payment action.",
        _ => "Invalid Space Search action.",
    };

    private async Task<string> Snapshot() =>
        JsonSerializer.Serialize(await _equipment.GetAsync(_session.AccountId, CancellationToken.None));

    private static IQueryCollection Query(string query) => new DefaultHttpContext { Request = { QueryString = new QueryString(query) } }.Request.Query;

    private DefaultHttpContext Build(string query, string body, string? dosid = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid ?? _session.Dosid}";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseText(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw failure;

        public override int Read(byte[] buffer, int offset, int count) => throw failure;
    }
}
