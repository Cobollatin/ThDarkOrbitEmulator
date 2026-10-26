using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.CandidateEndpoints;

// Provisional Unity JSON for the local Space Search event. A live session with a company sees its own
// progress; anyone else gets the full catalog, as before. The query rules are emulator-defined.
public static class SpaceSearchCandidateAction
{
    public const string EventId = "local_space_search";
    public const int MaxQueryValueLength = 64;

    // Any key is tolerated because the official query is unknown, but each at most once and bounded.
    public static bool ValidQuery(IQueryCollection query) =>
        query.All(pair => pair.Key.Length is >= 1 and <= MaxQueryValueLength && !pair.Key.Any(char.IsControl)
            && pair.Value.Count == 1 && pair.Value.ToString().Length <= MaxQueryValueLength
            && !pair.Value.ToString().Any(char.IsControl));

    public static SpaceSearchResponse Search(LocalEquipmentState? state)
    {
        var remaining = LocalSpaceSearch.Catalog
            .Where(planet => state is null || !state.SearchedPlanetIds.Contains(planet.Id)).ToArray();
        return new SpaceSearchResponse
        {
            Planets = remaining.Select(planet => planet.Id).ToArray(),
            Events = remaining.Length == 0 ? [] : [EventId],
            Rewards = remaining.Select(planet => planet.RewardItemId).ToArray()
        };
    }

    // Without a planet the reward is the account's next unsearched planet, or the first planet otherwise.
    public static SpaceSearchRewardResponse Reward(LocalSpaceSearchPlanet? planet, LocalEquipmentState? state)
    {
        planet ??= LocalSpaceSearch.Catalog.FirstOrDefault(item => state is null || !state.SearchedPlanetIds.Contains(item.Id))
            ?? LocalSpaceSearch.Catalog[0];
        var owned = state?.Inventory.FirstOrDefault(item => item.Id == planet.RewardItemId);
        return new SpaceSearchRewardResponse
        {
            Name = planet.Name,
            Item = planet.RewardItemId,
            Title = owned?.Name ?? planet.RewardName,
            Package = EventId
        };
    }

    public static async Task HandleSearchAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct)
    {
        if (!ValidQuery(context.Request.Query))
        { await ErrorAsync(context.Response, 400, "Invalid Space Search request.", ct); return; }
        await WriteAsync(context, Search(await StateAsync(context, sessions, equipment, ct)), ct);
    }

    public static async Task HandleRewardsAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct)
    {
        var query = context.Request.Query;
        if (!ValidQuery(query))
        { await ErrorAsync(context.Response, 400, "Invalid Space Search reward request.", ct); return; }
        LocalSpaceSearchPlanet? planet = null;
        if (query.TryGetValue("planet", out var planetValue))
        {
            var planetId = planetValue.ToString();
            if (!LocalSpaceSearch.IsPlanetIdShape(planetId))
            { await ErrorAsync(context.Response, 400, "The planet value is invalid.", ct); return; }
            if ((planet = LocalSpaceSearch.Find(planetId)) is null)
            { await ErrorAsync(context.Response, 404, "That planet is not part of Space Search.", ct); return; }
        }
        await WriteAsync(context, Reward(planet, await StateAsync(context, sessions, equipment, ct)), ct);
    }

    private static async Task<LocalEquipmentState?> StateAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct) =>
        context.Request.Cookies.TryGetValue("dosid", out var dosid) && !string.IsNullOrWhiteSpace(dosid)
        && dosid.Length <= LocalLogoutAction.MaxDosidLength
        && await sessions.TryGetByDosidAsync(dosid, ct) is { } session && CompanyCatalog.Find(session.Company) is not null
            ? await equipment.GetAsync(session.AccountId, ct)
            : null;

    private static Task WriteAsync<T>(HttpContext context, T body, CancellationToken ct) =>
        CandidateResponse.WriteAsync(context, body, ct);

    // Error messages are fixed text; rejected values are never echoed.
    private static Task ErrorAsync(HttpResponse response, int status, string message, CancellationToken ct) =>
        CandidateResponse.WriteAsync(response.HttpContext, new { error = message, status }, ct, status);
}
