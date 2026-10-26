using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThDarkOrbitEmulator.ApiService.Features;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.FlashApi.BrNews;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Mocks.Common;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter;
using ThDarkOrbitEmulator.ApiService.Features.Resources.CommandCenter.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class ClientStubResponseTests
{
    [Test]
    public void RedirectCapture_PreservesLiveCrossHostLocation()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 60952);
        const string liveLocation = "https://www.darkorbit.com/ProjectApi/Authentication";
        const string localLocation = "/ProjectApi/Authentication";

        Assert.That(EmulatorRedirectHelper.ToRedirectLocation(context.Request, liveLocation, localLocation),
            Is.EqualTo(localLocation));
        context.Request.Headers["X-Original-Host"] = "sas.bpsecure.com";
        Assert.That(EmulatorRedirectHelper.ToRedirectLocation(context.Request, liveLocation, localLocation),
            Is.EqualTo(liveLocation));
    }

    [Test]
    public void Links_UseClientFacingGameHostForRedirectedRequests()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 60952);

        Assert.That(ClientFacingGameHost.Resolve(context.Request), Is.EqualTo("http://localhost:60952"));

        foreach (var gameHost in new[] { "gbl3.darkorbit.com", "gbl6.darkorbit.com", "gbl12.darkorbit.com" })
        {
            context.Request.Headers["X-Original-Host"] = gameHost;
            var links = ClientStubResponses.Links(ClientFacingGameHost.Resolve(context.Request));
            Assert.Multiple(() =>
            {
                Assert.That(links.Imprint, Is.EqualTo("https://legal.bigpoint.com/PE/imprint/es"));
                Assert.That(links.Credits, Is.Empty);
                Assert.That(links.Forum, Is.EqualTo("/GameAPI.php?action=portal.redirectToBoard"));
                Assert.That(links.Help, Is.EqualTo("https://board-es.darkorbit.com/"));
                Assert.That(links.Support, Is.EqualTo($"https://{gameHost}/indexInternal.es?action=internalSupport"));
                Assert.That(links.AccountCenter, Is.EqualTo($"https://{gameHost}/indexInternal.es?action=internalAccount"));
            });
        }

        context.Request.Headers["X-Original-Host"] = "gbl3.darkorbit.com.evil.test";
        Assert.That(ClientFacingGameHost.Resolve(context.Request), Is.EqualTo("http://localhost:60952"));
        context.Request.Headers["X-Original-Host"] = "gbl0.darkorbit.com";
        Assert.That(ClientFacingGameHost.Resolve(context.Request), Is.EqualTo("http://localhost:60952"));
    }

    [TestCase(false, false, StatusCodes.Status404NotFound)]
    [TestCase(true, false, StatusCodes.Status401Unauthorized)]
    [TestCase(true, true, StatusCodes.Status200OK)]
    public async Task ForumLink_HandlesActionAndSession(bool knownAction, bool authorized, int expectedStatus)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await GameApiForumResponse.WriteAsync(context.Response, knownAction, authorized, CancellationToken.None);

        Assert.That(context.Response.StatusCode, Is.EqualTo(expectedStatus));
        if (expectedStatus == StatusCodes.Status200OK)
        {
            Assert.That(context.Response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            body.Position = 0;
            using var reader = new StreamReader(body);
            Assert.That(await reader.ReadToEndAsync(), Does.Contain("Back to game"));
        }
        else
        {
            Assert.That(body.Length, Is.Zero);
        }
    }

    [Test]
    public void ClientRedirect_UsesCapturedStatusAndHtmlContentType()
    {
        var response = new DefaultHttpContext().Response;
        response.Headers.Append("Set-Cookie", "dosid=synthetic; Path=/");

        ClientRedirectResponse.Apply(response, "/ProjectApi/Authentication?authUser=22&token=synthetic");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(response.ContentType, Is.EqualTo("text/html; charset=UTF-8"));
            Assert.That(response.Headers.Location.ToString(), Does.StartWith("/ProjectApi/Authentication"));
            Assert.That(response.Headers.SetCookie.ToString(), Does.Contain("dosid=synthetic"));
        });
    }

    [Test]
    public void LoginPayload_PreservesCapturedFieldsAndTypes()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);
        var session = new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session" };
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;

        Assert.That(payload["userID"]!.GetValue<string>(), Is.EqualTo("7"));
        Assert.That(payload["sessionID"]!.GetValue<string>(), Is.EqualTo("synthetic-session"));
        Assert.That(payload["cdn"]!.GetValue<string>(),
            Is.EqualTo("https://darkorbit-22.bpsecure.com/"));
        Assert.That(payload["host"]!.GetValue<string>(), Is.EqualTo("https://localhost:60952"));
        Assert.That(payload["languageXmlHash"], Is.Null);
        Assert.That(payload["useHash"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(payload["autoStartEnabled"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(payload["display2d"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(payload["fullscreen"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(payload["browser"]!.GetValue<string>(), Is.EqualTo("Unknown"));
        Assert.That(payload["gameclientAllowedInitDelay"]!.GetValue<string>(), Is.EqualTo("10"));
        foreach (var field in new[] { "itemXmlHash", "resourcesXmlHash", "resources3dXmlHash",
                     "resources3dparticlesXmlHash", "resourceachievementsXmlHash", "resourceEventsXmlHash",
                     "resourceJumpGateXmlHash", "profileXmlHash", "loadingscreenHash", "gameclientHash",
                     "loadingscreenAssetsXmlHash", "crossdomainHash" })
            Assert.That(payload[field]!.GetValue<string>(), Does.Match("^[a-fA-F0-9]{32}$"), field);
        Assert.That(payload["showAdvertisingHint"]!.GetValue<bool>(), Is.False);
        Assert.That(payload["serverInfo"]!["timeStamp"]!.GetValue<long>(), Is.GreaterThan(0));
        Assert.That(payload["hangars"]!["data"]!["hangars"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That(payload["hangars"]!["data"]!["faction"]!.GetValue<string>(), Is.EqualTo("0"));
        var hangar = payload["hangars"]!["data"]!["hangars"]![0]!;
        Assert.That(hangar["hangarId"]!.GetValue<string>(), Is.EqualTo("000007"));
        Assert.That(hangar["modelId"]!.GetValue<int>(), Is.EqualTo(534));
        Assert.That(hangar["shipId"]!.GetValue<string>(), Is.EqualTo("ship_pusat-plus"));
        Assert.That(hangar["tooltipIcon"]!.GetValue<string>(),
            Does.Match("^/do_img/global/items/ship/pusat-plus_100x100\\.png\\?__cv=[a-fA-F0-9]{32}$"));
        Assert.That(hangar["hangarActive"]!.GetValue<bool>(), Is.True);
        Assert.That(payload["hangars"]!["data"]!["hangars"]![1]!["hangarActive"]!.GetValue<bool>(), Is.False);

        var fixture = JsonNode.Parse(File.ReadAllText(FindFixture("unity-login.json")))!["data"]!.GetValue<string>();
        Assert.That(payload.AsObject().Select(pair => pair.Key).Order(),
            Is.EqualTo(JsonNode.Parse(fixture)!.AsObject().Select(pair => pair.Key).Order()));
    }

    [Test]
    public void LoginPayload_UsesClientFacingSchemeForRedirectedGameHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 60952);
        var session = new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session" };

        var direct = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;
        Assert.That(direct["requestScheme"]!.GetValue<string>(), Is.EqualTo("http"));
        Assert.That(direct["host"]!.GetValue<string>(), Is.EqualTo("http://localhost:60952"));

        context.Request.Headers["X-Original-Host"] = "gbl3.darkorbit.com";
        var redirected = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;
        Assert.That(redirected["requestScheme"]!.GetValue<string>(), Is.EqualTo("https"));
        Assert.That(redirected["host"]!.GetValue<string>(), Is.EqualTo("https://gbl3.darkorbit.com"));
        Assert.That(redirected["chatHost"]!.GetValue<string>(), Is.EqualTo("gbl3.darkorbit.com"));
        Assert.That(redirected["sharedImages"]!.GetValue<string>(), Is.EqualTo("https://gbl3.darkorbit.com"));
        Assert.That(redirected["boardLink"]!.GetValue<string>(), Does.StartWith("https://gbl3.darkorbit.com/"));

        context.Request.Headers["X-Original-Host"] = "gbl6.darkorbit.com";
        var redirectedGbl6 = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;
        Assert.That(redirectedGbl6["host"]!.GetValue<string>(), Is.EqualTo("https://gbl6.darkorbit.com"));

        context.Request.Headers["X-Original-Host"] = "gbl12.darkorbit.com";
        var redirectedGbl12 = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;
        Assert.That(redirectedGbl12["host"]!.GetValue<string>(), Is.EqualTo("https://gbl12.darkorbit.com"));
        Assert.That(redirectedGbl12["chatHost"]!.GetValue<string>(), Is.EqualTo("gbl12.darkorbit.com"));

        context.Request.Headers["X-Original-Host"] = "unrecognized.example";
        var unrecognized = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;
        Assert.That(unrecognized["host"]!.GetValue<string>(), Is.EqualTo("http://localhost:60952"));

        context.Request.Headers["X-Original-Host"] = "gbl3.darkorbit.com.evil.test";
        var spoofed = JsonNode.Parse(UnityLoginPayloadBuilder.Build(session, context.Request))!;
        Assert.That(spoofed["host"]!.GetValue<string>(), Is.EqualTo("http://localhost:60952"));
    }

    [Test]
    public void LoginPayload_PreservesLocalListenerPortWhenProxyOmitsItFromForwardedHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 60952);
        context.Request.Headers["X-Forwarded-Host"] = "localhost";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(
            new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session" }, context.Request))!;
        Assert.That(payload["host"]!.GetValue<string>(), Is.EqualTo("https://localhost:60952"));
        Assert.That(payload["boardLink"]!.GetValue<string>(),
            Is.EqualTo("https://localhost:60952/indexInternal.es?action=internalForum"));
    }

    [Test]
    public void LoginPayload_UsesConfiguredWebViewOriginForNativeDefaultPortRequests()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("api-game", 5505);
        context.Request.Headers["X-Forwarded-Host"] = "localhost";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["ClientGateway:HttpsOrigin"] = "https://localhost:60952" }).Build();
        context.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration).BuildServiceProvider();

        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(
            new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session" }, context.Request))!;
        Assert.That(payload["host"]!.GetValue<string>(), Is.EqualTo("https://localhost:60952"));
    }

    [Test]
    public void NewsButton_UsesStyledLocalCommandCenterPage()
    {
        var xml = ThDarkOrbitEmulator.ApiService.Features.UnityApi.News.CapturedNewsCatalog.BreakingNewsXml;
        Assert.That(xml, Does.Contain("/indexInternal.es?action=internalCommandCenter"));
        Assert.That(xml, Does.Not.Contain("/indexInternal.es?action=internalDock"));
    }

    [Test]
    public void LoginPayload_UsesAssignedHangarsAndManagedInstanceName()
    {
        var request = new DefaultHttpContext().Request;
        var custom = new ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login.Contracts.UnityHangar
        {
            SlotNumber = 1, ShipId = "ship_phoenix", ShipName = "Phoenix", ModelId = 1,
            HangarId = "123456", MapId = "1", HangarActive = true
        };
        var payload = JsonNode.Parse(UnityLoginPayloadBuilder.Build(
            new GameAuthSession { GameUserId = 7, Dosid = "synthetic-session" }, request,
            hangars: [custom], instanceName: "Global 3"))!;
        Assert.That(payload["hangars"]!["data"]!["hangars"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(payload["hangars"]!["data"]!["hangars"]![0]!["shipId"]!.GetValue<string>(), Is.EqualTo("ship_phoenix"));
        Assert.That(payload["serverInfo"]!["instanceName"]!.GetValue<string>(), Is.EqualTo("Global 3"));
    }

    [Test]
    public void LoginPayload_HangarIdIsStableAndAccountSpecific()
    {
        var context = new DefaultHttpContext();
        var first = JsonNode.Parse(UnityLoginPayloadBuilder.Build(
            new GameAuthSession { GameUserId = 7 }, context.Request))!;
        var second = JsonNode.Parse(UnityLoginPayloadBuilder.Build(
            new GameAuthSession { GameUserId = 8 }, context.Request))!;

        Assert.That(first["hangars"]!["data"]!["hangars"]![0]!["hangarId"]!.GetValue<string>(),
            Is.EqualTo("000007"));
        Assert.That(second["hangars"]!["data"]!["hangars"]![0]!["hangarId"]!.GetValue<string>(),
            Is.EqualTo("000008"));
    }

    [Test]
    public void XmlStubs_UseCapturedElementAndAttributeNames()
    {
        var news = XDocument.Parse(ClientStubResponses.BreakingNews);
        var layer = news.Root!.Element("layer")!;
        Assert.That(news.Root.Name.LocalName, Is.EqualTo("root"));
        Assert.That(layer.Attribute("prio"), Is.Not.Null);
        Assert.That(layer.Element("config"), Is.Not.Null);
        Assert.That((string?)layer.Attribute("countdown"), Is.AnyOf("0", "1"));
        Assert.That(layer.Element("assignments")!.Element("background")!.Attribute("assetID"), Is.Not.Null);
        Assert.That(layer.Element("actions")!.Element("button")!.Element("onclick")!.Element("link")!.Attribute("href"), Is.Not.Null);

        var resources = XDocument.Parse(CapturedCommandCenterResources.Xml);
        Assert.That(resources.Root!.Name.LocalName, Is.EqualTo("filecollection"));
        var locations = resources.Root.Elements("location").ToArray();
        var files = resources.Root.Elements("file").ToArray();
        Assert.That(locations, Has.Length.EqualTo(3));
        Assert.That(files, Has.Length.EqualTo(609));
        Assert.That(locations.Select(location => (string?)location.Attribute("id")),
            Is.EquivalentTo(new[] { "do_img_new_bg", "do_img_new_overlay", "resources_command-center" }));
        Assert.That(files.All(file => locations.Any(location =>
            (string?)location.Attribute("id") == (string?)file.Attribute("location"))), Is.True);
        Assert.That(files.All(file => System.Text.RegularExpressions.Regex.IsMatch(
            (string?)file.Attribute("hash") ?? string.Empty, "^[0-9a-f]{32}$")), Is.True);
        var file = files[0];
        foreach (var name in new[] { "debugView", "hash", "id", "location", "name", "type", "version" })
            Assert.That(file.Attribute(name), Is.Not.Null, name);
    }

    [Test]
    public async Task CommandCenterNewsWriter_EmitsExistingXmlAndHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 60952);
        context.Request.Headers["X-Forwarded-Host"] = "localhost";
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await CommandCenterNewsResponse.WriteAsync(context.Response, CancellationToken.None);

        body.Position = 0;
        using var reader = new StreamReader(body);
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("text/xml; charset=UTF-8"));
            Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store, no-cache, must-revalidate"));
            Assert.That(reader.ReadToEnd(), Is.EqualTo(ClientStubResponses.BreakingNews.Replace(
                "/indexInternal.es?action=internalCommandCenter",
                "https://localhost:60952/indexInternal.es?action=internalCommandCenter", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task CommandCenterResourceWriter_EmitsCapturedManifest()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await CapturedCommandCenterResources.WriteResponseAsync(context.Response, CancellationToken.None);

        body.Position = 0;
        using var reader = new StreamReader(body);
        var xml = await reader.ReadToEndAsync();
        Assert.Multiple(() =>
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/xml"));
            Assert.That(xml, Is.EqualTo(CapturedCommandCenterResources.Xml));
            Assert.That(context.Response.Headers["Content-Security-Policy"].ToString(),
                Is.EqualTo("frame-ancestors https://*.darkorbit.com/;"));
        });
    }

    [Test]
    public void LocalCompanySelection_OffersOnlyKnownCompanies()
    {
        Assert.That(LocalCompanySelection.ChoicePage, Does.Contain("id=\"companySelection\""));
        foreach (var company in new[] { "mmo", "eic", "vru", "tst" })
        {
            Assert.That(LocalCompanySelection.ChoicePage, Does.Contain($"id=\"confirm_{company}\""));
            Assert.That(LocalCompanySelection.ChoicePage, Does.Contain($"name=\"company\" value=\"{company}\""));
            Assert.That(LocalCompanySelection.IsValid(company), Is.True);
        }
        Assert.That(LocalCompanySelection.ChoicePage, Does.Contain("value=\"internalCompanyConfirm\""));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(LocalCompanySelection.ChoicePage, "<form class=\"company\"").Count, Is.EqualTo(4));
        Assert.That(LocalCompanySelection.IsValid("invalid"), Is.False);
        Assert.That(LocalCompanySelection.IsValid(null), Is.False);
        Assert.That(LocalCompanySelection.GamePage("mmo"), Does.Contain("id=\"game\""));
    }

    [TestCase("mmo", "Mars Mining Operations TEST")]
    [TestCase("eic", "Earth Industries Corporation TEST")]
    [TestCase("vru", "Venus Resources Unlimited TEST")]
    [TestCase("tst", "Test Company TEST")]
    public void LocalCompanyPages_RenderCustomDisplayName(string company, string displayName)
    {
        Assert.That(LocalCompanySelection.DisplayName(company), Is.EqualTo(displayName));
        Assert.That(LocalCompanySelection.ChoicePage, Does.Contain($"<h2>{displayName}</h2>"));
        Assert.That(LocalCompanySelection.ConfirmationPage(company), Does.Contain($"id=\"companyName\">{displayName}</p>"));
        Assert.That(LocalCompanySelection.GamePage(company), Does.Contain($"id=\"companyName\">{displayName}</p>"));
    }

    [Test]
    public void LinksAndNews_UseCapturedJsonNames()
    {
        var links = JsonNode.Parse(JsonSerializer.Serialize(ClientStubResponses.Links("https://localhost:60952")))!;
        Assert.That(links.AsObject().Select(pair => pair.Key).Order(),
            Is.EqualTo(new[] { "imprint", "credits", "forum", "support", "help", "accountCenter" }.Order()));
        Assert.That(links["support"]!.GetValue<string>(), Does.StartWith("https://localhost:60952/"));

        var news = JsonNode.Parse(JsonSerializer.Serialize(ClientStubResponses.News()))!;
        Assert.That(news.AsObject().Select(pair => pair.Key), Is.EqualTo(new[] { "data" }));
        Assert.That(news["data"]!.AsArray(), Has.Count.EqualTo(19));
        Assert.That(news["data"]![0]!.AsObject().Select(pair => pair.Key).Order(),
            Is.EqualTo(new[] { "newsId", "seconds" }.Order()));
        Assert.That(news["data"]![0]!["seconds"]!.GetValue<int>(), Is.GreaterThan(0));
    }

    [Test]
    public void NewsItem_ReferencesBreakingNewsLayerAndManifestAsset()
    {
        var news = ClientStubResponses.News();
        var breakingNews = XDocument.Parse(ClientStubResponses.BreakingNews);
        var manifest = XDocument.Parse(CapturedCommandCenterResources.Xml);

        Assert.That(news.Data.Select(item => item.NewsId), Is.Unique);
        Assert.That(breakingNews.Root!.Elements("layer").Count(), Is.EqualTo(news.Data.Count));

        foreach (var item in news.Data)
        {
            Assert.That(item.NewsId, Does.StartWith("BE_"));
            var layerName = item.NewsId[3..];
            var layer = breakingNews.Root!.Elements("layer")
                .SingleOrDefault(element => (string?)element.Attribute("name") == layerName);
            Assert.That(layer, Is.Not.Null, item.NewsId);
            Assert.That((string?)layer!.Attribute("countdown"), Is.AnyOf("0", "1"));
            Assert.That(item.Seconds, Is.GreaterThan(0));

            var assetId = (string?)layer.Element("assignments")?.Element("background")?.Attribute("assetID");
            Assert.That(assetId, Is.Not.Null.And.Not.Empty);
            Assert.That(manifest.Root!.Elements("file").Any(file =>
                $"{(string?)file.Attribute("id")}.{(string?)file.Attribute("type")}" == assetId),
                Is.True, assetId);
        }
    }

    [Test]
    public void CommandCenterCategories_MatchVersionedManifestAsset()
    {
        var manifest = XDocument.Parse(CapturedCommandCenterResources.Xml);
        var categoriesAsset = manifest.Root!.Elements("file").Single(file =>
            (string?)file.Attribute("id") == "categories" &&
            (string?)file.Attribute("location") == "resources_command-center");
        Assert.That((string?)categoriesAsset.Attribute("type"), Is.EqualTo("xml"));
        Assert.That((string?)categoriesAsset.Attribute("hash"),
            Does.Match("^[0-9a-f]{32}$"));

        var categories = XDocument.Parse(CapturedCommandCenterCategories.Xml);
        Assert.That(categories.Root!.Name.LocalName, Is.EqualTo("root"));
        var entries = categories.Root.Elements("category").ToList();
        Assert.That(entries.Select(entry => (string?)entry.Attribute("id")),
            Is.EqualTo(new[] { "sales", "events", "news", "deals", "upselling" }));
        Assert.That(entries.All(entry => int.TryParse((string?)entry.Attribute("prio"), out _)
                                         && !string.IsNullOrWhiteSpace((string?)entry.Attribute("textKey"))), Is.True);
        Assert.That((string?)entries[0].Attribute("isSpecial"), Is.EqualTo("true"));
        Assert.That(entries.Skip(1).All(entry => entry.Attribute("isSpecial") is null), Is.True);

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(CommandCenterCategoriesXmlResponse));
        using var reader = new StringReader(CapturedCommandCenterCategories.Xml);
        var contract = (CommandCenterCategoriesXmlResponse)serializer.Deserialize(reader)!;
        Assert.That(contract.Categories.Select(category => category.Id),
            Is.EqualTo(new[] { "sales", "events", "news", "deals", "upselling" }));
        Assert.That(contract.Categories.Select(category => category.Priority),
            Is.EqualTo(new[] { 5, 1, 3, 2, 5 }));
        Assert.That(contract.Categories.Single(category => category.IsSpecial).Id, Is.EqualTo("sales"));
        Assert.That(contract.Categories.Single(category => category.IsSpecialSpecified).Id, Is.EqualTo("sales"));
    }

    private static string FindFixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "ThDarkOrbitEmulator.Tests", "ClientTesting", "Fixtures", name);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(name);
    }

    [Test]
    public void GatewayForwardedRequest_ResolvesEmulatorHostAndRewritesLoginFormToLocalPath()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("api-game");
        context.Request.Headers["X-Forwarded-Host"] = "localhost:60952";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        Assert.Multiple(() =>
        {
            Assert.That(EmulatorRedirectHelper.IsEmulatorHost(context.Request), Is.True);
            Assert.That(ClientFacingGameHost.Resolve(context.Request), Is.EqualTo("https://localhost:60952"));
            Assert.That(
                EmulatorRedirectHelper.ToRedirectLocation(
                    context.Request,
                    "https://www.darkorbit.com/indexInternal.es?action=internalStart",
                    "/indexInternal.es?action=internalStart"),
                Is.EqualTo("/indexInternal.es?action=internalStart"));
        });

        const string sampleCapturedHtml = """
            <!DOCTYPE html>
            <html>
            <head><title>DarkOrbit Reloaded | MMO de acción espacial</title></head>
            <body>
            <img class="header_logo" src="https://darkorbit-22.bpsecure.com/do_img/global/intro/logo.png?__cv=123" alt="Logo" />
            <form name="bgcdw_login_form" method="post" class="bgcdw_login_form" action="https://sas.bpsecure.com/Sas/Authentication/Bigpoint?authUser=22&amp;token=abc">
                <input type="text" name="username" />
                <input type="password" name="password" />
            </form>
            <script src="https://js.hcaptcha.com/1/api.js?hl=es" async defer></script>
            <script src="https://sharedservices.bpsecure.com/bgc/js/bgc-2.0.0.min.js" defer></script>
            <footer>&copy; Bigpoint</footer>
            </body>
            </html>
            """;

        var rewritten = EmulatorRedirectHelper.RewriteLoginPageHtml(context.Request, sampleCapturedHtml);
        Assert.Multiple(() =>
        {
            Assert.That(rewritten, Does.Contain("action=\"/Sas/Authentication/Bigpoint?authUser=22&amp;token=abc\""));
            Assert.That(rewritten, Does.Not.Contain("action=\"https://sas.bpsecure.com/Sas/Authentication/Bigpoint"));
            Assert.That(rewritten, Does.Not.Contain("src=\"https://js.hcaptcha.com/1/api.js"));
            Assert.That(rewritten, Does.Not.Contain("src=\"https://sharedservices.bpsecure.com/bgc/js/bgc-2.0.0.min.js\""));
            Assert.That(rewritten, Does.Contain("<title>Space Emulator Portal | Local Login (Placeholder)</title>"));
            Assert.That(rewritten, Does.Contain("Space Emulator Placeholder"));
        });
    }
}
