using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Primitives;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class Menus1To15SpecificationTests
{
    private static IQueryCollection Query(string action, params (string Key, string Value)[] fields)
    {
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["action"] = action };
        foreach (var (key, value) in fields) values.Add(key, value);
        return new QueryCollection(values);
    }

    private static bool Accepts(int menu, IQueryCollection query) => menu switch
    {
        1 => LocalHangars.ValidPageQuery(query),
        2 => LocalHangarEquipmentSelection.TryParsePageQuery(query, out _, out _),
        3 => LocalShopPurchase.ValidPageQuery(query),
        4 => LocalClanAction.ValidPageQuery(query),
        5 or 6 => LocalPilotSkillAction.ValidPageQuery(query),
        7 => LocalTechFactoryAction.ValidPageQuery(query),
        8 => LocalSkylabAction.ValidPageQuery(query),
        9 => LocalGalaxyGateAction.ValidPageQuery(query),
        10 => LocalAssemblyAction.ValidPageQuery(query),
        11 => LocalAuctionAction.ValidPageQuery(query),
        12 => LocalQuestAction.ValidPageQuery(query),
        13 or 14 => LocalMessagingAction.ValidPageQuery(query),
        15 => LocalRankings.Validate(query) is null,
        _ => throw new ArgumentOutOfRangeException(nameof(menu))
    };

    private static readonly (int Menu, string Action, (string Key, string Value)[] Fields)[] Pages =
    [
        (1, "internalDock", []),
        (2, "internalDockEquipment", [("slot", "1"), ("configuration", "2")]),
        (3, "internalShop", []),
        (4, "internalClan", []),
        (5, "internalPilotSheet", []),
        (6, "internalPilotSheet", [("filter", "all")]),
        (7, "internalTechFactory", [("filter", "all")]),
        (8, "internalSkylab", []),
        (9, "internalGalaxyGates", []),
        (10, "internalAssembly", []),
        (11, "internalAuction", []),
        (12, "internalQuests", []),
        (13, "internalMessages", [("folder", "inbox")]),
        (14, "internalMessages", [("folder", "all")]),
        (15, "internalHallofFame", [("page", "1")])
    ];

    [Test]
    public void AllFifteenMenus_RecognizeCanonicalPageRoute_AndRejectUnknownQueryKeys()
    {
        foreach (var (menu, action, fields) in Pages)
        {
            Assert.That(Accepts(menu, Query(action, fields)), Is.True, $"menu {menu} canonical page");
            Assert.That(Accepts(menu, Query(action, [.. fields, ("unexpected", "1")])), Is.False,
                $"menu {menu} unknown field");
        }
    }

    [Test]
    public void AllFifteenMenus_RejectNonCanonicalAction()
    {
        foreach (var (menu, action, fields) in Pages)
            Assert.That(Accepts(menu, Query(action.ToUpperInvariant(), fields)), Is.False, $"menu {menu}");
    }

    [Test]
    public void PageBoundaries_RejectMalformedIdentifiers_AndConflictingViews()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepts(1, Query("internalDock", ("ship", "3"))), Is.False);
            Assert.That(Accepts(2, Query("internalDockEquipment", ("slot", "01"), ("configuration", "1"))), Is.False);
            Assert.That(Accepts(3, Query("internalShop", ("q", "a"))), Is.False);
            Assert.That(Accepts(4, Query("internalClan", ("joined", "mmo-guard"), ("left", "mmo-guard"))), Is.False);
            Assert.That(Accepts(5, Query("internalPilotSheet", ("trained", "x"), ("reset", "1"))), Is.False);
            Assert.That(Accepts(7, Query("internalTechFactory", ("filter", "invalid"))), Is.False);
            Assert.That(Accepts(8, Query("internalSkylab", ("collected", "2"))), Is.False);
            Assert.That(Accepts(9, Query("internalGalaxyGates", ("q", "!"))), Is.False);
            Assert.That(Accepts(10, Query("internalAssembly", ("status", "invalid"))), Is.False);
            Assert.That(Accepts(11, Query("internalAuction", ("placed", "!"))), Is.False);
            Assert.That(Accepts(12, Query("internalQuests", ("detail", ""))), Is.False);
            Assert.That(Accepts(13, Query("internalMessages", ("message", "01"))), Is.False);
            Assert.That(Accepts(14, Query("internalMessages", ("folder", "archive"))), Is.False);
            Assert.That(Accepts(15, Query("internalHallofFame", ("page", "0001"))), Is.False);
        });
    }

    [TestCase("1", true)]
    [TestCase("999999999", true)]
    [TestCase("0", false)]
    [TestCase("01", false)]
    [TestCase("+1", false)]
    [TestCase("1000000000", false)]
    public void MessageAndContactIds_UseCanonicalPositiveNineDigitSyntax(string id, bool expected)
    {
        Assert.That(LocalMessagingAction.TryParseId(id, out _), Is.EqualTo(expected));
    }

    [Test]
    public async Task HangarAndClanChanges_PersistAcrossStoreInstances_AndAreAccountScoped()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.That(await new LocalHangarStore(cache).SelectAsync(first, 2, CancellationToken.None), Is.True);
        Assert.That(await new LocalHangarStore(cache).GetActiveSlotAsync(first, CancellationToken.None), Is.EqualTo(2));
        Assert.That(await new LocalHangarStore(cache).GetActiveSlotAsync(second, CancellationToken.None), Is.EqualTo(1));
        Assert.That(await new LocalClanStore(cache).JoinAsync(first, "mmo", "mmo-guard", CancellationToken.None), Is.True);
        Assert.That(await new LocalClanStore(cache).GetMembershipAsync(first, CancellationToken.None), Is.EqualTo("mmo-guard"));
        Assert.That(await new LocalClanStore(cache).GetMembershipAsync(second, CancellationToken.None), Is.Null);
        Assert.That(await new LocalClanStore(cache).JoinAsync(first, "mmo", "eic-vanguard", CancellationToken.None), Is.False);
    }

    [Test]
    public void Rankings_AreDeterministicAndReadOnly()
    {
        var category = LocalRankings.Categories[0];
        var first = LocalRankings.Board(category, null);
        var second = LocalRankings.Board(category, null);
        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Has.Count.EqualTo(LocalRankings.PilotsPerCompany * 3));
        Assert.That(first.Select(entry => entry.Position), Is.EqualTo(Enumerable.Range(1, first.Count)));
        Assert.That(LocalRankings.Validate(Query("internalHallofFame", ("company", "tst")))?.Status, Is.EqualTo(400));
    }

    [TestCase("1", true)]
    [TestCase("2", true)]
    [TestCase("0", false)]
    [TestCase("3", false)]
    [TestCase("01", false)]
    [TestCase(" 1", false)]
    [TestCase("+1", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void HangarSlot_AcceptsOnlyCanonicalOneOrTwo(string? value, bool expected)
    {
        Assert.That(LocalHangars.TryParseSlot(value, out var slot), Is.EqualTo(expected));
        Assert.That(slot, Is.EqualTo(expected ? int.Parse(value!) : 0));
    }

    [Test]
    public void HangarFormKey_BindsSessionRequestedSlotAndCurrentSlot()
    {
        var issued = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var key = LocalHangars.CreateFormKey("dosid-a", 2, 1, issued);
        var parts = key.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string('0', 64)}";
        Assert.Multiple(() =>
        {
            Assert.That(LocalHangars.KeyIssued("dosid-a", 2, 1, key), Is.EqualTo(issued));
            Assert.That(LocalHangars.KeyIssued("dosid-b", 2, 1, key), Is.Null, "other session");
            Assert.That(LocalHangars.KeyIssued("dosid-a", 1, 1, key), Is.Null, "other requested slot");
            Assert.That(LocalHangars.KeyIssued("dosid-a", 2, 2, key), Is.Null, "stale current slot");
            Assert.That(LocalHangars.KeyIssued("dosid-a", 2, 1, tampered), Is.Null, "tampered signature");
            Assert.That(LocalHangars.KeyIssued("dosid-a", 2, 1, key + ".x"), Is.Null, "extra segment");
            Assert.That(LocalHangars.KeyIssued("dosid-a", 2, 1, ""), Is.Null, "empty");
            Assert.That(LocalHangars.CreateFormKey("dosid-a", 2, 1, issued), Is.Not.EqualTo(key), "fresh nonce");
        });
    }

    [Test]
    public void ActionQueries_RequireExactlyOneCanonicalActionKey()
    {
        (Func<IQueryCollection, bool> Valid, string Action)[] forms =
        [
            (LocalShopPurchase.ValidActionQuery, "internalShopBuy"),
            (LocalClanAction.ValidActionQuery, "internalClanJoin"),
            (LocalPilotSkillAction.ValidActionQuery, "internalPilotSkillReset"),
            (LocalMessagingAction.ValidActionQuery, "internalMessageSend"),
            (LocalGalaxyGateAction.ValidActionQuery, "internalGalaxyGateSpin"),
            (LocalAssemblyAction.ValidActionQuery, "internalAssemblyCraft")
        ];
        foreach (var (valid, action) in forms)
        {
            Assert.Multiple(() =>
            {
                Assert.That(valid(Query(action)), Is.True, action);
                Assert.That(valid(Query(action, ("from", "x"))), Is.False, $"{action} extra key");
                Assert.That(valid(new QueryCollection(new Dictionary<string, StringValues> { ["ACTION"] = action })), Is.False,
                    $"{action} key casing");
                Assert.That(valid(new QueryCollection(new Dictionary<string, StringValues> { ["action"] = new([action, action]) })),
                    Is.False, $"{action} duplicate value");
                Assert.That(valid(new QueryCollection()), Is.False, $"{action} missing");
            });
        }
        Assert.That(LocalGalaxyGateAction.ValidActionQuery(Query("internalGalaxyGateActivate")), Is.True);
        Assert.That(LocalGalaxyGateAction.ValidActionQuery(Query("internalgalaxygatespin")), Is.False);
        Assert.That(LocalAssemblyAction.ValidActionQuery(Query("internalAssemblyCrafted")), Is.False);
    }

    [TestCase(64, true)]
    [TestCase(65, false)]
    public void NavigationValues_AreLimitedToSixtyFourCharacters(int length, bool expected)
    {
        var value = new string('a', length);
        foreach (var (menu, action, fields) in Pages.Where(page => page.Menu is 1 or 3 or 4 or 5 or 13))
            Assert.That(Accepts(menu, Query(action, [.. fields, ("from", value)])), Is.EqualTo(expected), $"menu {menu}");
    }

    [Test]
    public void PageQueries_RejectControlCharactersAndRepeatedValues()
    {
        foreach (var (menu, action, fields) in Pages)
        {
            var repeated = new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["action"] = new([action, action]) };
            foreach (var (key, value) in fields) repeated.Add(key, value);
            Assert.That(Accepts(menu, new QueryCollection(repeated)), Is.False, $"menu {menu} repeated action");
        }
        foreach (var (menu, action, fields) in Pages.Where(page => page.Menu is 1 or 3 or 4 or 5 or 9 or 13))
        {
            Assert.That(Accepts(menu, Query(action, [.. fields, ("lang", "e\nn")])), Is.False, $"menu {menu} control");
            var repeated = new Dictionary<string, StringValues>(StringComparer.Ordinal) { ["action"] = action, ["lang"] = new(["en", "es"]) };
            foreach (var (key, value) in fields) repeated.Add(key, value);
            Assert.That(Accepts(menu, new QueryCollection(repeated)), Is.False, $"menu {menu} repeated lang");
        }
    }

    [Test]
    public void NoticeParameters_AreMutuallyExclusive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepts(1, Query("internalDock", ("activated", "2"))), Is.True);
            Assert.That(Accepts(1, Query("internalDock", ("activated", "3"))), Is.False);
            Assert.That(Accepts(4, Query("internalClan", ("joined", "mmo-guard"))), Is.True);
            Assert.That(Accepts(5, Query("internalPilotSheet", ("reset", "1"))), Is.True);
            Assert.That(Accepts(5, Query("internalPilotSheet", ("reset", "2"))), Is.False);
            Assert.That(Accepts(9, Query("internalGalaxyGates", ("spun", "alpha"))), Is.True);
            Assert.That(Accepts(9, Query("internalGalaxyGates", ("spun", "alpha"), ("activated", "alpha"))), Is.False);
            Assert.That(Accepts(13, Query("internalMessages", ("sent", "4"))), Is.True);
            Assert.That(Accepts(13, Query("internalMessages", ("readall", "1"))), Is.True);
            Assert.That(Accepts(13, Query("internalMessages", ("readall", "2"))), Is.False);
            Assert.That(Accepts(13, Query("internalMessages", ("sent", "4"), ("deleted", "4"))), Is.False);
            Assert.That(Accepts(13, Query("internalMessages", ("added", "4"), ("readall", "1"))), Is.False);
            Assert.That(LocalMessagingAction.NoticeOf(Query("internalMessages", ("removed", "7"))), Is.EqualTo(("removed", 7)));
            Assert.That(LocalMessagingAction.NoticeOf(Query("internalMessages", ("readall", "1"))), Is.EqualTo(("readall", 1)));
            Assert.That(LocalMessagingAction.NoticeOf(Query("internalMessages")), Is.Null);
        });
    }

    [Test]
    public void SearchAndDetail_CannotCombine_ButBlankSearchIsNoSearch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepts(9, Query("internalGalaxyGates", ("gate", "alpha"), ("q", "  "))), Is.True);
            Assert.That(Accepts(9, Query("internalGalaxyGates", ("gate", "alpha"), ("q", "al"))), Is.False);
            Assert.That(LocalGalaxyGateAction.Search(Query("internalGalaxyGates", ("q", "  al  "))), Is.EqualTo("al"));
            Assert.That(LocalGalaxyGateAction.Search(Query("internalGalaxyGates", ("q", "   "))), Is.Null);
            Assert.That(Accepts(10, Query("internalAssembly", ("detail", "laser_x2"), ("q", ""))), Is.True);
            Assert.That(Accepts(10, Query("internalAssembly", ("detail", "laser_x2"), ("q", "laser"))), Is.False);
        });
    }

    [TestCase("ab", true)]
    [TestCase(" ab", false)]
    [TestCase("ab ", false)]
    [TestCase("a", false)]
    [TestCase("a\tb", false)]
    public void ShopAndMessagingSearch_MustAlreadyBeTrimmed(string value, bool expected)
    {
        Assert.That(LocalShopPurchase.ValidSearch(value), Is.EqualTo(expected));
        Assert.That(LocalMessagingAction.ValidSearch(value), Is.EqualTo(expected));
    }

    [Test]
    public void SearchLengthBoundaries_DifferPerMenu()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalShopPurchase.ValidSearch(new string('a', 40)), Is.True);
            Assert.That(LocalShopPurchase.ValidSearch(new string('a', 41)), Is.False);
            Assert.That(LocalMessagingAction.ValidSearch(new string('a', 41)), Is.False);
            Assert.That(LocalGalaxyGateAction.ValidSearch(new string('a', 40)), Is.True);
            Assert.That(LocalGalaxyGateAction.ValidSearch(new string('a', 41)), Is.False);
            Assert.That(LocalGalaxyGateAction.ValidSearch(" a "), Is.False);
            Assert.That(LocalGalaxyGateAction.ValidSearch(""), Is.True);
            Assert.That(LocalGalaxyGateAction.ValidSearch("a!"), Is.False);
            Assert.That(LocalClan.IsWellFormedSearch("a"), Is.True);
            Assert.That(LocalClan.IsWellFormedSearch(new string('a', 32)), Is.True);
            Assert.That(LocalClan.IsWellFormedSearch(new string('a', 33)), Is.False);
            Assert.That(LocalClan.IsWellFormedSearch(" a"), Is.False);
            Assert.That(LocalClan.IsWellFormedSearch("a_b"), Is.False);
        });
    }

    [TestCase("mmo-guard", true)]
    [TestCase("a1", true)]
    [TestCase("-mmo", false)]
    [TestCase("mmo-", false)]
    [TestCase("mmo--guard", false)]
    [TestCase("Mmo", false)]
    [TestCase("mmo_guard", false)]
    [TestCase("", false)]
    public void ClanIds_AreLowercaseWordsJoinedBySingleHyphens(string value, bool expected)
    {
        Assert.That(LocalClanAction.IsWellFormedClanId(value), Is.EqualTo(expected));
    }

    [TestCase("shieldEngineering", true)]
    [TestCase("a", true)]
    [TestCase("Shield", false)]
    [TestCase("1shield", false)]
    [TestCase("shield-x", false)]
    public void SkillIds_StartLowercaseAndContainOnlyLettersOrDigits(string value, bool expected)
    {
        Assert.That(LocalPilotSkillAction.IsWellFormedSkillId(value), Is.EqualTo(expected));
    }

    [Test]
    public void RecipeIds_AreLowercaseAsciiWithUnderscoreOrHyphen_UpToSixtyFourCharacters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LocalAssemblyAction.MaxRecipeIdLength, Is.EqualTo(64));
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId("laser_x2-mk"), Is.True);
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId(new string('a', 64)), Is.True);
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId(new string('a', 65)), Is.False);
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId("Laser"), Is.False);
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId("laser x"), Is.False);
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId(""), Is.False);
            Assert.That(LocalAssemblyAction.IsWellFormedRecipeId(null), Is.False);
        });
    }

    [Test]
    public void SuccessRedirects_UseCanonicalNoticeLocations_ThatTheirPagesAccept()
    {
        (int Menu, string Location, string Expected)[] redirects =
        [
            (1, LocalHangars.ActivatedLocation(2), "/indexInternal.es?action=internalDock&activated=2"),
            (4, LocalClanAction.SuccessLocation(true, "mmo-guard"), "/indexInternal.es?action=internalClan&joined=mmo-guard"),
            (4, LocalClanAction.SuccessLocation(false, "mmo-guard"), "/indexInternal.es?action=internalClan&left=mmo-guard"),
            (5, LocalPilotSkillAction.SuccessLocation(null), "/indexInternal.es?action=internalPilotSheet&reset=1"),
            (5, LocalPilotSkillAction.SuccessLocation("shieldEngineering"),
                "/indexInternal.es?action=internalPilotSheet&trained=shieldEngineering"),
            (9, LocalGalaxyGateAction.RedirectTarget(true, "alpha"), "/indexInternal.es?action=internalGalaxyGates&spun=alpha"),
            (9, LocalGalaxyGateAction.RedirectTarget(false, "alpha", "all"), "/indexInternal.es?action=internalGalaxyGates&activated=alpha"),
            (9, LocalGalaxyGateAction.RedirectTarget(true, "alpha", "ready"),
                "/indexInternal.es?action=internalGalaxyGates&spun=alpha&filter=ready")
        ];
        foreach (var (menu, location, expected) in redirects)
        {
            Assert.That(location, Is.EqualTo(expected));
            var values = location[(location.IndexOf('?') + 1)..].Split('&').Select(pair => pair.Split('='))
                .ToDictionary(pair => pair[0], pair => new StringValues(Uri.UnescapeDataString(pair[1])), StringComparer.Ordinal);
            Assert.That(Accepts(menu, new QueryCollection(values)), Is.True, $"redirect {location} must render");
        }
    }
}
