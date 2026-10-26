using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local read-only Rankings / Hall of Fame. No Live ranking request was captured;
// categories, pilots, and scores are emulator-defined and never change account state.
public static class LocalRankings
{
    public const int PageSize = 10;
    public const int PilotsPerCompany = 12;
    public const int MaxValueLength = 64;

    public sealed record Category(string Code, string Title, long Base, long Step);

    public sealed record Entry(int Position, string Pilot, string Company, int Level, long Score);

    public sealed record Result(int Status, string Html);

    public static IReadOnlyList<Category> Categories { get; } =
    [
        new("experience", "Experience points", 98_000_000, 2_400_000),
        new("honour", "Honour points", 1_450_000, 36_000),
        new("rankpoints", "Rank points", 512_000, 12_000),
        new("aliens", "Aliens destroyed", 64_000, 1_500)
    ];

    public sealed record PilotStanding(Category Category, int GlobalPosition, int CompanyPosition, int Level, long Score);

    public sealed record CompanyStanding(int Position, string Company, long TotalScore, string TopPilot);

    public const int MaxPilotLength = 32;

    private static readonly string[] AllowedKeys = ["action", "category", "company", "page", "pilot", "q", "prc", "from"];

    private static readonly string[] Names =
    [
        "Nova", "Vortex", "Raptor", "Helix", "Orion", "Talon", "Zenith", "Specter", "Kestrel", "Quasar", "Nebula", "Draco"
    ];

    private static IEnumerable<CompanyDefinition> RankedCompanies =>
        CompanyCatalog.Companies.Where(company => company.Code != "tst");

    public static IReadOnlyList<Entry> Board(Category category, string? company)
    {
        var pilots = new List<(string Pilot, string Company, int Level, long Score)>();
        var companyIndex = 0;
        foreach (var definition in RankedCompanies)
        {
            for (var i = 0; i < PilotsPerCompany; i++)
            {
                // Deterministic interleaving so companies alternate near the top of the "all" board.
                var slot = i * 3 + companyIndex;
                pilots.Add(($"{Names[i]}-{definition.Code.ToUpperInvariant()}", definition.Code,
                    Math.Max(1, 32 - slot / 3),
                    Math.Max(1, category.Base - slot * category.Step + (companyIndex + 1) * 17)));
            }
            companyIndex++;
        }

        return pilots.Where(p => company is null || p.Company == company)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Pilot, StringComparer.Ordinal)
            .Select((p, index) => new Entry(index + 1, p.Pilot, p.Company, p.Level, p.Score))
            .ToArray();
    }

    public static IReadOnlyList<string> PilotNames { get; } = RankedCompanies
        .SelectMany(company => Names.Select(name => $"{name}-{company.Code.ToUpperInvariant()}"))
        .ToArray();

    // Positions on the global and own-company boards for every category; null when the pilot is not ranked.
    public static IReadOnlyList<PilotStanding>? Standings(string pilot)
    {
        if (!PilotNames.Contains(pilot, StringComparer.Ordinal))
            return null;
        return Categories.Select(category =>
        {
            var global = Board(category, null).Single(entry => entry.Pilot == pilot);
            var own = Board(category, global.Company).Single(entry => entry.Pilot == pilot);
            return new PilotStanding(category, global.Position, own.Position, global.Level, global.Score);
        }).ToArray();
    }

    // Company totals for a category, highest first; ties fall back to the company code.
    public static IReadOnlyList<CompanyStanding> CompanyStandings(Category category)
    {
        var board = Board(category, null);
        return RankedCompanies
            .Select(company => (Company: company.Code, Entries: board.Where(entry => entry.Company == company.Code).ToArray()))
            .OrderByDescending(item => item.Entries.Sum(entry => entry.Score)).ThenBy(item => item.Company, StringComparer.Ordinal)
            .Select((item, index) => new CompanyStanding(index + 1, item.Company, item.Entries.Sum(entry => entry.Score),
                item.Entries[0].Pilot))
            .ToArray();
    }

    // Menu-73: checks that need no session (keys, action, lengths, company filter, page and pilot syntax),
    // so the endpoint can answer 400 before 401 and the company redirect. Unknown selections stay 404 in Resolve.
    public static Result? Validate(IQueryCollection query)
    {
        foreach (var (key, values) in query)
        {
            // Query lookups ignore key case, so keys are compared ordinally (`CATEGORY` is tampering).
            if (!AllowedKeys.Contains(key, StringComparer.Ordinal))
                return Error(400, "Unknown ranking parameter.");
            if (values.Count != 1)
                return Error(400, $"Ranking parameter '{key}' must appear once.");
            var value = values.ToString();
            if (value.Length > MaxValueLength || value.Any(char.IsControl))
                return Error(400, $"Ranking parameter '{key}' is invalid.");
        }
        if (!string.Equals(query["action"].ToString(), "internalHallofFame", StringComparison.Ordinal))
            return Error(400, "Invalid Hall of Fame action.");

        if (query.ContainsKey("category") && query["category"].ToString().Length == 0)
            return Error(400, "Ranking category is required when supplied.");
        if (query["category"].ToString().Length > 32)
            return Error(400, "Ranking category is too long.");
        var companyCode = query["company"].ToString();
        if (companyCode.Length > 0 && companyCode != "all" && !RankedCompanies.Any(item => item.Code == companyCode))
            return Error(400, "Unknown ranking company filter.");
        if ((query.ContainsKey("page") && query["page"].ToString().Length == 0)
            || !TryPage(query["page"].ToString(), out _))
            return Error(400, "Ranking page must be a positive number.");
        if (query.ContainsKey("pilot"))
        {
            var pilot = query["pilot"].ToString();
            if (pilot.Length == 0 || pilot.Length > MaxPilotLength
                || !pilot.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return Error(400, "Ranking pilot is invalid.");
        }
        if (query.ContainsKey("q"))
        {
            var search = query["q"].ToString();
            if (search.Length is < 2 or > MaxPilotLength
                || !search.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return Error(400, "Ranking search is invalid.");
        }
        return null;
    }

    // The absent page defaults to one; otherwise 1-4 canonical ASCII digits.
    private static bool TryPage(string text, out int page)
    {
        page = 1;
        return text.Length == 0
            || (text.Length <= 4 && text.All(char.IsAsciiDigit) && text[0] != '0'
                && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1);
    }

    public static Result Resolve(IQueryCollection query, GameAuthSession session)
    {
        if (Validate(query) is { } invalid) return invalid;

        var categoryCode = query["category"].ToString();
        if (categoryCode.Length == 0) categoryCode = "experience";
        var category = Categories.FirstOrDefault(item => item.Code == categoryCode);
        if (category is null)
            return Error(404, "Unknown ranking category.");

        var companyCode = query["company"].ToString();
        var company = companyCode.Length > 0 && companyCode != "all" ? companyCode : null;
        TryPage(query["page"].ToString(), out var page);

        var search = query.ContainsKey("q") ? query["q"].ToString() : null;
        var board = Board(category, company);
        if (search is not null)
            board = board.Where(entry => entry.Pilot.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var pages = Math.Max(1, (board.Count + PageSize - 1) / PageSize);
        if (page > pages)
            return Error(404, "Ranking page does not exist.");

        if (query.ContainsKey("pilot"))
        {
            var pilot = query["pilot"].ToString();
            var standings = Standings(pilot);
            if (standings is null)
                return Error(404, "Unknown ranked pilot.");
            if (search is not null && !board.Any(entry => entry.Pilot == pilot))
                return Error(404, "Pilot is outside the selected ranking search.");
            return new Result(200, PilotPage(category, company, page, pilot, standings, search));
        }
        return new Result(200, Page(session, category, company, page, pages, board, search));
    }

    private static string PilotPage(Category category, string? company, int page, string pilot,
        IReadOnlyList<PilotStanding> standings, string? search)
    {
        var e = HtmlEncoder.Default;
        var pilotCompany = Board(category, null).Single(entry => entry.Pilot == pilot).Company;
        var visibleBoard = Board(category, company);
        if (search is not null)
            visibleBoard = visibleBoard.Where(entry => entry.Pilot.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var visibleIndex = visibleBoard.ToList().FindIndex(entry => entry.Pilot == pilot);
        var visibleEntry = visibleIndex < 0 ? null : visibleBoard[visibleIndex];
        var visiblePage = visibleIndex < 0 ? 0 : visibleIndex / PageSize + 1;
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Hall of Fame pilot - DarkOrbit local emulator</title><style>body{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}main{max-width:900px;margin:5vh auto;padding:24px}a{color:#8cdcff;margin-right:10px}table{width:100%;border-collapse:collapse}td,th{border-bottom:1px solid #4c7896;padding:6px;text-align:left}.current{color:#ffd27a}</style></head><body><main id="localHallOfFamePilot">
            """);
        html.Append("<h1>").Append(e.Encode(pilot)).Append("</h1><p id=\"rankingPilotCompany\">")
            .Append(e.Encode(CompanyCatalog.Find(pilotCompany)!.DisplayName))
            .Append(" · emulator-defined pilot</p><table id=\"rankingPilotTable\"><thead><tr><th>Category</th><th>Overall</th>")
            .Append("<th>In company</th><th>Level</th><th>Score</th></tr></thead><tbody>");
        foreach (var standing in standings)
            html.Append("<tr data-category=\"").Append(standing.Category.Code).Append('"')
                .Append(standing.Category == category ? " class=\"current\"" : "").Append("><td><a href=\"")
                .Append(e.Encode(Link(standing.Category.Code, company, 1, search)))
                .Append("\">").Append(e.Encode(standing.Category.Title)).Append("</a></td><td>#").Append(standing.GlobalPosition)
                .Append("</td><td>#").Append(standing.CompanyPosition).Append("</td><td>").Append(standing.Level)
                .Append("</td><td>").Append(standing.Score.ToString("N0", CultureInfo.InvariantCulture)).Append("</td></tr>");
        html.Append("</tbody></table><p id=\"rankingPilotContext\">");
        if (visibleEntry is null)
            html.Append("This pilot is outside the selected company. <a href=\"")
                .Append(e.Encode(Link(category.Code, pilotCompany, 1, search)))
                .Append("\">View their company ranking</a>");
        else
        {
            html.Append("Position #").Append(visibleEntry.Position).Append(" on the selected board");
            if (visiblePage != page)
                html.Append(" · <a href=\"").Append(e.Encode(Link(category.Code, company, visiblePage, search)))
                    .Append("\">View on page ").Append(visiblePage).Append("</a>");
        }
        html.Append("</p><p><a href=\"").Append(e.Encode(Link(category.Code, company, page, search)))
            .Append("\">Back to ranking</a><a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to menu</a></p></main></body></html>");
        return html.ToString();
    }

    private static string Page(GameAuthSession session, Category category, string? company, int page, int pages,
        IReadOnlyList<Entry> board, string? search)
    {
        var e = HtmlEncoder.Default;
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Hall of Fame - DarkOrbit local emulator</title><style>body{margin:0;background:#050b12;color:#e3f2fc;font:17px Arial,sans-serif}main{max-width:900px;margin:5vh auto;padding:24px}a{color:#8cdcff;margin-right:10px}table{width:100%;border-collapse:collapse}td,th{border-bottom:1px solid #4c7896;padding:6px;text-align:left}.own{color:#ffd27a}</style></head><body><main id="localHallOfFame"><h1>Hall of Fame</h1><p>Local emulator rankings (emulator-defined pilots and scores)</p>
            """);
        int MatchingCount(Category selectedCategory, string? selectedCompany) =>
            Board(selectedCategory, selectedCompany).Count(entry => search is null
                || entry.Pilot.Contains(search, StringComparison.OrdinalIgnoreCase));
        html.Append("<nav id=\"rankingCategories\">");
        foreach (var item in Categories)
        {
            var count = MatchingCount(item, company);
            html.Append("<a href=\"").Append(e.Encode(Link(item.Code, company, 1, search))).Append("\" data-count=\"")
                .Append(count).Append('"').Append(item == category ? " aria-current=\"page\"" : "")
                .Append('>').Append(e.Encode(item.Title)).Append(" (").Append(count).Append(")</a>");
        }
        var allCompanyCount = MatchingCount(category, null);
        html.Append("</nav><nav id=\"rankingCompanies\"><a href=\"").Append(e.Encode(Link(category.Code, null, 1, search))).Append("\"")
            .Append(" data-count=\"").Append(allCompanyCount).Append('"')
            .Append(company is null ? " aria-current=\"page\"" : "")
            .Append(">All companies (").Append(allCompanyCount).Append(")</a>");
        foreach (var item in RankedCompanies)
        {
            var count = MatchingCount(category, item.Code);
            html.Append("<a href=\"").Append(e.Encode(Link(category.Code, item.Code, 1, search))).Append("\" data-count=\"")
                .Append(count).Append('"').Append(item.Code == company ? " aria-current=\"page\"" : "")
                .Append('>').Append(e.Encode(item.DisplayName)).Append(" (").Append(count).Append(")</a>");
        }
        html.Append("</nav><form id=\"rankingSearch\" method=\"get\" action=\"/indexInternal.es\"><input type=\"hidden\" name=\"action\" value=\"internalHallofFame\"><input type=\"hidden\" name=\"category\" value=\"")
            .Append(e.Encode(category.Code)).Append("\"><input type=\"hidden\" name=\"company\" value=\"")
            .Append(e.Encode(company ?? "all")).Append("\"><label for=\"rankingQuery\">Find pilot</label> <input id=\"rankingQuery\" name=\"q\" minlength=\"2\" maxlength=\"32\" pattern=\"[A-Za-z0-9-]+\" required value=\"")
            .Append(e.Encode(search ?? "")).Append("\"> <button type=\"submit\">Search</button></form>");
        if (search is not null)
            html.Append("<p id=\"rankingSearchSummary\">").Append(board.Count)
                .Append(board.Count == 1 ? " pilot matches " : " pilots match ")
                .Append(e.Encode(search)).Append(". <a href=\"").Append(e.Encode(Link(category.Code, company, 1)))
                .Append("\">Clear search</a></p>");
        html.Append("<h2>").Append(e.Encode(category.Title)).Append(" · ")
            .Append(e.Encode(company is null ? "All companies" : CompanyCatalog.Find(company)!.DisplayName))
            .Append(" · page ").Append(page).Append(" of ").Append(pages).Append("</h2>");
        if (board.Count == 0)
            html.Append("<p id=\"rankingEmpty\">No pilots match this search.</p>");
        html.Append("<table id=\"rankingTable\"><thead><tr><th>#</th><th>Pilot</th><th>Company</th><th>Level</th><th>")
            .Append(e.Encode(category.Title)).Append("</th></tr></thead><tbody>");
        foreach (var entry in board.Skip((page - 1) * PageSize).Take(PageSize))
            html.Append("<tr><td>").Append(entry.Position).Append("</td><td><a href=\"")
                .Append(e.Encode(Link(category.Code, company, page, search) + "&pilot=" + entry.Pilot)).Append("\">")
                .Append(e.Encode(entry.Pilot)).Append("</a></td><td>").Append(e.Encode(entry.Company.ToUpperInvariant())).Append("</td><td>").Append(entry.Level)
                .Append("</td><td>").Append(entry.Score.ToString("N0", CultureInfo.InvariantCulture)).Append("</td></tr>");
        html.Append("</tbody></table><p id=\"ownRanking\" class=\"own\">Your pilot ")
            .Append(e.Encode(session.Username ?? "pilot"))
            .Append(" is unranked (0 points): local rankings do not track gameplay.</p>");
        AppendEntryThreshold(html, category, company);
        var standings = CompanyStandings(category);
        AppendOwnCompany(html, session.Company, category, standings);
        html.Append("<h3>Company standings</h3><table id=\"rankingCompanyStandings\"><thead><tr><th>#</th><th>Company</th>")
            .Append("<th>Total</th><th>Top pilot</th></tr></thead><tbody>");
        foreach (var standing in standings)
            html.Append("<tr data-company=\"").Append(standing.Company).Append('"')
                .Append(standing.Company == company ? " class=\"own\"" : "").Append("><td>").Append(standing.Position)
                .Append("</td><td>").Append(e.Encode(CompanyCatalog.Find(standing.Company)!.DisplayName))
                .Append(standing.Company == session.Company ? " <span class=\"ownCompanyTag\">(your company)</span>" : "").Append("</td><td>")
                .Append(standing.TotalScore.ToString("N0", CultureInfo.InvariantCulture)).Append("</td><td>")
                .Append(e.Encode(standing.TopPilot)).Append("</td></tr>");
        html.Append("</tbody></table><p>");
        if (page > 1)
            html.Append("<a href=\"").Append(e.Encode(Link(category.Code, company, page - 1, search))).Append("\">Previous</a>");
        if (page < pages)
            html.Append("<a href=\"").Append(e.Encode(Link(category.Code, company, page + 1, search))).Append("\">Next</a>");
        html.Append("<a href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\">Back to menu</a></p></main></body></html>");
        return html.ToString();
    }

    // Menu-102: the signed-in pilot's company in the current category (position, total, gap to the
    // company above, top pilot). TST is the local test company and is not on the boards.
    private static void AppendOwnCompany(StringBuilder html, string? companyCode, Category category,
        IReadOnlyList<CompanyStanding> standings)
    {
        var e = HtmlEncoder.Default;
        var definition = companyCode is null ? null : CompanyCatalog.Find(companyCode);
        html.Append("<p id=\"ownCompanyRanking\" class=\"own\"");
        if (definition is null)
        {
            html.Append(">Your company is not ranked.</p>");
            return;
        }
        var index = standings.ToList().FindIndex(item => item.Company == definition.Code);
        html.Append(" data-company=\"").Append(e.Encode(definition.Code)).Append("\">Your company ")
            .Append(e.Encode(definition.DisplayName));
        if (index < 0)
        {
            html.Append(" is the local test company and is not ranked.</p>");
            return;
        }
        var own = standings[index];
        html.Append(" is #").Append(own.Position).Append(" of ").Append(standings.Count).Append(" in ")
            .Append(e.Encode(category.Title)).Append(" with ")
            .Append(own.TotalScore.ToString("N0", CultureInfo.InvariantCulture)).Append(" points");
        if (index > 0)
            html.Append(", ").Append((standings[index - 1].TotalScore - own.TotalScore).ToString("N0", CultureInfo.InvariantCulture))
                .Append(" behind ").Append(e.Encode(CompanyCatalog.Find(standings[index - 1].Company)!.DisplayName));
        else if (standings.Count > 1)
            html.Append(", ").Append((own.TotalScore - standings[1].TotalScore).ToString("N0", CultureInfo.InvariantCulture))
                .Append(" ahead of ").Append(e.Encode(CompanyCatalog.Find(standings[1].Company)!.DisplayName));
        html.Append(". Top pilot: <a href=\"")
            .Append(e.Encode(Link(category.Code, definition.Code, 1) + "&pilot=" + own.TopPilot)).Append("\">")
            .Append(e.Encode(own.TopPilot)).Append("</a> · <a href=\"")
            .Append(e.Encode(Link(category.Code, definition.Code, 1))).Append("\">View company board</a></p>");
    }

    // Menu-183: the score needed to enter the selected board (category and company filter, ignoring search):
    // one point above the last listed pilot, with a link to that pilot's board page.
    public static (Entry Last, long Needed, int Page) EntryThreshold(Category category, string? company)
    {
        var board = Board(category, company);
        var last = board[^1];
        return (last, last.Score + 1, (board.Count + PageSize - 1) / PageSize);
    }

    private static void AppendEntryThreshold(StringBuilder html, Category category, string? company)
    {
        var e = HtmlEncoder.Default;
        var (last, needed, page) = EntryThreshold(category, company);
        html.Append("<p id=\"ownRankingThreshold\" data-entry-score=\"").Append(needed)
            .Append("\" data-entry-position=\"").Append(last.Position).Append("\">To enter this board you need ")
            .Append(needed.ToString("N0", CultureInfo.InvariantCulture)).Append(' ').Append(e.Encode(category.Title))
            .Append(" points, one more than #").Append(last.Position).Append(' ').Append(e.Encode(last.Pilot))
            .Append(" with ").Append(last.Score.ToString("N0", CultureInfo.InvariantCulture)).Append(". <a href=\"")
            .Append(e.Encode(Link(category.Code, company, page))).Append("\">View the last page</a></p>");
    }

    private static string Link(string category, string? company, int page, string? search = null) =>
        $"/indexInternal.es?action=internalHallofFame&category={category}&company={company ?? "all"}&page={page}"
        + (search is null ? "" : $"&q={Uri.EscapeDataString(search)}");

    public static Result Unauthorized() => Error(401, "Your session is missing or expired. Sign in again.");

    private static Result Error(int status, string message) => new(status,
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Hall of Fame error</title></head>" +
        $"<body><main><h1>Hall of Fame request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
        "<a href=\"/indexInternal.es?action=internalHallofFame\">Back to Hall of Fame</a></main></body></html>");
}
