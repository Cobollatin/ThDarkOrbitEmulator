using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using ThDarkOrbitEmulator.ApiService.Features.Economy;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only Messaging / Mail / Friends menu. No Live capture or IL2CPP literal
// establishes the official contract; pilots, notices, and limits are emulator-defined.
public sealed record LocalPilotContact(int Id, string Name, string Company);

public sealed record LocalMailMessage(int Id, int FromPilot, int ToPilot, string Subject, string Body, DateTimeOffset SentAt,
    bool Sent = false, bool Read = false);

public sealed record LocalMessagingState(List<LocalMailMessage> Messages, List<int> Friends, int NextId = 1, int Revision = 0);

public enum MessagingChange { Applied, Invalid, NotFound, Conflict, Full, Unchanged }

// With an EconomyStore, mailbox, friends, and single-use form keys persist in Postgres; cache-era state is imported once.
public sealed class LocalMessagingStore(IDistributedCache cache, EconomyStore? economy = null)
{
    public const int SystemSender = 0;
    public const int MaxFriends = 20;
    public const int MaxMessages = 50;
    public const int MaxMessageId = 999_999_999;
    public const int MaxSubjectLength = 60;
    public const int MaxBodyLength = 500;

    // Emulator-defined pilot directory; ids stay below the local GameUserId range used in tests.
    public static readonly IReadOnlyList<LocalPilotContact> Directory =
    [
        new(1001, "Nova-Hunter", "mmo"),
        new(1002, "Ironclad", "mmo"),
        new(1003, "Starbreaker", "eic"),
        new(1004, "Quasar_Kid", "eic"),
        new(1005, "Void Walker", "vru"),
        new(1006, "Helix", "vru"),
        new(1007, "Red Comet", "mmo"),
        new(1008, "Silent Orbit", "eic")
    ];

    // Emulator-defined system notices seeded into every new inbox.
    private static readonly (string Subject, string Body, int DaysAgo)[] SeedNotices =
    [
        ("Welcome to DarkOrbit", "Welcome, pilot. Visit the Hangar to prepare your ship and the Shop for equipment.", 3),
        ("Server maintenance", "Local emulator notice: scheduled maintenance windows are not simulated.", 2),
        ("Galaxy Gates", "Collect gate parts in Galaxy Gates and activate a gate once all parts are found.", 1)
    ];

    private static readonly SemaphoreSlim MutationLock = new(1, 1);
    private static readonly DistributedCacheEntryOptions StateOptions = new() { SlidingExpiration = TimeSpan.FromDays(30) };
    private static string Key(Guid accountId) => $"localmessaging:{accountId:N}";

    public static LocalPilotContact? FindPilot(int id) => Directory.FirstOrDefault(pilot => pilot.Id == id);

    // Sent mail is always read; received mail stays unread until opened or marked read (emulator-defined).
    public static bool IsUnread(LocalMailMessage message) => !message.Sent && !message.Read;

    public static string SenderName(int id) => id == SystemSender ? "DarkOrbit" : FindPilot(id)?.Name ?? "Unknown pilot";

    public static LocalMessagingState Seed(DateTimeOffset now) => new(
        SeedNotices.Select((notice, index) => new LocalMailMessage(index + 1, SystemSender, 0, notice.Subject, notice.Body,
            now.AddDays(-notice.DaysAgo))).ToList(), [], SeedNotices.Length + 1);

    public async Task<LocalMessagingState> GetAsync(Guid accountId, CancellationToken ct) =>
        await GetAsync(accountId, DateTimeOffset.UtcNow, ct);

    public async Task<LocalMessagingState> GetAsync(Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        if (economy is null) return await GetCachedAsync(accountId, now, ct);
        if (await cache.GetStringAsync(Key(accountId), ct) is not null)
        {
            await economy.ImportMessagingAsync(accountId, await GetCachedAsync(accountId, now, ct), ct);
            await cache.RemoveAsync(Key(accountId), ct);
        }
        return await economy.MessagingAsync(accountId, ct) ?? Seed(now);
    }

    private async Task<LocalMessagingState> GetCachedAsync(Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        var json = await cache.GetStringAsync(Key(accountId), ct);
        if (json is null) return Seed(now);
        LocalMessagingState? stored;
        try { stored = JsonSerializer.Deserialize<LocalMessagingState>(json); }
        catch (JsonException) { stored = null; }
        return stored is null ? Seed(now) : Sanitize(stored);
    }

    // Corrupt or edited cache entries and database rows never yield duplicate ids, unknown friends, or oversized lists.
    internal static LocalMessagingState Sanitize(LocalMessagingState stored)
    {
        var seen = new HashSet<int>();
        var messages = (stored.Messages ?? []).Where(message => message is not null && message.Id is > 0 and <= MaxMessageId
                && seen.Add(message.Id)
                && message.Subject is { Length: > 0 and <= MaxSubjectLength } && message.Body is { Length: <= MaxBodyLength }
                && (message.FromPilot == SystemSender || FindPilot(message.FromPilot) is not null || message.Sent))
            .OrderByDescending(message => message.SentAt).Take(MaxMessages).ToList();
        var friends = (stored.Friends ?? []).Where(id => FindPilot(id) is not null).Distinct().Take(MaxFriends).ToList();
        var nextId = Math.Max(Math.Max(1, stored.NextId), messages.Count == 0 ? 1 :
            (int)Math.Min((long)MaxMessageId + 1, (long)messages.Max(message => message.Id) + 1));
        return new LocalMessagingState(messages, friends, nextId, Math.Max(0, stored.Revision));
    }

    public Task<MessagingChange> AddFriendAsync(Guid accountId, int pilotId, int? expectedRevision, string? replayKey,
        CancellationToken ct) => MutateAsync(accountId, expectedRevision, replayKey, state =>
    {
        if (FindPilot(pilotId) is null) return (MessagingChange.NotFound, null);
        if (state.Friends.Contains(pilotId)) return (MessagingChange.Conflict, null);
        if (state.Friends.Count >= MaxFriends) return (MessagingChange.Full, null);
        return (MessagingChange.Applied, state with { Friends = [.. state.Friends, pilotId] });
    }, ct);

    public Task<MessagingChange> RemoveFriendAsync(Guid accountId, int pilotId, int? expectedRevision, string? replayKey,
        CancellationToken ct) => MutateAsync(accountId, expectedRevision, replayKey, state =>
        !state.Friends.Contains(pilotId) ? (FindPilot(pilotId) is null ? MessagingChange.NotFound : MessagingChange.Conflict, null)
            : (MessagingChange.Applied, state with { Friends = state.Friends.Where(id => id != pilotId).ToList() }), ct);

    public Task<MessagingChange> DeleteMessageAsync(Guid accountId, int messageId, int? expectedRevision, string? replayKey,
        CancellationToken ct) => MutateAsync(accountId, expectedRevision, replayKey, state =>
        state.Messages.All(message => message.Id != messageId) ? (MessagingChange.NotFound, null)
            : (MessagingChange.Applied, state with { Messages = state.Messages.Where(message => message.Id != messageId).ToList() }), ct);

    // Opening a received message marks it read without bumping the revision, so open forms stay valid.
    public Task<MessagingChange> MarkReadAsync(Guid accountId, int messageId, CancellationToken ct) =>
        MutateAsync(accountId, null, null, state =>
            state.Messages.FirstOrDefault(message => message.Id == messageId) is not { } found ? (MessagingChange.NotFound, null)
            : !IsUnread(found) ? (MessagingChange.Unchanged, null)
            : (MessagingChange.Applied, state with { Messages = state.Messages
                .Select(message => message.Id == messageId ? message with { Read = true } : message).ToList() }), ct, bumpRevision: false);

    // The "Mark all read" form is a normal revisioned, single-use action; nothing unread is reported as Unchanged.
    public Task<MessagingChange> MarkAllReadAsync(Guid accountId, int? expectedRevision, string? replayKey, CancellationToken ct) =>
        MutateAsync(accountId, expectedRevision, replayKey, state => !state.Messages.Any(IsUnread) ? (MessagingChange.Unchanged, null)
            : (MessagingChange.Applied, state with { Messages = state.Messages
                .Select(message => IsUnread(message) ? message with { Read = true } : message).ToList() }), ct);

    // Mail can only go to friends; the oldest message is dropped once the mailbox is full.
    public Task<MessagingChange> SendAsync(Guid accountId, int toPilot, string subject, string body, DateTimeOffset now,
        int? expectedRevision, string? replayKey, CancellationToken ct) => MutateAsync(accountId, expectedRevision, replayKey, state =>
    {
        if (FindPilot(toPilot) is null) return (MessagingChange.NotFound, null);
        if (!state.Friends.Contains(toPilot) || subject.Length is 0 or > MaxSubjectLength || body.Length > MaxBodyLength)
            return (MessagingChange.Invalid, null);
        if (state.NextId > MaxMessageId) return (MessagingChange.Full, null);
        var messages = new List<LocalMailMessage> { new(state.NextId, SystemSender, toPilot, subject, body, now, true) };
        messages.AddRange(state.Messages.OrderByDescending(message => message.SentAt).Take(MaxMessages - 1));
        return (MessagingChange.Applied, state with { Messages = messages, NextId = state.NextId + 1 });
    }, ct);

    private async Task<MessagingChange> MutateAsync(Guid accountId, int? expectedRevision, string? replayKey,
        Func<LocalMessagingState, (MessagingChange Result, LocalMessagingState? State)> change, CancellationToken ct,
        bool bumpRevision = true)
    {
        if (accountId == Guid.Empty) return MessagingChange.Invalid;
        if (economy is not null)
        {
            await GetAsync(accountId, ct);
            return await economy.ChangeMessagingAsync(accountId, expectedRevision, replayKey,
                () => Seed(DateTimeOffset.UtcNow), change, ct, bumpRevision);
        }
        await MutationLock.WaitAsync(ct);
        try
        {
            var replayCacheKey = replayKey is null ? null : $"localmessaging-action:{accountId:N}:{replayKey}";
            if (replayCacheKey is not null && await cache.GetStringAsync(replayCacheKey, ct) is not null)
                return MessagingChange.Conflict;
            var state = await GetAsync(accountId, ct);
            if (expectedRevision is not null && expectedRevision != state.Revision) return MessagingChange.Conflict;
            var (result, next) = change(state);
            if (result != MessagingChange.Applied || next is null) return result;
            await cache.SetStringAsync(Key(accountId),
                JsonSerializer.Serialize(next with { Revision = state.Revision + (bumpRevision ? 1 : 0) }), StateOptions, ct);
            if (replayCacheKey is not null)
                await cache.SetStringAsync(replayCacheKey, "1", new DistributedCacheEntryOptions
                    { AbsoluteExpirationRelativeToNow = LocalMessagingAction.FormLifetime + TimeSpan.FromMinutes(10) }, ct);
            return MessagingChange.Applied;
        }
        finally { MutationLock.Release(); }
    }
}

public static class LocalMessaging
{
    private static string E(string value) => HtmlEncoder.Default.Encode(value);

    private static string Form(GameAuthSession session, string action, string target, int revision, DateTimeOffset issued,
        string label, string extra = "") =>
        $"<form method=\"post\" action=\"/indexInternal.es?action={action}\">{extra}" +
        (target.Length == 0 ? "" : $"<input type=\"hidden\" name=\"{(action == "internalMessageDelete" ? "message" : "pilot")}\" value=\"{target}\">") +
        $"<input type=\"hidden\" name=\"rev\" value=\"{revision}\">" +
        $"<input type=\"hidden\" name=\"key\" value=\"{LocalMessagingAction.CreateFormKey(session.Dosid, action, target, revision, issued)}\">" +
        $"<button type=\"submit\">{label}</button></form>";

    // Menu-130: confirmation after a successful action. The text is derived from the saved state, never from the link,
    // and is hidden when the state no longer matches (the friend was removed again, the message reappeared, ...).
    public static string? Notice(LocalMessagingState state, string key, int value)
    {
        var inbox = state.Messages.Count(message => !message.Sent);
        var sentCount = state.Messages.Count(message => message.Sent);
        var mailbox = $"Mailbox: {state.Messages.Count} / {LocalMessagingStore.MaxMessages}.";
        var friends = $"Friends: {state.Friends.Count} / {LocalMessagingStore.MaxFriends}.";
        switch (key)
        {
            case "sent" when state.Messages.FirstOrDefault(message => message.Id == value && message.Sent) is { } mail:
                return $"Mail sent to {LocalMessagingStore.SenderName(mail.ToPilot)}: \"{mail.Subject}\". Sent folder: {sentCount}. {mailbox}" +
                    (state.Messages.Count >= LocalMessagingStore.MaxMessages ? " The mailbox is full; the oldest message is removed with each new one." : "");
            case "deleted" when value < state.NextId && state.Messages.All(message => message.Id != value):
                return $"Message deleted. Inbox: {inbox}, Sent: {sentCount}. {mailbox}";
            case "added" when state.Friends.Contains(value) && LocalMessagingStore.FindPilot(value) is { } added:
                return $"{added.Name} ({added.Company.ToUpperInvariant()}) added to your friends. {friends} You can now send {added.Name} mail." +
                    (state.Friends.Count >= LocalMessagingStore.MaxFriends ? " Your friend list is full." : "");
            case "removed" when !state.Friends.Contains(value) && LocalMessagingStore.FindPilot(value) is { } removed:
                return $"{removed.Name} removed from your friends. {friends}" +
                    (state.Friends.Count == 0 ? " Add a friend to send mail." : "");
            case "readall" when value == 1 && inbox > 0 && !state.Messages.Any(LocalMessagingStore.IsUnread):
                return $"All {inbox} received messages marked read.";
            default:
                return null;
        }
    }

    public static string Page(GameAuthSession session, LocalMessagingState state, int? openMessage = null,
        DateTimeOffset? issuedAt = null, string folder = "all", (string Key, int Value)? notice = null, string? search = null)
    {
        var noticeText = notice is { } shown ? Notice(state, shown.Key, shown.Value) : null;
        var noticeHtml = noticeText is null ? "" : $"<p id=\"messagingNotice\" role=\"status\">{E(noticeText)}</p>";
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var inbox = state.Messages.Where(message => !message.Sent).ToArray();
        var sent = state.Messages.Where(message => message.Sent).ToArray();
        var unread = inbox.Count(LocalMessagingStore.IsUnread);
        var matching = state.Messages.Where(message => search is null || Matches(message, search)).ToArray();
        var visible = matching.Where(message => folder == "all" || (folder == "sent") == message.Sent)
            .OrderByDescending(message => message.SentAt).ThenByDescending(message => message.Id).ToArray();
        var folderQuery = folder == "all" ? "" : $"&amp;folder={folder}";
        var searchQuery = search is null ? "" : $"&amp;q={E(Uri.EscapeDataString(search))}";
        var tabs = $"<nav id=\"folders\"><a href=\"/indexInternal.es?action=internalMessages{searchQuery}\"{(folder == "all" ? " aria-current=\"page\"" : "")}>All ({state.Messages.Count})</a> · " +
            $"<a href=\"/indexInternal.es?action=internalMessages&amp;folder=inbox{searchQuery}\"{(folder == "inbox" ? " aria-current=\"page\"" : "")}>Inbox ({inbox.Length})</a>" +
            (unread == 0 ? "" : $" <span id=\"unreadCount\">{unread} unread</span>") + " · " +
            $"<a href=\"/indexInternal.es?action=internalMessages&amp;folder=sent{searchQuery}\"{(folder == "sent" ? " aria-current=\"page\"" : "")}>Sent ({sent.Length})</a></nav>";
        var searchForm = $"<form method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalMessages\">" +
            (folder == "all" ? "" : $"<input type=\"hidden\" name=\"folder\" value=\"{folder}\">") +
            $"<label>Search mail <input name=\"q\" minlength=\"2\" maxlength=\"40\" value=\"{E(search ?? "")}\" required></label><button type=\"submit\">Search</button></form>" +
            (search is null ? "" : $"<p id=\"messageResults\">{visible.Length} matching messages. <a href=\"/indexInternal.es?action=internalMessages{folderQuery}\">Clear search</a></p>" +
                (visible.Length == 0 ? "<p id=\"noMessages\">No messages match this search.</p>" : ""));
        var filteredInbox = matching.Where(message => !message.Sent).ToArray();
        var filteredSent = matching.Where(message => message.Sent).ToArray();
        string Row(LocalMailMessage message) =>
            $"<tr{(LocalMessagingStore.IsUnread(message) ? " class=\"unread\"" : "")}><td>{E(message.Sent ? "To " + LocalMessagingStore.SenderName(message.ToPilot) : LocalMessagingStore.SenderName(message.FromPilot))}</td>" +
            $"<td><a href=\"/indexInternal.es?action=internalMessages{folderQuery}{searchQuery}&amp;message={message.Id}\">{(LocalMessagingStore.IsUnread(message) ? $"<strong>{E(message.Subject)}</strong> (new)" : E(message.Subject))}</a></td>" +
            $"<td>{message.SentAt:yyyy-MM-dd HH:mm}</td><td>{Form(session, "internalMessageDelete", message.Id.ToString(), state.Revision, issued, "Delete")}</td></tr>";
        var openIndex = Array.FindIndex(visible, message => message.Id == openMessage);
        var open = openIndex < 0 ? "" :
            $"<section id=\"message\"><h2>{E(visible[openIndex].Subject)}</h2><p>{E(visible[openIndex].Body)}</p>" +
            (openIndex > 0 ? $"<a id=\"previousMessage\" href=\"/indexInternal.es?action=internalMessages{folderQuery}{searchQuery}&amp;message={visible[openIndex - 1].Id}\">Previous</a> " : "") +
            (openIndex + 1 < visible.Length ? $"<a id=\"nextMessage\" href=\"/indexInternal.es?action=internalMessages{folderQuery}{searchQuery}&amp;message={visible[openIndex + 1].Id}\">Next</a> " : "") +
            $"<a id=\"closeMessage\" href=\"/indexInternal.es?action=internalMessages{folderQuery}{searchQuery}\">Close</a></section>";
        var friends = string.Join("", state.Friends.Select(id => LocalMessagingStore.FindPilot(id)!).Select(pilot =>
            $"<tr><td>{E(pilot.Name)}</td><td>{pilot.Company.ToUpperInvariant()}</td><td>{Form(session, "internalFriendRemove", pilot.Id.ToString(), state.Revision, issued, "Remove")}</td></tr>"));
        var directory = string.Join("", LocalMessagingStore.Directory.Where(pilot => !state.Friends.Contains(pilot.Id)).Select(pilot =>
            $"<tr><td>{E(pilot.Name)}</td><td>{pilot.Company.ToUpperInvariant()}</td><td>{Form(session, "internalFriendAdd", pilot.Id.ToString(), state.Revision, issued, "Add friend")}</td></tr>"));
        var options = string.Join("", state.Friends.Select(id => LocalMessagingStore.FindPilot(id)!)
            .Select(pilot => $"<option value=\"{pilot.Id}\">{E(pilot.Name)}</option>"));
        var compose = state.Friends.Count == 0 ? "<p>Add a friend to send mail.</p>" : Form(session, "internalMessageSend", "", state.Revision, issued, "Send",
            $"<select name=\"to\">{options}</select><input name=\"subject\" maxlength=\"{LocalMessagingStore.MaxSubjectLength}\" required>" +
            $"<textarea name=\"body\" maxlength=\"{LocalMessagingStore.MaxBodyLength}\"></textarea>");
        return $$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>Messages - DarkOrbit</title><style>body{background:#050b12;color:#e3f2fc;font:18px Arial,sans-serif}main{max-width:900px;margin:7vh auto}a{color:#8cdcff}td,th{padding:8px;border:1px solid #4c7896}button{background:#1789b5;color:white;padding:6px;border:0}</style></head><body><main id="messaging"><h1>Messages</h1><p>Pilot {{E(session.Username)}} · ID {{session.GameUserId}}</p>{{noticeHtml}}{{tabs}}{{searchForm}}{{open}}{{(folder == "sent" ? "" : $"<h2>Inbox ({inbox.Length})</h2>{(unread == 0 ? "" : Form(session, "internalMessageReadAll", "", state.Revision, issued, "Mark all read"))}<table id=\"inbox\"><tbody>{string.Join("", filteredInbox.Select(Row))}</tbody></table>")}}{{(folder == "inbox" ? "" : $"<h2>Sent ({sent.Length})</h2><table id=\"sent\"><tbody>{string.Join("", filteredSent.Select(Row))}</tbody></table>")}}<h2>Write mail</h2>{{compose}}<h2>Friends ({{state.Friends.Count}} / {{LocalMessagingStore.MaxFriends}})</h2><table id="friends"><tbody>{{friends}}</tbody></table><h2>Pilot directory</h2><table id="directory"><tbody>{{directory}}</tbody></table><p><small>Messaging is local only; mail is never delivered and changes no Credits, Uridium, or Hangar items.</small></p><p><a href="/indexInternal.es?action=internalWebGL&amp;from=internalStart">Back to game</a></p></main></body></html>
            """;
    }

    public static bool Matches(LocalMailMessage message, string search) =>
        message.Subject.Contains(search, StringComparison.OrdinalIgnoreCase)
        || message.Body.Contains(search, StringComparison.OrdinalIgnoreCase)
        || LocalMessagingStore.SenderName(message.Sent ? message.ToPilot : message.FromPilot)
            .Contains(search, StringComparison.OrdinalIgnoreCase);
}
