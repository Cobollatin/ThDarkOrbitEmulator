using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.ApiService.Features.GameAuth;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;

public static partial class LocalHangarEquipmentSelection
{
    // Emulator-defined bounds: starter stock never exceeds a few units, so 999 leaves headroom.
    public const int MaxQuantity = 999;
    private const int MaxPageParameterLength = 64;
    private static readonly string[] PageOptionalKeys = ["from", "prc", "lang"];
    // Menu-118: post-save notice key; the value is the changed item id.
    public const string SavedKey = "saved";

    public static string SavedLocation(int slot, int configuration, string itemId) =>
        $"/indexInternal.es?action=internalDockEquipment&slot={slot}&configuration={configuration}&{SavedKey}={Uri.EscapeDataString(itemId)}";

    public static async Task HandleAsync(HttpContext context, IGameAuthSessionStore sessions,
        LocalHangarEquipmentStore equipment, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsPost(request.Method))
        {
            response.Headers.Allow = "POST";
            await LocalHangarError.WriteAsync(response, 405, "Equipment changes must be submitted with POST.", ct);
            return;
        }
        if (!LocalHangarError.ValidActionQuery(request.Query)
            || request.Query["action"].ToString() is not ("internalDockEquipmentSet" or "internalDockEquipmentTransfer"))
        {
            await LocalHangarError.WriteAsync(response, 400, "Invalid equipment action.", ct);
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
            await LocalHangarError.WriteAsync(response, 403, "Choose a company before equipping a ship.", ct);
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
            await LocalHangarError.WriteAsync(response, 400, "The equipment form could not be read.", ct);
            return;
        }
        var transfer = request.Query["action"].ToString() == "internalDockEquipmentTransfer";
        var destination = 0;
        if (!(transfer
                ? LocalHangarError.HasExactly(form, "slot", "destination", "configuration", "item", "quantity")
                : LocalHangarError.HasExactly(form, "slot", "configuration", "item", "quantity"))
            || !TryParseChoice(form["slot"].ToString(), out var slot)
            || !TryParseChoice(form["configuration"].ToString(), out var configuration)
            || !TryParseQuantity(form["quantity"].ToString(), out var quantity)
            || transfer && (!TryParseChoice(form["destination"].ToString(), out destination)
                || destination == slot || quantity == 0)
            || !IsWellFormedItemId(form["item"].ToString()))
        {
            await LocalHangarError.WriteAsync(response, 400, "Invalid equipment fields.", ct);
            return;
        }
        var result = transfer
            ? await equipment.TransferAsync(session.AccountId, slot, destination, configuration,
                form["item"].ToString(), quantity, ct)
            : await equipment.SetAssignmentAsync(session.AccountId, slot, configuration,
                form["item"].ToString(), quantity, ct);
        if (result != LocalHangarChange.Applied)
        {
            await LocalHangarError.WriteAsync(response, result == LocalHangarChange.Conflict ? 409 : 400,
                result == LocalHangarChange.Conflict ? "Equipment selection is unchanged, exceeds available stock, or exceeds the ship's free slots." : "Invalid equipment selection.", ct);
            return;
        }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = SavedLocation(transfer ? destination : slot, configuration, form["item"].ToString());
    }

    /// <summary>Validates the GET page query: exactly one canonical action, slot and configuration, plus optional
    /// <c>from</c>/<c>prc</c>/<c>lang</c> (each once, at most 64 characters) and an optional lowercase
    /// <c>saved</c> item id (once, the same pattern as the form's <c>item</c>).</summary>
    public static bool TryParsePageQuery(IQueryCollection query, out int slot, out int configuration) =>
        TryParsePageQuery(query, out slot, out configuration, out _);

    public static bool TryParsePageQuery(IQueryCollection query, out int slot, out int configuration, out string? saved)
    {
        slot = 0;
        configuration = 0;
        saved = null;
        foreach (var (key, values) in query)
        {
            if (key is not ("action" or "slot" or "configuration" or SavedKey) && !PageOptionalKeys.Contains(key)) return false;
            if (values.Count != 1 || values[0] is not { } value || value.Length > MaxPageParameterLength
                || value.Any(char.IsControl)) return false;
        }
        if (query.ContainsKey(SavedKey))
        {
            if (!IsWellFormedItemId(query[SavedKey].ToString())) return false;
            saved = query[SavedKey].ToString();
        }
        var ok = query["action"].ToString() == "internalDockEquipment"
            && TryParseChoice(query["slot"].ToString(), out slot) && LocalHangars.IsValidSlot(slot)
            && TryParseChoice(query["configuration"].ToString(), out configuration);
        if (!ok) saved = null;
        return ok;
    }

    // Slot and configuration are each "1" or "2"; no sign, whitespace, or leading zero.
    private static bool TryParseChoice(string value, out int choice)
    {
        choice = value switch { "1" => 1, "2" => 2, _ => 0 };
        return choice != 0;
    }

    private static bool TryParseQuantity(string value, out int quantity)
    {
        quantity = 0;
        return QuantityPattern().IsMatch(value) && (quantity = int.Parse(value)) <= MaxQuantity;
    }

    internal static bool IsWellFormedItemId(string value) => ItemIdPattern().IsMatch(value);

    [GeneratedRegex("^(0|[1-9][0-9]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemIdPattern();
}
