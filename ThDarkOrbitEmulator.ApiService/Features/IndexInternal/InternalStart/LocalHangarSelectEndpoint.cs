using FastEndpoints;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

// Emulator-only action. The official post-company Dock mutation contract is unknown.
public sealed class LocalHangarSelectEndpoint(
    IGameAuthSessionStore sessions,
    LocalHangarStore hangars,
    LocalHangarEquipmentStore equipment,
    LocalGalaxyGateStore galaxyGates,
    LocalSkylabStore skylab,
    LocalTechFactoryStore techFactory,
    LocalClanStore clans,
    LocalPilotSkillStore pilotSkills,
    LocalMessagingStore messaging,
    LocalAccountPreferencesStore accountPreferences,
    TimeProvider clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/indexInternal.es");
        RoutePrefixOverride(string.Empty);
        AllowAnonymous();
        Description(description => description.WithTags("Local Hangar"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (HttpContext.Request.Query["action"].ToString() is "internalDockEquipmentSet" or "internalDockEquipmentTransfer")
            await LocalHangarEquipmentSelection.HandleAsync(HttpContext, sessions, equipment, ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalShopBuy", StringComparison.OrdinalIgnoreCase))
            await LocalShopPurchase.HandleAsync(HttpContext, sessions, equipment, HttpContext.RequestServices.GetRequiredService<ThDarkOrbitEmulator.ApiService.Features.Economy.EconomyStore>(), clock, ct);
        else if (HttpContext.Request.Query["action"].ToString() is "internalGalaxyGateSpin" or "internalGalaxyGateActivate")
            await LocalGalaxyGateAction.HandleAsync(HttpContext, sessions, galaxyGates, ct);
        else if (HttpContext.Request.Query["action"].ToString() is "internalSkylabUpgrade" or "internalSkylabCollect")
            await LocalSkylabAction.HandleAsync(HttpContext, sessions, skylab, ct, clock);
        else if (LocalProgressionAction.Actions.Contains(HttpContext.Request.Query["action"].ToString()))
            await LocalProgressionAction.HandleAsync(HttpContext, sessions, skylab, galaxyGates,
                HttpContext.RequestServices.GetRequiredService<ThDarkOrbitEmulator.ApiService.Features.Economy.EconomyStore>(), clock, ct);
        else if (HttpContext.Request.Query["action"].ToString() is "internalTechFactoryStart" or "internalTechFactoryClaim")
            await LocalTechFactoryAction.HandleAsync(HttpContext, sessions, techFactory, ct, clock);
        else if (HttpContext.Request.Query["action"].ToString() is "internalClanJoin" or "internalClanLeave")
            await LocalClanAction.HandleAsync(HttpContext, sessions, clans, clock, ct);
        else if (HttpContext.Request.Query["action"].ToString() == "internalClanOperate")
            await LocalClanOperations.HandleAsync(HttpContext, sessions, clans, clock, ct);
        else if (HttpContext.Request.Query["action"].ToString() is "internalPilotSkillAllocate" or "internalPilotSkillReset")
            await LocalPilotSkillAction.HandleAsync(HttpContext, sessions, pilotSkills, clock, ct);
        else if (LocalMessagingAction.Actions.Contains(HttpContext.Request.Query["action"].ToString()))
            await LocalMessagingAction.HandleAsync(HttpContext, sessions, messaging, clock, ct);
        else if (HttpContext.Request.Query["action"].ToString() is "internalAuctionBid" or "internalAuctionClaim")
            await LocalAuctionAction.HandleAsync(HttpContext, sessions, equipment, HttpContext.RequestServices.GetRequiredService<ThDarkOrbitEmulator.ApiService.Features.Economy.EconomyStore>(), clock, ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalAssemblyCraft", StringComparison.OrdinalIgnoreCase))
            await LocalAssemblyAction.HandleAsync(HttpContext, sessions, equipment, HttpContext.RequestServices.GetRequiredService<ThDarkOrbitEmulator.ApiService.Features.Economy.EconomyStore>(), ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalQuestClaim", StringComparison.OrdinalIgnoreCase))
            await LocalQuestAction.HandleAsync(HttpContext, sessions, equipment, ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalAccountSave", StringComparison.OrdinalIgnoreCase))
            await LocalAccountSettingsAction.HandleAsync(HttpContext, sessions, accountPreferences, ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalPaymentClaim", StringComparison.OrdinalIgnoreCase))
            await LocalPaymentAction.HandleAsync(HttpContext, sessions, equipment, ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalVoucherRedeem", StringComparison.OrdinalIgnoreCase))
            await LocalVoucherAction.HandleAsync(HttpContext, sessions, equipment, ct);
        else if (string.Equals(HttpContext.Request.Query["action"], "internalSpaceSearchExplore", StringComparison.OrdinalIgnoreCase))
            await LocalSpaceSearchAction.HandleAsync(HttpContext, sessions, equipment, ct);
        else if (HttpContext.Request.Query["action"].ToString() is "internalBattlePassClaim" or "internalExpeditionStart" or "internalExpeditionClaim")
            await LocalEventsAction.HandleAsync(HttpContext, sessions, equipment, clock, ct);
        else
            await LocalHangarSelection.HandleAsync(HttpContext, sessions, hangars, ct, clock);
    }
}

public static class LocalHangarSelection
{
    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarStore hangars, CancellationToken ct, TimeProvider? clock = null)
    {
        var request = context.Request;
        var response = context.Response;
        if (!request.Query["action"].Any(value => string.Equals(value, "internalDockSelect", StringComparison.OrdinalIgnoreCase)))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            await LocalHangarError.WriteAsync(response, 405, "Hangar selection must be submitted with POST.", ct);
            return;
        }
        if (!LocalHangarError.ValidActionQuery(request.Query))
        {
            await LocalHangarError.WriteAsync(response, 400, "Invalid hangar action.", ct);
            return;
        }

        if (!request.Cookies.TryGetValue("dosid", out var dosid)
            || await sessions.TryGetByDosidAsync(dosid, ct) is not { } session)
        {
            await LocalHangarError.WriteAsync(response, 401, "Your session is missing or expired.", ct);
            return;
        }

        if (!LocalCompanySelection.IsValid(session.Company))
        {
            await LocalHangarError.WriteAsync(response, 403, "Choose a company before selecting a ship.", ct);
            return;
        }

        if (!request.HasFormContentType)
        {
            await LocalHangarError.WriteAsync(response, 400, "Expected a form submission.", ct);
            return;
        }
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or BadHttpRequestException)
        {
            await LocalHangarError.WriteAsync(response, 400, "The hangar form could not be read.", ct);
            return;
        }
        if (!LocalHangarError.HasExactly(form, "slot", "current", "key")
            || !LocalHangars.TryParseSlot(form["slot"], out var slot)
            || !LocalHangars.TryParseSlot(form["current"], out var current) || current == slot)
        {
            await LocalHangarError.WriteAsync(response, 400, "Invalid hangar slot.", ct);
            return;
        }
        var issued = LocalHangars.KeyIssued(session.Dosid, slot, current, form["key"].ToString());
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (issued is null || issued > now + LocalHangars.ClockSkew)
        {
            await LocalHangarError.WriteAsync(response, 400, "The form is invalid or has been changed.", ct);
            return;
        }
        if (now - issued > LocalHangars.FormLifetime)
        {
            await LocalHangarError.WriteAsync(response, 409, "This form has expired. Reload the Hangar and try again.", ct);
            return;
        }
        var result = await hangars.ChangeAsync(session.AccountId, slot, current, ct);
        if (result != LocalHangarChange.Applied)
        {
            await LocalHangarError.WriteAsync(response, result == LocalHangarChange.Conflict ? 409 : 400,
                result == LocalHangarChange.Conflict
                    ? "The active hangar changed since this page was loaded, or this form was already submitted."
                    : "Invalid hangar slot.", ct);
            return;
        }

        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = LocalHangars.ActivatedLocation(slot);
        response.Headers.CacheControl = "no-store";
    }
}
