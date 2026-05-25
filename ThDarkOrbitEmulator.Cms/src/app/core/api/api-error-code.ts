/** Machine-readable codes returned by ApiService (keep in sync with ApiErrorCode.cs). */
export const ApiErrorCode = {
  CommonUnknown: 'common.unknown',
  CommonValidationFailed: 'common.validation_failed',
  IdentityFeatureDisabled: 'identity.feature_disabled',
  IdentityInvalidInput: 'identity.invalid_input',
  IdentityInvalidCredentials: 'identity.invalid_credentials',
  IdentityAccountNotRegistered: 'identity.account_not_registered',
  IdentityAuthenticationFailed: 'identity.authentication_failed',
  IdentityUsernameTaken: 'identity.username_taken',
  IdentityKeycloakUnavailable: 'identity.keycloak_unavailable',
  IdentityUnauthorized: 'identity.unauthorized',
  IdentityAccountNotFound: 'identity.account_not_found',
  IdentityNetwork: 'identity.network'
} as const;

export type ApiErrorCodeValue = (typeof ApiErrorCode)[keyof typeof ApiErrorCode];
