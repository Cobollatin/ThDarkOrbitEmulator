using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class Menus16To29AndEconomySpecificationTests
{
    private static IQueryCollection Query(string action, params (string Key, string Value)[] fields)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["action"] = action };
        foreach (var (key, value) in fields) values.Add(key, value);
        return new QueryCollection(values);
    }

    private static bool Accepts(int menu, IQueryCollection query) => menu switch
    {
        16 => LocalPaymentAction.ValidPageQuery(query),
        17 => LocalVoucherAction.ValidPageQuery(query),
        19 or 20 => LocalEventsAction.ValidPageQuery(query),
        24 => LocalLogbook.Validate(query) is null,
        26 => LocalAccountSettingsAction.ValidPageQuery(query),
        27 => LocalCommandCenter.Validate(query) is null,
        _ => throw new ArgumentOutOfRangeException(nameof(menu))
    };

    private static readonly (int Menu, string Action, (string Key, string Value)[] Fields)[] Pages =
    [
        (16, "internalPayment", [("filter", "all")]),
        (17, "internalVouchers", [("filter", "all")]),
        (19, "internalEvents", [("filter", "ready")]),
        (20, "internalEvents", [("expedition", "started")]),
        (24, "internalLogbook", [("page", "1")]),
        (26, "internalAccount", [("saved", "1")]),
        (27, "internalCommandCenter", [])
    ];

    [Test]
    public void ImplementedMenus_RecognizeCanonicalPageRoute_AndRejectUnknownQueryKeys()
    {
        foreach (var (menu, action, fields) in Pages)
        {
            Assert.That(Accepts(menu, Query(action, fields)), Is.True, $"menu {menu} canonical page");
            Assert.That(Accepts(menu, Query(action, [.. fields, ("unexpected", "1")])), Is.False,
                $"menu {menu} unknown field");
        }
    }

    [Test]
    public void ImplementedMenus_RejectNonCanonicalAction_DuplicateValues_AndControlCharacters()
    {
        foreach (var (menu, action, fields) in Pages)
        {
            Assert.That(Accepts(menu, Query(action.ToUpperInvariant(), fields)), Is.False, $"menu {menu} casing");
            var duplicate = new QueryCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
            {
                ["action"] = action, ["from"] = new StringValues(["a", "b"])
            });
            Assert.That(Accepts(menu, duplicate), Is.False, $"menu {menu} duplicate");
            Assert.That(Accepts(menu, Query(action, ("from", "a\u0001b"))), Is.False, $"menu {menu} control");
            Assert.That(Accepts(menu, Query(action, ("from", new string('a', 65)))), Is.False, $"menu {menu} overlong");
        }
    }

    [Test]
    public void PaymentMenu_ValidatesPackageIds_AndOnlyAcceptsCanonicalClaimQuery()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepts(16, Query("internalPayment", ("detail", "starter"))), Is.True);
            Assert.That(Accepts(16, Query("internalPayment", ("detail", "bad id"))), Is.False);
            Assert.That(Accepts(16, Query("internalPayment", ("claimed", new string('a', 33)))), Is.False);
            Assert.That(LocalPayment.IsWellFormed(new string('a', LocalPayment.MaxIdLength)), Is.True);
            Assert.That(LocalPaymentAction.ClaimedLocation("fleet"),
                Is.EqualTo("/indexInternal.es?action=internalPayment&claimed=fleet"));
            Assert.That(LocalPaymentAction.ValidClaimQuery(new QueryCollection()), Is.True);
            Assert.That(LocalPaymentAction.ValidClaimQuery(Query("internalPaymentClaim")), Is.True);
            Assert.That(LocalPaymentAction.ValidClaimQuery(Query("internalpaymentclaim")), Is.False);
            Assert.That(LocalPaymentAction.ValidClaimQuery(Query("internalPaymentClaim", ("x", "1"))), Is.False);
        });
    }

    [Test]
    public void PaymentCatalog_IsPracticeOnly_UniqueAndWithinGrantCap()
    {
        Assert.That(LocalPayment.Catalog.Select(package => package.Id), Is.Unique);
        Assert.That(LocalPayment.Catalog, Has.All.Matches<LocalPaymentPackage>(package =>
            package.Credits > 0 && package.Credits <= LocalPayment.MaxGrant && LocalPayment.IsWellFormed(package.Id)));
        Assert.That(LocalPayment.Packages(null).Select(view => view.AssetId), Has.All.EqualTo("credits"));
    }

    [Test]
    public void VoucherMenu_CodeShape_RedeemRedirect_AndNoticeDetailExclusion()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalVouchers.Catalog.Select(voucher => voucher.Code), Is.Unique);
            Assert.That(LocalVouchers.Catalog, Has.All.Matches<LocalVoucher>(voucher =>
                LocalVouchers.IsWellFormed(voucher.Code) && voucher.Credits is > 0 and <= (int)LocalVouchers.MaxGrant));
            Assert.That(LocalVouchers.IsWellFormed("TRAINING_500"), Is.False);
            Assert.That(LocalVouchers.IsWellFormed(new string('A', LocalVouchers.MaxCodeLength + 1)), Is.False);
            Assert.That(LocalVoucherAction.RedeemedLocation("HANGAR-750"),
                Is.EqualTo("/indexInternal.es?action=internalVouchers&redeemed=HANGAR-750"));
            Assert.That(LocalVoucherAction.RedeemedLocation("bad code"), Is.EqualTo("/indexInternal.es?action=internalVouchers"));
            Assert.That(Accepts(17, Query("internalVouchers", ("redeemed", "HANGAR-750"), ("voucher", "HANGAR-750"))), Is.False);
            Assert.That(LocalVoucherAction.ValidRedeemQuery(Query("internalVoucherRedeem")), Is.True);
            Assert.That(LocalVoucherAction.ValidRedeemQuery(Query("INTERNALVOUCHERREDEEM")), Is.False);
            Assert.That(LocalVouchers.IsKnownFilter("archived"), Is.False);
            Assert.That(LocalVouchers.IsWellFormedSearch(" HANGAR"), Is.False);
        });
    }

    [Test]
    public void BattlePassAndDispatch_ShareEventsRoute_WithExclusiveNotices()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepts(19, Query("internalEvents", ("claimed", "supplier"))), Is.True);
            Assert.That(Accepts(19, Query("internalEvents", ("claimed", "supplier"), ("expedition", "claimed"))), Is.False);
            Assert.That(Accepts(20, Query("internalEvents", ("expedition", "finished"))), Is.False);
            Assert.That(Accepts(19, Query("internalEvents", ("filter", "Ready"))), Is.False);
            Assert.That(Accepts(19, Query("internalEvents", ("q", "a"))), Is.False);
            Assert.That(Accepts(19, Query("internalEvents", ("q", " "))), Is.True);
            Assert.That(LocalEventsAction.SuccessLocation("internalBattlePassClaim", "engineer"),
                Is.EqualTo("/indexInternal.es?action=internalEvents&claimed=engineer"));
            Assert.That(LocalEventsAction.SuccessLocation("internalExpeditionStart", ""),
                Is.EqualTo("/indexInternal.es?action=internalEvents&expedition=started"));
            Assert.That(LocalEventsAction.SuccessLocation("internalExpeditionClaim", ""),
                Is.EqualTo("/indexInternal.es?action=internalEvents&expedition=claimed"));
            Assert.That(LocalEventsAction.ValidActionQuery(Query("internalBattlePassClaim", ("tier", "x"))), Is.False);
        });
    }

    [TestCase(false, false, 0, 1, "unavailable")]
    [TestCase(true, false, 0, 1, "in-progress")]
    [TestCase(true, false, 1, 1, "ready")]
    [TestCase(true, true, 1, 1, "claimed")]
    [TestCase(false, true, 0, 3, "claimed")]
    public void BattlePassTierStatus_TransitionsAndClaimedIsTerminal(bool enabled, bool claimed, long progress, long target,
        string expected)
    {
        Assert.That(LocalEvents.TierStatus(enabled, claimed, progress, target), Is.EqualTo(expected));
        Assert.That(LocalEvents.Filters.Select(filter => filter.Id), Does.Contain(expected));
    }

    [Test]
    public void BattlePassTiers_AreUniquePositive_AndDispatchCountdownNeverShowsZero()
    {
        Assert.That(LocalEvents.Tiers.Select(tier => tier.Id), Is.Unique);
        Assert.That(LocalEvents.Tiers, Has.All.Matches<LocalBattlePassTier>(tier =>
            tier.Target > 0 && tier.RewardCredits > 0 && LocalEvents.IsTierIdShape(tier.Id)));
        Assert.That(LocalEvents.HasTier("tycoon"), Is.True);
        Assert.That(LocalEvents.HasTier("ghost"), Is.False);
        var now = DateTimeOffset.UnixEpoch;
        Assert.That(LocalEvents.MinutesLeft(now.AddSeconds(1), now), Is.EqualTo(1));
        Assert.That(LocalEvents.MinutesLeft(now, now), Is.EqualTo(1));
        Assert.That(LocalEvents.MinutesLeft(now.AddSeconds(61), now), Is.EqualTo(2));
        Assert.That(LocalEvents.ExpeditionCost, Is.GreaterThan(0));
        Assert.That(LocalEvents.ExpeditionMinutes, Is.GreaterThan(0));
    }

    [Test]
    public void LogbookMenu_RejectsMalformedPagesSearchesAndDemoAssetFilter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("page", "0")))?.Status, Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("page", "")))?.Status, Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("q", "a")))?.Status, Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("q", "ok ")))?.Status, Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("category", "")))?.Status, Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("category", new string('a', 17))))?.Status,
                Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("source", "demo"), ("asset", "credits")))?.Status,
                Is.EqualTo(400));
            Assert.That(LocalLogbook.Validate(Query("internalLogbook", ("entry", "!")))?.Status, Is.EqualTo(400));
            Assert.That(LocalLogbook.PageSize, Is.GreaterThan(0));
            Assert.That(LocalLogbook.LedgerLimit, Is.GreaterThanOrEqualTo(LocalLogbook.PageSize));
        });
    }

    [TestCase("1", true)]
    [TestCase("1000000", true)]
    [TestCase("1000001", false)]
    [TestCase("0", true)]
    [TestCase("+1", false)]
    [TestCase(" 1", false)]
    [TestCase("12345678", false)]
    [TestCase("", false)]
    public void AccountSettingsRevision_UsesCanonicalBoundedDigits(string text, bool expected)
    {
        Assert.That(LocalAccountSettingsAction.TryParseRevision(text, out _), Is.EqualTo(expected));
    }

    [Test]
    public void AccountSettings_SavedAndUnchangedNoticesAreExclusive_AndSaveQueryIsCanonical()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepts(26, Query("internalAccount", ("saved", "0"))), Is.False);
            Assert.That(Accepts(26, Query("internalAccount", ("unchanged", "1"))), Is.True);
            Assert.That(Accepts(26, Query("internalAccount", ("unchanged", "2"))), Is.False);
            Assert.That(Accepts(26, Query("internalAccount", ("saved", "3"), ("unchanged", "1"))), Is.False);
            Assert.That(LocalAccountSettingsAction.SavedRevision(Query("internalAccount", ("saved", "4"))), Is.EqualTo(4));
            Assert.That(LocalAccountSettingsAction.SavedLocation(7), Is.EqualTo("/indexInternal.es?action=internalAccount&saved=7"));
            Assert.That(LocalAccountSettingsAction.ValidSaveQuery(Query("internalAccountSave")), Is.True);
            Assert.That(LocalAccountSettingsAction.ValidSaveQuery(Query("internalaccountsave")), Is.False);
            Assert.That(LocalAccountPreferencesStore.Languages.Select(language => language.Code),
                Does.Contain(LocalAccountPreferencesStore.Default.Language));
            Assert.That(LocalAccountPreferencesStore.Languages.Select(language => language.Code), Is.Unique);
        });
    }

    [Test]
    public void CommandCenterNews_ValidatesSortUrgencyAndCapturedSelections()
    {
        Assert.Multiple(() =>
        {
            foreach (var sort in LocalCommandCenter.SortOrders)
                Assert.That(Accepts(27, Query("internalCommandCenter", ("sort", sort))), Is.True, sort);
            foreach (var urgency in LocalCommandCenter.UrgencyGroups)
                Assert.That(Accepts(27, Query("internalCommandCenter", ("urgency", urgency))), Is.True, urgency);
            Assert.That(Accepts(27, Query("internalCommandCenter", ("sort", "random"))), Is.False);
            Assert.That(LocalCommandCenter.IsSelection(null, null), Is.True);
            Assert.That(LocalCommandCenter.IsItem("no-such-captured-item"), Is.False);
            Assert.That(LocalCommandCenter.Unauthorized().Status, Is.EqualTo(401));
        });
    }

    [Test]
    public void Logout_ExternalStepExpiresCookie_AndInternalStepIsTheSignedOutRedirect()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalLogoutAction.IsLogout("externalLogout"), Is.True);
            Assert.That(LocalLogoutAction.IsLogout("EXTERNALLOGOUT"), Is.True);
            Assert.That(LocalLogoutAction.IsLogout("internalLogout"), Is.False);
            Assert.That(LocalLogoutAction.IsSignedOutStep("internalWebGL", "externalLogout"), Is.True);
            Assert.That(LocalLogoutAction.IsSignedOutStep("internalWebGL", "internalStart"), Is.False);
            Assert.That(LocalLogoutAction.SuppressesSessionCookie("internalWebGL", "externalLogout"), Is.True);
            Assert.That(LocalLogoutAction.SuppressesSessionCookie("internalStart", null), Is.False);
            Assert.That(LocalLogoutAction.LogoutLocation, Does.EndWith("from=externalLogout"));
            Assert.That(LocalLogoutAction.ExpiredCookie, Does.StartWith("dosid=;").And.Contain("1970").And.Contain("HttpOnly"));
        });
    }

    [Test]
    public async Task Logout_MissingOrOverlongCookie_StillExpiresCookieWithoutSessionLookup()
    {
        foreach (var dosid in new[] { null, "", new string('a', LocalLogoutAction.MaxDosidLength + 1) })
        {
            var context = new DefaultHttpContext();
            await LocalLogoutAction.InvalidateAsync(context.Response, dosid, null!, CancellationToken.None);
            Assert.That(context.Response.Headers.SetCookie.ToString(), Is.EqualTo(LocalLogoutAction.ExpiredCookie));
        }

        var signedOut = new DefaultHttpContext();
        LocalLogoutAction.SignedOut(signedOut.Response);
        Assert.That(signedOut.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
    }

    [Test]
    public void EconomyResults_ExposeIdempotentDuplicateOutcome_AndClosedFailureSets()
    {
        Assert.That(Enum.GetNames<EconomyPurchaseResult>(),
            Is.EquivalentTo(new[] { "Applied", "Duplicate", "Invalid", "Unavailable", "InsufficientFunds" }));
        Assert.That(Enum.GetNames<EconomyCraftResult>(),
            Is.EquivalentTo(new[] { "Applied", "Duplicate", "Invalid", "NotFound", "Unavailable", "InsufficientInputs" }));
        Assert.That(Enum.GetNames<LocalEventResult>(), Does.Contain("AlreadyClaimed").And.Contain("InsufficientCredits"));
        Assert.That(LocalPayment.MaxGrant, Is.EqualTo(LocalVouchers.MaxGrant));
    }

    [TestCase("internalDailyLogin")]
    [TestCase("internalDispatch")]
    [TestCase("internalEternalBlacklight")]
    [TestCase("internalAstralAscension")]
    [TestCase("internalChrominRush")]
    [TestCase("internalShipWarp")]
    public void UnimplementedMenus_HaveNoValidatorThatAcceptsTheirAction(string action)
    {
        foreach (var menu in new[] { 16, 17, 19, 24, 26, 27 })
            Assert.That(Accepts(menu, Query(action)), Is.False, $"{action} via menu {menu}");
    }

    private static LocalEquipmentState State(int credits = 10000) => new([], []) { Credits = credits };

    private static EconomyRewardOffer Offer(string source, string id, long amount, bool enabled = true,
        string assetId = "credits", string? name = null) =>
        new(source, id, name ?? id, assetId, amount, "", 0, enabled);

    [Test]
    public void PaymentPackages_BuiltInCatalogIsEnabledCredits_AndAnyOfferListReplacesIt()
    {
        var builtIn = LocalPayment.Packages(null);
        Assert.Multiple(() =>
        {
            Assert.That(builtIn.Select(package => package.Id), Is.EqualTo(LocalPayment.Catalog.Select(package => package.Id)));
            Assert.That(builtIn.All(package => package.Enabled && package.AssetId == "credits"), Is.True);
            Assert.That(LocalPayment.Packages([]), Is.Empty, "an empty offer list is authoritative, not a fallback");
            var offers = LocalPayment.Packages([Offer("payment", "uri-pack", 50, enabled: false, assetId: "uridium")]);
            Assert.That(offers.Single(), Is.EqualTo(new LocalPayment.PackageView("uri-pack", "uri-pack", 50, "uridium", false)));
        });
    }

    [Test]
    public void PaymentFind_IsOrdinalExact_AndAssetLabelsOnlyKnownCurrencies()
    {
        var catalog = LocalPayment.Packages(null);
        Assert.Multiple(() =>
        {
            Assert.That(LocalPayment.Find(catalog, "fleet")?.Id, Is.EqualTo("fleet"));
            Assert.That(LocalPayment.Find(catalog, "Fleet"), Is.Null);
            Assert.That(LocalPayment.Find(catalog, " fleet"), Is.Null);
            Assert.That(LocalPayment.Find(catalog, null), Is.Null);
            Assert.That(LocalPayment.AssetLabel("credits"), Is.EqualTo("Credits"));
            Assert.That(LocalPayment.AssetLabel("uridium"), Is.EqualTo("Uridium"));
            Assert.That(LocalPayment.AssetLabel("xenomit"), Is.EqualTo("xenomit"));
        });
    }

    [Test]
    public void PaymentStatus_ClaimedOutranksDisabled_AndFiltersPartitionTheCatalog()
    {
        var state = State();
        state.ClaimedPaymentPackages.Add("a");
        IReadOnlyList<LocalPayment.PackageView> catalog =
        [
            new("a", "Alpha", 10, "credits", false),
            new("b", "Beta", 20, "credits", true),
            new("c", "Gamma", 30, "uridium", false)
        ];
        Assert.Multiple(() =>
        {
            Assert.That(catalog.Select(package => LocalPayment.Status(state, package)),
                Is.EqualTo(new[] { "claimed", "available", "unavailable" }));
            var partitioned = LocalPayment.Filters.Where(filter => filter != "all")
                .SelectMany(filter => LocalPayment.Visible(state, catalog, filter)).Select(package => package.Id);
            Assert.That(partitioned, Is.EquivalentTo(LocalPayment.Visible(state, catalog, "all").Select(package => package.Id)));
        });
    }

    [Test]
    public void PaymentFilterShape_IsSeparateFromMembership_AndSearchHasNoEdgeSpaces()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalPayment.IsWellFormedFilter("pending"), Is.True);
            Assert.That(LocalPayment.IsKnownFilter("pending"), Is.False);
            Assert.That(LocalPayment.IsWellFormedFilter("Claimed"), Is.False);
            Assert.That(LocalPayment.IsWellFormedFilter(new string('a', 17)), Is.False);
            Assert.That(LocalPayment.IsKnownFilter("Claimed"), Is.False);
            Assert.That(LocalPayment.IsWellFormedSearch("ar"), Is.True);
            Assert.That(LocalPayment.IsWellFormedSearch("a"), Is.False);
            Assert.That(LocalPayment.IsWellFormedSearch(" ar"), Is.False);
            Assert.That(LocalPayment.IsWellFormedSearch("ar "), Is.False);
            Assert.That(LocalPayment.IsWellFormedSearch("hangar_supply-2"), Is.True);
            Assert.That(LocalPayment.IsWellFormedSearch(new string('a', 41)), Is.False);
            Assert.That(LocalPayment.IsWellFormedSearch("a.b"), Is.False);
        });
    }

    [Test]
    public void PaymentSearch_MatchesIdNameOrAssetIgnoringCase_AndDetailNeverCrossesFilters()
    {
        var state = State();
        state.ClaimedPaymentPackages.Add("fleet");
        var catalog = LocalPayment.Packages(null);
        Assert.Multiple(() =>
        {
            Assert.That(LocalPayment.Visible(state, catalog, "all", "ARMADA").Select(package => package.Id), Is.EqualTo(new[] { "armada" }));
            Assert.That(LocalPayment.Visible(state, catalog, "all", "squadron supply").Single().Id, Is.EqualTo("squadron"));
            Assert.That(LocalPayment.Visible(state, catalog, "all", "CREDITS"), Has.Count.EqualTo(catalog.Count));
            Assert.That(LocalPayment.HasDetail(state, catalog, "claimed", "fleet"), Is.True);
            Assert.That(LocalPayment.HasDetail(state, catalog, "available", "fleet"), Is.False);
            Assert.That(LocalPayment.HasDetail(state, catalog, "pending", "fleet"), Is.False);
            Assert.That(LocalPayment.HasDetail(state, catalog, "all", "fleet", "armada"), Is.False);
        });
    }

    [Test]
    public void PaymentClaimedNotice_RequiresAClaimOnThisAccount_AndShowsBalanceOnlyForCredits()
    {
        var state = State(1234);
        state.ClaimedPaymentPackages.Add("u");
        state.ClaimedPaymentPackages.Add("starter");
        IReadOnlyList<LocalPayment.PackageView> catalog =
            [new("starter", "Starter supply", 1000, "credits", true), new("u", "Uri", 5, "uridium", true), new("x", "X", 1, "credits", true)];
        Assert.Multiple(() =>
        {
            Assert.That(LocalPayment.ClaimedNotice(state, catalog, "x", 1234), Is.Empty, "unclaimed package");
            Assert.That(LocalPayment.ClaimedNotice(state, catalog, "missing", 1234), Is.Empty, "unknown package");
            Assert.That(LocalPayment.ClaimedNotice(state, catalog, "Starter", 1234), Is.Empty, "case mismatch");
            Assert.That(LocalPayment.ClaimedNotice(state, catalog, null, 1234), Is.Empty);
            Assert.That(LocalPayment.ClaimedNotice(state, catalog, "starter", 1234),
                Does.Contain("1,000 Credits added").And.Contain("Balance: 1,234 local credits."));
            Assert.That(LocalPayment.ClaimedNotice(state, catalog, "u", 1234),
                Does.Contain("5 Uridium added").And.Not.Contain("Balance:"));
        });
    }

    [TestCase(0L, 10000, true, "credits", "unavailable")]
    [TestCase(-1L, 10000, true, "credits", "unavailable")]
    [TestCase(1L, 10000, true, "credits", "available")]
    [TestCase(LocalVouchers.MaxGrant, 10000, true, "credits", "available")]
    [TestCase(LocalVouchers.MaxGrant + 1, 10000, true, "credits", "unavailable")]
    [TestCase(500L, 10000, false, "credits", "unavailable")]
    [TestCase(500L, int.MaxValue - 500, true, "credits", "available")]
    [TestCase(500L, int.MaxValue - 499, true, "credits", "balance-limit")]
    [TestCase(500L, int.MaxValue, true, "uridium", "available")]
    public void VoucherStatus_EnforcesGrantCapAndInt32CreditCeiling(long amount, int credits, bool enabled, string asset,
        string expected) =>
        Assert.That(LocalVouchers.Status(State(credits), "CODE", amount, enabled, asset), Is.EqualTo(expected));

    [Test]
    public void VoucherStatus_RedeemedIsTerminal_EvenWhenDisabledOrOverCap()
    {
        var state = State(int.MaxValue);
        state.RedeemedVoucherCodes.Add("CODE");
        Assert.Multiple(() =>
        {
            Assert.That(LocalVouchers.Status(state, "CODE", LocalVouchers.MaxGrant + 1, false), Is.EqualTo("redeemed"));
            Assert.That(LocalVouchers.Status(state, "code", 500, true), Is.EqualTo("balance-limit"), "codes are ordinal");
        });
    }

    [Test]
    public void VoucherDetail_UnavailableFilterIncludesBalanceLimit_AndUnknownOrMiscasedCodesAreNull()
    {
        var state = State(int.MaxValue - 600);
        IReadOnlyList<EconomyRewardOffer> offers = [Offer("voucher", "SMALL", 500), Offer("voucher", "BIG", 700)];
        Assert.Multiple(() =>
        {
            Assert.That(LocalVouchers.DetailPage(state, offers, "unavailable", "BIG"), Does.Contain("data-status=\"balance-limit\""));
            Assert.That(LocalVouchers.DetailPage(state, offers, "unavailable", "SMALL"), Is.Null);
            Assert.That(LocalVouchers.DetailPage(state, offers, "available", "SMALL"), Does.Contain("Code 1 of 1 in available."));
            Assert.That(LocalVouchers.DetailPage(state, offers, "all", "big"), Is.Null);
            Assert.That(LocalVouchers.DetailPage(state, offers, "all", "NONE"), Is.Null);
            Assert.That(LocalVouchers.DetailPage(state, offers, "bogus", "BIG"), Does.Contain("data-filter=\"all\""));
            Assert.That(LocalVouchers.DetailPage(state, offers, "all", "BIG", "SMALL"), Is.Null, "search narrows detail");
        });
    }

    [Test]
    public void VoucherRedeemedNotice_IsNullForUnknown_NeutralForUnredeemed_AndConfirmsOnlyRedeemedCodes()
    {
        var state = State(2500);
        state.RedeemedVoucherCodes.Add("TRAINING-500");
        Assert.Multiple(() =>
        {
            Assert.That(LocalVouchers.RedeemedNotice(state, null, "NOPE"), Is.Null);
            Assert.That(LocalVouchers.RedeemedNotice(state, null, "training-500"), Is.Null);
            Assert.That(LocalVouchers.RedeemedNotice(state, null, "HANGAR-750"),
                Does.Contain("data-redeemed=\"false\"").And.Not.Contain("+750"));
            Assert.That(LocalVouchers.RedeemedNotice(state, null, "TRAINING-500"),
                Does.Contain("data-redeemed=\"true\"").And.Contain("+500 credits.").And.Contain("Balance: 2,500 local credits."));
            Assert.That(LocalVouchers.RedeemedNotice(state, [Offer("voucher", "TRAINING-500", 9, assetId: "uridium")], "TRAINING-500"),
                Does.Contain("+9 uridium.").And.Not.Contain("Balance:"));
        });
    }

    [Test]
    public void VoucherSearch_IsTrimmedOneTo32_AndCodeShapeRejectsUnderscore()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalVouchers.IsWellFormedSearch("a"), Is.True);
            Assert.That(LocalVouchers.IsWellFormedSearch("hangar 750"), Is.True);
            Assert.That(LocalVouchers.IsWellFormedSearch(" a"), Is.False);
            Assert.That(LocalVouchers.IsWellFormedSearch(new string('a', 33)), Is.False);
            Assert.That(LocalVouchers.IsWellFormedSearch("a_b"), Is.False);
            Assert.That(LocalVouchers.IsWellFormed("A_B"), Is.False);
            Assert.That(LocalVouchers.IsWellFormed("A B"), Is.False);
            Assert.That(LocalVouchers.IsWellFormed(new string('A', LocalVouchers.MaxCodeLength)), Is.True);
            Assert.That(LocalVouchers.IsWellFormed(""), Is.False);
            Assert.That(LocalVouchers.Catalog.Select(voucher => voucher.Code), Is.Unique);
            Assert.That(LocalVouchers.Catalog.All(voucher => LocalVouchers.IsWellFormed(voucher.Code)), Is.True);
        });
    }

    // Unity API candidate events (spaceSearch.php / ssRewards.php): query bounds, error precedence and headers.
    private static IQueryCollection CandidateQuery(params (string Key, string[] Values)[] fields) =>
        new QueryCollection(fields.ToDictionary(field => field.Key, field => new StringValues(field.Values), StringComparer.Ordinal));

    [TestCase(1, true)]
    [TestCase(SpaceSearchCandidateAction.MaxQueryValueLength, true)]
    [TestCase(SpaceSearchCandidateAction.MaxQueryValueLength + 1, false)]
    public void CandidateQuery_KeyLengthIsBoundedInclusively(int length, bool expected) =>
        Assert.That(SpaceSearchCandidateAction.ValidQuery(CandidateQuery((new string('k', length), ["v"]))), Is.EqualTo(expected));

    [TestCase(0, true)]
    [TestCase(SpaceSearchCandidateAction.MaxQueryValueLength, true)]
    [TestCase(SpaceSearchCandidateAction.MaxQueryValueLength + 1, false)]
    public void CandidateQuery_ValueLengthIsBoundedInclusively(int length, bool expected) =>
        Assert.That(SpaceSearchCandidateAction.ValidQuery(CandidateQuery(("k", [new string('v', length)]))), Is.EqualTo(expected));

    [Test]
    public void CandidateQuery_RejectsRepeatsAndControlCharactersButAllowsEmptyQuery()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpaceSearchCandidateAction.ValidQuery(QueryCollection.Empty), Is.True);
            Assert.That(SpaceSearchCandidateAction.ValidQuery(CandidateQuery(("planet", ["mars", "venus"]))), Is.False);
            Assert.That(SpaceSearchCandidateAction.ValidQuery(CandidateQuery(("pla\u0001net", ["mars"]))), Is.False);
            Assert.That(SpaceSearchCandidateAction.ValidQuery(CandidateQuery(("planet", ["ma\nrs"]))), Is.False);
            Assert.That(SpaceSearchCandidateAction.ValidQuery(CandidateQuery(("a", ["1"]), ("b", ["2"]))), Is.True);
        });
    }

    [TestCase("?planet=Mars", 400, "The planet value is invalid.")]
    [TestCase("?planet=", 400, "The planet value is invalid.")]
    [TestCase("?planet=mars1", 400, "The planet value is invalid.")]
    [TestCase("?planet=pluto", 404, "That planet is not part of Space Search.")]
    [TestCase("?planet=mars&planet=venus", 400, "Invalid Space Search reward request.")]
    public async Task CandidateRewards_ShapeErrorsPrecedeCatalogLookupAndNeverEchoTheValue(string query, int status, string error)
    {
        var (context, body) = await RunCandidateAsync(query, SpaceSearchCandidateAction.HandleRewardsAsync);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(status));
            Assert.That(body.AsObject().Select(field => field.Key), Is.EqualTo(new[] { "error", "status" }));
            Assert.That(body["error"]!.GetValue<string>(), Is.EqualTo(error));
            Assert.That(body["status"]!.GetValue<int>(), Is.EqualTo(status));
            Assert.That(body.ToJsonString(), Does.Not.Contain("pluto").And.Not.Contain("Mars"));
        });
    }

    [Test]
    public async Task CandidateSearch_UnknownDosidIsAnonymousAndResponsesAreUncachedJson()
    {
        var (context, body) = await RunCandidateAsync("", SpaceSearchCandidateAction.HandleSearchAsync, "dosid=unknown");
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(200));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/json"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(body["planets"]!.AsArray().Select(item => item!.GetValue<string>()),
                Is.EqualTo(LocalSpaceSearch.Catalog.Select(planet => planet.Id)));
            Assert.That(body["events"]!.AsArray().Select(item => item!.GetValue<string>()),
                Is.EqualTo(new[] { SpaceSearchCandidateAction.EventId }));
        });
    }

    [Test]
    public async Task CandidateSearch_InvalidQueryIsA400WithFixedTextAndTheSameHeaders()
    {
        var (context, body) = await RunCandidateAsync("?q=" + new string('x', 65), SpaceSearchCandidateAction.HandleSearchAsync);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
            Assert.That(body["error"]!.GetValue<string>(), Is.EqualTo("Invalid Space Search request."));
        });
    }

    [Test]
    public void CandidateReward_AnonymousWithoutPlanetDescribesTheFirstCatalogPlanet()
    {
        var reward = SpaceSearchCandidateAction.Reward(null, null);
        var first = LocalSpaceSearch.Catalog[0];
        Assert.Multiple(() =>
        {
            Assert.That(reward.Name, Is.EqualTo(first.Name));
            Assert.That(reward.Item, Is.EqualTo(first.RewardItemId));
            Assert.That(reward.Title, Is.EqualTo(first.RewardName));
            Assert.That(reward.Package, Is.EqualTo(SpaceSearchCandidateAction.EventId));
            Assert.That(reward.Special, Is.False);
            Assert.That(reward.Time, Is.Zero);
        });
    }

    [Test]
    public void CandidateSearch_AnonymousPairsPlanetsWithRewardsInCatalogOrder()
    {
        var response = SpaceSearchCandidateAction.Search(null);
        Assert.Multiple(() =>
        {
            Assert.That(response.Planets, Is.EqualTo(LocalSpaceSearch.Catalog.Select(planet => planet.Id)));
            Assert.That(response.Rewards, Is.EqualTo(LocalSpaceSearch.Catalog.Select(planet => planet.RewardItemId)));
        });
    }

    private static async Task<(HttpContext Context, JsonNode Body)> RunCandidateAsync(string query,
        Func<HttpContext, IGameAuthSessionStore, LocalHangarEquipmentStore, CancellationToken, Task> handler,
        string? cookie = null)
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString(query);
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        context.Response.Body = new MemoryStream();
        await handler(context, new GameAuthSessionStore(cache), new LocalHangarEquipmentStore(cache), CancellationToken.None);
        context.Response.Body.Position = 0;
        return (context, JsonNode.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync())!);
    }
}
