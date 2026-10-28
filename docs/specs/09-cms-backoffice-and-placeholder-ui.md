# SPEC-09: Angular CMS Backoffice & Generic Placeholder Branding

## 1. Scope & Purpose
Specifies `ThDarkOrbitEmulator.Cms` (Angular, served by Aspire resource `cms-players`, dev URL `http://localhost:4300`), its routes and RBAC guards, the `/api/v1/backoffice/*` and `/api/v1/economy/*` contracts it consumes, the Keycloak permission model, and the strict generic placeholder branding policy.

Verified by `Backoffice/BackofficeAndPlaceholderCmsSpecificationTests` (contracts, RBAC, CMS source invariants), `Architecture/BrandingPlaceholderComplianceTests` (portal rewrite + CMS titles), `Authorization/PermissionAuthorizationHandlerTests`, `Authorization/KeycloakClaimsTransformationTests`, `Backoffice/BackofficeIdentityServiceTests`, `Backoffice/ManualActivityTrackerTests`, `Integration/MeshBackofficeEndpointTests` and `Integration/AdminCommandHttpTests`.

## 2. CMS Route Map
`app.routes.ts` lazy-loads `identityRoutes` and `backofficeRoutes` at `''`; any unknown path (`**`) redirects to `/`.

| CMS Route | Component | Guard | Denied (anonymous) | Denied (authenticated) |
| :--- | :--- | :--- | :--- | :--- |
| `/` | `ProfileComponent` (pilot dashboard) | `authGuard` | `/login` | — |
| `/login` | `LoginComponent` | `guestGuard` | — | `/` |
| `/register` | `RegisterComponent` | `guestGuard` | — | `/` |
| `/backoffice` | `BackofficeShellComponent` → redirects to `overview` | `backofficeGuard` (any `backoffice.*` permission) | `/login` | `/` |
| `/backoffice/overview` | `OverviewComponent` | `permissionGuard('backoffice.ops.read')` | `/login` | `/` |
| `/backoffice/identity` | `IdentityUsersComponent` (users) | `permissionGuard('backoffice.identity.read')` | `/login` | `/` |
| `/backoffice/keycloak` | `KeycloakUsersComponent` (roles / Keycloak users) | `permissionGuard('backoffice.keycloak.read')` | `/login` | `/` |
| `/backoffice/operations` | `OperationsComponent` (cache clear) | `permissionGuard('backoffice.cache.write')` | `/login` | `/` |
| `/backoffice/mesh` | `MeshComponent` (service mesh & world) | `permissionGuard('backoffice.ops.read')` | `/login` | `/` |
| `/backoffice/runtime-vars` | `RuntimeVarsComponent` (admin commands & runtime variables) | `permissionGuard('backoffice.ops.read')` | `/login` | `/` |
| `/backoffice/economy` | `EconomyComponent` (catalog, listings/stock, vouchers/reward offers, auction lots, balances = compensation, ledger & stock audit) | `economyAdminGuard` (realm role `emulator-admin`) | `/login` | `/` |
| `/backoffice/cockpit` | `CockpitComponent` (Unity cockpit catalog: languages, server lists / instances, hangar ship catalog, per-account hangar slots, locale overrides) | `economyAdminGuard` (realm role `emulator-admin`) | `/login` | `/` |
| `/backoffice/progression` | `ProgressionComponent` (active hangars, Skylab jobs, Galaxy Gate runs, clan stations and relations) | `economyAdminGuard` (realm role `emulator-admin`) | `/login` | `/` |

Invariants:
- The shell navigation (`backoffice-shell.component.html`) shows each link only under the same permission/role as its route guard; a hidden link is never reachable via a weaker guard.
- Catalog, vouchers, compensation and audit are **tabs of `/backoffice/economy`**, not separate routes. `/backoffice/users`, `/backoffice/catalog`, `/backoffice/vouchers`, `/backoffice/compensation` and `/backoffice/audit` do not exist; they fall through `**` to `/`. (Open gap: dedicated routes may be split out later; this spec and its tests must then be updated together.)
- Guards are UX only. The API enforces every permission server-side (§4).

## 3. RBAC Model
- Permissions are Keycloak client roles on `thdarkorbit-api` (`EmulatorPermissions.All`, order fixed): `backoffice.identity.read`, `backoffice.identity.write`, `backoffice.identity.delete`, `backoffice.keycloak.read`, `backoffice.cache.write`, `backoffice.ops.read`, `backoffice.ops.write`.
- Policy names are `Permission:<permission>` (`EmulatorPermissions.PolicyName`). `AddEmulatorAuthorization` registers one `PermissionRequirement` policy per permission plus one `RequireRole` policy per realm role, and sets **no fallback policy** (game client endpoints stay anonymous).
- Realm composite roles (`KeycloakRbacDefinitions.RealmRolePermissions`):

| Realm Role | Permissions |
| :--- | :--- |
| `emulator-admin` | all seven |
| `emulator-operator` | all except `backoffice.identity.delete` |
| `emulator-viewer` (default for new users) | `backoffice.identity.read`, `backoffice.keycloak.read`, `backoffice.ops.read` |

- CMS `RbacService` decodes the access token stored in `sessionStorage[tokenStorageKey]` (`cms_access_token`), reads `resource_access['thdarkorbit-api'].roles`, and expands `emulator-admin` to exactly `EmulatorPermissions.All`. `canAccessBackoffice()` is true when any permission starts with `backoffice.`.

## 4. `/api/v1/backoffice/*` Endpoint Contracts
All are FastEndpoints (`Version(1)`, prefix `api/v1`), require an authenticated principal, and the listed policy. This table is exhaustive: a new backoffice endpoint without a row here fails the spec test.

| Verb | Route | Required Permission | Slice |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/backoffice/auth/permissions` | authenticated only | `Backoffice/Auth` — returns `{ username, permissions[], realmRoles[], roles[] }` |
| `GET` | `/api/v1/backoffice/identity/users` | `backoffice.identity.read` | `Backoffice/Identity` |
| `GET` | `/api/v1/backoffice/identity/users/count` | `backoffice.identity.read` | `Backoffice/Identity` |
| `DELETE` | `/api/v1/backoffice/identity/users` | `backoffice.identity.delete` | `Backoffice/Identity` — returns `{ deleted }` |
| `GET` | `/api/v1/backoffice/keycloak/users` | `backoffice.keycloak.read` | `Backoffice/Keycloak` |
| `GET` | `/api/v1/backoffice/operations/overview` | `backoffice.ops.read` | `Backoffice/Operations` |
| `GET` | `/api/v1/backoffice/operations/mesh` | `backoffice.ops.read` | `World/Mesh` |
| `POST` | `/api/v1/backoffice/operations/cache/clear` | `backoffice.cache.write` | `Backoffice/Operations` |
| `POST` | `/api/v1/backoffice/activity-tracking/sessions` | `backoffice.ops.read` | `Backoffice/ActivityTracking` |
| `GET` | `/api/v1/backoffice/activity-tracking/sessions/{sessionId}` | `backoffice.ops.read` | `Backoffice/ActivityTracking` |
| `POST` | `/api/v1/backoffice/activity-tracking/sessions/{sessionId}/events` | `backoffice.ops.read` | `Backoffice/ActivityTracking` |
| `GET` | `/api/v1/backoffice/activity-tracking/sessions/{sessionId}/events/stream` | `backoffice.ops.read` | `Backoffice/ActivityTracking` (SSE) |
| `GET` | `/api/v1/backoffice/mesh/topology` | `backoffice.ops.read` | `World/Mesh` |
| `PUT` | `/api/v1/backoffice/mesh/scale` | `backoffice.ops.write` | `World/Mesh` — `DesiredReplicas` 1..16, `MaxMapsPerNode` 1..256 |
| `POST` | `/api/v1/backoffice/mesh/rebalance` | `backoffice.ops.write` | `World/Mesh` |
| `POST` | `/api/v1/backoffice/mesh/nodes/{nodeId}/drain` | `backoffice.ops.write` | `World/Mesh` |
| `POST` | `/api/v1/backoffice/mesh/nodes/{nodeId}/activate` | `backoffice.ops.write` | `World/Mesh` |
| `GET` | `/api/v1/backoffice/world/overview` | `backoffice.ops.read` | `World/Mesh` |
| `POST` | `/api/v1/backoffice/world/pilots/{userId}/teleport` | `backoffice.ops.write` | `World/Mesh` — map 1..16, x 0..21000, y 0..13100 |
| `GET` | `/api/v1/backoffice/runtime-variables` | `backoffice.ops.read` | `World/Admin` |
| `GET` | `/api/v1/backoffice/runtime-vars` | `backoffice.ops.read` | `World/Admin` (alias) |
| `GET` | `/api/v1/backoffice/runtime-variables/history` | `backoffice.ops.read` | `World/Admin` |
| `GET` | `/api/v1/backoffice/runtime-vars/history` | `backoffice.ops.read` | `World/Admin` (alias) |
| `GET` | `/api/v1/backoffice/runtime-variables/{key}` | `backoffice.ops.read` | `World/Admin` |
| `GET` | `/api/v1/backoffice/runtime-vars/{key}` | `backoffice.ops.read` | `World/Admin` (alias) |
| `PUT` | `/api/v1/backoffice/runtime-variables/{key}` | `backoffice.ops.write` | `World/Admin` |
| `PUT` | `/api/v1/backoffice/runtime-vars/{key}` | `backoffice.ops.write` | `World/Admin` (alias) |
| `POST` | `/api/v1/backoffice/runtime-variables/{key}/reset` | `backoffice.ops.write` | `World/Admin` |
| `POST` | `/api/v1/backoffice/runtime-vars/{key}/reset` | `backoffice.ops.write` | `World/Admin` (alias) |
| `POST` | `/api/v1/backoffice/runtime-variables/history/{changeId}/rollback` | `backoffice.ops.write` | `World/Admin` |
| `POST` | `/api/v1/backoffice/runtime-vars/history/{changeId}/rollback` | `backoffice.ops.write` | `World/Admin` (alias) |
| `POST` | `/api/v1/backoffice/admin/command` | `backoffice.ops.write` | `World/Admin` (see SPEC-08) |
| `GET` | `/api/v1/backoffice/admin/command/history` | `backoffice.ops.read` | `World/Admin` |

Rules:
- Every mutating verb requires a `.write`/`.delete` permission. The only exception is activity tracking, a read-side diagnostic recorder gated by `backoffice.ops.read`.
- `GET /auth/permissions` requires an authenticated principal and returns distinct claim values in `{ username, permissions, realmRoles, roles }`. `username` uses the identity name, then `preferred_username`, then the empty string. Keycloak transformation reads realm roles only from `realm_access.roles` and permission roles only from `resource_access.thdarkorbit-api.roles`; roles for another client cannot grant emulator permissions. An unauthenticated identity is left unchanged. Missing, malformed, or unexpected JSON shapes contribute no roles; non-string and blank array elements are ignored while valid strings in the same array survive. Duplicate role strings and repeated transformation do not create duplicate claims. Each client permission is represented both as a `permission` claim and a role claim; each realm role is represented as a `realm_role` claim and a role claim. The transformer is registered once in the authorization services and this endpoint does not allow anonymous access.
- The permissions response reads claims across every identity in the principal. Each of its three arrays independently keeps the first occurrence of each exact, case-sensitive value in claim enumeration order; a duplicate in another identity does not add a second entry. `username` resolves through the principal's first identity before the `preferred_username` fallback, so a name claim in a later identity does not override a fallback on the first. This endpoint projects existing claims only; it does not query identity storage or Keycloak.
- Economy administration is a minimal-API group `MapGroup("/api/v1/economy").RequireAuthorization(EmulatorRoles.Admin)` (assets, items, recipes, listings + stock + stock-audit, auction lots, reward offers, account balances + ledger). It is restricted to `emulator-admin` on both the server and the CMS (`economyAdminGuard`).
- Unity cockpit catalog administration is a minimal-API group `MapGroup("/api/v1/backoffice/cockpit").RequireAuthorization(EmulatorRoles.Admin)` (`CockpitCatalogEndpoints`, migration `030_cockpit_catalog`). Like economy, it is not a FastEndpoints slice, so it is listed in the cockpit contract below rather than in the table above. It is restricted to `emulator-admin` on both the server and the CMS (`economyAdminGuard`).
- The CMS `BackofficeApiService` builds URLs as `${apiBaseUrl}/backoffice/${path}` and `${apiBaseUrl}/economy/${path}` with `apiBaseUrl = '/api/v1'`; every path it calls MUST exist in this table, the economy group, or the cockpit group.

### Unity cockpit catalog contract (Languages, Server Lists, Hangar Ships)

| Resource | Read | Write | Consumer |
| :--- | :--- | :--- | :--- |
| Languages | `GET /api/v1/backoffice/cockpit/languages` | `PUT /api/v1/backoffice/cockpit/languages/{code}` | `/unityApi/login.php` language selection; public `GET /unityApi/languages.php` (enabled rows only) |
| Server lists / instances | `GET /api/v1/backoffice/cockpit/servers` | `PUT /api/v1/backoffice/cockpit/servers/{code}` | `/unityApi/login.php` default instance name; public `GET /unityApi/servers.php`; portal `internalServers` page |
| Hangar ship catalog | `GET /api/v1/backoffice/cockpit/ships` | `PUT /api/v1/backoffice/cockpit/ships/{shipId}` | Ship descriptors for hangar assignments |
| Per-account hangar slots | `GET /api/v1/backoffice/cockpit/accounts/{accountId}/hangars` | `PUT /api/v1/backoffice/cockpit/accounts/{accountId}/hangars/{slot}` | `/unityApi/login.php` player ships, Dock and world account summary |
| Locale overrides | `GET /api/v1/backoffice/cockpit/locale-overrides` | `PUT /api/v1/backoffice/cockpit/locale-overrides/{language}/{category}/{key}` | Stored for Backoffice editing only |

- Hangar origin: the first Unity login, or the first Backoffice view of that account, creates the two starter assignments (Pusat Plus and Phoenix) in Postgres with stable six-digit hangar IDs. Later admin edits survive login. World combat stat formulas stay slot based.
- Limits: the Unity build has not been observed calling `languages.php` or `servers.php`, so these are emulator contracts. The Unity asset bundle does not read locale overrides yet. Server descriptors do not reconfigure the running mesh or its TCP listener. Evidence: `docs/cockpit-catalog.md`.
- Cockpit write routes reject JSON nulls for required host, ship icon path, hangar ID, and locale value with HTTP 400 before mutation. `CockpitBackofficePostgresTests.CockpitRoutes_RejectNullFieldsWithoutChangingPersistedCatalog` exercises the HTTP routes against PostgreSQL and confirms a valid server update persists. Runtime-variable set, reset, history, rollback, aliases, and cross-replica visibility are covered by `AdminCommandHttpTests` and `RuntimeVariablePostgresTests`.

### Player progression and clan operations

The minimal API group `/api/v1/backoffice/progression` requires the `emulator-admin` realm role for every route. The CMS uses the matching admin guard and generic Space Emulator CMS labels. Account IDs are UUIDs; the hangar route displays both assigned slots and the authoritative active slot used by `POST /unityApi/activate.php`.

| Resource | Inspect | Override |
| :--- | :--- | :--- |
| Active hangar | `GET /accounts/{accountId}/hangar` | `PUT /accounts/{accountId}/hangar` `{ slot }` (assigned slot 1 or 2) |
| Skylab jobs | `GET /accounts/{accountId}/skylab` | `PUT /accounts/{accountId}/skylab/collectors/{collectorId}` and `PUT /accounts/{accountId}/skylab/refinery` `{ readyAt }` |
| Gate runs | `GET /accounts/{accountId}/gates` | `PUT /accounts/{accountId}/gates/{gateId}` `{ wave, kills, npcHp, shipHp, lives }` |
| Clan station | `GET /clans/{clanId}/station` | `PUT /clans/{clanId}/station/{moduleId}` `{ level, readyAt }` |
| Clan relations | `GET /clans/{clanId}/relations` | `PUT /clans/{clanId}/relations/{otherClanId}` `{ status, proposer }` |

Operator actions also expose `POST /accounts/{accountId}/skylab/collectors/{collectorId}/complete-timer`, `POST /accounts/{accountId}/skylab/refinery/complete-timer`, and `POST /accounts/{accountId}/gates/{gateId}/reset-wave` or `/complete-wave`. Timer completion sets an existing deadline to the current UTC time so gameplay can finish or claim the job. Wave reset clears kills and restores NPC HP on an unfinished run; wave completion advances one wave and marks the final wave completed. These administrative state changes do not create gate rewards or achievements. Missing or completed runs return 404; unknown gate IDs return 400.

The Angular `/backoffice/progression` view provides these actions next to the existing hangar, Skylab, gate, station, and diplomacy controls. The interactive `scripts/admin/Start-AdminCli.ps1` launcher loads `admin-cli.mjs`, whose `/hangar`, `/skylab`, `/gate`, and `/clan` commands call the same admin-only routes. `/help` lists their syntax, and Tab completes command names, gate IDs/actions, collector IDs, station modules, relation statuses, and hangar slots. Account arguments are UUIDs. Examples: `/hangar <uuid> 2`, `/skylab <uuid> collector prometium ready`, `/gate <uuid> alpha complete`, `/clan mmo-guard relations mmo-orbit allied mmo-guard`.

Timer and station overrides update existing jobs/modules only; missing records return 404. Gate overrides update existing unfinished runs only and validate the gate wave, kill count, HP and lives; they do not create a completion, achievement, or reward. Completed runs return 404 on override. Relation overrides update existing same-company clan pairs and accept only `pending`, `allied`, or `war`. Gameplay still owns starting jobs, activating gates, creating diplomacy records, and paying gate rewards. `ProgressionBackofficePostgresTests` exercises the HTTP routes and persisted state.

### Backoffice operations overview and cache contract

- `GET /operations/overview` returns HTTP 200 with `services[]` and `links[]`. Its six service names, in order, are `api-game`, `gateway-players`, `data-postgres`, `sessions-redis`, `auth-keycloak`, and `cms-players`. The first two have status `healthy`; the remaining four have status `running`. These are fixed display labels, not live dependency probes or health checks. Callers must not infer service availability from them.
- The six quick-link labels, in order, are `Aspire dashboard`, `pgAdmin`, `Grafana`, `Prometheus`, `RedisInsight (Aspire → sessions-redis)`, and `Keycloak admin`. Every URL points to a loopback host. Links are local development shortcuts, not a dynamic service-discovery response.
- `POST /operations/cache/clear` requires `backoffice.cache.write`. It executes Redis `FLUSHALL` through the injected connection multiplexer and returns HTTP 200 `{ "cleared": true }` only after the command completes. `FLUSHALL` clears every database on that Redis server, including sessions and world keys; this route is an administrative destructive operation, not a per-user cache eviction.

### Backoffice identity account contract

- `GET /identity/users` delegates to `IBackofficeIdentityService.ListAccountsAsync` with a fixed limit of 100. The service orders accounts by username before applying the limit and returns `id` (GUID), `username` (string), `email` (string), and `gameUserId` (64-bit integer). A null stored email is projected as an empty string. A zero service limit returns no rows. Listing does not affect the independent total count.
- `GET /identity/users/count` returns `{ count }` for all accounts, including rows beyond the list limit.
- `DELETE /identity/users` removes all accounts and returns `{ deleted }` with the number removed. A repeated delete returns zero, after which both count and list are empty.
- All three endpoints remain in `Backoffice/Identity` and depend on the application `IBackofficeIdentityService`; they do not access EF contexts directly. Listing and count require `backoffice.identity.read`; deletion requires `backoffice.identity.delete`.
- At the HTTP boundary, the list passes the fixed limit `100` and the request cancellation token to the application service. Its HTTP 200 body is `{ "users": [...] }`; every row preserves service order and projects `id` as a GUID, `username` and `email` as strings, and `gameUserId` as a 64-bit integer. An empty service result is `users: []`. The endpoint does not sort again or synthesize email values; null email normalization belongs to the service.
- The count endpoint passes the cancellation token and returns HTTP 200 `{ "count": number }`, retaining 64-bit counts beyond `Int32.MaxValue`. Delete likewise passes the token and returns HTTP 200 `{ "deleted": number }`, including zero when there are no remaining accounts. A successful delete response does not itself include an updated list or count.

### Backoffice Keycloak user listing contract

- `GET /keycloak/users` requires `backoffice.keycloak.read` and delegates to `IBackofficeKeycloakService.ListUsersAsync` with a fixed limit of 100 and the request cancellation token. The endpoint performs no separate sort or truncation; it preserves the service's row order.
- A successful response is HTTP 200 `{ "users": [...] }`. Each row contains string `id`, `username`, and `email`, plus boolean `enabled`; an empty service result yields `users: []`. The endpoint copies all four service fields without changing an empty email. The infrastructure service obtains an admin token and requests the configured realm's users with `max=100`; missing admin configuration, token failure, or a non-success users response yields an empty list. This behavior does not prove the upstream server returned no users.
- The endpoint remains in `Backoffice/Keycloak` and depends only on the application service interface. Keycloak HTTP and admin options belong in identity infrastructure.

### Manual activity tracking contract

- `POST /activity-tracking/sessions` takes `subjectId` and `reason`. The server derives `operatorName` from the authenticated identity name, then `preferred_username`, then `unknown`. The tracker rejects blank or whitespace-only values for all three fields, trims accepted values, assigns a distinct GUID-format `id`, and records `startedAtUtc` from its clock. Subjects are arbitrary diagnostic identifiers; an identity-domain user is not required.
- A successful session start returns HTTP 200 `{ "session": { "id", "subjectId", "operatorName", "reason", "startedAtUtc" } }`. The `id` is a 32-character `N`-format GUID; the stored session and response have the same trimmed subject and reason. When both identity name and `preferred_username` exist, identity name wins. If neither exists, `operatorName` is `unknown`. A blank subject or reason throws at the tracker boundary before a session is recorded; direct endpoint invocation does not turn that exception into a successful response.
- `POST /activity-tracking/sessions/{sessionId}/events` takes `type`, `source`, `details`, and optional `occurredAtUtc`. Blank or whitespace-only `type` and `source` are invalid; accepted text fields are trimmed. The endpoint supplies the current UTC time when `occurredAtUtc` is absent. An unknown session returns 404 before append. The tracker preserves an explicitly supplied timestamp.
- Sequences start at 1 and increase per session. The snapshot retains only the newest 250 events in sequence order; pruning does not reset sequence numbers. Each session has an independent event history. A snapshot or append against an unknown session fails at the tracker boundary.
- `GET /activity-tracking/sessions/{sessionId}` returns the session and retained snapshot, or 404. The SSE stream returns 404 for an unknown session; otherwise it replays the retained snapshot, then streams future events as `id: <sequence>`, `event: activity`, and JSON `data:` lines. It uses `text/event-stream` and `Cache-Control: no-store`.
- The session ID is a 32-character GUID in `N` format. `startedAtUtc` comes from the tracker's injected `TimeProvider`; event timestamps are caller supplied at the tracker boundary. A snapshot is a point-in-time copy: later appends do not change a previously returned list. Concurrent appends receive unique, contiguous sequence numbers within each session; each session starts its own sequence at 1. Subscriber registration and replay snapshot capture occur under the same session lock as event publication. A stream joining after pruning replays only the retained 250 events, then receives subsequent events once, in sequence order.
- The API registers one singleton tracker per process and all four activity endpoints depend on that shared interface. Its in-memory history therefore belongs to one API process; no cross-replica persistence or replay is promised.
- At the event HTTP boundary, the route `sessionId` is looked up before append. An unknown ID returns HTTP 404 without creating a session or event. A known session accepts `{ type, source, details, occurredAtUtc? }`; the tracker rejects blank `type` or `source`, trims these fields and `details`, and assigns the next 64-bit sequence. Explicit `occurredAtUtc` is retained, including its offset; if omitted, the endpoint uses the current UTC time. Success returns HTTP 200 `{ "event": { "sequence", "sessionId", "type", "source", "details", "occurredAtUtc" } }`. Direct endpoint invocation leaves blank-field exceptions visible and records no event.
- `GET /activity-tracking/sessions/{sessionId}` returns HTTP 404 for an unknown ID. For a known ID, HTTP 200 contains `{ "session": { "id", "subjectId", "operatorName", "reason", "startedAtUtc" }, "events": [...] }`. A new session has `events: []`; subsequent events remain in ascending sequence order. The endpoint reads the tracker snapshot and does not re-sort or synthesize entries.
- `GET /activity-tracking/sessions/{sessionId}/events/stream` checks session existence before setting SSE headers or subscribing. An unknown ID returns HTTP 404 with no stream body. A known session responds with `Content-Type: text/event-stream` and `Cache-Control: no-store`. Each retained replay event and each later event is written as three UTF-8 lines, `id: <sequence>`, `event: activity`, and `data: <JSON event>`, followed by a blank line and an immediate response flush. The JSON event contains the tracker sequence, session ID, type, source, details, and timestamp; JSON escaping keeps embedded newlines in details within one `data:` line. The subscription uses the request cancellation token, so disconnect or cancellation ends delivery and removes the subscriber. No heartbeat, `Last-Event-ID` resume, or cross-process replay is provided.
- The tracker fans each new event out once to every active subscriber of its session. Subscriptions to another session receive only that session's replay and live events, with independent sequence numbers. Cancelling a subscriber while it waits for a new event ends the wait and unregisters that subscriber; later appends remain in the bounded session snapshot and a new subscriber can replay them. Subscriber channels and synchronization live in the tracker; the HTTP endpoint owns SSE formatting and response headers.
- The 250-event limit applies to the retained session snapshot, not to events already published to an active subscriber. If an active subscriber falls behind while 260 events are appended, it can still read all 260 in order from its channel; a subscriber joining afterward replays only sequences 11 through 260. The late replay then continues with newly appended events. Retention pruning never renumbers events or silently removes events already queued for an active subscriber.

## 5. Placeholder Branding Invariant
- No file under `ThDarkOrbitEmulator.Cms/src` or `ThDarkOrbitEmulator.Cms/public` may contain `DarkOrbit`, `Bigpoint` or `bpsecure` (case-insensitive) in its name or text. The sole tolerated token is the technical Keycloak client id `thdarkorbit-api`, which is never rendered.
- The CMS ships no logo images (`png/jpg/gif/svg/webp`); `app-shell.component.html` renders a `[LOGO]` badge and `PLACEHOLDER UI`.
- Titles: `index.html` `<title>Space Emulator CMS (Placeholder)</title>`; `en.json` brand `Space Emulator CMS`; `es.json` `CMS del Emulador Espacial`.
- Emulator-rewritten portal login HTML (`EmulatorRedirectHelper.RewriteLoginPageHtml`) uses `<title>Space Emulator Portal | Local Login (Placeholder)</title>`, `SPACE EMULATOR`, `Space Emulator Placeholder`, and posts to the local `/Sas/Authentication/Bigpoint?` path (a protocol route name, not branding).
- The signed-in local spacemap document uses `<title>Spacemap - Space Emulator (Placeholder)</title>` and contains no official branding token in its served HTML. The `internalStart` and `internalMapRevolution` route writes this shared document.

## 6. Test Traceability
| Requirement | Test |
| :--- | :--- |
| §2 routes, guards, nav parity | `BackofficeAndPlaceholderCmsSpecificationTests.CmsTopLevelRoutes_*`, `CmsBackofficeChildRoutes_*`, `CmsBackofficeGuards_*`, `CmsShellNavigation_*` |
| §3 RBAC matrix & policies | `KeycloakRealmRoles_MapToSpecifiedPermissionMatrix`, `AddEmulatorAuthorization_*`, `CmsRbacService_*`, `PermissionAuthorizationHandlerTests`, `KeycloakClaimsTransformationTests` |
| §4 endpoint contracts | `BackofficeEndpoints_*`, `EconomyAdministration_*`, `CmsApiClient_*`, `Spec09_DocumentsEveryBackofficeRouteAndCmsRoute`, `MeshBackofficeEndpointTests`, `AdminCommandHttpTests` |
| §4 progression operator actions and CLI catalog | `ProgressionBackofficePostgresTests.AdminRoutes_InspectAndOverridePersistedProgressionWithoutAwardingGateRewards`, `AdminCliProgressionTests.CliCatalog_ParsesProgressionRoutesAndCompletesCommands` |
| §4 permissions claim parsing, client isolation and idempotence | `BackofficeAndPlaceholderCmsSpecificationTests.KeycloakClaimsTransformation_*`, `ApiServiceVerticalSliceTests.BackofficePermissions_UsesAuthenticatedIdentityAndRegisteredKeycloakTransformation` |
| §4 identity account ordering, projection, limit, count, delete and endpoint cancellation | `BackofficeIdentityServiceTests`, `BackofficeAndPlaceholderCmsSpecificationTests.BackofficeIdentity*`, `ApiServiceVerticalSliceTests.BackofficeIdentityEndpoints_StayInIdentitySliceAndDelegateToApplicationService` |
| §4 manual activity tracking | `ManualActivityTrackerTests`, `ApiServiceVerticalSliceTests.ActivityTrackingEndpoints_StayInBackofficeSliceAndRequireOpsRead`, `ApiServiceVerticalSliceTests.ActivityTracking_UsesOneProcessLocalTrackerAcrossEndpoints` |
| §4 activity session-start HTTP projection, operator fallback and blank input | `BackofficeAndPlaceholderCmsSpecificationTests.ActivitySessionStart_*`, `ApiServiceVerticalSliceTests.ActivityTrackingSessionStart_KeepsIdentityProjectionAndStorageInsideSlice` |
| §4 activity event append and snapshot HTTP responses | `BackofficeAndPlaceholderCmsSpecificationTests.ActivityEventAppend_*`, `ActivitySnapshot_*`, `ActivityEndpoints_UnknownSessionReturns404WithoutCreatingOne`, `ApiServiceVerticalSliceTests.ActivityTrackingEventAndSnapshot_KeepRouteLookupAndProjectionInsideSlice` |
| §4 activity SSE HTTP framing, replay, live delivery and unknown session | `BackofficeAndPlaceholderCmsSpecificationTests.ActivityStream_*`, `ApiServiceVerticalSliceTests.ActivityTrackingStream_EnforcesSessionLookupAndSseBoundaryInsideSlice` |
| §4 activity subscriber fan-out, session isolation and cancellation | `ManualActivityTrackerTests.SubscribeAsync_FansOutToEachSubscriberWithoutCrossingSessionBoundary`, `SubscribeAsync_CancellationStopsWaitingAndFutureSubscribersCanReplay`, `ApiServiceVerticalSliceTests.ActivityTrackingFanout_KeepsSubscriberStateInTrackerAndTransportInEndpoint` |
| §4 activity retention versus active subscriber delivery | `ManualActivityTrackerTests.SubscribeAsync_ActiveSubscriberReceivesEventsPrunedFromLateReplay`, `ApiServiceVerticalSliceTests.ActivityTrackingRetention_BoundsSnapshotsWithoutDiscardingSubscriberDelivery` |
| §4 operations payload and cache boundary | `BackofficeAndPlaceholderCmsSpecificationTests.BackofficePermissions_*`, `BackofficeAndPlaceholderCmsSpecificationTests.BackofficeOverview_*`, `ApiServiceVerticalSliceTests.BackofficeOperations_KeepReadAndCacheWriteEndpointsInTheirSlice` |
| §4 Keycloak listing limit, projection and layer boundary | `BackofficeAndPlaceholderCmsSpecificationTests.BackofficeKeycloakUsers_*`, `ApiServiceVerticalSliceTests.BackofficeKeycloakListing_StaysInKeycloakSliceAndUsesApplicationBoundary` |
| §5 branding | `CmsSourcesAndAssets_NeverShipOfficialBranding`, `CmsPlaceholderTitlesAndBadges_*`, `BrandingPlaceholderComplianceTests` |
