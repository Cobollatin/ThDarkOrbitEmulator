using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.World;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public sealed class WorldPilotResolverSpecificationTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase(" \t ")]
    public async Task BlankToken_IsRejectedBeforeSessionOrAccountAccess(string? token)
    {
        var sessions = new Sessions(ValidSession());
        var account = new AccountState();
        var (pilot, error) = await new WorldPilotResolver(sessions, account)
            .ResolveAsync(42, token!, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pilot, Is.Null);
            Assert.That(error, Is.EqualTo(WorldLoginError.UnknownSession));
            Assert.That(sessions.Calls, Is.Zero);
            Assert.That(account.GetCalls, Is.Zero);
            Assert.That(account.DetailCalls, Is.Zero);
        });
    }

    [TestCase(128, true)]
    [TestCase(129, false)]
    public async Task TokenLength_HasInclusive128CharacterLimit(int length, bool accepted)
    {
        var sessions = new Sessions(ValidSession());
        var account = new AccountState();
        var token = new string('x', length);
        var (pilot, error) = await new WorldPilotResolver(sessions, account)
            .ResolveAsync(42, token, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pilot is not null, Is.EqualTo(accepted));
            Assert.That(error, Is.EqualTo(accepted ? default(WorldLoginError) : WorldLoginError.UnknownSession));
            Assert.That(sessions.Calls, Is.EqualTo(accepted ? 1 : 0));
            Assert.That(sessions.LastToken, Is.EqualTo(accepted ? token : null));
            Assert.That(account.GetCalls, Is.EqualTo(accepted ? 1 : 0));
            Assert.That(account.DetailCalls, Is.EqualTo(accepted ? 1 : 0));
        });
    }

    [TestCase(-1L)]
    [TestCase(0L)]
    [TestCase(43L)]
    public async Task NonmatchingOrNonpositiveUser_IsRejectedBeforeAccountAccess(long userId)
    {
        var sessions = new Sessions(ValidSession());
        var account = new AccountState();
        var (pilot, error) = await new WorldPilotResolver(sessions, account)
            .ResolveAsync(userId, "valid-token", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pilot, Is.Null);
            Assert.That(error, Is.EqualTo(WorldLoginError.UserMismatch));
            Assert.That(sessions.Calls, Is.EqualTo(1));
            Assert.That(account.GetCalls, Is.Zero);
            Assert.That(account.DetailCalls, Is.Zero);
        });
    }

    [Test]
    public async Task ExpiredSession_PrecedesUserAndCompanyErrors()
    {
        var session = ValidSession();
        session = new GameAuthSession
        {
            AccountId = session.AccountId, GameUserId = session.GameUserId,
            Company = null, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var account = new AccountState();
        var (pilot, error) = await new WorldPilotResolver(new Sessions(session), account)
            .ResolveAsync(-1, "valid-token", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pilot, Is.Null);
            Assert.That(error, Is.EqualTo(WorldLoginError.ExpiredSession));
            Assert.That(account.GetCalls, Is.Zero);
            Assert.That(account.DetailCalls, Is.Zero);
        });
    }

    [Test]
    public async Task Success_ProjectsAccountAndDetailFieldsWithoutInventingValues()
    {
        var session = ValidSession();
        var account = new AccountState();
        var (pilot, error) = await new WorldPilotResolver(new Sessions(session), account)
            .ResolveAsync(42, "valid-token", CancellationToken.None);

        Assert.That(error, Is.EqualTo(default(WorldLoginError)));
        Assert.That(pilot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(pilot!.AccountId, Is.EqualTo(session.AccountId));
            Assert.That(pilot.UserName, Is.EqualTo(session.Username));
            Assert.That(pilot.IsAdmin, Is.EqualTo(session.IsAdmin));
            Assert.That(pilot.Company, Is.EqualTo("mmo"));
            Assert.That(pilot.MapId, Is.EqualTo(1));
            Assert.That(pilot.FactionId, Is.EqualTo(1));
            Assert.That(pilot.HangarSlot, Is.EqualTo(2));
            Assert.That(pilot.ShipId, Is.EqualTo("ship-2"));
            Assert.That(pilot.Credits, Is.EqualTo(123));
            Assert.That(pilot.Uridium, Is.EqualTo(456));
            Assert.That(pilot.Stats, Is.SameAs(account.Details.Stats));
            Assert.That(pilot.Experience, Is.EqualTo(789));
            Assert.That(pilot.Honor, Is.EqualTo(321));
            Assert.That(pilot.Level, Is.EqualTo(7));
            Assert.That(pilot.ConfigurationTwoSpeed, Is.EqualTo(465));
            Assert.That(pilot.ConfigurationTwoStats, Is.SameAs(account.Details.ConfigurationTwoStats));
            Assert.That(pilot.BaseSpeedAtLoad, Is.EqualTo(390));
            Assert.That(pilot.Companions, Is.SameAs(account.Details.Companions));
            Assert.That(pilot.ConfigurationTwoCompanions, Is.SameAs(account.Details.ConfigurationTwoCompanions));
            Assert.That(account.GetCalls, Is.EqualTo(1));
            Assert.That(account.DetailCalls, Is.EqualTo(1));
            Assert.That(account.DetailSlot, Is.EqualTo(2));
            Assert.That(account.DetailShipId, Is.EqualTo("ship-2"));
        });
    }

    private static GameAuthSession ValidSession() => new()
    {
        AccountId = Guid.NewGuid(), Username = "pilot", GameUserId = 42,
        Company = "mmo", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private sealed class AccountState : IWorldAccountState
    {
        public int GetCalls { get; private set; }
        public int DetailCalls { get; private set; }
        public int DetailSlot { get; private set; }
        public string? DetailShipId { get; private set; }
        public WorldAccountDetails Details { get; } = new(
            new WorldShipStats(390, 1000, 500, 50, 25, []), 789, 321, 7, 465,
            new WorldShipStats(465, 1200, 600, 60, 30, []), [], [], 390);

        public Task<(int Slot, string ShipId, long Credits, long Uridium)> GetAsync(
            GameAuthSession session, CancellationToken ct)
        {
            GetCalls++;
            return Task.FromResult((2, "ship-2", 123L, 456L));
        }

        public Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, CancellationToken ct)
        {
            DetailCalls++;
            DetailSlot = slot;
            return Task.FromResult<WorldAccountDetails?>(Details);
        }

        public Task<WorldAccountDetails?> DetailsAsync(GameAuthSession session, int slot, string shipId,
            CancellationToken ct)
        {
            DetailShipId = shipId;
            return DetailsAsync(session, slot, ct);
        }
    }

    private sealed class Sessions(GameAuthSession? session) : IGameAuthSessionStore
    {
        public int Calls { get; private set; }
        public string? LastToken { get; private set; }
        public Task<GameAuthSession?> TryGetByDosidAsync(string dosid, CancellationToken ct)
        {
            Calls++;
            LastToken = dosid;
            return Task.FromResult(session);
        }
        public Task<GameAuthSessionCreateResult> CreateSessionAsync(AccountSummary account,
            string? keycloakAccessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<GameAuthTokenRotationResult?> RotateRedirectTokenAsync(string redirectToken,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GameAuthSession?> TryGetByRedirectTokenAsync(string redirectToken,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SelectCompanyAsync(string dosid, string company,
            CancellationToken ct) => throw new NotSupportedException();
        public Task InvalidateAsync(string dosid, CancellationToken ct) => throw new NotSupportedException();
    }
}
