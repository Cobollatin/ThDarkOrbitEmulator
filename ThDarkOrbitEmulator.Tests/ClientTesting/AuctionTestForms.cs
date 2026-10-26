using Microsoft.AspNetCore.WebUtilities;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

namespace ThDarkOrbitEmulator.Tests.ClientTesting;

// Older Auction tests post keyless bodies; this signs them the way the Auction page would.
internal static class AuctionTestForms
{
    public static string WithKey(string? dosid, string query, string body, DateTimeOffset? issuedAt = null)
    {
        if (dosid is null || body.Contains("key=", StringComparison.Ordinal) || body.StartsWith('{') || body.StartsWith("--", StringComparison.Ordinal))
            return body;
        var operation = query.Contains("internalAuctionClaim", StringComparison.Ordinal) ? "claim" : "bid";
        var item = QueryHelpers.ParseQuery(body).TryGetValue("item", out var values) ? values[0] ?? "" : "";
        return $"{body}&key={Uri.EscapeDataString(LocalAuctionAction.CreateFormKey(dosid, operation, item, issuedAt))}";
    }
}
