using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Shared request validation for the read-only Forum, Help, and Support pages.
// The pages never change account state, so validation is the only guard needed.
public static partial class LocalLinksRequest
{
    public const int MaxIdLength = 32;
    public const int MaxValueLength = 64;
    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 40;

    public enum Page { Forum, Help, Support }

    public sealed record Result(int Status, string Html);

    // "from", "prc", and "lang" are tolerated because other local menu links carry them.
    private static readonly string[] CommonKeys = ["action", "from", "prc", "lang"];

    public static Result Resolve(IQueryCollection query, GameAuthSession? session, Page page, DateTimeOffset? now = null)
    {
        if (session is null)
            return Error(page, StatusCodes.Status401Unauthorized, "Your session is missing or has expired. Sign in again.");
        if (session.Company is not null && !LocalCompanySelection.IsValid(session.Company))
            return Error(page, StatusCodes.Status403Forbidden, "Your account has an unknown company. Choose a company again.");

        var idKey = page == Page.Support ? "section" : "topic";
        // Keys match exactly (as on the other local menus); only Forum and Help have categories.
        foreach (var (key, values) in query)
        {
            if (!string.Equals(key, idKey, StringComparison.Ordinal)
                && !(page != Page.Support && string.Equals(key, "category", StringComparison.Ordinal))
                && !string.Equals(key, "q", StringComparison.Ordinal)
                && !CommonKeys.Contains(key, StringComparer.Ordinal))
                return Error(page, StatusCodes.Status400BadRequest, "Unknown parameter. Open this page from the local menu.");
            if (values.Count != 1)
                return Error(page, StatusCodes.Status400BadRequest, "Each parameter must appear once.");
            if (values.ToString().Length > MaxValueLength)
                return Error(page, StatusCodes.Status400BadRequest, $"Parameter values must be at most {MaxValueLength} characters.");
            if (values.ToString().Any(char.IsControl))
                return Error(page, StatusCodes.Status400BadRequest, "Parameter values must not contain control characters.");
        }

        // Support: an omitted section opens the overview; an explicitly empty "section=" is malformed, as on Launcher
        // and Rankings. Forum and Help keep treating an empty topic as the index.
        if (page == Page.Support && query.ContainsKey(idKey) && query[idKey].ToString().Length == 0)
            return Error(page, StatusCodes.Status400BadRequest, $"The {idKey} must not be empty. Omit it to see all topics.");
        var id = query[idKey].ToString();
        string? selected = id.Length == 0 ? null : id;
        if (selected is not null && !IdPattern().IsMatch(selected))
            return Error(page, StatusCodes.Status400BadRequest,
                $"The {idKey} must be 1-{MaxIdLength} lowercase letters, digits, or hyphens.");

        string? category = null;
        if (page != Page.Support && query.ContainsKey("category"))
        {
            category = query["category"].ToString();
            if (!IdPattern().IsMatch(category))
                return Error(page, StatusCodes.Status400BadRequest,
                    $"The category must be 1-{MaxIdLength} lowercase letters, digits, or hyphens.");
            if (!LocalForumHelp.IsCategory(category))
                return Error(page, StatusCodes.Status404NotFound, "That category does not exist. Choose one from the list.");
        }

        // An empty or blank "q" (a submitted empty search box) means no search; the text is never echoed in errors.
        string? search = null;
        if (query.ContainsKey("q") && query["q"].ToString().Trim() is { Length: > 0 } text)
        {
            if (text.Length is < MinSearchLength or > MaxSearchLength)
                return Error(page, StatusCodes.Status400BadRequest,
                    $"Search text must be {MinSearchLength}-{MaxSearchLength} characters.");
            if (selected is not null)
                return Error(page, StatusCodes.Status400BadRequest, page == Page.Support
                    ? "Search cannot be combined with a section."
                    : "Search cannot be combined with a topic.");
            search = text;
        }

        var known = page == Page.Support ? LocalSupport.IsSection(selected) : LocalForumHelp.IsTopic(selected);
        if (!known)
            return Error(page, StatusCodes.Status404NotFound, $"That {idKey} does not exist. Choose one from the list.");
        if (!LocalForumHelp.InCategory(selected, category))
            return Error(page, StatusCodes.Status404NotFound, "That topic is not in the selected category. Choose one from the list.");

        return new(StatusCodes.Status200OK, page == Page.Support
            ? LocalSupport.Page(selected, session, now, search)
            : LocalForumHelp.Page(page == Page.Forum, selected, category, search));
    }

    public static async Task WriteAsync(HttpResponse response, Result result, CancellationToken ct)
    {
        response.StatusCode = result.Status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsync(
            IndexInternalStartEndpoint.AdaptHtmlForClient(response.HttpContext.Request, result.Html), ct);
    }

    public static Result Error(Page page, int status, string message)
    {
        var (title, action) = page switch
        {
            Page.Forum => ("Forum", "internalForum"),
            Page.Help => ("Help", "internalHelp"),
            _ => ("Support", "internalSupport")
        };
        return new(status,
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{title} error</title></head>" +
            $"<body><main id=\"localLinksError\"><h1>{title} request failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            (status == StatusCodes.Status401Unauthorized
                ? "<a href=\"/\">Back to sign-in</a>"
                : $"<a href=\"/indexInternal.es?action={action}\">Back to {title}</a>") +
            "</main></body></html>");
    }

    // GameAPI.php only answers the captured portal.redirectToBoard action.
    public static Result UnknownGameApiAction() =>
        Error(Page.Forum, StatusCodes.Status404NotFound, "Unknown GameAPI action. Only the forum board link is served locally.");

    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
