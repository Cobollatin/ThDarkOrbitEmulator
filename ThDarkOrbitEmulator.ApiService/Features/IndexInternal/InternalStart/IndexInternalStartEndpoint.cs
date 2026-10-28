using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.ApiService.Features.Economy;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// DNS: gbl3.darkorbit.com | GET /indexInternal.es | flow ids: 771b953a-11da-47a5-9b69-8736a73849f0, 5ac0f020-1134-40af-9d67-7fd7ab4fa419
[method: Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
public sealed class IndexInternalStartEndpoint(ILogger<IndexInternalStartEndpoint> logger,
    IGameAuthSessionStore gameAuthSessionStore,
    IIdentityCommandService identityCommandService,
    LocalHangarStore localHangarStore,
    ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.CockpitCatalogStore cockpitCatalog,
    LocalHangarEquipmentStore equipmentStore,
    EconomyStore economyStore,
    LocalGalaxyGateStore galaxyGateStore,
    LocalSkylabStore skylabStore,
    LocalTechFactoryStore techFactoryStore,
    LocalClanStore clanStore,
    LocalPilotSkillStore pilotSkillStore,
    LocalMessagingStore messagingStore,
    LocalAccountPreferencesStore accountPreferencesStore,
    TimeProvider clock) : EndpointWithoutRequest
{
    // Preserve the direct endpoint-test constructor while the hosted service
    // supplies the persistent cockpit catalog through dependency injection.
    public IndexInternalStartEndpoint(ILogger<IndexInternalStartEndpoint> logger,
        IGameAuthSessionStore gameAuthSessionStore, IIdentityCommandService identityCommandService,
        LocalHangarStore localHangarStore, LocalHangarEquipmentStore equipmentStore,
        EconomyStore economyStore, LocalGalaxyGateStore galaxyGateStore,
        LocalSkylabStore skylabStore, LocalTechFactoryStore techFactoryStore,
        LocalClanStore clanStore, LocalPilotSkillStore pilotSkillStore,
        LocalMessagingStore messagingStore, LocalAccountPreferencesStore accountPreferencesStore,
        TimeProvider clock)
        : this(logger, gameAuthSessionStore, identityCommandService, localHangarStore,
            null!, equipmentStore, economyStore, galaxyGateStore, skylabStore, techFactoryStore,
            clanStore, pilotSkillStore, messagingStore, accountPreferencesStore, clock) { }

    private const string FrameAncestorsPolicy = "frame-ancestors https://*.darkorbit.com/;";

    // The native WebView closes when navigation reaches internalWebGL. Local menu links use
    // internalMenu so the menu remains available after that one-time game handoff.
    public static bool IsUnityClient(HttpRequest request) =>
        request.Headers.UserAgent.ToString().Contains("Unity Client", StringComparison.OrdinalIgnoreCase);

    public static string AdaptHtmlForClient(HttpRequest request, string html) =>
        IsUnityClient(request)
            ? html.Replace("action=internalWebGL", "action=internalMenu", StringComparison.OrdinalIgnoreCase)
            : html;

    public static string AdaptLocationForClient(HttpRequest request, string location) =>
        IsUnityClient(request)
            ? location.Replace("action=internalWebGL", "action=internalMenu", StringComparison.OrdinalIgnoreCase)
            : location;

    private Task WriteClientHtmlAsync(string html, CancellationToken ct) =>
        HttpContext.Response.WriteAsync(AdaptHtmlForClient(HttpContext.Request, html), ct);

    private async Task WriteEconomyPageAsync(GameAuthSession session, string page, CancellationToken ct)
    {
        await equipmentStore.GetAsync(session.AccountId, ct);
        await WriteClientHtmlAsync(LocalEconomyHeader.Add(page,
            await economyStore.BalancesAsync(session.AccountId, ct)), ct);
    }

    public override void Configure()
    {
        Get("/indexInternal.es");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("DNS:gbl3.darkorbit.com"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var requestContext = IndexInternalStartRequestContext.Parse(HttpContext.Request);
        var dosid = string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid)
            ? Guid.NewGuid().ToString("N")
            : requestContext.Cookies.Dosid;

        var menuRedirect = PreCompanyMenuRedirect.ForAction(requestContext.Query.Action);
        var session = string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid)
            ? null
            : await gameAuthSessionStore.TryGetByDosidAsync(requestContext.Cookies.Dosid, ct);
        if (session is null && gameAuthSessionStore is GameAuthSessionStore && !string.IsNullOrWhiteSpace(requestContext.Cookies.Dosid))
        {
            session = await Features.World.WorldHttpBridge.TryRehydrateSessionAsync(
                HttpContext, requestContext.Cookies.Dosid, ct);
        }
        var hasCompany = LocalCompanySelection.IsValid(session?.Company);
        var isMenuHub = string.Equals(requestContext.Query.Action, "internalWebGL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestContext.Query.Action, "internalMenu", StringComparison.OrdinalIgnoreCase);
        var variant = LocalAccountLinkResponse.IsKnownAction(requestContext.Query.Action)
            ? "local-account-link"
            : menuRedirect is not null
            ? hasCompany ? "local-menu-page" : "redirect-to-internalCompanyChoose"
            : isMenuHub
            && string.Equals(requestContext.Query.From, "internalStart", StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(requestContext.Query.Action, "internalMenu", StringComparison.OrdinalIgnoreCase) ? "internalMenu" : "internalWebGL")
            : LocalLogoutAction.IsLogout(requestContext.Query.Action)
                ? "externalLogout"
                : "redirect-to-internalWebGL";

        logger.LogInformation(
            "IndexInternal redirect. Variant: {Variant}, Action: {Action}, Prc: {Prc}, From: {From}, UserAgent: {UserAgent}",
            variant,
            requestContext.Query.Action,
            requestContext.Query.Prc,
            requestContext.Query.From,
            requestContext.Metadata.UserAgent);

        if (!LocalLogoutAction.SuppressesSessionCookie(requestContext.Query.Action, requestContext.Query.From))
            HttpContext.Response.Headers.Append("Set-Cookie", $"dosid={dosid}; path=/; samesite=none; secure; HttpOnly");
        HttpContext.Response.Headers.Append("Expires", "Thu, 19 Nov 1981 08:52:00 GMT");
        HttpContext.Response.Headers.Append("Cache-Control", "no-store, no-cache, must-revalidate");
        HttpContext.Response.Headers.Append("Pragma", "no-cache");
        HttpContext.Response.Headers.Append("Content-Security-Policy", FrameAncestorsPolicy);
        HttpContext.Response.Headers.Append("Content-Language", "es");
        HttpContext.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        if (LocalLogoutAction.IsLogout(requestContext.Query.Action))
        {
            await LocalLogoutAction.HandleAsync(HttpContext.Response, requestContext.Cookies.Dosid,
                gameAuthSessionStore, ct);
            await HttpContext.Response.CompleteAsync();
            return;
        }

        if (LocalLogoutAction.IsSignedOutStep(requestContext.Query.Action, requestContext.Query.From))
        {
            LocalLogoutAction.SignedOut(HttpContext.Response);
            await HttpContext.Response.CompleteAsync();
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalServers", StringComparison.Ordinal))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            if (session is null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await WriteClientHtmlAsync(LocalCompanySelection.SignedOutPage, ct);
                return;
            }
            var rows = (cockpitCatalog is null
                    ? new[] { new ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.ClientServer(
                        "local", "Local emulator", "127.0.0.1", 1, "online", true, true) }
                    : await cockpitCatalog.ServersAsync(ct)).Where(item => item.Enabled)
                .Select(item => $"<li><strong>{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(item.InstanceName)}</strong> ({System.Text.Encodings.Web.HtmlEncoder.Default.Encode(item.Code)}) — {System.Text.Encodings.Web.HtmlEncoder.Default.Encode(item.Status)}, map {item.MapId}, host {System.Text.Encodings.Web.HtmlEncoder.Default.Encode(item.Host)}</li>");
            await WriteClientHtmlAsync("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Servers</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:8vh auto}li{padding:12px;border-bottom:1px solid #42627b}a{color:#8cdcff}</style></head><body><main><h1>Servers</h1><ul>"
                + string.Join("", rows) + "</ul><a href=\"/indexInternal.es?action=internalMenu\">Back to menu</a></main></body></html>", ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalCompanyChoose", StringComparison.OrdinalIgnoreCase))
        {
            await LocalCompanyChoiceAction.HandleAsync(HttpContext, session, gameAuthSessionStore,
                identityCommandService, false, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalCompanyConfirm", StringComparison.OrdinalIgnoreCase))
        {
            await LocalCompanyChoiceAction.HandleAsync(HttpContext, session, gameAuthSessionStore,
                identityCommandService, true, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalAccount", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalAccountSettingsAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalAccountSettingsAction.ErrorAsync(HttpContext.Response, 400, "Invalid Account center page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalAccountSettingsAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            var (accountPreferences, accountRevision) = await accountPreferencesStore.GetStateAsync(session.AccountId, ct);
            await WriteEconomyPageAsync(session, LocalAccountSettings.Page(session, accountPreferences, accountRevision,
                LocalAccountSettingsAction.SavedRevision(HttpContext.Request.Query),
                LocalAccountSettingsAction.IsUnchangedNotice(HttpContext.Request.Query),
                cockpitCatalog is null ? null : (await cockpitCatalog.LanguagesAsync(ct)).Where(item => item.Enabled)
                    .Select(item => (item.Code, item.Name)).ToArray()), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalCommandCenter", StringComparison.OrdinalIgnoreCase))
        {
            // Menu-72: the query is checked before the session (400, then 401, then company, then 404).
            var commandCenterInvalid = LocalCommandCenter.Validate(HttpContext.Request.Query);
            if (commandCenterInvalid is null && session is not null && !hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var commandCenter = commandCenterInvalid ?? (session is null
                ? LocalCommandCenter.Unauthorized()
                : LocalCommandCenter.Resolve(HttpContext.Request.Query, clock.GetUtcNow()));
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            HttpContext.Response.Headers.CacheControl = "no-store";
            HttpContext.Response.StatusCode = commandCenter.Status;
            if (commandCenter.Status == StatusCodes.Status200OK)
                await WriteEconomyPageAsync(session!, commandCenter.Html, ct);
            else
                await WriteClientHtmlAsync(commandCenter.Html, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalMapOverview", StringComparison.OrdinalIgnoreCase))
        {
            // Menu-71: the query is checked before the session (400, then 401, then company), like the other detail views.
            var mapOverviewInvalid = LocalMapOverview.Validate(HttpContext.Request.Query);
            if (mapOverviewInvalid is null && session is not null && !hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var mapOverview = mapOverviewInvalid ?? (session is null
                ? LocalMapOverview.Unauthorized()
                : LocalMapOverview.Resolve(HttpContext.Request.Query, session));
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            HttpContext.Response.StatusCode = mapOverview.Status;
            if (mapOverview.Status == StatusCodes.Status200OK)
                await WriteEconomyPageAsync(session!, mapOverview.Html, ct);
            else
                await WriteClientHtmlAsync(mapOverview.Html, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalLogbook", StringComparison.OrdinalIgnoreCase))
        {
            // Menu-73: the query is checked before the session (400, then 401, then company, then 403/404).
            var logbookInvalid = LocalLogbook.Validate(HttpContext.Request.Query);
            if (logbookInvalid is null && session is not null && !hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var logbookNow = clock.GetUtcNow();
            var logbookLive = session is not null && (session.ExpiresAt == default || session.ExpiresAt > logbookNow);
            // Menu-74: unknown categories and other accounts' entries are refused before the ledger read.
            if (logbookInvalid is null && logbookLive)
                logbookInvalid = LocalLogbook.Precheck(HttpContext.Request.Query, session!);
            // Menu-96: run the starter import (also done by WriteEconomyPageAsync) before the ledger read,
            // so the first visit already lists the import rows it writes.
            if (logbookInvalid is null && logbookLive)
                await equipmentStore.GetAsync(session!.AccountId, ct);
            var ledger = logbookInvalid is null && logbookLive
                ? await economyStore.LedgerAsync(session!.AccountId, LocalLogbook.LedgerLimit, ct)
                : [];
            var logbook = logbookInvalid ?? LocalLogbook.Resolve(HttpContext.Request.Query, session, logbookNow, ledger);
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            HttpContext.Response.Headers.CacheControl = "no-store";
            HttpContext.Response.StatusCode = logbook.Status;
            if (logbook.Status == StatusCodes.Status200OK)
                await WriteEconomyPageAsync(session!, logbook.Html, ct);
            else
                await WriteClientHtmlAsync(logbook.Html, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalHallofFame", StringComparison.OrdinalIgnoreCase))
        {
            // Menu-73: the query is checked before the session (400, then 401, then company, then 404).
            var rankingsInvalid = LocalRankings.Validate(HttpContext.Request.Query);
            if (rankingsInvalid is null && session is not null && !hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var rankings = rankingsInvalid ?? (session is null
                ? LocalRankings.Unauthorized()
                : LocalRankings.Resolve(HttpContext.Request.Query, session));
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            HttpContext.Response.Headers.CacheControl = "no-store";
            HttpContext.Response.StatusCode = rankings.Status;
            if (rankings.Status == StatusCodes.Status200OK)
                await WriteEconomyPageAsync(session!, rankings.Html, ct);
            else
                await WriteClientHtmlAsync(rankings.Html, ct);
            return;
        }

        if (LocalAccountLinkResponse.IsKnownAction(requestContext.Query.Action))
        {
            var linksPage = requestContext.Query.Action!.ToLowerInvariant() switch
            {
                "internalsupport" => LocalLinksRequest.Page.Support,
                "internalforum" => LocalLinksRequest.Page.Forum,
                "internalhelp" => LocalLinksRequest.Page.Help,
                _ => (LocalLinksRequest.Page?)null
            };
            if (linksPage is not null)
            {
                await LocalLinksRequest.WriteAsync(HttpContext.Response,
                    LocalLinksRequest.Resolve(HttpContext.Request.Query, session, linksPage.Value), ct);
                return;
            }
            await LocalAccountLinkResponse.WriteAsync(HttpContext.Response, requestContext.Query.Action!,
                session is not null, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalDockEquipment", StringComparison.OrdinalIgnoreCase))
        {
            // Menu-76: the query is checked before the session (400, then 401, then 403), like the other menus.
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalHangarEquipmentSelection.TryParsePageQuery(HttpContext.Request.Query, out var slot, out var configuration, out var saved))
            {
                await LocalHangarError.WriteAsync(HttpContext.Response, 400, "Invalid equipment selection.", ct);
                return;
            }
            if (session is null)
            {
                await LocalHangarError.WriteAsync(HttpContext.Response, 401, "Your session is missing or expired.", ct);
                return;
            }
            // Menu-86: a pilot without a company is sent to company choice (302), as Live does for menu pages.
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session!, LocalHangarEquipment.Page(session!, slot, configuration,
                await equipmentStore.GetAsync(session!.AccountId, ct), saved,
                cockpitCatalog is null ? null : (await cockpitCatalog.HangarsAsync(session, slot, ct))
                    .FirstOrDefault(item => item.SlotNumber == slot)), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalShop", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalShopPurchase.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalShopPurchase.ErrorAsync(HttpContext.Response, 400, "Invalid Shop filter or page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalShopPurchase.ErrorAsync(HttpContext.Response, 401, "Your session has expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var category = HttpContext.Request.Query["category"].Count == 1 ? HttpContext.Request.Query["category"].ToString() : null;
            var itemId = HttpContext.Request.Query["item"].Count == 1 ? HttpContext.Request.Query["item"].ToString() : null;
            var search = HttpContext.Request.Query["q"].Count == 1 ? HttpContext.Request.Query["q"].ToString() : null;
            var listings = await economyStore.ListingsAsync(ct);
            if ((category is not null && !listings.Any(item => LocalShop.CategoryFor(item).Equals(category, StringComparison.OrdinalIgnoreCase)))
                || (itemId is not null && !listings.Any(item => item.Id == itemId && (category is null || LocalShop.CategoryFor(item).Equals(category, StringComparison.OrdinalIgnoreCase))
                    && LocalShop.MatchesSearch(item, search))))
            {
                await LocalShopPurchase.ErrorAsync(HttpContext.Response, 404, "Shop item or category not found.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await economyStore.ImportAsync(session.AccountId, await equipmentStore.GetAsync(session.AccountId, ct), ct);
            await WriteEconomyPageAsync(session, LocalShop.PageFromDatabase(session, listings,
                await economyStore.BalancesAsync(session.AccountId, ct),
                await economyStore.InventoryAsync(session.AccountId, ct), category, itemId,
                await economyStore.PurchasesAsync(session.AccountId, ct), search), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalPayment", StringComparison.OrdinalIgnoreCase)
            && hasCompany)
        {
            if (!LocalPaymentAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalPaymentAction.ErrorAsync(HttpContext.Response, 400, "Invalid Payment page request.", ct);
                return;
            }
            var paymentFilter = LocalPaymentAction.Filter(HttpContext.Request.Query);
            var paymentSearch = LocalPaymentAction.Search(HttpContext.Request.Query);
            if (!LocalPayment.IsKnownFilter(paymentFilter))
            {
                await LocalPaymentAction.ErrorAsync(HttpContext.Response, 404, "That Payment filter does not exist.", ct);
                return;
            }
            var paymentOffers = await economyStore.RewardOffersAsync("payment", ct);
            var paymentDetail = LocalPaymentAction.DetailId(HttpContext.Request.Query);
            var paymentCatalog = LocalPayment.Packages(paymentOffers);
            if (paymentDetail is not null && LocalPayment.Find(paymentCatalog, paymentDetail) is null)
            {
                await LocalPaymentAction.ErrorAsync(HttpContext.Response, 404, "This package does not exist.", ct);
                return;
            }
            var paymentState = await equipmentStore.GetAsync(session!.AccountId, ct);
            if (paymentDetail is not null && !LocalPayment.HasDetail(paymentState, paymentCatalog, paymentFilter, paymentDetail, paymentSearch))
            {
                await LocalPaymentAction.ErrorAsync(HttpContext.Response, 404, "This package is not listed under that filter.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            HttpContext.Response.Headers.CacheControl = "no-store";
            await WriteEconomyPageAsync(session!, LocalPayment.Page(paymentState, offers: paymentOffers, detail: paymentDetail,
                claimed: LocalPaymentAction.ClaimedId(HttpContext.Request.Query), filter: paymentFilter, search: paymentSearch), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalVouchers", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalVoucherAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 400, "Invalid Vouchers page request.", ct);
                return;
            }
            // Menu-70: a malformed code is rejected before the session check, like the other detail views.
            var voucherDetail = HttpContext.Request.Query["voucher"];
            if (voucherDetail.Count == 1 && !LocalVouchers.IsWellFormed(voucherDetail.ToString()))
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 400, "Invalid voucher code.", ct);
                return;
            }
            var voucherRedeemed = HttpContext.Request.Query["redeemed"];
            if (voucherRedeemed.Count == 1 && !LocalVouchers.IsWellFormed(voucherRedeemed.ToString()))
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 400, "Invalid redeemed voucher code.", ct);
                return;
            }
            var voucherSearch = HttpContext.Request.Query["q"];
            if (voucherSearch.Count == 1 && !LocalVouchers.IsWellFormedSearch(voucherSearch.ToString()))
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 400, "Invalid voucher search.", ct);
                return;
            }
            if (session is null)
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var voucherFilter = HttpContext.Request.Query["filter"].Count == 0 ? "all" : HttpContext.Request.Query["filter"].ToString();
            if (!LocalVouchers.IsKnownFilter(voucherFilter))
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 404, "Unknown voucher filter.", ct);
                return;
            }
            var voucherState = await equipmentStore.GetAsync(session.AccountId, ct);
            var voucherOffers = await economyStore.RewardOffersAsync("voucher", ct);
            var voucherNotice = voucherRedeemed.Count == 1
                ? LocalVouchers.RedeemedNotice(voucherState, voucherOffers, voucherRedeemed.ToString())
                : "";
            var voucherPage = voucherNotice is null ? null
                : voucherDetail.Count == 1
                ? LocalVouchers.DetailPage(voucherState, voucherOffers, voucherFilter, voucherDetail.ToString(),
                    voucherSearch.Count == 1 ? voucherSearch.ToString() : null)
                : LocalVouchers.Page(voucherState, voucherOffers, voucherFilter, voucherNotice,
                    voucherSearch.Count == 1 ? voucherSearch.ToString() : null);
            if (voucherPage is null)
            {
                await LocalVoucherAction.ErrorAsync(HttpContext.Response, 404, "Unknown voucher code for this list.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, voucherPage, ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalSpaceSearch", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalSpaceSearchAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalSpaceSearchAction.ErrorAsync(HttpContext.Response, 400, "Invalid Space Search page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalSpaceSearchAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var searchPlanet = HttpContext.Request.Query.TryGetValue("planet", out var planetValue) ? planetValue.ToString() : null;
            if (searchPlanet is not null && LocalSpaceSearch.Find(searchPlanet) is null)
            {
                await LocalSpaceSearchAction.ErrorAsync(HttpContext.Response, 404, "That planet is not part of Space Search.", ct);
                return;
            }
            var spaceSearchState = await equipmentStore.GetAsync(session.AccountId, ct);
            var planetSearch = LocalSpaceSearchAction.Search(HttpContext.Request.Query);
            if (searchPlanet is not null && !LocalSpaceSearch.Matches(spaceSearchState, LocalSpaceSearch.Find(searchPlanet)!, planetSearch))
            {
                await LocalSpaceSearchAction.ErrorAsync(HttpContext.Response, 404, "That planet is not in the search result.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalSpaceSearch.Page(
                spaceSearchState,
                HttpContext.Request.Query.TryGetValue("filter", out var searchFilter) ? searchFilter.ToString() : "all",
                searchPlanet,
                HttpContext.Request.Query.TryGetValue("explored", out var exploredPlanet) ? exploredPlanet.ToString() : null,
                planetSearch), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalEvents", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalEventsAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalEventsAction.ErrorAsync(HttpContext.Response, 400, "Invalid Events page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalEventsAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var eventOffers = await economyStore.RewardOffersAsync("battle_pass", ct);
            var eventDetail = HttpContext.Request.Query.TryGetValue("detail", out var detailValue)
                ? detailValue.ToString() : null;
            if (eventDetail is not null && !LocalEvents.HasTier(eventDetail, eventOffers))
            {
                await LocalEventsAction.ErrorAsync(HttpContext.Response, 404, "That battle pass tier does not exist.", ct);
                return;
            }
            var eventFilter = HttpContext.Request.Query.TryGetValue("filter", out var eventFilterValue)
                ? eventFilterValue.ToString() : null;
            var eventSearch = HttpContext.Request.Query.TryGetValue("q", out var eventSearchValue)
                ? eventSearchValue.ToString().Trim() : null;
            if (string.IsNullOrWhiteSpace(eventSearch)) eventSearch = null;
            var eventState = await equipmentStore.GetAsync(session.AccountId, ct);
            if (eventDetail is not null && !LocalEvents.TierInFilter(eventState, eventDetail, eventFilter, eventOffers, eventSearch))
            {
                await LocalEventsAction.ErrorAsync(HttpContext.Response, 404, "That battle pass tier is not in the selected result.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalEvents.Page(
                eventState, clock.GetUtcNow(),
                eventOffers, eventDetail,
                HttpContext.Request.Query.TryGetValue("claimed", out var claimedTier) ? claimedTier.ToString() : null,
                HttpContext.Request.Query.TryGetValue("expedition", out var expeditionNotice) ? expeditionNotice.ToString() : null,
                eventFilter, eventSearch), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalGalaxyGates", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalGalaxyGateAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalGalaxyGateAction.ErrorAsync(HttpContext.Response, 400,
                    "Invalid Galaxy Gates query.", ct);
                return;
            }
            if (session is null)
            {
                await LocalGalaxyGateAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                // Live gbl6 capture: internalGalaxyGates before company selection is a 302 to company choice.
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var gateState = await galaxyGateStore.GetAsync(session.AccountId, ct);
            var gateDetail = HttpContext.Request.Query.TryGetValue("gate", out var gateValue) ? gateValue.ToString() : null;
            var gateFilter = HttpContext.Request.Query.TryGetValue("filter", out var gateFilterValue) ? gateFilterValue.ToString() : null;
            if (!LocalGalaxyGates.IsFilter(gateFilter))
            {
                await LocalGalaxyGateAction.ErrorAsync(HttpContext.Response, 404, "That Galaxy Gates filter does not exist.", ct);
                return;
            }
            if (!LocalGalaxyGates.HasGate(gateState, gateDetail, gateFilter))
            {
                await LocalGalaxyGateAction.ErrorAsync(HttpContext.Response, 404, "That Galaxy Gate does not exist.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalGalaxyGates.Page(session, gateState, gateDetail,
                LocalGalaxyGateAction.NoticeGate(HttpContext.Request.Query, "spun"),
                LocalGalaxyGateAction.NoticeGate(HttpContext.Request.Query, "activated"), gateFilter,
                LocalGalaxyGateAction.Search(HttpContext.Request.Query)), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalSkylab", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalSkylabAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalSkylabAction.ErrorAsync(HttpContext.Response, 400,
                    "Invalid Skylab query.", ct);
                return;
            }
            if (session is null)
            {
                await LocalSkylabAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var skylabFilter = HttpContext.Request.Query.TryGetValue("filter", out var skylabFilterValue) ? skylabFilterValue.ToString() : null;
            if (!LocalSkylab.IsFilter(skylabFilter))
            {
                await LocalSkylabAction.ErrorAsync(HttpContext.Response, 404,
                    "Unknown Skylab filter.", ct);
                return;
            }
            var skylabDetail = HttpContext.Request.Query["detail"].ToString();
            if (skylabDetail.Length > 0 && !LocalSkylab.IsCollector(skylabDetail))
            {
                await LocalSkylabAction.ErrorAsync(HttpContext.Response, 404,
                    "Unknown Skylab collector.", ct);
                return;
            }
            var skylabState = await skylabStore.GetAsync(session.AccountId, ct);
            if (skylabDetail.Length > 0 && !LocalSkylab.InFilter(skylabState, skylabDetail, skylabFilter))
            {
                await LocalSkylabAction.ErrorAsync(HttpContext.Response, 404,
                    "That collector is not in the selected filter.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalSkylab.Page(session,
                skylabState, clock.GetUtcNow(),
                skylabDetail.Length > 0 ? skylabDetail : null,
                HttpContext.Request.Query.TryGetValue("upgraded", out var skylabUpgraded) ? skylabUpgraded.ToString() : null,
                HttpContext.Request.Query.ContainsKey("collected"), skylabFilter,
                LocalSkylabAction.Search(HttpContext.Request.Query)), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalTechFactory", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalTechFactoryAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalTechFactoryAction.ErrorAsync(HttpContext.Response, 400,
                    "Invalid Tech Factory request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalTechFactoryAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (CompanyCatalog.Find(session.Company) is null)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var recipes = await techFactoryStore.GetRecipesAsync(ct);
            var techDetail = HttpContext.Request.Query["detail"].ToString();
            var techFilter = HttpContext.Request.Query["filter"].ToString();
            var techState = await techFactoryStore.GetAsync(session.AccountId, ct);
            if (techDetail.Length > 0 && !LocalTechFactory.HasRecipe(
                    LocalTechFactory.VisibleRecipes(recipes, techState.Components, techFilter), techDetail))
            {
                await LocalTechFactoryAction.ErrorAsync(HttpContext.Response, 404,
                    "Unknown Tech Factory recipe.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalTechFactory.Page(session,
                techState, clock.GetUtcNow(), recipes, techDetail.Length > 0 ? techDetail : null,
                techFilter.Length > 0 ? techFilter : null,
                HttpContext.Request.Query.TryGetValue("started", out var techStarted) ? techStarted.ToString() : null,
                HttpContext.Request.Query.TryGetValue("claimed", out var techClaimed) ? techClaimed.ToString() : null,
                LocalTechFactoryAction.Search(HttpContext.Request.Query)), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalClan", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalClanAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalClanAction.ErrorAsync(HttpContext.Response, 400, "Invalid Clan page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalClanAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var clanDetail = HttpContext.Request.Query["detail"].ToString();
            var clanSearch = HttpContext.Request.Query["q"].ToString();
            if (clanDetail.Length > 0 && !LocalClan.HasDetail(session.Company, clanDetail, clanSearch))
            {
                await LocalClanAction.ErrorAsync(HttpContext.Response, 404, "This clan does not exist.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalClan.Page(session,
                await clanStore.GetMembershipAsync(session.AccountId, ct), clock.GetUtcNow(),
                clanDetail.Length > 0 ? clanDetail : null, HttpContext.Request.Query["filter"].ToString(),
                HttpContext.Request.Query["joined"].ToString(), HttpContext.Request.Query["left"].ToString(),
                clanSearch), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalMessages", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            // Menu-75: the query is checked before the session (400, then 401, then company, then 404).
            var query = HttpContext.Request.Query;
            var openMessage = (int?)null;
            if (!LocalMessagingAction.ValidPageQuery(query))
            {
                await LocalMessagingAction.ErrorAsync(HttpContext.Response, StatusCodes.Status400BadRequest,
                    "Invalid Messages request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalMessagingAction.ErrorAsync(HttpContext.Response, StatusCodes.Status401Unauthorized,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var messaging = await messagingStore.GetAsync(session.AccountId, clock.GetUtcNow(), ct);
            var folder = query["folder"].Count == 1 ? query["folder"].ToString() : "all";
            var search = query["q"].Count == 1 ? query["q"].ToString() : null;
            if (query["message"].Count == 1)
            {
                LocalMessagingAction.TryParseId(query["message"].ToString(), out var messageId);
                if (messaging.Messages.All(message => message.Id != messageId ||
                    (folder == "inbox" && message.Sent) || (folder == "sent" && !message.Sent) ||
                    (search is not null && !LocalMessaging.Matches(message, search))))
                {
                    await LocalMessagingAction.ErrorAsync(HttpContext.Response, StatusCodes.Status404NotFound,
                        "This message does not exist.", ct);
                    return;
                }
                openMessage = messageId;
                // Opening a received message marks it read; the revision is unchanged so forms on other pages stay valid.
                if (await messagingStore.MarkReadAsync(session.AccountId, messageId, ct) == MessagingChange.Applied)
                    messaging = await messagingStore.GetAsync(session.AccountId, clock.GetUtcNow(), ct);
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalMessaging.Page(session, messaging, openMessage, clock.GetUtcNow(), folder,
                LocalMessagingAction.NoticeOf(query), search), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalPilotSheet", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalPilotSkillAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalPilotSkillAction.ErrorAsync(HttpContext.Response, 400, "Invalid Pilot Sheet page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalPilotSkillAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            if (!LocalPilotSkillAction.TryFindDetail(HttpContext.Request.Query, out var pilotDetail))
            {
                await LocalPilotSkillAction.ErrorAsync(HttpContext.Response, 404, "This skill does not exist.", ct);
                return;
            }
            if (!LocalPilotSkillAction.TryFindFilter(HttpContext.Request.Query, out var pilotFilter))
            {
                await LocalPilotSkillAction.ErrorAsync(HttpContext.Response, 404, "This skill filter does not exist.", ct);
                return;
            }
            var pilotState = await pilotSkillStore.GetAsync(session.AccountId, ct);
            if (pilotDetail is not null && !LocalPilotSheet.Matches(pilotDetail, pilotState, pilotFilter))
            {
                await LocalPilotSkillAction.ErrorAsync(HttpContext.Response, 404, "This skill is not in the selected filter.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            var pilotQuery = HttpContext.Request.Query;
            await WriteEconomyPageAsync(session, LocalPilotSheet.Page(session, pilotState, clock.GetUtcNow(), pilotDetail,
                pilotQuery.ContainsKey("trained") ? pilotQuery["trained"].ToString() : null,
                pilotQuery.ContainsKey("reset"), pilotFilter), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalAuction", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalAuctionAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalAuctionAction.ErrorAsync(HttpContext.Response, 400, "Invalid Auction page request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalAuctionAction.ErrorAsync(HttpContext.Response, 401,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var auctionFilter = HttpContext.Request.Query.TryGetValue("filter", out var filterValue) ? filterValue.ToString() : null;
            if (!LocalAuction.IsKnownFilter(auctionFilter))
            {
                await LocalAuctionAction.ErrorAsync(HttpContext.Response, 404, "That Auction filter does not exist.", ct);
                return;
            }
            await equipmentStore.GetAsync(session.AccountId, ct);
            var auctionLots = await economyStore.AuctionLotsAsync(session.AccountId, ct);
            var auctionLot = HttpContext.Request.Query.TryGetValue("lot", out var lotValue) ? lotValue.ToString() : null;
            var auctionNow = clock.GetUtcNow();
            if (!LocalAuction.HasLot(auctionLots, auctionLot, auctionFilter, auctionNow))
            {
                await LocalAuctionAction.ErrorAsync(HttpContext.Response, 404, "That Auction lot does not exist.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalAuction.PageFromDatabase(session, auctionLots,
                await economyStore.BalancesAsync(session.AccountId, ct), auctionNow, auctionLot,
                LocalAuctionAction.NoticeLot(HttpContext.Request.Query, "placed"),
                LocalAuctionAction.NoticeLot(HttpContext.Request.Query, "claimed"), auctionFilter,
                await economyStore.InventoryAsync(session.AccountId, ct),
                LocalAuctionAction.Search(HttpContext.Request.Query)), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalAssembly", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            if (!LocalAssemblyAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalAssemblyAction.ErrorAsync(HttpContext.Response, StatusCodes.Status400BadRequest,
                    "Invalid Assembly request.", ct);
                return;
            }
            if (session is null)
            {
                await LocalAssemblyAction.ErrorAsync(HttpContext.Response, StatusCodes.Status401Unauthorized,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            await equipmentStore.GetAsync(session.AccountId, ct);
            var assemblyRecipes = (await economyStore.RecipesAsync(ct))
                .Where(recipe => !recipe.Id.StartsWith("tech_", StringComparison.Ordinal)).ToArray();
            var assemblyRecipe = HttpContext.Request.Query.TryGetValue("detail", out var recipeValue) ? recipeValue.ToString() : null;
            if (!LocalAssembly.HasRecipe(assemblyRecipes, assemblyRecipe))
            {
                await LocalAssemblyAction.ErrorAsync(HttpContext.Response, StatusCodes.Status404NotFound,
                    "That Assembly recipe does not exist.", ct);
                return;
            }
            var craftedRecipe = HttpContext.Request.Query.TryGetValue("crafted", out var craftedValue)
                && assemblyRecipes.Any(recipe => recipe.Id == craftedValue.ToString()) ? craftedValue.ToString() : null;
            var craftedCount = craftedRecipe is null ? 0 : await economyStore.RecipeCraftCountAsync(session.AccountId, craftedRecipe, ct);
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalAssembly.PageFromDatabase(session, assemblyRecipes,
                await economyStore.BalancesAsync(session.AccountId, ct),
                await economyStore.InventoryAsync(session.AccountId, ct),
                HttpContext.Request.Query.TryGetValue("status", out var statusValue) ? statusValue.ToString() : "all",
                assemblyRecipe, craftedRecipe, craftedCount, LocalAssemblyAction.Search(HttpContext.Request.Query)), ct);
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalQuests", StringComparison.OrdinalIgnoreCase))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            // Menu-70: query and detail id are checked before the session (400, then 401), like the other detail views.
            if (!LocalQuestAction.ValidPageQuery(HttpContext.Request.Query))
            {
                await LocalQuestAction.ErrorAsync(HttpContext.Response, StatusCodes.Status400BadRequest,
                    "Invalid Quests request.", ct);
                return;
            }
            var questDetail = HttpContext.Request.Query["detail"].Count == 0 ? null : HttpContext.Request.Query["detail"].ToString();
            if (questDetail is not null && !LocalQuests.IsWellFormedId(questDetail))
            {
                await LocalQuestAction.ErrorAsync(HttpContext.Response, StatusCodes.Status400BadRequest,
                    "Invalid quest detail.", ct);
                return;
            }
            var questClaimed = HttpContext.Request.Query["claimed"].Count == 0 ? null : HttpContext.Request.Query["claimed"].ToString();
            if (questClaimed is not null && !LocalQuests.IsWellFormedId(questClaimed))
            {
                await LocalQuestAction.ErrorAsync(HttpContext.Response, StatusCodes.Status400BadRequest,
                    "Invalid quest claim notice.", ct);
                return;
            }
            if (session is null)
            {
                await LocalQuestAction.ErrorAsync(HttpContext.Response, StatusCodes.Status401Unauthorized,
                    "Your session is missing or expired. Sign in again.", ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            var questFilter = HttpContext.Request.Query["filter"].Count == 0 ? "all" : HttpContext.Request.Query["filter"].ToString();
            if (!LocalQuests.IsKnownFilter(questFilter))
            {
                await LocalQuestAction.ErrorAsync(HttpContext.Response, StatusCodes.Status404NotFound,
                    "Unknown quest filter.", ct);
                return;
            }
            var questState = await equipmentStore.GetAsync(session.AccountId, ct);
            questState.NpcKills = await economyStore.NpcKillsAsync(session.AccountId, ct);
            questState.StreunerKills = await economyStore.NpcKillsAsync(session.AccountId, ct, "streuner");
            var questOffers = await economyStore.RewardOffersAsync("quest", ct);
            var questSearch = LocalQuestAction.Search(HttpContext.Request.Query);
            if (questDetail is not null && !LocalQuests.HasDetail(questState, questOffers, questFilter, questDetail, questSearch))
            {
                await LocalQuestAction.ErrorAsync(HttpContext.Response, StatusCodes.Status404NotFound,
                    "Quest detail was not found in this filter.", ct);
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteEconomyPageAsync(session, LocalQuests.Page(session, questState, questOffers, questFilter, questDetail, questClaimed,
                questSearch), ct);
            return;
        }

        // Menu-191: a non-canonical Dock query is 400 before the company-choice redirect, like Map overview.
        if (menuRedirect is not null && !hasCompany
            && string.Equals(menuRedirect.RequestedAction, "internalDock", StringComparison.OrdinalIgnoreCase)
            && !LocalHangars.ValidPageQuery(HttpContext.Request.Query))
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            await LocalHangarError.WriteAsync(HttpContext.Response, 400, "Invalid Hangar query.", ct);
            return;
        }

        // Menu-199: the same rule for Payment; the captured 302 used the canonical query.
        if (menuRedirect is not null && !hasCompany
            && string.Equals(menuRedirect.RequestedAction, "internalPayment", StringComparison.OrdinalIgnoreCase)
            && !LocalPaymentAction.ValidPageQuery(HttpContext.Request.Query))
        {
            await LocalPaymentAction.ErrorAsync(HttpContext.Response, 400, "Invalid Payment page request.", ct);
            return;
        }

        if (menuRedirect is not null && !hasCompany)
        {
            ClientRedirectResponse.Apply(HttpContext.Response, menuRedirect.Location);
            await HttpContext.Response.CompleteAsync();
            return;
        }

        if (menuRedirect is not null)
        {
            var dock = string.Equals(menuRedirect.RequestedAction, "internalDock", StringComparison.OrdinalIgnoreCase);
            if (dock)
            {
                HttpContext.Response.Headers.CacheControl = "no-store";
                if (!LocalHangars.ValidPageQuery(HttpContext.Request.Query))
                {
                    await LocalHangarError.WriteAsync(HttpContext.Response, 400, "Invalid Hangar query.", ct);
                    return;
                }
            }
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            var page = LocalCompanySelection.MenuPage(menuRedirect.RequestedAction);
            if (dock)
            {
                var activeSlot = await localHangarStore.GetActiveSlotAsync(session!.AccountId, ct);
                page = LocalHangars.Page(session, activeSlot, clock.GetUtcNow(),
                    LocalHangars.ActivatedSlot(HttpContext.Request.Query), LocalHangars.ShipSlot(HttpContext.Request.Query),
                    cockpitCatalog is null ? null : await cockpitCatalog.HangarsAsync(session, activeSlot, ct));
            }
            await WriteEconomyPageAsync(session!, page!, ct);
            return;
        }

        if (isMenuHub)
        {
            if (session is null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                HttpContext.Response.ContentType = "text/html; charset=UTF-8";
                await WriteClientHtmlAsync(LocalCompanySelection.SignedOutPage, ct);
                await HttpContext.Response.CompleteAsync();
                return;
            }
            // Captured native handoff: HTTP 200 text/html whose entire body is the dosid.
            // OpenWebLogin consumes the navigation URL; the companion login.php call reads
            // the same cookie. Browsers retain the local HTML hub at this action.
            if (IsUnityClient(HttpContext.Request)
                && string.Equals(requestContext.Query.Action, "internalWebGL", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Response.StatusCode = StatusCodes.Status200OK;
                HttpContext.Response.ContentType = "text/html; charset=UTF-8";
                HttpContext.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(session.Dosid);
                await HttpContext.Response.WriteAsync(session.Dosid, ct);
                await HttpContext.Response.CompleteAsync();
                return;
            }
            HttpContext.Response.StatusCode = StatusCodes.Status200OK;
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            var page = !hasCompany ? LocalCompanySelection.ChoicePage
                : string.Equals(requestContext.Query.From, "internalCompanyChoose", StringComparison.OrdinalIgnoreCase)
                    ? LocalCompanySelection.ConfirmationPage(session!.Company!)
                    : LocalCompanySelection.GamePage(session!.Company!, session.Username, session.GameUserId);
            if (hasCompany) await equipmentStore.GetAsync(session!.AccountId, ct);
            await WriteClientHtmlAsync(hasCompany
                ? LocalEconomyHeader.Add(page, await economyStore.BalancesAsync(session!.AccountId, ct)) : page, ct);
            await HttpContext.Response.CompleteAsync();
            return;
        }

        if (string.Equals(requestContext.Query.Action, "internalMapRevolution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestContext.Query.Action, "internalStart", StringComparison.OrdinalIgnoreCase))
        {
            if (session is null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                HttpContext.Response.ContentType = "text/html; charset=UTF-8";
                await WriteClientHtmlAsync(LocalCompanySelection.SignedOutPage, ct);
                return;
            }
            if (!hasCompany)
            {
                ClientRedirectResponse.Apply(HttpContext.Response, "/indexInternal.es?action=internalCompanyChoose");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            if (IsUnityClient(HttpContext.Request)
                && string.Equals(requestContext.Query.Action, "internalStart", StringComparison.OrdinalIgnoreCase))
            {
                ClientRedirectResponse.Apply(HttpContext.Response,
                    "/indexInternal.es?action=internalWebGL&from=internalStart");
                await HttpContext.Response.CompleteAsync();
                return;
            }
            HttpContext.Response.ContentType = "text/html; charset=UTF-8";
            await WriteClientHtmlAsync(Features.World.LocalSpacemapPage.Html, ct);
            return;
        }

        ClientRedirectResponse.Apply(HttpContext.Response,
            AdaptLocationForClient(HttpContext.Request, "/indexInternal.es?action=internalWebGL&from=internalStart"));
        await HttpContext.Response.CompleteAsync();
    }
}
