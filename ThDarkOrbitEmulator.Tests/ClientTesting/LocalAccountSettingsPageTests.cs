using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

public class LocalAccountSettingsPageTests
{
    private static GameAuthSession Session(string? company = "mmo", string name = "pilot") =>
        new() { Username = name, GameUserId = 7, Company = company };

    [TestCase("mmo", "Mars Mining Operations TEST (MMO)")]
    [TestCase("eic", "Earth Industries Corporation TEST (EIC)")]
    [TestCase("vru", "Venus Resources Unlimited TEST (VRU)")]
    [TestCase("tst", "Test Company TEST (TST)")]
    public void Page_ShowsCompanyFullNameAndCode(string company, string expected)
    {
        var page = LocalAccountSettings.Page(Session(company), LocalAccountPreferencesStore.Default);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"data-company=\"{company}\">Company: {expected}</p>"));
            Assert.That(page, Does.Not.Contain("internalCompanyChoose"));
        });
    }

    [TestCase(null)]
    [TestCase("xyz")]
    public void Page_WithoutKnownCompany_LinksToCompanyChoice(string? company)
    {
        var page = LocalAccountSettings.Page(Session(company), LocalAccountPreferencesStore.Default);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("Company: none"));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalCompanyChoose\""));
            Assert.That(page, Does.Not.Contain("data-company="));
        });
    }

    [TestCase("es", "Español")]
    [TestCase("en", "English")]
    [TestCase("de", "Deutsch")]
    [TestCase("fr", "Français")]
    [TestCase("it", "Italiano")]
    [TestCase("pl", "Polski")]
    [TestCase("tr", "Türkçe")]
    [TestCase("pt", "Português")]
    public void Page_UsesSavedLanguageForLangAttributeAndSummary(string code, string name)
    {
        var page = LocalAccountSettings.Page(Session(), new LocalAccountPreferences(code, true, true), 3);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.StartWith($"<!doctype html><html lang=\"{code}\">"));
            Assert.That(page, Does.Contain($"data-language=\"{code}\">{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(name)}</dd>"));
            Assert.That(page, Does.Contain($"value=\"{code}\" selected"));
            Assert.That(Regex.Matches(page, " selected").Count, Is.EqualTo(3));
        });
    }

    [Test]
    public void Page_UnknownLanguage_FallsBackToDefault()
    {
        var page = LocalAccountSettings.Page(Session(), new LocalAccountPreferences("<xx>", true, true));
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.StartWith("<!doctype html><html lang=\"es\">"));
            Assert.That(page, Does.Contain("data-language=\"es\""));
            Assert.That(page, Does.Not.Contain("<xx>"));
            Assert.That(LocalAccountSettings.LanguageName("zz"), Is.EqualTo("Español"));
        });
    }

    [TestCase(true, false, "On", "Off")]
    [TestCase(false, true, "Off", "On")]
    [TestCase(false, false, "Off", "Off")]
    public void Page_SummaryShowsSoundAndNotifications(bool sound, bool notifications, string soundText, string notificationsText)
    {
        var page = LocalAccountSettings.Page(Session(), new LocalAccountPreferences("en", sound, notifications), 2);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"<dd id=\"accountSound\">{soundText}</dd>"));
            Assert.That(page, Does.Contain($"<dd id=\"accountNotifications\">{notificationsText}</dd>"));
        });
    }

    [TestCase(0, "Defaults (never saved)")]
    [TestCase(-4, "Defaults (never saved)")]
    [TestCase(1, "Saved 1 time")]
    [TestCase(2, "Saved 2 times")]
    [TestCase(LocalAccountPreferencesStore.MaxRevision, "Saved 1000000 times")]
    public void SaveSummary_DescribesRevision(int revision, string expected) =>
        Assert.That(LocalAccountSettings.SaveSummary(revision), Is.EqualTo(expected));

    [Test]
    public void Page_DefaultPreferences_HaveNoResetForm()
    {
        var page = LocalAccountSettings.Page(Session(), LocalAccountPreferencesStore.Default, 4);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("id=\"accountDefaults\""));
            Assert.That(page, Does.Not.Contain("id=\"accountReset\""));
            Assert.That(page, Does.Contain("data-rev=\"4\">Saved 4 times"));
            Assert.That(Regex.Matches(page, "action=internalAccountSave").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Page_ChangedPreferences_OfferResetWithCurrentRevision()
    {
        var page = LocalAccountSettings.Page(Session(), new LocalAccountPreferences("en", false, true), 5);
        var reset = Regex.Match(page, "<form id=\"accountReset\".*?</form>").Value;
        Assert.Multiple(() =>
        {
            Assert.That(reset, Is.Not.Empty);
            Assert.That(reset, Does.Contain("action=\"/indexInternal.es?action=internalAccountSave\""));
            Assert.That(reset, Does.Contain("name=\"language\" value=\"es\""));
            Assert.That(reset, Does.Contain("name=\"sound\" value=\"true\""));
            Assert.That(reset, Does.Contain("name=\"notifications\" value=\"true\""));
            Assert.That(reset, Does.Contain("name=\"rev\" value=\"5\""));
            Assert.That(Regex.Matches(reset, "name=\"").Count, Is.EqualTo(4));
            Assert.That(page, Does.Not.Contain("id=\"accountDefaults\""));
        });
    }

    [Test]
    public void Page_LinksToRelatedLocalMenus_AndHidesSessionSecrets()
    {
        var session = new GameAuthSession
        { Username = "pilot", GameUserId = 7, Company = "mmo", Dosid = "secret-dosid-value", AccountId = Guid.NewGuid() };
        var page = LocalAccountSettings.Page(session, LocalAccountPreferencesStore.Default);
        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalPilotSheet\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalLogbook\""));
            Assert.That(page, Does.Contain("href=\"/indexInternal.es?action=internalSupport&amp;section=account\""));
            Assert.That(LocalSupport.IsSection("account"), Is.True);
            Assert.That(page, Does.Not.Contain("secret-dosid-value"));
            Assert.That(page, Does.Not.Contain(session.AccountId.ToString()));
        });
    }

    [Test]
    public async Task ResetForm_PostedToSaveAction_RestoresDefaults_AndReplayConflicts()
    {
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var sessions = new GameAuthSessionStore(cache);
        var store = new LocalAccountPreferencesStore(cache);
        var session = (await sessions.CreateSessionAsync(
            new AccountSummary(Guid.NewGuid(), "pilot", null, 7), null, CancellationToken.None)).Session;
        Assert.That(await sessions.SelectCompanyAsync(session.Dosid, "eic", CancellationToken.None), Is.True);
        Assert.That(await store.SaveAsync(session.AccountId, "de", "false", "false", 0, CancellationToken.None),
            Is.EqualTo(AccountPreferencesChange.Applied));
        session = (await sessions.TryGetByDosidAsync(session.Dosid, CancellationToken.None))!;
        var (preferences, revision) = await store.GetStateAsync(session.AccountId, CancellationToken.None);
        var page = LocalAccountSettings.Page(session, preferences, revision);
        var body = FormBody(Regex.Match(page, "<form id=\"accountReset\".*?</form>").Value);

        var reset = Request(session.Dosid, body);
        await LocalAccountSettingsAction.HandleAsync(reset, sessions, store, CancellationToken.None);
        var replay = Request(session.Dosid, body);
        await LocalAccountSettingsAction.HandleAsync(replay, sessions, store, CancellationToken.None);

        var after = await store.GetStateAsync(session.AccountId, CancellationToken.None);
        var reloaded = LocalAccountSettings.Page(session, after.Preferences, after.Revision);
        Assert.Multiple(() =>
        {
            Assert.That(reset.Response.StatusCode, Is.EqualTo(303));
            Assert.That(replay.Response.StatusCode, Is.EqualTo(409));
            Assert.That(after.Preferences, Is.EqualTo(LocalAccountPreferencesStore.Default));
            Assert.That(after.Revision, Is.EqualTo(2));
            Assert.That(reloaded, Does.Contain("id=\"accountDefaults\""));
            Assert.That(reloaded, Does.Contain("Company: Earth Industries Corporation TEST (EIC)"));
        });
    }

    private static string FormBody(string form) => string.Join("&", Regex.Matches(form,
            "<input type=\"hidden\" name=\"([a-z]+)\" value=\"([^\"]*)\">")
        .Select(match => $"{match.Groups[1].Value}={Uri.EscapeDataString(match.Groups[2].Value)}"));

    private static DefaultHttpContext Request(string dosid, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Headers.Cookie = $"dosid={dosid}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }
}
