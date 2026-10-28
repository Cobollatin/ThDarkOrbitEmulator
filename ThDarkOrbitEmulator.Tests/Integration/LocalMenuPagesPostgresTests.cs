using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Integration;

// Menu-77: drives the real GET /indexInternal.es endpoint against Postgres for the 200 pages
// (and ledger-dependent 404s) that LocalMenuDetailRoutingTests cannot reach without a database.
[TestFixture]
[Category("Integration")]
public sealed class LocalMenuPagesPostgresTests
{
    private PostgreSqlContainer _postgres = null!;
    private IConfiguration _config = null!;
    private static int _nextGameUserId = 77_000;

    [TestCase("internalPilotSheet", "<title>Pilot Sheet - DarkOrbit</title>")]
    [TestCase("internalHallofFame", "id=\"localHallOfFame\"")]
    [TestCase("internalCommandCenter", "id=\"commandCenter\"")]
    public async Task CockpitWebViewDestination_WithSession_ReturnsPage(string action, string marker)
    {
        var context = await SendAsync($"?action={action}", await HarnessAsync());
        var body = Body(context);

        AssertPage(context, body);
        Assert.That(body, Does.Contain(marker));
    }

    [Test]
    public async Task Events_Search_UsesSeededBattlePassOffersThroughEndpoint()
    {
        var harness = await HarnessAsync();
        var context = await SendAsync("?action=internalEvents&filter=in-progress&q=buy", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.That(body, Does.Contain("<tr data-tier=\"quartermaster\" data-status=\"in-progress\">"));
        Assert.That(body, Does.Contain("<tr data-tier=\"tycoon\" data-status=\"in-progress\">"));
        Assert.That(body, Does.Not.Contain("<tr data-tier=\"artisan\""));
    }

    [TestCase("&filter=ready&detail=quartermaster")]
    [TestCase("&q=craft&detail=supplier")]
    public async Task Events_DetailOutsideSelectedResult_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalEvents{query}", await HarnessAsync()));

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("thdarkorbit").WithUsername("app").WithPassword("app").Build();
        await _postgres.StartAsync(ct);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:identity-write"] = _postgres.GetConnectionString() }).Build();
        await new SqlMigrationHostedService(_config, Options.Create(new IdentityDatabaseOptions
        { AutoMigrate = true, BackupBeforeMigration = false, Schema = "identity" }),
            NullLogger<SqlMigrationHostedService>.Instance).StartAsync(ct);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _postgres.DisposeAsync();

    [TestCase(1, 1)]
    [TestCase(1, 2)]
    [TestCase(2, 1)]
    [TestCase(2, 2)]
    public async Task DockEquipment_ValidSelection_Returns200WithStarterInventoryAndBalances(int slot, int configuration)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalDockEquipment&slot={slot}&configuration={configuration}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("id=\"hangarEquipment\""));
            Assert.That(body, Does.Contain($"Hangar slot {slot} · Configuration {configuration}"));
            Assert.That(body, Does.Contain("LF-3 laser"));
            Assert.That(body, Does.Contain("P.E.T. 10"));
            Assert.That(body, Does.Contain("action=internalDockEquipmentSet"));
        });
    }

    [Test]
    public async Task DockEquipment_WithNavigationParameters_StillReturns200()
    {
        var harness = await HarnessAsync();
        var context = await SendAsync("?action=internalDockEquipment&slot=2&configuration=2&from=dock&lang=en", harness);

        AssertPage(context, Body(context));
    }

    [TestCase("", true, true)]
    [TestCase("&folder=all", true, true)]
    [TestCase("&folder=inbox", true, false)]
    [TestCase("&folder=sent", false, true)]
    public async Task Messages_Folder_Returns200WithMatchingTables(string query, bool inbox, bool sent)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalMessages{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("id=\"messaging\""));
            Assert.That(body, Does.Contain($"ID {harness.Session.GameUserId}"));
            Assert.That(body.Contains("id=\"inbox\""), Is.EqualTo(inbox));
            Assert.That(body.Contains("id=\"sent\""), Is.EqualTo(sent));
            Assert.That(body, Does.Contain("Inbox (3)").Or.Not.Contain("id=\"inbox\""));
        });
    }

    // The seeded mailbox holds three inbox notices (ids 1-3).
    [TestCase("&message=1")]
    [TestCase("&folder=inbox&message=3")]
    [TestCase("&folder=all&message=2")]
    public async Task Messages_OpenSeededMessage_Returns200WithMessageSection(string query)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalMessages{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.That(body, Does.Contain("<section id=\"message\">"));
    }

    [TestCase("")]
    [TestCase("&page=1")]
    [TestCase("&category=all")]
    [TestCase("&category=economy&page=1")]
    public async Task Logbook_FirstPage_Returns200(string query)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalLogbook{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.That(body, Does.Contain("id=\"localLogbook\""));
    }

    // Menu-74 left these 404s depending on the ledger read; they are now covered through the endpoint.
    [TestCase("&page=999")]
    [TestCase("&category=combat&page=999")]
    [TestCase("&entry={0}-999999")]
    [TestCase("&entry={0}-999999&page=1")]
    public async Task Logbook_LedgerDependentMiss_Returns404(string query)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync(
            $"?action=internalLogbook{string.Format(query, harness.Session.GameUserId)}", harness);

        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(Body(context), Does.Not.Contain("id=\"economyBalances\""));
        });
    }

    // Regression: sum(bigint) is numeric in Postgres, which used to throw InvalidCastException on every
    // Postgres-backed menu page because LocalHangarEquipmentStore.GetAsync reads the craft count.
    [Test]
    public async Task CraftCount_WithoutCrafts_ReturnsZeroAsInt64()
    {
        var harness = await HarnessAsync();

        Assert.That(await harness.Economy.CraftCountAsync(harness.Session.AccountId,
            TestContext.CurrentContext.CancellationToken), Is.EqualTo(0L));
    }

    // Menu-78: Shop, Auction, Assembly, Skylab and Tech Factory 200 pages and catalog-dependent 404s
    // against the migration-seeded economy catalog.
    [TestCase("", "LF-3 laser", "P.E.T. 10")]
    [TestCase("&category=Equipment", "LF-3 laser", "SG3N-B02 shield")]
    [TestCase("&category=drones", "Flax drone", "Iris")]
    [TestCase("&item=pet_10", "P.E.T. 10", "action=internalShopBuy")]
    [TestCase("&category=Drones&item=drone_iris", "Limit 2 per account; purchased 0", "Low stock")]
    public async Task Shop_SeededCatalog_Returns200(string query, string first, string second)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalShop{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<main id=\"shop\">"));
            Assert.That(body, Does.Contain(first));
            Assert.That(body, Does.Contain(second));
        });
    }

    [Test]
    public async Task Shop_ItemFilter_ShowsOnlyThatItem()
    {
        var harness = await HarnessAsync();
        var body = Body(await SendAsync("?action=internalShop&item=laser_lf3", harness));

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<h2>LF-3 laser</h2>"));
            Assert.That(body, Does.Not.Contain("<h2>P.E.T. 10</h2>"));
        });
    }

    [TestCase("&category=Rockets")]
    [TestCase("&item=laser_lf99")]
    [TestCase("&category=PET&item=laser_lf3")]
    public async Task Shop_UnknownCategoryOrItem_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalShop{query}", await HarnessAsync()));

    [TestCase("")]
    [TestCase("&lot=laser_lf3")]
    [TestCase("&lot=drone_flax")]
    public async Task Auction_SeededLots_Returns200(string query)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalAuction{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<main id=\"auction\">"));
            Assert.That(body, Does.Contain("LF-3 laser"));
            Assert.That(body, Does.Contain("SG3N-B02 shield"));
            Assert.That(body, Does.Contain("Flax drone"));
            Assert.That(body, Does.Contain("700"));
        });
    }

    [Test]
    public async Task Auction_UnknownLot_Returns404() =>
        AssertMiss(await SendAsync("?action=internalAuction&lot=pet_10", await HarnessAsync()));

    [TestCase("", "lf3")]
    // The starter wallet (1,000 assembly components, 10,000 credits) covers every seeded recipe.
    [TestCase("&status=missing", "id=\"assembly-empty\"")]
    [TestCase("&status=ready", "Ready (3)")]
    [TestCase("&detail=lf3", "assembly-recipe")]
    [TestCase("&status=ready&detail=shield", "assembly-back")]
    public async Task Assembly_SeededRecipes_Returns200WithoutTechRecipes(string query, string marker)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalAssembly{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain(marker));
            Assert.That(body, Does.Contain("recipes ready"));
            Assert.That(body, Does.Not.Contain("tech_speed"));
        });
    }

    [TestCase("&detail=tech_speed")]
    [TestCase("&detail=lf99")]
    public async Task Assembly_UnknownOrTechRecipe_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalAssembly{query}", await HarnessAsync()));

    [TestCase("")]
    [TestCase("&detail=prometium")]
    [TestCase("&detail=terbium")]
    public async Task Skylab_StarterCollectors_Returns200(string query)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalSkylab{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<title>Skylab - DarkOrbit</title>"));
            Assert.That(body, Does.Contain("Prometium"));
            Assert.That(body, Does.Contain("Endurium"));
            Assert.That(body.Contains("id=\"skylabDetailUpgrade\""), Is.EqualTo(query.Length > 0));
        });
    }

    [Test]
    public async Task Skylab_UnknownCollector_Returns404() =>
        AssertMiss(await SendAsync("?action=internalSkylab&detail=xenomit", await HarnessAsync()));

    [TestCase("")]
    [TestCase("&filter=affordable")]
    [TestCase("&filter=shortfall")]
    [TestCase("&detail=tech_speed")]
    [TestCase("&detail=tech_energy")]
    public async Task TechFactory_SeededRecipes_Returns200(string query)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalTechFactory{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<title>Tech Factory - DarkOrbit</title>"));
            Assert.That(body.Contains("id=\"techFactoryDetail\""), Is.EqualTo(query.Contains("detail")));
        });
    }

    [TestCase("&detail=lf3")]
    [TestCase("&detail=tech_unknown")]
    public async Task TechFactory_UnknownOrAssemblyRecipe_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalTechFactory{query}", await HarnessAsync()));

    // Menu-79: Galaxy Gates, Clan, Pilot Sheet, Quests and Vouchers 200 pages and detail misses
    // against Postgres (wallet header, migration-seeded quest and voucher reward offers).
    [TestCase("", false)]
    [TestCase("&gate=alpha", true)]
    [TestCase("&gate=epsilon", true)]
    public async Task GalaxyGates_StarterGates_Returns200(string query, bool detail)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalGalaxyGates{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<main id=\"galaxy-gates\">"));
            Assert.That(body, Does.Contain("Activated gates: 0 / 5"));
            Assert.That(body.Contains("id=\"gate-detail\""), Is.EqualTo(detail));
        });
    }

    [Test]
    public async Task GalaxyGates_UnknownGate_Returns404() =>
        AssertMiss(await SendAsync("?action=internalGalaxyGates&gate=zeta", await HarnessAsync()));

    [TestCase("", "Mars Guard", false)]
    [TestCase("&filter=recruiting", "Mars Traders", false)]
    [TestCase("&filter=closed", "Mars Elite", false)]
    [TestCase("&detail=mmo-orbit", "Mars Orbit", true)]
    [TestCase("&filter=closed&detail=mmo-elite", "Mars Elite", true)]
    public async Task Clan_CompanyDirectory_Returns200(string query, string clan, bool detail)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalClan{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<main id=\"clan\">"));
            Assert.That(body, Does.Contain(clan));
            // The session picked MMO, so other companies' clans are never listed.
            Assert.That(body, Does.Not.Contain("Earth Vanguard"));
            Assert.That(body.Contains("id=\"clan-detail\""), Is.EqualTo(detail));
        });
    }

    // Another company's clan and an unknown clan are both misses for an MMO pilot.
    [TestCase("&detail=eic-orbit")]
    [TestCase("&detail=mmo-unknown")]
    public async Task Clan_OtherCompanyOrUnknownClan_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalClan{query}", await HarnessAsync()));

    [TestCase("", false)]
    [TestCase("&detail=hull", true)]
    [TestCase("&detail=rocketFusion", true)]
    public async Task PilotSheet_StarterSkills_Returns200(string query, bool detail)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalPilotSheet{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<title>Pilot Sheet - DarkOrbit</title>"));
            Assert.That(body, Does.Contain("Hull Training"));
            Assert.That(body, Does.Contain("Rocket Fusion"));
            Assert.That(body.Contains("id=\"pilotSkillDetail\""), Is.EqualTo(detail));
        });
    }

    [Test]
    public async Task PilotSheet_UnknownSkill_Returns404() =>
        AssertMiss(await SendAsync("?action=internalPilotSheet&detail=ammo", await HarnessAsync()));

    // A fresh pilot has no purchases or crafts, so every seeded quest is in progress.
    [TestCase("", false)]
    [TestCase("&filter=in-progress", false)]
    [TestCase("&detail=first_purchase", true)]
    [TestCase("&filter=in-progress&detail=master_engineer", true)]
    public async Task Quests_SeededOffers_Returns200(string query, bool detail)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalQuests{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("<main id=\"quests\""));
            Assert.That(body, Does.Contain("First supply order"));
            Assert.That(body, Does.Contain("Master engineer"));
            Assert.That(body, Does.Contain("Purchases: 0 · Crafts: 0"));
            Assert.That(body.Contains("id=\"questDetail\""), Is.EqualTo(detail));
        });
    }

    [TestCase("&detail=unknown_quest")]
    [TestCase("&filter=claimed&detail=first_purchase")]
    [TestCase("&filter=ready&detail=first_craft")]
    public async Task Quests_DetailOutsideSeedOrFilter_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalQuests{query}", await HarnessAsync()));

    [TestCase("&q=first", "3 of 7 quests match.", false)]
    [TestCase("&filter=in-progress&q=first&detail=first_purchase", "Quest 2 of 3", true)]
    [TestCase("&filter=claimed&q=first", "No claimed quests match this search.", false)]
    public async Task Quests_Search_UsesSeededOffersThroughEndpoint(string query, string marker, bool detail)
    {
        var context = await SendAsync($"?action=internalQuests{query}", await HarnessAsync());
        var body = Body(context);

        AssertPage(context, body);
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain(marker));
            Assert.That(body, Does.Not.Contain("<tr data-quest=\"bulk_buyer\""));
            Assert.That(body.Contains("id=\"questDetail\""), Is.EqualTo(detail));
        });
    }

    [TestCase("&q=first&detail=bulk_buyer")]
    [TestCase("&filter=claimed&q=first&detail=first_purchase")]
    public async Task Quests_DetailOutsideSearchOrStatus_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalQuests{query}", await HarnessAsync()));

    [TestCase("", "<main id=\"vouchers\" data-filter=\"all\">")]
    [TestCase("&filter=available", "Redeemed 0 of 2 codes")]
    [TestCase("&filter=redeemed", "<main id=\"vouchers\" data-filter=\"redeemed\">")]
    [TestCase("&voucher=TRAINING-500", "<main id=\"voucherDetail\" data-voucher=\"TRAINING-500\"")]
    [TestCase("&filter=available&voucher=HANGAR-750", "Reward: 750 credits")]
    public async Task Vouchers_SeededCodes_Returns200(string query, string marker)
    {
        var harness = await HarnessAsync();
        var context = await SendAsync($"?action=internalVouchers{query}", harness);
        var body = Body(context);

        AssertPage(context, body);
        Assert.That(body, Does.Contain(marker));
    }

    // Codes are matched exactly within the selected list; nothing is redeemed for a fresh pilot.
    [TestCase("&voucher=UNKNOWN-1")]
    [TestCase("&voucher=training-500")]
    [TestCase("&filter=redeemed&voucher=TRAINING-500")]
    public async Task Vouchers_UnknownCodeForList_Returns404(string query) =>
        AssertMiss(await SendAsync($"?action=internalVouchers{query}", await HarnessAsync()));

    private static void AssertMiss(DefaultHttpContext context) =>
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound), Body(context));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            Assert.That(Body(context), Does.Not.Contain("id=\"economyBalances\""));
        });

    private static void AssertPage(DefaultHttpContext context, string body) =>
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK), body);
            Assert.That(context.Response.ContentType, Does.StartWith("text/html"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
            // Every 200 menu page carries the Postgres wallet header.
            Assert.That(body, Does.Contain("id=\"economyBalances\""));
            Assert.That(body, Does.Contain("credits: "));
        });

    private sealed record Harness(GameAuthSessionStore Sessions, GameAuthSession Session, EconomyStore Economy,
        LocalHangarEquipmentStore Equipment, LocalGalaxyGateStore Gates, LocalMessagingStore Messages,
        LocalSkylabStore Skylab, LocalTechFactoryStore TechFactory, LocalClanStore Clan, LocalPilotSkillStore PilotSkills);

    private async Task<Harness> HarnessAsync()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var gameUserId = Interlocked.Increment(ref _nextGameUserId);
        var accountId = Guid.NewGuid();
        await using (var db = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO identity.accounts(id,username,game_user_id) VALUES (@a,@u,@g)", db);
            insert.Parameters.AddWithValue("a", accountId);
            insert.Parameters.AddWithValue("u", $"menu-pilot-{gameUserId}");
            insert.Parameters.AddWithValue("g", gameUserId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        var cache = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider()
            .GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(accountId, $"menu-pilot-{gameUserId}", null, gameUserId), null, ct)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "mmo", ct), Is.True);
        session = (await sessions.TryGetByDosidAsync(session.Dosid, ct))!;
        var economy = new EconomyStore(_config);
        return new Harness(sessions, session, economy, new LocalHangarEquipmentStore(cache, economy),
            new LocalGalaxyGateStore(cache, economy), new LocalMessagingStore(cache),
            new LocalSkylabStore(cache, TimeProvider.System, economy),
            new LocalTechFactoryStore(cache, TimeProvider.System, economy), new LocalClanStore(cache, economy),
            new LocalPilotSkillStore(cache));
    }

    private static async Task<DefaultHttpContext> SendAsync(string query, Harness harness)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/indexInternal.es";
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers.Cookie = $"dosid={harness.Session.Dosid}";
        context.Response.Body = new MemoryStream();

        // Hangar and preference stores are not reached by these pages.
        FastEndpointsTestResolver.Reset();
        var endpoint = Factory.Create<IndexInternalStartEndpoint>(context,
            NullLogger<IndexInternalStartEndpoint>.Instance, harness.Sessions, null!, null!, harness.Equipment,
            harness.Economy, harness.Gates, harness.Skylab, harness.TechFactory, harness.Clan, harness.PilotSkills, harness.Messages, null!, TimeProvider.System);
        await endpoint.HandleAsync(TestContext.CurrentContext.CancellationToken);
        return context;
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }
}
