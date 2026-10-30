using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only account preferences. No official settings write contract was captured.
public sealed record LocalAccountPreferences(string Language, bool SoundEnabled, bool NotificationsEnabled);

// Unchanged: the submitted values already match the stored ones, so nothing is written and the revision stays.
public enum AccountPreferencesChange { Applied, Invalid, Conflict, Unchanged }

// With an EconomyStore, preferences and their revision persist in Postgres; cache-era state is imported once.
public sealed class LocalAccountPreferencesStore(IDistributedCache cache, EconomyStore? economy = null, CockpitCatalogStore? catalog = null)
{
    public static readonly LocalAccountPreferences Default = new("es", true, true);

    // Emulator-defined language list, matching the Unity client's language codes (DarkOrbit.Settings static
    // constructor) so the launch-screen choice can be stored as-is.
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("es", "Español"), ("en", "English"), ("de", "Deutsch"), ("fr", "Français"),
        ("it", "Italiano"), ("pl", "Polski"), ("tr", "Türkçe"), ("pt", "Português"),
        ("en_US", "English US"), ("sv", "Svenska"), ("ru", "Русский"), ("fi", "Suomi"), ("da", "Dansk"),
        ("no", "Norsk"), ("cs", "Čeština"), ("sk", "Slovenčina"), ("pt_BR", "Português BR"), ("ro", "Română"),
        ("el", "Ελληνικά"), ("bg", "Български"), ("hu", "Magyar"), ("es_CO", "Español (Colombia)"),
        ("es_VE", "Español (Venezuela)"), ("es_CL", "Español (Chile)"), ("es_AR", "Español (Argentina)"),
        ("es_PE", "Español (Perú)"), ("ja", "日本語")
    ];

    public const int MaxRevision = 1_000_000;
    private static readonly SemaphoreSlim MutationLock = new(1, 1);
    private static readonly DistributedCacheEntryOptions StateOptions = new() { SlidingExpiration = TimeSpan.FromDays(30) };
    private static string Key(Guid accountId) => $"localaccountprefs:{accountId:N}";

    private sealed record StoredPreferences(string Language, bool SoundEnabled, bool NotificationsEnabled, int Revision);

    public async Task<LocalAccountPreferences> GetAsync(Guid accountId, CancellationToken ct) =>
        (await GetStateAsync(accountId, ct)).Preferences;

    public async Task<(LocalAccountPreferences Preferences, int Revision)> GetStateAsync(Guid accountId, CancellationToken ct)
    {
        if (economy is null) return await GetCachedAsync(accountId, ct);
        if (await cache.GetStringAsync(Key(accountId), ct) is not null)
        {
            var (legacy, revision) = await GetCachedAsync(accountId, ct);
            await economy.ImportAccountPreferencesAsync(accountId, legacy, revision, ct);
            await cache.RemoveAsync(Key(accountId), ct);
        }
        return await economy.AccountPreferencesAsync(accountId, ct) ?? (Default, 0);
    }

    private async Task<(LocalAccountPreferences Preferences, int Revision)> GetCachedAsync(Guid accountId, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        if (json is null) return (Default, 0);
        StoredPreferences? stored;
        try { stored = JsonSerializer.Deserialize<StoredPreferences>(json); }
        catch (JsonException) { return (Default, 0); }
        if (stored is null) return (Default, 0);
        return Sanitize(stored.Language, stored.SoundEnabled, stored.NotificationsEnabled, stored.Revision);
    }

    // Corrupt cache entries and hand-edited database rows fall back to the default language and a clamped revision.
    public static (LocalAccountPreferences Preferences, int Revision) Sanitize(string? language, bool sound,
        bool notifications, int revision) =>
        (new LocalAccountPreferences(Languages.Any(item => item.Code == language) ? language! : Default.Language,
            sound, notifications), Math.Clamp(revision, 0, MaxRevision));

    public async Task<bool> SaveAsync(Guid accountId, string language, string sound, string notifications,
        CancellationToken ct) =>
        await SaveAsync(accountId, language, sound, notifications, null, ct)
            is AccountPreferencesChange.Applied or AccountPreferencesChange.Unchanged;

    public async Task<AccountPreferencesChange> SaveAsync(Guid accountId, string language, string sound,
        string notifications, int? expectedRevision, CancellationToken ct) =>
        (await SaveStateAsync(accountId, language, sound, notifications, expectedRevision, ct)).Change;

    // Returns the change and the revision it wrote (Applied) or found (Unchanged, Conflict).
    public async Task<(AccountPreferencesChange Change, int Revision)> SaveStateAsync(Guid accountId, string language,
        string sound, string notifications, int? expectedRevision, CancellationToken ct)
    {
        if (accountId == Guid.Empty || (catalog is null
                ? Languages.All(item => item.Code != language)
                : !(await catalog.LanguagesAsync(ct)).Any(item => item.Code == language && item.Enabled))
            || sound is not ("true" or "false") || notifications is not ("true" or "false"))
            return (AccountPreferencesChange.Invalid, 0);
        var requested = new LocalAccountPreferences(language, sound == "true", notifications == "true");
        if (economy is not null)
        {
            await GetStateAsync(accountId, ct); // imports a cache-era entry before the revision check
            return await economy.ChangeAccountPreferencesAsync(accountId, requested, expectedRevision, ct);
        }
        await MutationLock.WaitAsync(ct);
        try
        {
            var (current, revision) = await GetCachedAsync(accountId, ct);
            if (expectedRevision is not null && expectedRevision != revision) return (AccountPreferencesChange.Conflict, revision);
            if (current == requested) return (AccountPreferencesChange.Unchanged, revision);
            var next = revision >= MaxRevision ? 1 : revision + 1;
            var stored = new StoredPreferences(language, requested.SoundEnabled, requested.NotificationsEnabled, next);
            await cache.SetStringAsync(Key(accountId), JsonSerializer.Serialize(stored), StateOptions, ct);
            return (AccountPreferencesChange.Applied, next);
        }
        finally { MutationLock.Release(); }
    }
}

public static class LocalAccountSettings
{
    public static string LanguageName(string code) =>
        LocalAccountPreferencesStore.Languages.FirstOrDefault(item => item.Code == code).Name
        ?? LanguageName(LocalAccountPreferencesStore.Default.Language);

    private static string OnOff(bool value) => value ? "On" : "Off";

    // Revision 0 means nothing was saved in the current 30-day cache window.
    public static string SaveSummary(int revision) => revision <= 0
        ? "Defaults (never saved)"
        : $"Saved {revision} {(revision == 1 ? "time" : "times")}";

    // Shown after the save redirect. A `saved` revision that is no longer current (another tab saved since,
    // or the page was bookmarked) gets a neutral notice instead of a false confirmation.
    public static string SavedNotice(int? savedRevision, int revision) => savedRevision switch
    {
        null => "",
        var saved when saved == revision =>
            $"<p id=\"accountSaved\" role=\"status\" data-rev=\"{revision}\">Preferences saved (change {revision}).</p>",
        var saved => $"<p id=\"accountSavedStale\" role=\"status\" data-rev=\"{saved}\">Preferences changed again after change {saved}. The values below are the latest.</p>"
    };

    // Shown after a save that matched the stored values: nothing was written and the change count stayed.
    public static string UnchangedNotice(bool unchanged, int revision) => unchanged
        ? $"<p id=\"accountUnchanged\" role=\"status\" data-rev=\"{revision}\">No changes to save. Your preferences already match.</p>"
        : "";

    public static string Page(GameAuthSession session, LocalAccountPreferences preferences, int revision = 0,
        int? savedRevision = null, bool unchanged = false,
        IReadOnlyList<(string Code, string Name)>? availableLanguages = null)
    {
        var languageChoices = availableLanguages ?? LocalAccountPreferencesStore.Languages;
        var languages = string.Join("", languageChoices.Select(item =>
            $"<option value=\"{item.Code}\"{Selected(preferences.Language, item.Code)}>{HtmlEncoder.Default.Encode(item.Name)}</option>"));
        static string Selected(string current, string value) => current == value ? " selected" : "";
        var lang = languageChoices.Any(item => item.Code == preferences.Language)
            ? preferences.Language : LocalAccountPreferencesStore.Default.Language;
        var companyName = LocalCompanySelection.DisplayName(session.Company);
        var company = companyName is null
            ? "<p id=\"accountCompany\">Company: none · <a href=\"/indexInternal.es?action=internalCompanyChoose\">Choose a company</a></p>"
            : $"<p id=\"accountCompany\" data-company=\"{session.Company}\">Company: {HtmlEncoder.Default.Encode(companyName)} ({session.Company!.ToUpperInvariant()})</p>";
        var defaultCode = languageChoices.FirstOrDefault().Code ?? LocalAccountPreferencesStore.Default.Language;
        var defaults = LocalAccountPreferencesStore.Default with { Language = defaultCode };
        string DisplayLanguage(string code) => languageChoices.FirstOrDefault(item => item.Code == code).Name ?? LanguageName(code);
        var isDefault = preferences == defaults;
        var reset = isDefault
            ? "<p id=\"accountDefaults\">These are the default preferences.</p>"
            : $"<form id=\"accountReset\" method=\"post\" action=\"/indexInternal.es?action=internalAccountSave\"><input type=\"hidden\" name=\"language\" value=\"{defaults.Language}\"><input type=\"hidden\" name=\"sound\" value=\"true\"><input type=\"hidden\" name=\"notifications\" value=\"true\"><input type=\"hidden\" name=\"rev\" value=\"{revision}\"><button type=\"submit\">Reset to defaults ({HtmlEncoder.Default.Encode(DisplayLanguage(defaults.Language))}, sound on, notifications on)</button></form>";
        var summary = $"<dl id=\"accountSummary\"><dt>Language</dt><dd id=\"accountLanguage\" data-language=\"{lang}\">{HtmlEncoder.Default.Encode(DisplayLanguage(lang))}</dd><dt>Sound</dt><dd id=\"accountSound\">{OnOff(preferences.SoundEnabled)}</dd><dt>Notifications</dt><dd id=\"accountNotifications\">{OnOff(preferences.NotificationsEnabled)}</dd><dt>Changes</dt><dd id=\"accountRevision\" data-rev=\"{revision}\">{SaveSummary(revision)}</dd></dl>";
        return $$"""
            <!doctype html><html lang="{{lang}}"><head><meta charset="utf-8"><title>Account center - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:800px;margin:7vh auto}a{color:#8cdcff}label{display:block;margin:16px 0}button{background:#1789b5;color:white;padding:10px;border:0}dt{float:left;clear:left;width:160px;color:#8cb4cc}dd{margin:0 0 8px 170px}</style></head><body><main id="localAccountCenter"><h1>Account center</h1><p>Pilot: {{HtmlEncoder.Default.Encode(session.Username)}} · ID {{session.GameUserId}}</p>{{company}}{{SavedNotice(savedRevision, revision)}}{{UnchangedNotice(unchanged, revision)}}<h2>Current preferences</h2>{{summary}}<p id="accountLoginLanguage">The local Unity login response uses the saved language.</p><h2>Preferences</h2><p id="accountNotice">Local emulator preferences only. Saving does not change the game client settings. Reload the page if another tab saved first.</p><form method="post" action="/indexInternal.es?action=internalAccountSave"><label>Language <select name="language">{{languages}}</select></label><label>Sound <select name="sound"><option value="true"{{Selected(preferences.SoundEnabled.ToString().ToLowerInvariant(), "true")}}>On</option><option value="false"{{Selected(preferences.SoundEnabled.ToString().ToLowerInvariant(), "false")}}>Off</option></select></label><label>Notifications <select name="notifications"><option value="true"{{Selected(preferences.NotificationsEnabled.ToString().ToLowerInvariant(), "true")}}>On</option><option value="false"{{Selected(preferences.NotificationsEnabled.ToString().ToLowerInvariant(), "false")}}>Off</option></select></label><input type="hidden" name="rev" value="{{revision}}"><button type="submit">Save preferences</button></form>{{reset}}<p><a href="/indexInternal.es?action=internalPilotSheet">Pilot Sheet</a> · <a href="/indexInternal.es?action=internalLogbook">Logbook</a> · <a href="/indexInternal.es?action=internalSupport&amp;section=account">Account help</a> · <a href="/indexInternal.es?action=internalSupport">Support</a> · <a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }
}
