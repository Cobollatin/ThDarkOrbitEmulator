namespace ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;

// Captured on gbl6.darkorbit.com before company selection: each menu request
// returns HTTP 302 to the company choice page. There is no response body DTO.
public sealed record PreCompanyMenuRedirect(string RequestedAction, string Location)
{
    private static readonly HashSet<string> CapturedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "internalDock",
        "internalGalaxyGates",
        "internalPayment"
    };

    public static PreCompanyMenuRedirect? ForAction(string? action)
    {
        if (action is null || !CapturedActions.Contains(action))
            return null;

        // The destination action is captured; the local URL is synthetic.
        return new PreCompanyMenuRedirect(action, "/indexInternal.es?action=internalCompanyChoose");
    }
}
