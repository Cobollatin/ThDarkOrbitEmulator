namespace ThDarkOrbitEmulator.ApiService.Common;

/// <summary>
/// Stable machine-readable error codes returned to clients (CMS, game clients).
/// </summary>
public static class ApiErrorCode
{
    public const string CommonUnknown = "common.unknown";
    public const string CommonValidationFailed = "common.validation_failed";

    public const string IdentityFeatureDisabled = "identity.feature_disabled";
    public const string IdentityInvalidInput = "identity.invalid_input";
    public const string IdentityInvalidCredentials = "identity.invalid_credentials";
    public const string IdentityAccountNotRegistered = "identity.account_not_registered";
    public const string IdentityAuthenticationFailed = "identity.authentication_failed";
    public const string IdentityUsernameTaken = "identity.username_taken";
    public const string IdentityKeycloakUnavailable = "identity.keycloak_unavailable";
    public const string IdentityUnauthorized = "identity.unauthorized";
    public const string IdentityAccountNotFound = "identity.account_not_found";
}
