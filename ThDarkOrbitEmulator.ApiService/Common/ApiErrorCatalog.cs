namespace ThDarkOrbitEmulator.ApiService.Common;

public static class ApiErrorCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ApiErrorCode.CommonUnknown] = "An unexpected error occurred.",
        [ApiErrorCode.CommonValidationFailed] = "The request is invalid.",
        [ApiErrorCode.IdentityFeatureDisabled] = "This feature is currently disabled.",
        [ApiErrorCode.IdentityInvalidInput] = "Username and password are required.",
        [ApiErrorCode.IdentityInvalidCredentials] = "Username or password is incorrect.",
        [ApiErrorCode.IdentityAccountNotRegistered] = "No local account exists for that username. Register first.",
        [ApiErrorCode.IdentityAuthenticationFailed] = "Keycloak rejected the sign-in. Try registering again.",
        [ApiErrorCode.IdentityUsernameTaken] = "That username is already registered.",
        [ApiErrorCode.IdentityKeycloakUnavailable] = "Identity service is unavailable. Try again shortly.",
        [ApiErrorCode.IdentityUnauthorized] = "You must sign in to continue.",
        [ApiErrorCode.IdentityAccountNotFound] = "Account was not found."
    };

    public static string GetMessage(string code)
        => Messages.TryGetValue(code, out var message) ? message : Messages[ApiErrorCode.CommonUnknown];
}
