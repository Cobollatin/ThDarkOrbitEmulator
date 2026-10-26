using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.Identity.Application;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Local browser flow; the official company confirmation request has not been captured.
public static class LocalCompanyChoiceAction
{
    // Emulator-defined: the navigation keys other local pages accept, plus `preview`
    // on the choice page and `company` on confirm.
    private static readonly string[] ChooseKeys = ["action", "from", "prc", "lang", "preview"];
    private static readonly string[] ConfirmKeys = ["action", "company", "from", "prc", "lang"];
    private const int MaxValueLength = 64;

    // Serializes first choices per account in this process so concurrent
    // confirmations for different companies cannot both persist.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = new();

    public static async Task HandleAsync(HttpContext context, GameAuthSession? session,
        IGameAuthSessionStore sessions, IIdentityCommandService identity, bool confirm, CancellationToken ct)
    {
        var response = context.Response;
        if (session is null)
        {
            await WriteErrorAsync(response, StatusCodes.Status401Unauthorized,
                "Your session is missing or expired. Sign in again to choose a company.", ct);
            return;
        }

        var queryError = ValidateQuery(context.Request.Query, confirm ? ConfirmKeys : ChooseKeys);
        if (queryError is not null)
        {
            await WriteErrorAsync(response, StatusCodes.Status400BadRequest, queryError, ct);
            return;
        }

        if (!confirm)
        {
            if (LocalCompanySelection.IsValid(session.Company))
            {
                ClientRedirectResponse.Apply(response, IndexInternalStartEndpoint.AdaptLocationForClient(context.Request, "/indexInternal.es?action=internalWebGL&from=internalStart"));
                await response.CompleteAsync();
                return;
            }

            var preview = context.Request.Query.TryGetValue("preview", out var previewValues) ? previewValues[0] : null;
            var previewError = ValidatePreview(preview);
            if (previewError is not null)
            {
                await WriteErrorAsync(response, previewError.Value.Status, previewError.Value.Message, ct);
                return;
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/html; charset=UTF-8";
            response.Headers.CacheControl = "no-store";
            await response.WriteAsync(LocalCompanySelection.ChoicePageFor(preview), ct);
            return;
        }

        var company = context.Request.Query["company"].ToString();
        if (!LocalCompanySelection.IsValid(company))
        {
            await WriteErrorAsync(response, StatusCodes.Status400BadRequest,
                "Unknown company. Choose one of the listed companies.", ct);
            return;
        }

        var gate = AccountLocks.GetOrAdd(session.AccountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read under the lock: a concurrent request may have chosen already.
            var current = await sessions.TryGetByDosidAsync(session.Dosid, ct);
            if (current is null)
            {
                await WriteErrorAsync(response, StatusCodes.Status401Unauthorized,
                    "Your session expired before the company was saved. Sign in again.", ct);
                return;
            }

            // A retry of the same browser navigation is harmless; another faction
            // would invalidate hangar and menu state already bound to this account.
            if (LocalCompanySelection.IsValid(current.Company))
            {
                if (!string.Equals(current.Company, company, StringComparison.Ordinal))
                {
                    await WriteErrorAsync(response, StatusCodes.Status409Conflict,
                        $"This pilot already serves {LocalCompanySelection.DisplayName(current.Company)}; the company cannot be changed.", ct);
                    return;
                }
            }
            else if (!await identity.SetCompanyAsync(current.AccountId, company, ct))
            {
                // The account row is missing or another session already stored a different company.
                await WriteErrorAsync(response, StatusCodes.Status409Conflict,
                    "This account already has a different company or is no longer available. Sign in again.", ct);
                return;
            }
            else if (!await sessions.SelectCompanyAsync(current.Dosid, company, ct))
            {
                await WriteErrorAsync(response, StatusCodes.Status401Unauthorized,
                    "Your session expired before the company was saved. Sign in again.", ct);
                return;
            }
        }
        finally { gate.Release(); }

        ClientRedirectResponse.Apply(response, IndexInternalStartEndpoint.AdaptLocationForClient(context.Request, "/indexInternal.es?action=internalWebGL&from=internalCompanyChoose"));
        await response.CompleteAsync();
    }

    internal static string? ValidateQuery(IQueryCollection query, IReadOnlyCollection<string> allowed)
    {
        foreach (var (key, values) in query)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
                return "The request contains an unsupported parameter.";
            if (values.Count != 1)
                return "Each parameter may appear only once.";
            if ((values[0]?.Length ?? 0) > MaxValueLength)
                return "A parameter value is too long.";
        }

        return null;
    }

    // Preview is read-only, so an unknown code is a missing page (404) rather than a bad form (400).
    public static (int Status, string Message)? ValidatePreview(string? preview)
    {
        if (preview is null)
            return null;
        if (preview.Length == 0 || preview.Any(char.IsControl))
            return (StatusCodes.Status400BadRequest, "The company preview is empty or malformed.");
        if (!LocalCompanySelection.IsValid(preview))
            return (StatusCodes.Status404NotFound, "Unknown company preview. Choose one of the listed companies.");
        return null;
    }

    private static async Task WriteErrorAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=UTF-8";
        response.Headers.CacheControl = "no-store";
        var link = status == StatusCodes.Status401Unauthorized
            ? "<a href=\"/index.es?action=externalHome\">Sign in</a>"
            : "<a href=\"/indexInternal.es?action=internalCompanyChoose\">Back to company choice</a>";
        await response.WriteAsync("<!doctype html><html><head><meta charset=\"utf-8\"><title>Company selection error</title></head>" +
            $"<body><main id=\"companyError\"><h1>Company selection failed ({status})</h1><p>{HtmlEncoder.Default.Encode(message)}</p>" +
            $"{link}</main></body></html>", ct);
    }
}
