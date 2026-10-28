using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.UnityApi.Login;

public static class CockpitCatalogEndpoints
{
    private static readonly Regex Code = new("\\A[a-z][a-z0-9_-]{1,31}\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void MapCockpitCatalog(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/backoffice/cockpit").RequireAuthorization(EmulatorRoles.Admin).WithTags("Cockpit catalog");
        admin.MapGet("/languages", (CockpitCatalogStore store, CancellationToken ct) => store.LanguagesAsync(ct));
        admin.MapPut("/languages/{code}", async (string code, ClientLanguage input, CockpitCatalogStore store, CancellationToken ct) =>
        {
            if (code != input.Code || !LocalAccountPreferencesStore.Languages.Any(item => item.Code == code)
                || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || input.IsDefault && !input.Enabled
                || !input.IsDefault && (await store.LanguagesAsync(ct)).Any(item => item.Code == code && item.IsDefault))
                return Results.BadRequest();
            await store.UpsertLanguageAsync(input, ct); return Results.NoContent();
        });
        admin.MapGet("/servers", (CockpitCatalogStore store, CancellationToken ct) => store.ServersAsync(ct));
        admin.MapPut("/servers/{code}", async (string code, ClientServer input, CockpitCatalogStore store, CancellationToken ct) =>
        {
            if (code != input.Code || !Code.IsMatch(code) || string.IsNullOrWhiteSpace(input.InstanceName) || input.InstanceName.Length > 80
                || string.IsNullOrWhiteSpace(input.Host) || Uri.CheckHostName(input.Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)
                || input.MapId <= 0 || input.Status is not ("online" or "maintenance" or "offline") || input.IsDefault && !input.Enabled
                || !input.IsDefault && (await store.ServersAsync(ct)).Any(item => item.Code == code && item.IsDefault))
                return Results.BadRequest();
            await store.UpsertServerAsync(input, ct); return Results.NoContent();
        });
        admin.MapGet("/ships", (CockpitCatalogStore store, CancellationToken ct) => store.ShipsAsync(ct));
        admin.MapPut("/ships/{shipId}", async (string shipId, CatalogShip input, CockpitCatalogStore store, CancellationToken ct) =>
        {
            if (shipId != input.ShipId || !shipId.StartsWith("ship_", StringComparison.Ordinal) || shipId.Length > 80
                || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || input.ModelId <= 0
                || input.IconPath is null || !input.IconPath.StartsWith("/do_img/", StringComparison.Ordinal) || input.IconPath.Contains("..", StringComparison.Ordinal))
                return Results.BadRequest();
            await store.UpsertShipAsync(input, ct); return Results.NoContent();
        });
        admin.MapGet("/accounts/{accountId:guid}/hangars", (Guid accountId, CockpitCatalogStore store, CancellationToken ct) => store.AssignmentsAsync(accountId, ct));
        admin.MapPut("/accounts/{accountId:guid}/hangars/{slot:int}", async (Guid accountId, int slot, HangarAssignment input, CockpitCatalogStore store, CancellationToken ct) =>
        {
            if (accountId != input.AccountId || slot != input.SlotNumber || slot is < 1 or > 2 || input.HangarId is null || !Regex.IsMatch(input.HangarId, "\\A[0-9]{6}\\z"))
                return Results.BadRequest();
            if (!(await store.ShipsAsync(ct)).Any(item => item.ShipId == input.ShipId && item.Enabled)) return Results.BadRequest();
            await store.UpsertAssignmentAsync(input, ct); return Results.NoContent();
        });
        admin.MapGet("/locale-overrides", (CockpitCatalogStore store, CancellationToken ct) => store.LocaleOverridesAsync(ct));
        admin.MapPut("/locale-overrides/{language}/{category}/{key}", async (string language, string category, string key, LocaleOverride input, CockpitCatalogStore store, CancellationToken ct) =>
        {
            if (language != input.LanguageCode || category != input.Category || key != input.Key
                || !Code.IsMatch(language) || !Code.IsMatch(category) || !Regex.IsMatch(key, "\\A[a-zA-Z0-9_.-]{1,160}\\z")
                || input.Value is null || input.Value.Length > 4000 || !(await store.LanguagesAsync(ct)).Any(item => item.Code == language)) return Results.BadRequest();
            await store.UpsertLocaleOverrideAsync(input, ct); return Results.NoContent();
        });

        // Public descriptors are safe to read without a game session. The client build may
        // still have its own server/language picker transport, so these are explicit contracts.
        app.MapGet("/unityApi/languages.php", async (CockpitCatalogStore store, CancellationToken ct) =>
            Results.Json((await store.LanguagesAsync(ct)).Where(item => item.Enabled))).AllowAnonymous();
        app.MapGet("/unityApi/servers.php", async (CockpitCatalogStore store, CancellationToken ct) =>
            Results.Json((await store.ServersAsync(ct)).Where(item => item.Enabled))).AllowAnonymous();
    }
}
