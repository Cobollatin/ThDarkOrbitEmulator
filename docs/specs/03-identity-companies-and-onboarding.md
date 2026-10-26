# SPEC-03: Identity Bounded Context, Companies & Onboarding

## 1. Scope & Purpose
This spec covers the Identity bounded context and the first-login onboarding flow:

- the `Account` aggregate and how it is persisted,
- registering accounts, handling duplicates and assigning game user IDs,
- the `CompanyCatalog` (`mmo`, `eic`, `vru`, `tst`) and the `LocalCompanySelection` choice and confirmation pages,
- starter equipment and currency provisioning,
- the Keycloak realm roles and permissions (`KeycloakRbacDefinitions`),
- the JSON HTTP slices `identity/register`, `identity/login` and `identity/me` (§8).

Verification:

| Kind | Test suite |
| :--- | :--- |
| Architecture | `DomainLayerPurityTests`, `ApplicationLayerConventionsTests`, `InfrastructureLayerConventionsTests`, `ApiServiceVerticalSliceTests.CompanyOnboarding_KeepsMapAndServerFactsInCatalogAndMapsSlice`, `ApiServiceVerticalSliceTests.IdentityHttpSlices_GateOnFlagFirstAndDependOnlyOnApplicationContracts` |
| Feature / contract | `Identity/IdentityAndOnboardingSpecificationTests`, `Identity/IdentityHttpEndpointSpecificationTests` (§8), `ClientTesting/CompanySessionTests`, `ClientTesting/LocalCompanyChoiceActionTests`, `ClientTesting/LocalCompanyChoiceValidationTests`, `ClientTesting/LocalCompanyConfirmationPageTests`, `ClientTesting/LocalCompanyPreviewTests`, `Backoffice/BackofficeIdentityServiceTests` |
| PostgreSQL (Docker) | `Integration/IdentityPostgresMigrationTests` |

> **Naming note:** there is no class named `LocalAccountStore`. Accounts are stored by `IdentityCommandService` and `IdentityQueryService`, which use `IdentityWriteDbContext` and `IdentityReadDbContext`. Per-account local state (inventory and wallet) is stored by `LocalHangarEquipmentStore` (§6). This spec uses those real names.

## 2. Layering & Domain Invariants
1. `ThDarkOrbitEmulator.Identity.Domain` contains only `Accounts/Account.cs`. It has no external dependencies.
2. `ThDarkOrbitEmulator.Identity.Application` defines:
   - `IIdentityCommandService` and `IIdentityQueryService`,
   - the commands and queries `RegisterAccountCommand`, `ValidateCredentialsQuery` and `GetAccountByUsernameQuery`, plus the `AccountSummary` result,
   - `CompanyCatalog`, `IKeycloakService` and the backoffice identity contracts.
3. `ThDarkOrbitEmulator.Identity.Infrastructure` implements:
   - the CQRS persistence (`IdentityWriteDbContext`, `IdentityReadDbContext`),
   - the numbered PostgreSQL migrations in `Migrations/Sql/NNN_*.{up,down}.pgsql`,
   - `GameUserIdGenerator`,
   - the Keycloak realm and RBAC bootstrap.
4. **`Account` shape.** `Account` is `sealed` and has exactly five public properties:

   | Property | Type | Default | Column |
   | :--- | :--- | :--- | :--- |
   | `Id` | `Guid` | a new GUID for each instance | `id` (PK) |
   | `Username` | `string` | empty | `username`, required, max 128, **unique** |
   | `Email` | `string?` | `null` | `email`, optional, max 256, not unique |
   | `GameUserId` | `long` | `0` until registration assigns it | `game_user_id`, required, **unique** |
   | `Company` | `string?` | `null` (company not chosen yet) | `company`, `VARCHAR(3)` NULL, check constraint |

   The table is `<schema>.accounts`. The default schema is `identity`.
5. **No secrets.** `Account` has no password field. Passwords exist only in Keycloak. `RegisterAccountCommand.Password` is never written to the identity store.
6. **Schema names.** `PostgresIdentifier.RequireSchema` accepts a schema name only if it matches `^[a-zA-Z_][a-zA-Z0-9_]*$`. Anything else throws `InvalidOperationException`, including empty or whitespace names, a leading digit, `.`, `;`, quotes and spaces. The check exists because the schema name is placed directly into raw SQL such as the sequence call.

## 3. Registration, Duplicates & Game User IDs
1. **Username normalization.** `IdentityCommandService.ExecuteAsync(RegisterAccountCommand)` trims the username (`string.Trim()`) and stores the trimmed value.
2. **Duplicate usernames.** If an account with the same trimmed username already exists, registration returns `false`. It adds no row and **does not draw from the ID sequence**. Usernames are compared ordinally and are case-sensitive, so `Pilot` and `pilot` are two different accounts. The database's unique index on `username` is the final guard against concurrent registrations.
   A rejected duplicate leaves the first account's ID, email, game user ID and company unchanged, even when the retry supplies a different email or password.
3. **Duplicate emails.** Email is optional and is stored exactly as given. It is **not** an identity key: two accounts may share an email, and `null` is allowed. If email-based uniqueness is ever required, it must be added here, in a new migration and in the tests together.
4. **Credential queries.**
   - `ValidateCredentialsQuery` returns `true` only when an account exists with the trimmed username. The password check is delegated to Keycloak (SPEC-01).
   - The lookup is by username only. Passing an email address, the empty string, or a different letter case returns `false`.
   - `GetAccountByUsernameQuery` returns an `AccountSummary(Id, Username, Email, GameUserId, Company)`, or `null` if no account matches.
5. **Game user ID sequence.**
   - Each successful registration calls `IGameUserIdGenerator.NextAsync()` exactly once.
   - `GameUserIdGenerator` runs `SELECT nextval('<schema>.game_user_id_seq')`.
   - Migrations `002` and `003` create the sequence `START WITH 100000000 INCREMENT BY 1` and make it the column default. They also add the unique index `ux_identity_accounts_game_user_id`.
   - IDs therefore start at `100000000` and increase strictly. A rejected duplicate never consumes an ID.

## 4. Company Catalog
1. `CompanyCatalog.Companies` lists exactly these companies, in this order:

   | Code | Display name | Faction ID | Starter map |
   | :--- | :--- | :--- | :--- |
   | `mmo` | Mars Mining Operations TEST | 1 | `1` |
   | `eic` | Earth Industries Corporation TEST | 2 | `5` |
   | `vru` | Venus Resources Unlimited TEST | 3 | `9` |
   | `tst` | Test Company TEST | 4 | `13` |

2. **Catalog invariants:**
   - Codes, faction IDs and starter maps are each unique.
   - Codes are three lowercase letters.
   - Display names end with ` TEST`, which keeps them placeholder branding.
   - The starter map is `(factionId - 1) * 4 + 1`.
3. **Exact matching.** `CompanyCatalog.Find` matches ordinally and exactly. It returns `null` for `null`, empty or whitespace input, upper- or mixed-case codes (`MMO`), padded codes (` mmo`) and unknown codes (`ru`, `xyz`). The helpers `LocalCompanySelection.IsValid`, `Faction`, `StarterMap` and `DisplayName` follow `Find` exactly: for any code `Find` rejects, they return `false` or `null`.
4. **Database constraint.** Migration `005_add_test_company` replaces the company check constraint with `CHECK (company IN ('mmo', 'eic', 'vru', 'tst'))`. That list must stay identical to the catalog codes.
5. **Map catalogue.** `LocalMapsXml.Maps` (`/xml/maps.php`) is built from the catalog. The choice and confirmation pages show the game server for a company's starter map taken from that list.

## 5. Company Choice & Confirmation Flow
1. **Choice page.** `internalCompanyChoose` renders `LocalCompanySelection.ChoicePage`, which shows one `<form>` per catalog company. Each form contains:
   - `action=internalCompanyConfirm`,
   - a hidden `company=<code>` field,
   - a `confirm_<code>` button labelled `Choose <CODE>`,
   - a `Details` link to `PreviewHref(code)`.

   When no preview is selected, the page has no `#companyPreview` and no `aria-current`.
2. **Preview.** `ChoicePageFor(code)` highlights exactly one company (`aria-current="true"`) and adds `#companyPreview` with a `#previewConfirm` button. Previewing never persists anything. An unknown preview code renders the same page as `ChoicePage`. The preview's faction and starter map come from `CompanyCatalog`; its game server comes from the `LocalMapsXml.Maps` entry for that map. The "Shares starter map with" field lists other catalog companies on that map, or `none` when there are none. All four current catalog maps are distinct, so every preview shows `none`. TST's preview says it uses its own starter map.
3. **Persistence: the first valid choice wins.** `IIdentityCommandService.SetCompanyAsync(accountId, company)`:
   - returns `false` for a non-catalog code and stores nothing,
   - returns `false` for an unknown account,
   - stores the company and returns `true` if the stored value is `null` or not a catalog code (legacy data),
   - returns `true` without writing if the same company is chosen again (an idempotent retry),
   - returns `false` if a *different* valid company is already stored, which leaves the stored value unchanged.

   On PostgreSQL this is a single conditional `UPDATE`, so exactly one first choice wins even across several API instances.
   The write predicate identifies the account and permits a write only when `Company` is `null` or outside the current catalog. If no row is updated, the service reads the stored company and accepts only an ordinally equal retry. This rule applies to all four catalog choices. Replacing a legacy value consumes the one available first choice: a later different choice is rejected.
4. **Endpoint mapping.** `LocalCompanyChoiceAction` maps the results to HTTP responses:
   - session missing or expired: `401`,
   - a different company is already bound to the session or account: `409`,
   - success: redirect to the confirmation page.
5. **Confirmation page.** `ConfirmationPage(code)` takes every fact from the stored company and the catalog, never from the redirect URL. It shows:
   - the display name,
   - `<faction>-1 (map <starter>)`,
   - links to the hangar, shop, starter map and help,
   - `#continueToGame`, which points to `internalWebGL&from=internalStart`.

   `#companyTestNotice` appears only for `tst`.
   The game server comes from the `LocalMapsXml.Maps` entry for the catalog starter map. TST's notice names its catalog starter map (`13` currently); it must never claim that TST shares another company's map.
6. **Game page.** `GamePage` renders the pilot card. The pilot name is HTML-encoded, and a blank name becomes `Pilot`. `pilotGameId` is shown only when the ID is greater than 0.

## 6. Starter Equipment & Currency Provisioning
1. `LocalHangarEquipmentStore.GetAsync(accountId)` provisions a new state for an account that has no cached state. Every account gets its own copy, so changes to one account's state never affect another's.
2. **Starter wallet:**
   - `Credits = 10000`
   - `AssemblyComponents = 1000`
3. **Starter inventory**, in this order (none of it is assigned to a hangar):

   | Item ID | Quantity | Type |
   | :--- | :--- | :--- |
   | `laser_lf3` | 4 | laser |
   | `laser_lf2` | 2 | laser |
   | `shield_sg3n_b02` | 4 | shield |
   | `shield_sg3n_a03` | 2 | shield |
   | `drone_flax` | 1 | drone |
   | `drone_iris` | 1 | drone |
   | `pet_10` | 1 | pet |
   | `extra_cpu` | 0 | extra |

4. When the authoritative `EconomyStore` is configured, the starter state is imported once (`legacy_import`). From then on, balances and owned items come from the ledger (SPEC-05).

## 7. Keycloak RBAC
1. The API client is `thdarkorbit-api`. Permissions are client roles and are named `backoffice.<area>.<verb>`.
2. **Permission matrix:**

   | Permission | `emulator-admin` | `emulator-operator` | `emulator-viewer` |
   | :--- | :---: | :---: | :---: |
   | `backoffice.identity.read` | ✔ | ✔ | ✔ |
   | `backoffice.identity.write` | ✔ | ✔ | |
   | `backoffice.identity.delete` | ✔ | | |
   | `backoffice.keycloak.read` | ✔ | ✔ | ✔ |
   | `backoffice.cache.write` | ✔ | ✔ | |
   | `backoffice.ops.read` | ✔ | ✔ | ✔ |
   | `backoffice.ops.write` | ✔ | ✔ | |

3. **RBAC invariants:**
   - Roles are strictly nested: viewer ⊂ operator ⊂ admin.
   - Admin has exactly `AllPermissions`.
   - Viewer has only `*.read` permissions.
   - Only admin has `identity.delete`.
   - No role grants a permission that is missing from `AllPermissions`.
4. **Default role.** New players get `DefaultRealmRole = emulator-viewer`, the least-privileged role. `KeycloakService` assigns it when no explicit role is requested.

## 8. Identity HTTP Slices (`Features/Identity/`)
These three FastEndpoints slices are the JSON entry points to §3 and §7. Every error body is `ApiErrorResponse` `{ "code", "message" }`, where `message` is always `ApiErrorCatalog.GetMessage(code)`. Each endpoint returns after writing its first error, so later dependencies are never called.

| Slice | Route (API version 1) | Access | Feature flag |
| :--- | :--- | :--- | :--- |
| `Register/RegisterEndpoint` | `POST identity/register` | anonymous | `Identity.Register` |
| `Login/LoginEndpoint` | `POST identity/login` | anonymous | `Identity.Login` |
| `Profile/ProfileEndpoint` | `GET identity/me` | authenticated | none |

1. **Register: checks in order.**
   1. If the flag is disabled, return `404 identity.feature_disabled`. Keycloak and the store are not called.
   2. If `Username` or `Password` is `null`, empty or whitespace, return `400 identity.invalid_input` without calling Keycloak or the account store. This includes explicit JSON `null` despite the request type's non-null C# annotations.
   3. Trim the username. The password and email are forwarded **exactly as sent**, including surrounding spaces and a `null` email.
   4. Call `IKeycloakService.CreateUserAsync(username, password, email, ct)` with no realm role, so the default role from §7.4 applies. If it returns `false`, return `503 identity.keycloak_unavailable`. The local store is never written.
   5. Call `RegisterAccountCommand(username, email, password)`. If it returns `false` (a duplicate, §3.2), return `409 identity.username_taken`.
   6. Otherwise return `200 { "created": true }`.

   Keycloak always runs before the local store, and both calls receive the request's cancellation token. Realm roles and company choice are never changed here.
2. **Login: checks in order.**
   1. If the flag is disabled, return `404 identity.feature_disabled`.
   2. Reject a `null`, empty or whitespace username or password with `400 identity.invalid_input` before trimming. Explicit JSON `null` must not throw or reach the account store or Keycloak. Then trim the valid username.
   3. Call `ValidateCredentialsQuery(trimmed)`. If it returns `false`, return `401 identity.account_not_registered`. No Keycloak token is requested.
   4. Call `RequestAccessTokenAsync(trimmed, password)`, where the password is passed verbatim. A `null`, empty or whitespace token returns `401 identity.authentication_failed`.
   5. Otherwise return `200 { "authenticated": true, "token": <Keycloak token verbatim> }`.
3. **Profile.**
   - The username is the first non-null value of the `preferred_username` claim, then `ClaimTypes.Name`, then `Identity.Name`.
   - The fallback only happens when a claim is **missing**. A whitespace `preferred_username` is still chosen and then rejected with `401 identity.unauthorized`; the `Name` claim is not used.
   - The chosen value goes to `GetAccountByUsernameQuery` **untrimmed**.
   - If no account matches, return `404 identity.account_not_found`.
   - Otherwise return `200` with exactly three fields: `username`, `email` (may be `null`) and `gameUserId`. `Id` and `Company` are never exposed.
4. **Known gap.** A local duplicate (step 1.5) comes back as `409` only **after** the Keycloak user has been created. A retry with the same name can therefore leave an orphaned Keycloak user. This is pinned as current behaviour, not as an intended design.

Verification: `Identity/IdentityHttpEndpointSpecificationTests` uses `FastEndpoints.Factory.Create` with recording fakes for `IFeatureManager`, `IKeycloakService`, `IIdentityCommandService` and `IIdentityQueryService`. It pins the check order, the call sequence, null and whitespace input rejection, which values are trimmed or forwarded verbatim, the error codes and messages, the response field names, cancellation forwarding, and each endpoint's route, verb and anonymous access. `ApiServiceVerticalSliceTests.IdentityHttpSlices_GateOnFlagFirstAndDependOnlyOnApplicationContracts` pins login validation before trimming.

## 9. Open Gaps
- The official company confirmation contract has not been captured. All choice, preview and confirmation markup is emulator-defined.
- Email uniqueness is not enforced (§3.3).
- Registration can orphan a Keycloak user when the local store reports a duplicate (§8.4).
