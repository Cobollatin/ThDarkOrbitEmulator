using System.Xml.Linq;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.News.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.News;

// Public identifiers, countdowns, categories and background filenames from
// 20261021-224636-login decoded flows 84 and 86. Actions remain local stubs.
public static class CapturedNewsCatalog
{
    public sealed record Entry(string Name, int Seconds, int Priority, string Category, string Background);

    private static readonly Entry[] Entries =
    [
        new("50cent_deal", 414768, 1, "deals", "50cent-booster_s_201803.jpg"),
        new("unstableExpansionAug2022", 933168, 1, "news", "breaking_news_umexpansionAug22.jpg"),
        new("halloweenoct2026", 933168, 1, "news", "breaking_news_Halloween2026.jpg"),
        new("battlePass", 933168, 1, "deals", "battlepass_s_201907.jpg"),
        new("Voyagers2607", 1278768, 1, "events", "breaking_news_Voyagers__Ascent_202607_303x168.jpg"),
        new("bk_and_lf_sale", 65568, 1, "deals", "bootykey-logfile_s_201802.jpg"),
        new("bonus_gg_reward", 65568, 1, "events", "breaking_news_bonusgg.jpg"),
        new("obsidian202609", 328368, 1, "deals", "bbox_obsidian_s_202609.jpg"),
        new("TrinityTrials2601", 846768, 1, "events", "breaking_news_trinityTrialsJan2026.jpg"),
        new("LevelupResourceSales_202509", 933168, 2, "news", "breaking_news_LevelupResourceSales202509.jpg"),
        new("OdysseusLaser2024", 65568, 2, "news", "breaking_news_asmodeusLaser_202403.jpg"),
        new("DSE202505", 846768, 3, "events", "breaking_news_DSE_202505.jpg"),
        new("SeasonPass202608", 328368, 3, "events", "news_Season_Pass_202608.jpg"),
        new("basilisk202309", 933168, 4, "news", "breaking_news_basilisk202309.jpg"),
        new("stellar_pathfinder", 846768, 4, "news", "stellar_pathfinder_b_201907.jpg"),
        new("uridiumBank", 755550769, 4, "news", "uridiumbank_2019.jpg"),
        new("monthlyDeluxePack102020", 755550769, 4, "deals", "breaking_news_monthlyDeluxePack102020.jpg"),
        new("instagram", 755550769, 4, "news", "DO_instagram_breakingnews.jpg"),
        new("discord", 755550769, 10, "news", "discord_s_201802.jpg")
    ];

    public static IReadOnlyList<Entry> MenuEntries { get; } = Array.AsReadOnly(Entries);

    // Start of the Live login capture; news.php was fetched within the first minute.
    public static DateTimeOffset SnapshotAt { get; } = new(2026, 10, 21, 22, 46, 36, TimeSpan.FromHours(-5));

    public static UnityNewsResponse News() => new()
    {
        Data = Entries.Select(entry => new UnityNewsItem
        {
            NewsId = "BE_" + entry.Name,
            Seconds = entry.Seconds
        }).ToArray()
    };

    // Countdowns tick from the capture snapshot. When one reaches zero it restarts
    // with its captured duration (emulator choice; Live rotates notices instead),
    // so all 19 items stay listed and the brNews layers stay in step.
    public static UnityNewsResponse News(DateTimeOffset now) => new()
    {
        Data = Entries.Select(entry => new UnityNewsItem
        {
            NewsId = "BE_" + entry.Name,
            Seconds = RemainingSeconds(entry.Seconds, now)
        }).ToArray()
    };

    public static int RemainingSeconds(int snapshotSeconds, DateTimeOffset now)
    {
        if (snapshotSeconds <= 0)
            return 0;
        var elapsed = (long)Math.Floor((now - SnapshotAt).TotalSeconds);
        if (elapsed <= 0)
            return snapshotSeconds;
        return (int)(snapshotSeconds - elapsed % snapshotSeconds);
    }

    public static string BreakingNewsXml { get; } = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XDocument(
        new XDeclaration("1.0", "UTF-8", null),
        new XElement("root", Entries.Select(entry =>
            new XElement("layer",
                new XAttribute("name", entry.Name),
                new XAttribute("prio", entry.Priority),
                new XAttribute("countdown", "1"),
                new XAttribute("category", entry.Category),
                new XElement("config"),
                new XElement("assignments", new XElement("background", new XAttribute("assetID", entry.Background))),
                new XElement("actions", new XElement("button",
                    new XAttribute("style", "primary"),
                    new XAttribute("text", "Continue"),
                    new XAttribute("position", "center"),
                    new XElement("onclick",
                    new XElement("link", new XAttribute("href", "/indexInternal.es?action=internalCommandCenter")))))))))
        .ToString(SaveOptions.DisableFormatting);
}
