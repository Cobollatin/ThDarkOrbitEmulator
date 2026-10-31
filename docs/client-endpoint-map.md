# DarkOrbit Unity login endpoint map

## Native Unity handoff (t109u, 2026-10-25)

The saved Live login HAR `20261021-224636-login` records a Unity 1.1.100 request to
`GET /indexInternal.es?action=internalStart&prc=100` (ordinal 76, HTTP 302)
with `Location: /indexInternal.es?action=internalWebGL&from=internalStart`.
The follow-up (ordinal 77, HTTP 200, `text/html; charset=UTF-8`) contains only
the session's `dosid` in its body. The corresponding IDs in
`extracted_flow.jsonl` are `771b953a-11da-47a5-9b69-8736a73849f0` and
`5ac0f020-1134-40af-9d67-7fd7ab4fa419`. The saved Live HAR then has
`GET /unityApi/login.php` (ordinal 81), `/unityApi/links.php` (82), and
`/unityApi/news.php` (85). Their existing JSON DTOs remain the emulator contract.

The local endpoint gives this 302 and opaque body to the native Unity user agent
after session lookup. Browser `internalWebGL` continues to serve the local menu,
and the native menu remains available at `internalMenu`; the browser Spacemap
remains at `internalMapRevolution`. The examined HAR has no `instance.php` or
`agent.php` request, and no raw TCP exchange. Their native response formats and
the provisional WorldServer protocol remain unverified.

## World-97 emulator map conflict routing (2026-10-24)

Emulator-only world commands returning `409 {"status":"map_changed"}` set `X-Mesh-Map-Owner-Node` to the current pilot map's lease owner after refreshing distributed state. The local Spacemap configuration and jump controls are checked against two live Kestrel replicas. This is not a captured official Unity endpoint or header.

## World-95 emulator stale configuration switch (2026-10-24)

Emulator-only `POST /spacemap/world/config` accepts optional integer `mapId` beside `configuration`. A stale source map returns HTTP 409 `{ "status": "map_changed" }` without switching the ship. The shipped local page refreshes `GET /spacemap/world/state` and renders the destination map and configuration. A live Kestrel/jsdom client check held the old-map request until after a second client jumped. This is not a captured official Unity endpoint.

## World-93 emulator configuration controls (2026-10-24)

No endpoint shape changed. The local Spacemap page serializes emulator-only `POST /spacemap/world/config` calls and refreshes `GET /spacemap/world/state` after success or cooldown conflict. A live Kestrel/jsdom check observed ordered requests, HTTP 409 `cooldownRemainingMs` for the reverse click, and the accepted configuration in both DOM and state. These are not captured official Unity endpoints.

## World-92 emulator stale collectible pickup (2026-10-24)

Emulator-only `POST /spacemap/world/collect` accepts optional integer `mapId` beside `boxId`. A stale source map returns HTTP 409 `{ "status": "map_changed" }` before claiming the box or awarding currency; the local page follows with `GET /spacemap/world/state`. A live Kestrel/jsdom check observed the rejected delayed pickup, no wallet credit and the rendered destination map. These are not captured official Unity endpoints.

## World-91 emulator stale laser fire (2026-10-24)

Emulator-only `POST /spacemap/world/fire` accepts optional integer `mapId` beside `npcId`. A stale source map returns HTTP 409 `{ "status": "map_changed" }` before selection or damage; the local page follows with `GET /spacemap/world/state`. A live Kestrel/jsdom check observed the rejected delayed shot and rendered destination map. These are not captured official Unity endpoints.

## World-90 emulator stale NPC target (2026-10-24)

Emulator-only `POST /spacemap/world/target` accepts optional integer `mapId` beside `npcId`. A stale source map returns HTTP 409 `{ "status": "map_changed" }`; the local page follows with `GET /spacemap/world/state`. A live Kestrel/jsdom page check observed the rejected delayed selection and the rendered destination map. These are not captured official Unity endpoints.

## World-89 emulator ordered NPC targeting (2026-10-24)

No endpoint or payload shape changed. The local page serializes emulator-only `POST /spacemap/world/target` requests from one page instance. A live Kestrel/jsdom client held the first request before delivery, selected a second NPC and observed its later selection through `/target` and a `/fire` response other than `not_selected`. These are not captured official Unity endpoints.

## World-88 emulator stale jump (2026-10-24)

Emulator-only `POST /spacemap/world/jump` accepts optional integer `mapId` in addition to `portalId`. A stale source map returns HTTP 409 `{ "status": "map_changed" }`; the shipped local page follows with `GET /spacemap/world/state` and renders the new map. These are not captured official Unity endpoints.

## World-87 emulator same-map laser response (2026-10-24)

No endpoint or payload shape changed. The shipped page used emulator-only `POST /spacemap/world/target` and `POST /spacemap/world/fire`; live Kestrel responses for nonlethal and lethal hits arrived after the page selected another NPC. The page kept the new target and status. These are not captured official Unity endpoints.

## World-86 emulator laser input (2026-10-24)

No endpoint or payload shape changed. The shipped local page sends one emulator-only `POST /spacemap/world/fire` while a shot is pending, even after repeated Fire input. A live Kestrel/jsdom client confirmed the request count and subsequent NPC combat. This is not a captured official Unity endpoint.

## World-85 emulator movement conflict refresh (2026-10-24)

No endpoint or payload shape changed. After emulator-only `POST /spacemap/world/move` returns `409 {"status":"map_changed"}`, the shipped local page now calls `GET /spacemap/world/state` immediately and renders the new map. A live Kestrel/jsdom client observed this after another authenticated HTTP client jumped the pilot. These are not captured official Unity endpoints.

## World-84 emulator external map transition (2026-10-24)

No endpoint or payload changed. The local page used emulator-only `GET /spacemap/world/state` to observe a second client's `POST /spacemap/world/jump` and discarded delayed old-map `/target` and `/fire` responses. These are not captured official Unity endpoints.

## World-83 emulator movement revision across Redis replicas (2026-10-24)

No endpoint or payload shape changed. Emulator-only `POST /spacemap/world/move` on replica B returned `409 {"status":"move_changed"}` after replica A accepted a newer `expectedMoveId` for the same pilot. The shipped local page fetched `GET /spacemap/world/state`, rendered the newer destination, then retried successfully. Two live Kestrel hosts used distinct Redis connections. These are not captured official Unity endpoints.

## World-82 emulator stale move revision (2026-10-24)

Emulator-only `/spacemap/world/{enter,state,move}` exposes `pilot.moveId` (GUID string) and accepts optional `expectedMoveId` (GUID string) on `POST /move`. A stale revision returns HTTP 409 `{ "status": "move_changed" }` without changing saved travel. The shipped local page observed the conflict and refreshed its DOM over live Kestrel HTTP. These are not captured official Unity endpoints.

## World-81 emulator ordered movement (2026-10-24)

No endpoint or payload shape changed. The shipped local page serializes emulator-only `POST /spacemap/world/move` requests from one page instance, sending the latest queued click after the active request settles. A live Kestrel page check observed the final destination through `GET /spacemap/world/state`. These are not captured official Unity endpoints.

## World-80 emulator source-map movement (2026-10-24)

The emulator-only `POST /spacemap/world/move` accepts optional integer `mapId`, now sent by the shipped local Spacemap page. If the pilot has left that map when the command executes, it returns HTTP 409 `{ "status": "map_changed" }` and does not change travel. A live local page observed this after jumping from map 1 to map 2 while its move was delayed. This is not a captured official Unity endpoint.

## World-79 emulator state poll during movement (2026-10-24)

No endpoint or payload shape changed. The shipped local page suppressed an emulator-only `GET /spacemap/world/state` while `POST /spacemap/world/move` was pending before server delivery, then resumed state polling after the move rendered. These routes are not captured official Unity endpoints.

## World-78 emulator delayed state poll (2026-10-24)

No endpoint or JSON shape changed. The shipped local page received an emulator-only `GET /spacemap/world/state` response prepared before a `POST /spacemap/world/move`, and discarded the delayed state response after the move rendered. These are local emulator routes, not captured official Unity endpoints.

## World-77 emulator movement response order (2026-10-24)

No endpoint or JSON shape changed. The shipped local page issued two emulator-only `POST /spacemap/world/move` requests, rendered the second destination, and discarded the first response when it arrived later. This is not a captured official Unity endpoint.

## World-76 emulator single-flight jump (2026-10-24)

No endpoint or JSON shape changed. The shipped local page issued exactly one emulator-only `POST /spacemap/world/jump` despite repeated input while its HTTP 200 response was pending, then rendered the destination map. This does not establish an official Unity endpoint.

## World-75 emulator late combat responses (2026-10-24)

No endpoint shape changed. The shipped local page received a real emulator-only `POST /spacemap/world/target` HTTP 200 and, in a separate run, `POST /spacemap/world/fire` HTTP 409 from map 1 after `POST /spacemap/world/jump` reached map 2. It discarded each late response without restoring the old target or status. These are emulator contracts, not captured official Unity endpoints.

## World-74 emulator late move response (2026-10-24)

No endpoint shape changed. The shipped local page received a real emulator-only `POST /spacemap/world/move` response from map 1 after `POST /spacemap/world/jump` reached map 2. It discarded the stale response and sent no `POST /spacemap/world/collect`. These are emulator contracts, not captured official Unity endpoints.

## World-73 emulator late state response (2026-10-24)

No endpoint shape changed. The shipped local page received a real emulator-only `GET /spacemap/world/state` response from map 1 after a successful `POST /spacemap/world/jump` to map 2. It discarded the old response and sent no `POST /spacemap/world/collect`. These are emulator contracts, not captured official Unity endpoints.

## World-72 emulator jump cancels old-map pickup (2026-10-24)

No endpoint shape changed. The shipped local page sent emulator-only `POST /spacemap/world/move`, `POST /spacemap/world/jump`, and `GET /spacemap/world/state`. Its jump from map 1 to map 2 cleared the old box selection, so it sent no `POST /spacemap/world/collect`. These are emulator contracts, not captured official Unity endpoints.

## World-71 emulator NPC target cancels pickup (2026-10-24)

No endpoint shape changed. The shipped local page sent emulator-only `POST /spacemap/world/move` toward a box and `POST /spacemap/world/target` for a Streuner. After repeated `GET /spacemap/world/state` responses placed the pilot in pickup range, it sent no `POST /spacemap/world/collect`. This is emulator client behavior, not a captured official Unity endpoint.

## World-70 emulator cross-replica pickup (2026-10-24)

No endpoint shape changed. The shipped local page called emulator-only `GET /spacemap/world/state` on replica A and observed a box disappear after another authenticated client received HTTP 200 from `POST /spacemap/world/collect` on replica B. A sent no duplicate collect request. These are emulator contracts, not captured official Unity endpoints.

## World-68 emulator Spacemap pickup after admin change (2026-10-24)

No endpoint shape changed. The shipped page on replica B used emulator-only `GET /spacemap/world/state`, `POST /spacemap/world/move`, and `POST /spacemap/world/collect` after an in-game admin TCP change on replica A. Its collect response was HTTP 200 with a 200-credit reward at a distance above the default 180-unit range. No official Unity endpoint was captured.

## World-67 emulator Spacemap pickup-range HUD (2026-10-24)

No endpoint shape changed. The emulator-only `GET /spacemap/world/state` already returns integer `collectionRadius`. The shipped page now renders that value in `#pickupRange`. A live page on replica B observed 180, 400 and 180 after in-game TCP admin set and rollback on replica A through PostgreSQL and Redis. No official Unity endpoint was captured.

## World-66 emulator pickup conflict (2026-10-24)

Emulator-only `POST /spacemap/world/collect` returns `409 { "status": "distant" }` when the current `world.collectibles.pickup_radius` is smaller than the ship's distance. The shipped page retains the selected box, consumes the new `collectionRadius` from `GET /spacemap/world/state`, and retries in range. A live local browser run observed 400 → 180 and then a successful collection. No official Unity endpoint was captured.

## World-65 emulator Spacemap collectible radius (2026-10-24)

Emulator-only `POST /spacemap/world/enter` and `GET /spacemap/world/state` include integer `collectionRadius` in world units. The shipped page uses it to time `POST /spacemap/world/collect`. A live local page received radius 400 and collected beyond the previous 180-unit trigger. No official Unity endpoint or payload was captured.

## World-64 emulator cross-replica reward retry (2026-10-24)

The shipped local page sent emulator-only `POST /spacemap/world/fire` to replica B after killing the NPC through replica A. With separate real Redis connections, B returned HTTP 409 `{ "status": "destroyed" }` and retried the original reward. The page also polled `GET /spacemap/world/state` on B and saw the pilot at `(0,0)` with the NPC absent. No official Unity endpoint was captured.

## World-63 emulator reward retry without selection (2026-10-24)

The emulator-only `POST /spacemap/world/fire` accepts a killed NPC retry by its recorded killer even when `world:npc:target:<userId>` has been removed. The live local page observed HTTP 409 `{ "status": "destroyed" }` after the selection key was deleted. Another account remains ineligible. No official Unity endpoint was captured.

## World-61 distant NPC reward retry (2026-10-24)

The emulator-only `POST /spacemap/world/fire` still returns 409 `{ "status": "destroyed" }` for a retry by the killer. It now reaches the saved reward even if `GET /spacemap/world/state` places the pilot beyond laser range. The shipped local page observed the distant position and retried over live Kestrel HTTP. No official Unity endpoint was captured.

## World-60 emulator Spacemap reward retry (2026-10-24)

The existing emulator-only `POST /spacemap/world/fire` returns 409 `{ "status": "destroyed" }` when the same client retries a lethal shot. The server reattempts idempotent economy credit with the reward snapshot saved at the kill, so later multiplier changes do not alter that kill's amount. The shipped Spacemap page ran over live Kestrel HTTP, sent the retry, and received the 409 after the host changed the credits multiplier. No official Unity endpoint was captured.

## World-58 emulator Spacemap laser reward (2026-10-24)

The emulator-only `POST /spacemap/world/fire` lethal-hit JSON includes `reward` with credited NPC fields `credits`, `uridium`, `experience`, and `honor` after runtime multipliers. Nonlethal hits use `reward: null`. The shipped Spacemap page displayed the values from a live Kestrel response. This is not a captured official Unity endpoint.


## World-57 emulator Spacemap NPC count (2026-10-24)

No endpoint or payload shape changed. The shipped local page reads the existing `npcs` array from `POST /spacemap/world/enter` and `GET /spacemap/world/state` and displays its visible count. Live Kestrel/jsdom requests observed the count change from 2 to 1 after a second shared-cache world instance killed an NPC. This is an emulator-only client surface, not a captured official Unity endpoint.

## World-52 emulator TCP accepted laser result (2026-10-24)

No HTTP endpoint changed. Successful emulator `LaserFireRequest=27` now returns `LaserFireResultCommand=36` with `(npcId,"hit")` after the existing damage, kill and reward events. A loopback TCP client decoded the nonlethal and lethal sequences. This is not a captured official Unity packet.

## World-51 emulator TCP laser-fire result (2026-10-24)

No HTTP or official Unity endpoint was added. The emulator TCP world listener now answers a rejected `LaserFireRequest` with `LaserFireResultCommand=36` (`unselected`/`distant`/`missing`/`destroyed`/`invalid`). These statuses match the browser `/spacemap/world/fire` 409 payloads.


## World-50 emulator Spacemap combat conflicts (2026-10-24)

No official Unity endpoint was added. Emulator-only `POST /spacemap/world/fire` returns HTTP 409 `{ "status": "distant" }` when a selected NPC exceeds laser range. The shipped Spacemap client now renders this as “Target out of laser range. Move closer.” Live Kestrel/jsdom requests confirmed `/enter`, `/state`, `/target`, `/move` and `/fire`, with NPC health and removal reflected in the DOM.


## World-47 admin rollback conflict (2026-10-24)

Emulator-only `POST /api/v1/backoffice/admin/command` with `var rollback <id> <reason>` (and the Spacemap rollback route that uses the same dispatcher) now returns HTTP 409 `{ "error": "Rollback conflict: …" }` when a later change superseded change `<id>`. The shipped CLI surfaced this over live local HTTP against real PostgreSQL/Redis. This is not a captured official endpoint.

## World-46 emulator in-game admin rollback contract (2026-10-24)

The emulator-only `POST /spacemap/world/admin/vars/rollback` now requires JSON `{ "changeId": number, "reason": string }` with a nonblank reason of at most 256 characters; omission returns 400. The Spacemap form includes its reason in `POST /spacemap/world/admin/command`, then renders actor/node/reason/change ID/timestamp from `GET /spacemap/world/admin/vars/history`. These requests were exercised over live local HTTP by the shipped page in jsdom. They are not captured official Unity endpoints.

## World-45 (2026-10-24)

No new endpoints. `GET /spacemap/world/state` on replica B now follows a runtime variable resolved through real `sessions-redis`/PostgreSQL in `InGameAdminSocket_OnReplicaA_RealRedisAndPostgres_UpdatesLiveSpacemapAndSocketOnReplicaB`.


## World-44 in-game admin panel live requests (2026-10-24)

The shipped local Spacemap page called emulator-only `GET /spacemap/world/admin/vars`, `GET /spacemap/world/admin/vars/history`, `POST /spacemap/world/admin/command`, and `POST /spacemap/world/admin/vars/rollback` over real HTTP. The same page consumed `GET /spacemap/world/state` and rendered safe-zone changes 1000 → 500 → 1000. These routes are not captured official Unity endpoints.

## World-43 in-game admin command to live Spacemap state (2026-10-24)

No new endpoints. Provisional TCP command 32 `/var set` and `/var rollback` on replica A change the `GET /spacemap/world/state` safe-zone result on replica B through the shared runtime-variable cache key. Covered by `InGameAdminSocket_OnReplicaA_UpdatesLiveSpacemapAndSocketClientOnReplicaB`.


## World-42 CMS edit to live Spacemap state (2026-10-24)

The World-41 CMS `PUT /api/v1/backoffice/runtime-vars/{key}` and history rollback on replica A now reach the shipped local Spacemap page through real `POST /spacemap/world/enter` and `GET /spacemap/world/state` calls on replica B. The DOM showed the effective safe-zone radius changing to 500 and back to 1000. These routes are emulator contracts; no captured official Unity endpoint changed.

## World-41 CMS runtime-variable endpoints exercised live (2026-10-24)

- `GET /api/v1/backoffice/runtime-vars`, `GET /api/v1/backoffice/runtime-vars/history`, `PUT /api/v1/backoffice/runtime-vars/{key}`, `POST /api/v1/backoffice/runtime-vars/history/{changeId}/rollback` were called by the shipped CMS component over real HTTP with a Bearer token and a CORS preflight. Live spec: `ThDarkOrbitEmulator.Cms/src/app/features/backoffice/runtime-vars/runtime-vars.component.live.spec.ts` (skipped unless `CMS_LIVE_API_BASE` is set).

## World-40 cross-replica socket and Spacemap (2026-10-24)

No endpoint or payload shape changed. The existing emulator-only `/spacemap/world/{enter,state,move}` endpoints served the live local page on replica B while the CLI sent `POST /api/v1/backoffice/admin/command` to replica A. On the same variable set and rollback, B's provisional TCP WorldServer client received `SpaceStationCommand` and `SafeZoneCommand` updates. Native Unity protocol compatibility remains unverified.

## World-39 split-replica endpoints (2026-10-24)

`POST /api/v1/backoffice/admin/command` (CLI) and `/spacemap/world/{enter,state,move}` (page) were served by two different hosts in one live check, with no change to the contracts. Runtime-variable reads on the Spacemap host come from the shared distributed cache key `runtime-variable:v1:<key>`. These are emulator-only endpoints.

## World-38 emulator Spacemap move (2026-10-24)

The shipped local Spacemap JavaScript sent `POST /spacemap/world/move` after a canvas click, then consumed `GET /spacemap/world/state` over real HTTP. The returned interpolated pilot coordinates advanced the DOM position and canvas ship drawing. These are emulator-only endpoints, not captured official Unity contracts.

## World-37 live Spacemap DOM via Server Management CLI (2026-10-24)

- New client script `scripts/client-testing/verify-spacemap-live.mjs <baseUrl> <dosid>` runs the shipped `LocalSpacemapPage` HTML/JS in jsdom. All `/spacemap/world/*` fetches go over real HTTP to a live host, with no synthetic responses. Between state polls it runs the shipped `scripts/admin/Invoke-ServerAdminCommand.ps1` as a real PowerShell process.
- New `[Explicit]` test `ServerManagementCliLiveSocketTests.ServerManagementCli_PeaceRadiusSetAndRollback_UpdateLiveSpacemapSafeZoneDom` starts one Kestrel host. It serves both the backoffice admin command route and the real `WorldHttpBridge.Map` routes over one shared runtime-variable registry. The pilot is parked at (2800,1300), 800 units from home station 100.
- Observed in the client DOM: `#safeZone` = `Protected`, then `Outside (radius 500)` after CLI `var set world.station.peace_radius 500`, then `Protected` after CLI `var rollback 1`. Position stayed `2800 / 1300`. Requests: `POST /enter` followed by three `GET /state`. Both changes were attributed to `ServerManagement` (changeIds 1 and 2).
- This closes the World-36 gap where the DOM check and the endpoint check were separate.
- Remaining gaps: the registry and dispatcher are in-memory, so PostgreSQL/Redis cross-replica propagation is not covered. The page ran in jsdom, not the signed-in Unity Vuplex WebView. The Angular CMS was not exercised.

## World-36 local browser safe-zone state (2026-10-24)

No endpoint or JSON shape changed. Emulator-only `GET /spacemap/world/state` already returns the effective `map.station.peaceRadius`; the local Spacemap now displays its inside/outside status. A direct handler check observed radius 1000 → 500 → 1000, and the shipped JavaScript rendered those states with synthetic fetch responses. Native Unity usage is unverified.

## World-35 Server Management CLI to TCP safe zone (2026-10-24)

No HTTP endpoint or JSON shape changed. `POST /api/v1/backoffice/admin/command` with `var set world.station.peace_radius …` and `var rollback …`, sent by the shipped CLI, returned `ServerManagement`-attributed change entries. A connected loopback TCP client then decoded the provisional station and safe-zone packets for both changes.

## World-34 emulator TCP safe-zone updates (2026-10-24)

No HTTP endpoint or JSON shape changed. The provisional connected TCP client now receives `SpaceStationCommand` (7) after `world.station.peace_radius` changes and `SafeZoneCommand` (9) when its interpolated ship position crosses the effective radius. A loopback client decoded these packets. Native Unity handling is unverified.

## World-33 emulator TCP live jump radius (2026-10-24)

No HTTP endpoint changed. The provisional TCP `JumpRequest` handler uses the current `world.portal.jump_radius` for each jump; a loopback client confirmed rejection and acceptance on one socket. Native Unity handling is unverified.

## World-32 emulator TCP own same-map teleport (2026-10-24)

No HTTP endpoint changed. The provisional TCP listener sends `MoveCommand` (12) with zero duration to a pilot whose saved stationary position changes through a same-map teleport on another replica. A loopback client confirmed the packet and movement afterward; native Unity handling is unverified.

## World-30 emulator TCP remote teleport (2026-10-24)

No HTTP endpoint or JSON shape changed. The provisional TCP listener now sends `MoveCommand` (12) with zero duration to an idle observer when a visible pilot teleports to new coordinates on the same map from another replica. This packet has not been confirmed in the native Unity client.

## World-29 emulator presence batching (2026-10-24)

No endpoint change. `/spacemap/world/state` and TCP world responses are unchanged; mesh maintenance ticks now write each map roster once per due batch.

## World-28 emulator state presence cadence (2026-10-24)

No official endpoint or JSON shape changed. Repeated emulator-only `GET /spacemap/world/state` polls no longer rewrite map presence on every request; the local 15-second heartbeat is shared with the mesh tick. A state request that restores a saved map transition removes old-map presence immediately. This is not a captured native Unity contract.

## World-26 local browser pilot rendering (2026-10-24)

No endpoint changed. The local Spacemap page now consumes the existing `pilots` entries (`id`, `name`, `x`, `y`) from emulator-only `GET /spacemap/world/state` to paint other ships and show a nearby count. This is not a captured official Unity endpoint or verified native client behavior.

## World-21 state-poll travel retiming (2026-10-24)

No official endpoint changed. The emulator-only `GET /spacemap/world/state` and `POST /spacemap/world/enter` now reconcile active travel with the current runtime speed variable before responding. The provisional TCP listener broadcasts a revised `MoveCommand` before changed ship attributes. A loopback client confirmed the TCP sequence and saved travel; native Unity and signed-in browser behavior remain unverified.

## World-20 idle TCP attributes (2026-10-24)

No HTTP endpoint changed. An admitted emulator TCP socket sends provisional `ShipAttributesCommand` when effective speed or laser damage changes during its one-second idle poll. A connected loopback client decoded the change and rollback; native Unity handling and live CMS/CLI-to-world propagation remain unverified.

## World-19 idle TCP handoff (2026-10-24)

No HTTP endpoint changed. The emulator sends provisional TCP `MapChangeCommand` (19) on an idle admitted socket within one polling interval after a remote teleport or map lease reassignment. Connected loopback clients decoded the route without sending another packet. Native Unity packet handling remains unverified.

## World-18 local TCP route after remote teleport (2026-10-24)

No HTTP endpoint changed. On the next packet from an old socket after a remote teleport, the emulator sends provisional TCP `MapChangeCommand` (19) with the saved destination map and lease owner's address. A loopback client observed this command; native Unity packet compatibility remains unverified.

## World-17 local presence after remote teleport (2026-10-24)

No Unity endpoint or packet changed. The emulator-only `GET /spacemap/world/state` now observes map 5 without a duplicate map 1 presence after another replica teleports a pilot and the old node heartbeats. The shipped Spacemap script redraws the map and coordinates on its next poll in a synthetic jsdom exercise. Native Unity behavior remains unverified.

## World-16 local state-poll repair fence (2026-10-24)

Emulator-only `GET /spacemap/world/state` and `POST /spacemap/world/enter` can repair hull at the home station. If the map lease is missing or changes before the write, they now return JSON 409 `{ "status": "map_lease_changed" }` without repairing hull. This is not a captured official Unity endpoint or packet.

## World-15 local target lease contract (2026-10-24)

The emulator-only `POST /spacemap/world/target` now binds NPC selection to the map lease epoch under the shared mesh mutex. A missing or changed lease returns JSON 409 `{ "status": "map_lease_changed" }` before writing the shared target lock. The matching provisional TCP SelectShipRequest uses the same path. This is not a captured official Unity endpoint or packet.

## World-14 local browser lease conflict (2026-10-24)

The emulator-only `POST /spacemap/world/{move,config,jump,collect,fire}` mutations bind to a live map lease epoch. A missing or changed lease returns JSON 409 `{ "status": "map_lease_changed" }`; the shipped browser client shows a retry message. Direct endpoint tests and a jsdom client run cover this local contract. No captured official Unity HTTP endpoint or TCP packet changed.

## Client-verify-1 local script exercise (2026-10-24)

The real emulator Spacemap script was run in jsdom against synthetic `fetch` responses for `POST /spacemap/world/enter`, `GET /state`, `POST /move`, and in-game admin command/history/rollback routes. It rendered all four company variants and sent the expected canvas move and admin requests. This does not add captured Unity endpoints or confirm server responses. See [client verification report](client-verification-report.md).

## World-12 local TCP lease fence (2026-10-24)

No HTTP endpoint or captured Unity contract changed. The provisional TCP listener now checks its admitted map lease epoch inside movement, configuration and portal state mutations. If ownership changed, it sends local `MapChangeCommand` (19) and closes. The native client's handling of command 19 remains unverified.

## World-11 TCP owner route (2026-10-24)

The emulator's provisional TCP listener sends local `MapChangeCommand` (19) with map ID, owner node ID, and TCP host:port when login lands on the wrong node or a connected socket loses its lease. It then closes the socket. This changes no HTTP endpoint or captured Unity contract. Browser `/spacemap/world/*` requests still use shared state through any API replica; native handling of command 19 is unverified.

## World-10 verification surface (2026-10-24)

`WorldMeshJourneyTests` checks the emulator-only `/xml/maps.php` lease addresses and `/spacemap/world/{enter,state,move,config,jump,collect,target,fire}` behavior across separate node stores. The Backoffice integration test checks `POST /api/v1/backoffice/mesh/nodes/{nodeId}/drain` lease reassignment and preserved pilot state. The tested TCP packets are the provisional local contract; native Unity endpoint and packet compatibility remain unverified.

## World-8 local collection bridge (2026-10-24)

`GET /spacemap/world/state` includes a `boxes` array (`id,kind,x,y,assetId,amount`). `POST /spacemap/world/collect` accepts JSON `{ "boxId": int }` with the `dosid` cookie, verifies server position within 180 units and shared availability, and responds with reward details or a JSON 409 status (`missing`, `distant`, `claimed`, `cargo_full`). This is an emulator-only browser contract. The analogous local TCP request uses command 20; no official Unity collectible packet has been captured.

## Mesh-5 CMS operations (2026-10-24)

The Angular CMS reads `/api/v1/backoffice/mesh/topology` and `/api/v1/backoffice/world/overview` every 10 seconds. Its admin controls call `PUT /mesh/scale`, `POST /mesh/rebalance`, `POST /mesh/nodes/{nodeId}/drain`, new `POST /mesh/nodes/{nodeId}/activate`, and `POST /world/pilots/{userId}/teleport` under `/api/v1/backoffice`. Activation accepts only a live draining node. These are emulator backoffice contracts, not captured Unity endpoints.

## Mesh-4 Backoffice routes (2026-10-24)

The emulator adds `GET /api/v1/backoffice/mesh/topology`, `PUT /mesh/scale`, `POST /mesh/nodes/{nodeId}/drain`, `POST /mesh/rebalance`, `GET /api/v1/backoffice/world/overview`, and `POST /world/pilots/{userId}/teleport` under the same `/api/v1/backoffice` prefix. They are admin operations, not captured client endpoints. Reads use `OpsRead`; mutations use `OpsWrite`.

## Mesh-3 world routing (2026-10-24)

`GET /xml/maps.php` and `/xml/maps.xml` resolve each starter map through the live mesh lease and active node TCP address. Maps without a live owner use the configured local world listener. `/unityApi/login.php` retains the captured HTTPS `host` and adds an emulator-only `gameServerIP` value for the selected map, using the same owner/fallback rule. The native client has not been verified to read this extension or parse `host:port` in map XML. `gateway-players` now forwards `/spacemap/world/*` through YARP service discovery. Shared Redis presence and travel let any API replica serve consecutive browser requests. Successful authenticated world replies expose `X-Mesh-Node-Id` and `X-Mesh-Map-Owner-Node`; state replies include a `pilots` occupancy array.

## Mesh-2 registry (2026-10-24)

`api-game` now publishes its actual bound world TCP port, process status and map leases in the Redis-backed mesh registry. No Unity-facing endpoint or payload changed. Map XML still advertises the responding process's port; it does not yet resolve the lease owner for a requested map. Native client compatibility remains unverified.

The admin-only `GET /api/v1/backoffice/operations/mesh` returns the node and lease snapshot for `OpsRead` users. It is an emulator operations contract, not a captured client endpoint.


## Mesh-1 dynamic local world port (2026-10-24)

With `api-game` replicas above one, each local process binds an available loopback TCP port and advertises its own bound port in `/xml/maps.php` and `/xml/maps.xml`. These are emulator-defined map responses. The gateway has no pilot affinity or map-owner routing yet, so the advertised endpoint may not be the owner of later requests. Default one-replica behavior remains `127.0.0.1:60954`; native client parsing is unverified.

## Phase 2 configuration and companions (world-6, 2026-10-24)

`GET /spacemap/world/state` now includes effective `pilot.laserDamage` and a `pilot.companions` array (`kind,itemId,index,formation,x,y`) alongside current shield and speed. `POST /spacemap/world/config` and `POST /spacemap/world/move` with a configuration selection enforce a five-second switch cooldown (JSON 409 with `cooldownRemainingMs`); a successful switch recalculates speed, shield and laser damage and retimes flight. The TCP listener adds local attribute, cooldown and companion state packets. These are emulator-defined contracts, not observed official endpoints or Unity wire bytes. See [world protocol](world-protocol.md).

Configuration, shield, cooldown timestamp and travel now survive re-entry through the distributed cache. Simultaneous commands across replicas are not serialized yet; see [server mesh scaling](server-mesh-scaling.md).

## Phase 2 browser Spacemap (world-5, 2026-10-24)

The local menu's LAUNCH link and `internalStart` open `internalMapRevolution`, a 2D canvas world. Cookie-authenticated JSON endpoints at `/spacemap/world/enter`, `/spacemap/world/state`, `/spacemap/world/move`, and `/spacemap/world/config` use the same pilot resolver, world session store and movement engine as the loopback TCP listener. Map, station and gates come from `WorldMapInstance.Starter`. Movement is server interpolated and shared with socket observers. These paths are emulator-defined and have not been observed on the official servers. See [world protocol](world-protocol.md).

## Phase 2 local ship movement (world-4, 2026-10-24)

The authenticated loopback world listener accepts provisional `MoveRequest` and configuration selection packets, interpolates server position using the active loadout's speed, clamps destinations to the 21000 × 13100 map, broadcasts `MoveCommand` to pilots on that map, and saves the travel trajectory for reconnect. Out-of-bounds requests set a local radiation warning packet. This changes no HTTP endpoint. The local packet contract and Unity compatibility limits are in [world protocol](world-protocol.md).

## Phase 2 local hero initialization (world-3, 2026-10-24)

The TCP listener now follows authenticated login with local map/station/portal/safe-zone packets and a hero ship snapshot for the selected company, active hangar and saved configuration-1 equipment. The snapshot includes derived ship stats, credits and uridium. The map is 21000 × 13100 with a `(2000,1300)` home spawn. The wire contract and evidence limits are in [world protocol](world-protocol.md). No HTTP endpoint changed. XP/Honor/Level remain starter defaults because the economy has no progression balances. Unity rendering and native packet compatibility remain unverified.

## Phase 2 local world listener (world-2, 2026-10-24)

ApiService now has a configurable loopback TCP listener (`WorldServer`, default `127.0.0.1:60954`). When enabled, `/xml/maps.php` and `/xml/maps.xml` put that address in `GameServerIP` for starter maps 1/MMO, 5/EIC, 9/VRU, and 13/TST. `/unityApi/login.php` continues to put the HTTPS game origin in `host` and the selected starter map in `mapID`; its `sessionID` is checked at the TCP login boundary. The TCP framing and command IDs currently implement a documented local diagnostic contract in [world protocol](world-protocol.md). Actual Unity address parsing and wire compatibility remain unverified; no Redirect or Live world connection was claimed.

## Phase 2 world protocol inventory (world-1, 2026-10-24)

[World protocol](world-protocol.md) inventories the IL2CPP version/login, ship initialization/spawn, map entity and movement commands. The captured `/unityApi/login.php` nested JSON supplies user/session/map/hangar fields; `DarkOrbit.MapsPattern` names `ID` and `GameServerIP` for a candidate `/xml/maps.php` lookup. Neither the Live map XML body, TCP port, frame format, command IDs nor a completed world connection is verified. The local maps XML remains provisional. No HTTP endpoint or response changed in world-1.

## Local Events battle pass search (menu-212, 2026-10-24)

`GET /indexInternal.es?action=internalEvents` accepts optional `q` (tier ID, name, or reward text) with `filter` and `detail`. A trimmed nonblank search must be 2–40 ASCII letters, digits, spaces, hyphens or underscores. A malformed, repeated or wrong-case key returns no-store HTML 400 before session lookup; a detail outside the searched status result returns HTML 404. Blank `q` shows all tiers. Search changes no reward action or saved state. This contract is emulator-defined and unverified in Redirect.

## Local Auction lot search (menu-208, 2026-10-24)

`GET /indexInternal.es?action=internalAuction` accepts optional `q` with the existing `filter` and notice keys. It matches lot ID, item ID and name case-insensitively after trimming. Nonblank text outside 2–40 ASCII letters, digits, spaces, hyphens or underscores, `q` with `lot`, repeated or wrong-case keys, and control characters return the existing no-store HTML 400. Blank `q` shows the full list. No POST action, successful redirect or persisted state changed. This search is emulator-defined and unverified in Redirect.

## Local Galaxy Gates search (menu-206, 2026-10-24)

`GET /indexInternal.es?action=internalGalaxyGates` accepts one optional `q` alongside `filter` and the existing notice keys. It matches gate IDs and names case-insensitively after trimming. Nonblank text outside 2-40 ASCII letters, digits, spaces, hyphens, or underscores, `q` with `gate`, a repeated or wrong-case key, or control characters return the existing no-store HTML 400. Blank `q` shows the full list. No action form, route, or persisted state changed. This search is emulator-defined and unverified in Redirect.

## Local Tech Factory recipe search (menu-205, 2026-10-24)

`GET /indexInternal.es?action=internalTechFactory` accepts one optional `q` view parameter, matched case-insensitively against recipe ids and names, alongside `filter`. Nonblank trimmed text outside 2-40 characters, a nonblank `q` combined with `detail`, a repeated or wrong-case key, or control characters return the existing no-store HTML 400. An empty or blank `q` means no search. No routes, forms or state changed. This is emulator-defined and unverified in the Redirect client.

## Local Quest claim timestamp bounds (menu-204, 2026-10-24)

`POST /indexInternal.es?action=internalQuestClaim` returns its existing no-store HTML 400 for a correctly signed 12-digit Unix issue time beyond the supported `DateTimeOffset` range, before reward state is read or changed. A representable time beyond the five-minute future skew also remains 400. Request fields and successful 303 responses are unchanged. This is emulator-defined hardening, unverified in Redirect.

## Local Support section search (menu-202, 2026-10-24)

`GET /indexInternal.es?action=internalSupport` accepts an optional `q` on the overview. It matches local section IDs, titles, and descriptions case-insensitively and returns a count, clear link, and zero-result message. An empty or whitespace-only value opens the full overview. A nonblank value must be 2–40 characters after trimming and cannot be combined with `section`; repeated or wrong-case `q` and control characters return the existing no-store HTML 400. No POST action, captured link target, or stored state changed. This search is emulator-defined and unverified in Redirect.

## Local Pilot Sheet page query spelling (menu-201, 2026-10-24)

`GET /indexInternal.es?action=internalPilotSheet` now requires exact lowercase
query keys (`action`, `from`, `prc`, `lang`, `detail`, `filter`, `trained`,
`reset`) and the canonical `internalPilotSheet` action value. Wrong-case keys or
values, unknown keys and control characters return the no-store Pilot Sheet HTML
400 before session lookup or the pre-company 302 to `internalCompanyChoose`,
without echoing input. Canonical requests and the POST allocate/reset actions
are unchanged. This is an emulator-defined validation rule; Redirect client use
is unverified.

## Local Space Search page query spelling (menu-200, 2026-10-24)

`GET /indexInternal.es?action=internalSpaceSearch` now requires exact lowercase
query keys (`action`, `from`, `prc`, `lang`, `filter`, `planet`, `explored`) and
the canonical `internalSpaceSearch` action value. Wrong-case keys or values,
unknown keys and control characters return a no-store HTML 400 before session
lookup or the captured pre-company 302 to `internalCompanyChoose`, without
echoing input. Canonical requests and the POST explore action are unchanged.
This is an emulator-defined validation rule; Redirect client use is unverified.

## Local Payment page query spelling (menu-199, 2026-10-24)

`GET /indexInternal.es?action=internalPayment` now requires exact lowercase
query keys (`action`, `from`, `prc`, `lang`, `detail`, `claimed`, `filter`,
`q`) and the canonical `internalPayment` action value. Wrong-case keys or
values, unknown keys and control characters return a no-store HTML 400 before
the session lookup and before the captured pre-company 302 to
`internalCompanyChoose`; the input is not echoed. Canonical requests are
unchanged, including that 302. Not verified in the Redirect client.

## Local Events battle pass status filter (menu-211, 2026-10-24)

`GET /indexInternal.es?action=internalEvents` accepts an optional exact
lowercase `filter=all|ready|in-progress|claimed|unavailable`. Any other value,
casing, or a repeated key returns no-store HTML 400 before the session lookup.
With a valid session and company, `detail` must name a tier whose current
status matches the filter; otherwise the response is HTML 404. The POST actions,
redirect locations, and saved rewards are unchanged. The parameter and its
values are emulator-defined (no Live capture) and remain unverified in Redirect.

## Local Events page query spelling (menu-198, 2026-10-24)

`GET /indexInternal.es?action=internalEvents` now requires exact lowercase
query keys and the canonical `internalEvents` action value. Wrong-case shell,
detail, and notice keys return no-store HTML 400 before session lookup or
company-selection redirect, without echoing rejected input. Valid list,
detail, and notice URLs are unchanged; no endpoint, POST action, or persisted
reward state changed. This is an emulator-defined validation rule and remains
unverified in Redirect. Tests: `LocalEventsQueryKeyTests` and
`LocalMenuDetailRoutingTests`.

## Local Auction query spelling (menu-194, 2026-10-24)

`GET /indexInternal.es?action=internalAuction` now requires exact lowercase
shell query keys and the canonical action value. Wrong-case keys or values
return no-store HTML 400 before session lookup or company-choice redirect,
without echoing rejected input. Canonical list, filter, detail and notice
queries, and signed bid/claim POSTs are unchanged. No endpoint or payload was
added. This is an emulator-defined validation rule; Redirect client use
remains unverified. Covered by `LocalAuctionQueryKeyTests` and
`LocalMenuDetailRoutingTests`.

## Local Command Center query spelling (menu-190, 2026-10-24)

`GET /indexInternal.es?action=internalCommandCenter` now requires exact
lowercase query keys and the canonical `action` value. Differently cased
keys or action values return no-store HTML 400 before session lookup or a
company-selection redirect. No endpoint, successful response, or state
changed. This is an emulator-defined validation rule; Redirect client use
remains unverified.

## Local command center asset response bounds (menu-184, 2026-10-24)

`GET /resources/command-center/categories.xml` now sends the exact UTF-8
content length. It and manifest artwork under `/do_img/new/` send
`X-Content-Type-Options: nosniff` on 200 and 304. A 304 retains its ETag and
cache policy but has no body or content length. Paths, content, and status
rules are unchanged; Redirect client use remains unverified.

## Local Hall of Fame entry threshold (menu-183, 2026-10-24)

`GET /indexInternal.es?action=internalHallofFame` list pages now include a
`#ownRankingThreshold` paragraph: the score needed to enter the selected
category/company board (one point above the last listed pilot), that pilot,
and a link to the board's last page. Page and search do not change it. No
endpoint, status code, or state changed; the rule is emulator-defined and
Redirect client use remains unverified.

## Local Hall of Fame empty filters (menu-179, 2026-10-24)

`GET /indexInternal.es?action=internalHallofFame` now rejects an explicitly
empty `category=` or `page=` with no-store HTML 400 before session lookup.
Omitted keys keep the experience and first-page defaults. No endpoint or
successful response changed. This is an emulator-defined validation rule;
Redirect client use remains unverified.

## Local Unity hangar ID bounds (menu-178, 2026-10-24)

`GET /unityApi/login.php` continues to return the same nested JSON fields and
two synthetic local hangars. Their `hangarId` strings now remain distinct,
numeric, and six digits wide even when the signed 64-bit local game user ID is
large. The first ID uses the user ID modulo 900,000, and the second adds
100,000 to that bounded value; ordinary small user IDs retain their previous
IDs. The Dock page uses the same values. This normalization follows the six
digit shape in the allowlisted Live login projection, but the mapping is an
emulator choice and has not been verified in Redirect.

## Local Logbook asset filter (menu-176, 2026-10-24)

`GET /indexInternal.es?action=internalLogbook` accepts an optional exact-case
`asset={id}` query to show only that signed-in account's saved ledger entries
for one asset. Asset tabs, search, category/source tabs, pagination, and entry
detail navigation retain the filter; choosing Demo entries clears it. The
summary shows only the selected asset. Empty, repeated, malformed, or
differently cased asset parameters return HTML 400, as does combining an asset
with `source=demo`. A well-formed asset absent from the account's ledger
returns 404. The filter reads ledger state without changing it. This is an
emulator-defined route and remains unverified in Redirect.

## Local Galaxy Gates filtered actions (menu-171, 2026-10-24)

`GET /indexInternal.es?action=internalGalaxyGates` now requires the exact
lowercase `action` key and value, plus exact lowercase optional query keys.
Malformed casing returns no-store HTML 400 before the session check. A Spin or
Activate form shown under `filter=activated|ready|incomplete` includes that
filter as a hidden field. The corresponding POST accepts only a single known
filter value and redirects successful actions to
`internalGalaxyGates&spun={gate}&filter={filter}` or
`&activated={gate}&filter={filter}`. The saved gate state still supplies the
confirmation. Invalid, repeated, or differently cased form fields return
HTML 400 without spending Galaxy Gate energy or changing progress. Unfiltered
forms and redirects retain their previous shape. These are emulator-defined
rules; Redirect client use remains unverified.

## Local Account page query spelling (menu-170, 2026-10-24)

`GET /indexInternal.es?action=internalAccount` requires the exact lowercase
`action` key and `internalAccount` value. Optional `from`, `prc`, `lang`,
`saved`, and `unchanged` keys must also be lowercase (`unchanged` must be exactly `1`
and cannot be combined with `saved`; menu-187). Differently cased keys or action values
return no-store HTML 400 before the company-selection redirect. The page and
save responses are otherwise unchanged. This is an emulator-defined rule;
Redirect client use remains unverified.

## Local Command center search (menu-168, 2026-10-24)

`GET /indexInternal.es?action=internalCommandCenter` now accepts optional `q`
with `category`, `sort`, and `urgency`. It searches the captured notice name,
category id, and background filename case-insensitively. Category and urgency
counts reflect matching notices; item detail links keep the search and navigate
only matching siblings. `q` must be 2–40 ASCII letters, digits, spaces,
hyphens, or underscores without edge spaces. An invalid or repeated value
returns HTML 400 before session lookup; an item outside the results returns
HTML 404. This query and page behavior are emulator-defined, with no Live
post-selection response or Redirect client verification.

## Local Hangar equipment form bounds (menu-166, 2026-10-24)

`GET /indexInternal.es?action=internalDockEquipment` rejects control characters
in optional `from`, `prc`, and `lang` values with the existing HTML 400 before
session lookup. The rendered quantity input now caps its maximum at the POST
limit of 999, including when a local inventory grant exceeds that amount. A
legacy assignment above 999 can be lowered through the same form. No request
fields or successful response changed. This is emulator-defined and unverified
in Redirect.

## Local Auction inventory capacity (menu-165, 2026-10-24)

`GET /indexInternal.es?action=internalAuction` now reads the account's saved
item inventory. For an ended lot with a saved bid, the list and detail omit
the Claim form when the reward item's stack is at the signed 64-bit limit;
the detail explains that inventory space is needed. The `claimable` filter
still describes the lot's bid and timer state. Claim POST fields and statuses
are unchanged and still check capacity in the transaction. This is
emulator-defined and unverified in Redirect.

## Local Auction action availability (menu-162, 2026-10-24)

`GET /indexInternal.es?action=internalAuction` now omits Claim forms for ended
lots without a saved bid and omits Bid forms when the current local Credits
balance cannot cover the minimum new hold. This applies to the list and lot
detail. An affordable raise considers only the difference above the existing
held bid. The detail still explains an unaffordable minimum. POST fields,
validation, and persisted bidding rules are unchanged. This is emulator-defined;
no Live post-selection Auction response or Redirect use was captured.

## Local Voucher credit capacity (menu-160, 2026-10-24)

`GET /indexInternal.es?action=internalVouchers` marks a Credits reward as
`balance-limit` when it would exceed the local integer credit balance. The
Unavailable filter includes that code; list and detail pages omit its Redeem
form. `POST /indexInternal.es?action=internalVoucherRedeem` returns no-store
HTML 409 with a specific credit-limit message and preserves the unredeemed code.
After spending credits, the same code can be redeemed. Request fields and
successful 303 responses are unchanged. This is emulator-defined; no Live
post-selection Voucher response or Redirect client verification was captured.

## Local Tech Factory output capacity (menu-159, 2026-10-24)

`GET /indexInternal.es?action=internalTechFactory` reads the ready job's saved
output quantity and current resource-wallet balance. When claiming would exceed
the signed 64-bit balance limit, the page shows `#techFactoryClaimBlocked` and
omits the Claim form. `POST ...?action=internalTechFactoryClaim` returns the
existing no-store HTML 409 with a specific full-wallet message. The job remains
ready; spending enough output resource permits a later claim. The transaction
still checks capacity atomically, so a balance change after page load cannot
overflow the wallet. No fields or successful response changed. This is
emulator-defined; neither Live nor Redirect post-company use was captured.

## Local Events reward capacity (menu-158, 2026-10-24)

`GET /indexInternal.es?action=internalEvents` marks a ready expedition as
`reward-unavailable` when the shield reward is missing from local inventory or
its quantity is at the local integer limit. It shows an explanation instead of
a Claim form. The `expedition=started` notice also explains that state. The
existing POST claim still checks capacity, returns HTML 409, and saves no claim
on failure. No request fields or successful response changed. This is
emulator-defined; no Live Events page or Redirect use was captured.

## Local Shop quantity capacity (menu-157, 2026-10-24)

`POST /indexInternal.es?action=internalShopBuy` checks both saved inventory and
per-listing purchased quantity against the signed 64-bit limit before debiting
local currency. A full counter returns the existing HTML 409 unavailable page
without recording the order or changing the wallet. The Shop GET page disables
the corresponding Buy form at either limit. Fields and successful responses are
unchanged. This is emulator-defined; no Live purchase or Redirect client
verification exists.

## Local Payment package search (menu-152, 2026-10-24)

`GET /indexInternal.es?action=internalPayment` accepts optional `q` (2-40 ASCII
letters, digits, spaces, hyphens, or underscores; no edge spaces) alongside
`filter`, `detail`, and `claimed`. It searches package IDs, offer names, and
asset IDs case-insensitively. Results and filter counts are scoped to the
search; detail navigation keeps it. A detail outside the results returns HTML
404. Malformed, repeated, or wrong-case `q` returns HTML 400 for a
company-selected session. The overall claim summary and claim POST are unchanged.
This query and HTML are emulator-defined; no Live Payment page or Redirect use
was captured.

## Local Shop currency-bound purchase forms (menu-151, 2026-10-24)

`GET /indexInternal.es?action=internalShop` now signs each database-backed
purchase form for its displayed item and currency. On
`POST /indexInternal.es?action=internalShopBuy`, changing the hidden `currency`
while reusing that form's `idempotencyKey` returns the existing uncached HTML
400 before catalog or database access. The user can still select any valid
quantity from 1 to 100; it is checked against current price, balance, stock,
and account limits at purchase time. Earlier local forms need a reload. No
field names, response statuses, or successful purchase behavior changed. This
is emulator-defined; no Live Shop purchase or Redirect verification exists.

## Local Shop purchase timestamp validation (menu-150, 2026-10-24)

`POST /indexInternal.es?action=internalShopBuy` rejects a correctly signed
`idempotencyKey` whose canonical Unix issue time is beyond .NET's supported
`DateTimeOffset` range with the existing uncached HTML 400 error. The key is
rejected before catalog or database access. Form fields, response shape, and
successful purchase behavior are unchanged. This validation is emulator-defined;
there is no captured Live Shop purchase request or Redirect verification.

## Local Skylab collector search (menu-207, 2026-10-24)

`GET /indexInternal.es?action=internalSkylab` accepts an optional exact-case `q`. It matches collector IDs and names, ignoring case. A blank value is the full list. Otherwise the trimmed value must be 2-40 ASCII letters, digits, spaces, `-` or `_`. A repeated `q`, wrong-case `Q`, control characters, or `q` combined with `detail` return the no-store HTML 400 before the session check. Filter tabs and the search form keep both `filter` and `q`. Detail links, upgrade/collect forms, and the `upgraded`/`collected` redirects are unchanged. No new routes. The behavior is emulator-defined. Tests: `LocalSkylabSearchTests`.

## Local Skylab exact page query keys (menu-192, 2026-10-24)

`GET /indexInternal.es?action=internalSkylab` requires the exact lowercase shell keys (`action`, `from`, `prc`, `lang`) and the canonical `internalSkylab` action value. Wrong-case keys or action values and control characters return no-store HTML 400 before session or company checks, with no redirect and no reflected input. The existing `detail`, `filter`, `upgraded` and `collected` query rules and signed POST actions are unchanged. The rule is emulator-defined; no post-selection Live Skylab response was captured. Tests: `LocalSkylabQueryKeyTests`, `LocalMenuDetailRoutingTests`.

## Local Skylab storage cap (menu-149, 2026-10-24)

`POST /indexInternal.es?action=internalSkylabCollect` still credits at most 60
whole minutes of resource production. After a successful collection, both the
cache and Postgres progress clocks advance past all elapsed whole minutes, so
time beyond the storage cap cannot be collected by submitting another form.
An unfinished fractional minute remains toward the next collection. The
Skylab page states this cap. No request fields or response status codes changed;
this is emulator-defined behavior without a Live post-selection capture or
Redirect client verification.

## Local Messaging search (menu-142, 2026-10-24)

`GET /indexInternal.es?action=internalMessages` accepts optional `q` (2–40
characters, no edge whitespace or controls) alongside `folder`. It searches
message subject, body, and displayed sender or recipient case-insensitively,
scoped to the selected folder. The result count, folder tabs, and message
Previous/Next/Close links preserve the search. A message outside the results
returns HTML 404 without marking it read. Invalid or repeated `q` returns
HTML 400 before session lookup. This query and HTML are emulator-defined;
there is no Live post-selection Messaging contract or Redirect verification.

## Local Shop catalog search (menu-141, 2026-10-24)

`GET /indexInternal.es?action=internalShop` accepts optional `q` with an exact
lowercase key, 2–40 characters, no leading/trailing whitespace or control
characters. It searches saved listing ID, display name, item type, rarity and
displayed category case-insensitively. It combines with `category` and survives
category tabs and item detail links. The page shows a result count and an empty
state; an item outside the search returns HTML 404. Invalid or repeated `q`
returns HTML 400 before session lookup. The search and HTML are emulator-defined;
no Live post-selection Shop contract or Redirect client verification exists.

## Local Quest search (menu-209, 2026-10-24)

`GET /indexInternal.es?action=internalQuests` accepts optional `q` (exact lowercase key, once). A nonblank value is trimmed and must be 2–40 ASCII letters, digits, spaces, hyphens or underscores (the Auction search rule); otherwise the page returns HTML 400 (`no-store`) before the session check. Blank `q` shows the full list. The search matches quest ID, name and reward text, case-insensitive, and combines with `filter` and `detail`. Tab counts reflect the matches, tab, detail and Previous/Next/Close links retain `q`, and a detail outside the matches returns 404. Totals, achievements and the claim form are unchanged. Emulator-defined; no Live contract.

## Local Quest detail navigation (menu-66, 2026-10-24)

`GET /indexInternal.es?action=internalQuests` accepts optional `detail={questId}`
alongside the existing `filter=all|ready|in-progress|claimed|unavailable`.
Each quest name opens a detail panel with progress, remaining count, reward,
status, and Previous/Next/Close links scoped to the selected filter. A ready
quest has a signed Claim form; claimed, incomplete, and disabled quests do not.
An empty, repeated, malformed, or wrong-case `detail` returns HTML 400; an
unknown or out-of-filter quest returns 404 without reflecting the submitted ID.
These query and HTML rules are emulator-defined. No Live post-selection Quest
capture or Redirect client verification exists.

## Local Logbook detail navigation (menu-65, 2026-10-24)

Menu-143 (2026-10-24): the local-only `GET /indexInternal.es?action=internalLogbook` also accepts `q` to search event text, category, and local ledger asset, reason, and menu. It combines with `category` and `source`, narrows counts and pages, and stays in list, detail, and Previous/Next links. A detail outside the results returns 404. `q` must be exact-case, once, 2–40 ASCII letters, digits, spaces, hyphens or underscores, without edge spaces; malformed input returns HTML 400 before session lookup. Search is read-only and emulator-defined; no Live or Redirect Logbook request has been captured.

Menu-138 (2026-10-24): the local-only `internalLogbook` GET accepts optional
`source=all|local|demo` (exact lowercase key and value). `all` is the default;
`local` lists actual account ledger entries, and `demo` lists the 32 synthetic
entries without the ledger summary. The selected source survives category tabs,
pagination, entry links, and detail Previous/Next links. An entry outside that
source returns 404. An empty, repeated, or differently cased key returns 400;
an unknown or differently cased value returns 404. No Live endpoint or payload
supports this local filter, and Redirect client behavior is unverified.

`GET /indexInternal.es?action=internalLogbook` now carries the selected
`category=all|combat|economy|missions|account` and `page=N` into entry links.
Detail pages link back to that list page and to adjacent entries in the
selected category, including across page boundaries. An entry outside an
explicit category or page returns HTML 404; a direct detail URL without
`page` infers the entry's page. This is an emulator-defined HTML contract.
There is no Live post-selection Logbook capture or Redirect client verification.

## Local Messaging validation (menu-64, 2026-10-24)

`GET /indexInternal.es?action=internalMessages` requires exact-case query keys
and the canonical action value. Wrong-case keys or values return HTML 400.
`POST /indexInternal.es?action=internalMessageSend` can create the final
nine-digit message ID; another send returns HTML 409 without changing mailbox
state or revision. These rules are emulator-defined. No official post-selection
Messaging contract or Redirect client verification exists.

## Local Hall of Fame pilot context (menu-63, 2026-10-24)

`GET /indexInternal.es?action=internalHallofFame` keeps a pilot's level
consistent between the all-company board, company board, and four-category
profile. The optional `pilot` detail remains directly addressable under any
valid company filter. Its context panel shows the pilot's position on the
selected board and links to the page containing that row, or explains a
different-company filter and links to the pilot's company board. The detail's
Back link preserves the requested category, company, and page. Names, scores,
levels, HTML, and query rules are emulator-defined; there is no captured Live
Hall of Fame contract or Redirect client verification.

## Local Messaging folders (menu-61, 2026-10-24)

`GET /indexInternal.es?action=internalMessages` accepts optional
`folder=all|inbox|sent` (default `all`) and `message={id}`. Tabs display
mailbox counts; message links and Previous/Next/Close navigation stay within
the selected folder. A message belonging to another folder returns HTML 404.
Empty, repeated, unknown, or wrong-case folder values return HTML 400. The
folder query and HTML are emulator-defined; no Live Messaging endpoint or
Redirect client behavior was captured.

## Local Tech Factory recipe filters (menu-60, 2026-10-24)

`GET /indexInternal.es?action=internalTechFactory` accepts optional
`filter=all|affordable|shortfall` (default `all`). Counts and visible recipes
use the account's current component balance; detail links and their
Previous/Next/Close navigation preserve the filter. A selected recipe outside
the filtered set returns HTML 404. An empty, repeated, wrong-case, or unknown
filter, noncanonical query key, or noncanonical `action` value returns HTML 400.
These are emulator-defined HTML query and view rules. Start/Claim forms and
their persisted economy transactions are unchanged. There is no captured Live
Tech Factory page or Redirect client verification.

## Local Tech Factory recipe details (menu-59, 2026-10-24)

`GET /indexInternal.es?action=internalTechFactory` now accepts an optional
`detail={recipeId}` for an enabled recipe. The detail panel shows its component
cost, production time, owned output, component balance after a start, and the
current job status. It offers the existing signed Start form only when the slot
is empty and the component balance covers the cost. The overview counts recipes
the pilot can currently afford. A malformed, repeated, or wrong-case `detail`
returns HTML 400; an unknown or disabled recipe returns HTML 404 after session
and company checks. The query and all presentation values are emulator-defined;
there is no captured Live Tech Factory page or Redirect client verification.

## Local economy foundation (2026-10-23)

The CMS admin screen uses the emulator-only `/api/v1/economy` catalog, grant, and audit routes. Listing reads now expose `restockIntervalSeconds` and `restockAmount` for editing saved rules. The UI builds recipe part arrays and sends the API's serialized `inputsJson`/`outputsJson` fields; listing prices use `pricesJson`. These are local CMS contracts, with no official client capture or Redirect verification. See [economy.md](economy.md).

The emulator-only `/api/v1/economy` administration routes require the Keycloak `emulator-admin` realm role. They validate referenced definitions, reject deletion of in-use items, record the admin subject for manual grants and stock changes, and expose filtered cursor pages of ledger and stock audit records. See [economy.md](economy.md). These routes have no official client contract and were not captured from Live servers.

Economy menu wiring now submits `currency` with Shop buys so one listed currency is debited, and includes the currency in successful purchase idempotency matching. A missing value uses Credits for older local forms. Shop displays per-account purchase limits. `GET /indexInternal.es?action=internalAssembly` reads enabled recipes from Postgres; `POST ...?action=internalAssemblyCraft` consumes resource, currency, and item inputs and grants outputs in one transaction. `GET /indexInternal.es?action=internalTechFactory` reads timed database recipes; production claims credit the resource wallet. Auction GET/bid/claim uses persistent personal lots and transactional Credits holds and item grants. Payment, Voucher, Quest, and battle pass menus read persistent reward offers and grant through stable ledger keys; expedition and Space Search item rewards use durable grant keys. The authenticated shell and economy pages show all currency and resource balances. Admin `/api/v1/economy/auction-lots` and `/reward-offers` list and edit emulator definitions. All paths and payloads in this paragraph are emulator-defined, without an official post-selection capture.

`GET /indexInternal.es?action=internalShop` now renders Postgres listings, wallet balances, owned inventory, item details, rarity and stock notices, and a per-form purchase key signed to the local session and listing. Missing/expired session returns HTML 401, missing company 403, empty or duplicate filters 400, and unknown or mismatched item/category filters 404. `POST /indexInternal.es?action=internalShopBuy` requires exactly `item`, `quantity` (1–100), `currency`, and `idempotencyKey`; missing, duplicate, extra, or tampered fields return HTML 400, missing/expired session 401, wrong company 403, unknown listing 404, and sold-out/limited or replayed purchases 409. It executes a Postgres transaction for currency debit, shared Hangar item grant, optional finite stock decrement, per-account limit, purchase key, ledger, and stock audit. The three additional LF-2, SG3N-A03, and Iris offers and their prices, Iris restock, and notices are emulator-defined. Admin-only `/api/v1/economy` routes manage assets, items, recipes, listings, stock, and balances; see [economy.md](economy.md). These are emulator-defined routes and payloads. No Live or Redirect capture was performed because the mitmproxy CA prerequisite is prohibited; official Shop request shapes remain unknown.

The local `internalGalaxyGateSpin` action lazily imports starter energy and legacy gate progress, then persists gate parts and the `galaxy_energy` debit in one account-locked Postgres transaction. Activation is durable too. This is emulator behavior, without a captured post-selection request contract.

## Local authenticated shell logout (2026-10-23)

The synthetic `GET /indexInternal.es?action=internalWebGL` page now requires a
live `dosid` session; an absent or expired session returns 401. Its navigation
includes `GET /indexInternal.es?action=externalLogout`, which removes the local
session from distributed cache, expires the `dosid` cookie, and follows the
existing two-step redirect through `internalWebGL&from=externalLogout` to
`/index.es?action=externalHome&loginError=94`. A leftover redirect-token record
cannot recover the invalidated session. The logout action and redirect chain
already existed locally; cache invalidation, cookie expiration, and the shell
link are emulator behavior. No new Live capture or Redirect client verification
was run because the mitmproxy CA prerequisite is prohibited.

Menu-39 (2026-10-24) hardening, emulator-defined: logout always succeeds,
ignores and never reflects its query, and accepts any casing of the action. A
missing, unknown, stale or whitespace cookie still receives the expired `dosid`
cookie (the only `Set-Cookie`) and the 302. Cookies longer than 128 characters
are not looked up. Both steps send `Cache-Control: no-store`. Logout ends only
the cookie's session; redirect-token rotation and company selection cannot
restore it. Only the logout action and `internalWebGL&from=externalLogout` skip
the fresh `dosid` cookie; `from=externalLogout` on any other action no longer
does.

## Local command center visuals (2026-10-23)

`GET /indexInternal.es?action=internalCommandCenter` now renders image elements
for the 19 displayed notice backgrounds. Its navigation includes all five IDs
from the captured categories XML: `events`, `deals`, `news`, `sales`, and
`upselling`; the last two currently have no displayed notices. The saved
manifest maps each background filename to `/do_img/new/bg/{name}.jpg`.
Redirect mode now routes GET image paths under `/do_img/new/bg/` and
`/do_img/new/overlay/` on `darkorbit-22.bpsecure.com` to the local gateway.
The `/do_img/` route checks paths against the captured 609-file manifest and
returns distinct deterministic 303×168 PNG artwork for listed JPG/PNG entries,
with `image/png` and one-hour caching. Other image paths keep the existing
generic fallback. `GET /resources/command-center/categories.xml` continues
to serve the captured public XML snapshot. The manifest and category fields
are Live/public evidence; all generated artwork is emulator data. Original
image bytes, dimensions, and post-selection client requests remain unknown.
No new Live capture was run because installing the mitmproxy CA is prohibited.

Menu-40 (2026-10-24): manifest JPG/PNG artwork under `/do_img/new/` and
`GET /resources/command-center/categories.xml` (plus the `/xml/Categories.xml`
alias) send `ETag: "{manifest hash}-{version}"` and
`Cache-Control: public, max-age=3600`. A matching `If-None-Match` returns 304
without a body. Artwork is generated from the canonical manifest path, so path
casing does not change the bytes. The validator format is emulator-defined;
no official CDN `ETag` or `Last-Modified` header was captured.

## Local Unity startup and hangar images (2026-10-23)

`GET /unityApi/login.php` keeps the captured outer `data` JSON string and
the nested `UnityLoginData` and nine-field hangar record. Its `lang` value now
comes from the account-scoped saved local Account preference (`es`, `en`, `de`,
`fr`, `it`, `pl`, `tr`, or `pt`; default `es`). The login builder uses the same
allowlist as Account settings and falls back to `es` for an unsupported value.
The active hangar still comes from `LocalHangarStore`.
The two locally advertised relative image paths,
`/do_img/global/items/ship/pusat-plus_100x100.png` and
`/do_img/global/items/ship/phoenix_100x100.png`, now serve distinct 100×100
emulator PNG artwork. The captured Pusat URL's `__cv` query is accepted by
the same path. Other `/do_img/` paths retain the generic placeholder. Since menu-98 the local route sends `Cache-Control: no-store` on every response and rejects a missing, unknown, logged-out or over-128-character `dosid` with 401 and no cookie (emulator-defined; not observed Live).

The saved Live login response established the first image URL, field names,
and JSON types, but its account had 33 hangars. The second ship, local artwork,
and preference propagation are emulator contracts; no official image bytes or
post-selection menu behavior were captured. Redirect client use of these
changes remains unverified. No Live capture was run because the mitmproxy CA
prerequisite is prohibited. `dotnet build --no-restore` passed with 12 existing
MessagePack advisories; all 161 non-integration tests passed.

## Forum, Help, and Support validation (2026-10-24)

`internalForum`, `internalHelp`, `internalSupport`, and
`/GameAPI.php?action=portal.redirectToBoard` share `LocalLinksRequest`
validation and return a small HTML error page (`id="localLinksError"`) with
`Cache-Control: no-store`:

- **401:** missing or expired `dosid` session (links back to sign-in).
- **403:** the session has a company that is not in the catalog. A session
  without a company yet can still read these pages.
- **400:** a parameter other than `action`, `from`, `prc`, `lang`, or the
  page's `topic`/`section`; any parameter repeated; any value over 64
  characters; or an id that is not 1-32 lowercase letters, digits, or hyphens.
- **404:** a well-formed but unknown `topic`/`section`, or (since 2026-10-24)
  any `/GameAPI.php` action other than `portal.redirectToBoard`, which used to
  return an empty 404.

Since 2026-10-24 error pages no longer repeat the rejected parameter name or
id. Forum and Help have 12 topics (`company`, `hangar`, `shop`, `assembly`,
`skylab`, `tech-factory`, `gates`, `events`, `quests`, `auction`, `clan`,
`account`), and the index shows the topic count. Each topic page links to the
local menu it describes (`id="openMenu"`) and to the previous and next topics
on the same board.

An empty `topic=`/`section=` shows the index. The Forum index now shows three
emulator-defined board notices (`local-only`, `redirect-mode`,
`practice-economy`). Support adds `payment` (links to local Payment and
Vouchers) and `security` (links to local log out) sections. These pages are
read-only and share no state with other menus. All of this text and every
status code are emulator-defined; no Live Forum, Help, or Support body was
captured.

## Local Support menu (2026-10-23)

`GET /indexInternal.es?action=internalSupport` now opens a local guidance hub
from the authenticated menu and the existing `/unityApi/links.php` support URL.
Optional `section` values `getting-started`, `account`, `connection`,
`community`, `payment`, `security`, `legal`, and `client-links` show details; unknown sections return 404. A valid
`dosid` session is required (401 otherwise). The hub links to local Help,
Forum, and Account pages and to the captured external Bigpoint imprint. Since
menu-103 `section=connection` also lists session-expiry, company, and
starter-map/game-server checks (`#supportConnectionChecks`, server address from
the `/xml/maps.xml` catalogue); no route or parameter changed. It
does not submit a support ticket or add a social or credits URL: the saved Live
links response has an empty `credits` value and no social fields. The sections
and HTML are local emulator contracts. No official Support destination body or
post-selection client navigation was captured, and Redirect client use is
unverified. No new Live capture was run because the mitmproxy CA prerequisite
is prohibited. `dotnet build --no-restore` passed with 12 existing MessagePack
advisories; all 160 non-integration tests passed.

## Company choice preview (menu-48, 2026-10-24)

`GET /indexInternal.es?action=internalCompanyChoose` accepts one optional, read-only
`preview` parameter with a lowercase catalog code (`mmo`, `eic`, `vru`, `tst`). Each
card links to its preview. The preview marks the card with `aria-current` and adds a
`#companyPreview` panel: code, faction number, starter map, the game server address from
the `/xml/maps.php` catalogue, other companies sharing the starter map, an emulator-written
summary, and a `#previewConfirm` button that submits the existing
`internalCompanyConfirm` form. Previewing persists nothing. Errors are HTML with
`no-store`: 400 for an empty, repeated, or control-character value or a wrong-case key,
404 for an unknown or wrong-case code (not echoed), and 400 for `preview` on confirm.
A pilot who already chose is still redirected to `internalWebGL`. The parameter, panel
and text are emulator-defined; the Live choice page showed only the three confirm
controls, and Redirect client use is unverified.

## Company choice validation (2026-10-24)

`internalCompanyChoose` and the local-only `internalCompanyConfirm` now accept only
`action`, `from`, `prc`, and `lang`, plus one `company` on confirm. Each may appear
once, at most 64 characters long. Errors are HTML pages with `Cache-Control: no-store`:
400 for an unknown, repeated, oversized, or invalid parameter (including a wrong-case or
unknown company), 401 for a missing or expired session, and 409 for a company that differs
from the one already stored. Within one process, confirmations are serialized per account.
`IdentityCommandService.SetCompanyAsync` keeps the first valid company, so a stale
second session cannot switch it. Login hangars always report exactly one active slot.
The routes are unchanged. The validation rules and page labels are emulator-defined.

## Local company choice completion (2026-10-23)

`GET /indexInternal.es?action=internalCompanyChoose` now requires a live `dosid`
session (401 otherwise). Before a choice it renders the local four-company form;
after a choice it redirects to `internalWebGL`. The local-only
`GET /indexInternal.es?action=internalCompanyConfirm&company=…` requires the
session, accepts only catalog company codes (400 otherwise), persists the first
choice in identity and session state, and redirects to `internalWebGL`. Retrying
the same choice is idempotent; attempting a different company returns 409 and
does not change the session. Fresh login sessions inherit the account choice,
which controls the Unity startup faction and starter map. The fourth TST company
and all confirmation behavior are emulator contracts. Saved Live evidence shows
only the pre-selection HTML, not the official confirmation request; no new Live
capture was run because the CA prerequisite is prohibited. Redirect client use
after this change is unverified. `dotnet build --no-restore` passed with 12
existing MessagePack advisories, and all 157 non-integration tests passed.

## Local Forum and Help (2026-10-22)

`GET /indexInternal.es?action=internalForum` and `internalHelp` now show a
session-gated local topic index. Optional `topic` values `company`, `hangar`,
`shop`, `gates`, and `events` open detail guidance; an unknown topic returns
404. `GET /GameAPI.php?action=portal.redirectToBoard` uses the same local
Forum content and requires a `dosid` session. The `/GameAPI.php` action came
from the saved Live `/unityApi/links.php` response; the local Forum/Help routes
come from emulator `unityApi/login.php` links. Topic IDs, text, HTML, and
navigation are emulator contracts, not captured official responses. Posting
is not implemented. No new Live capture was run because the mitmproxy CA
prerequisite is prohibited; Redirect client use remains unverified.

### GameAPI forum link hardening (menu-104, 2026-10-24)

`GET /GameAPI.php` handling moved to `GameApiForumAction`. Only a single,
exactly cased `action=portal.redirectToBoard` is served; a missing, empty,
repeated, differently cased (`PORTAL.REDIRECTTOBOARD`, `ACTION=`) or other
action returns the HTML 404 before any session lookup (it was previously
matched case-insensitively). A `dosid` cookie over 128 characters returns 401
without a lookup, as on `login.php` and logout. Everything else (topic,
category, navigation keys, 401/403/400/404 pages, `no-store`) is the shared
`internalForum` validation. Tests: `GameApiForumActionTests` (30 cases). These
rules are emulator choices; no Live response to this link was captured.

### Forum and Help category filter (menu-44, 2026-10-24)

`internalForum`, `internalHelp`, and `GameAPI.php?action=portal.redirectToBoard`
accept an optional `category` (emulator-defined ids: `getting-started`,
`hangar`, `shop`, `production`, `activities`, `economy`, `community`,
`account`). The index shows `All (12)` plus one tab per category with its topic
count; a filtered list shows `N topics in {Category}` and hides the Forum board
notices. A detail page links `More in {Category}`. A malformed category returns
400, an unknown category 404, and a `topic` outside the given `category` 404.
`internalSupport` still rejects `category` as an unknown parameter. For all
three pages, parameter keys now match exactly (`ACTION=`, `Topic=` return 400)
and values with control characters return 400 without echoing them. Tests:
`LocalForumHelpCategoryTests`. No capture was run; client use is unverified.

### Forum and Help topic search (menu-111, 2026-10-24)

`internalForum`, `internalHelp`, and `GameAPI.php?action=portal.redirectToBoard`
accept an optional `q`. The search is case-insensitive and matches a substring
of a topic's title, category, answer, or menu name. It can be combined with
`category`. The text is trimmed, and an empty or blank `q` (an empty search
box) shows the normal index. Otherwise the text must be 2-40 characters, or the
page returns 400. Combining `q` with `topic` also returns 400. The `q` key must
match exactly, appear once, and contain no control characters. Error pages never
repeat the search text. The index and category pages have a GET search form
(`#topicSearch`) that keeps the current category. Results show
`N topics match "{q}"` (HTML-encoded), plus ` in {Category}` when a category is
set, and a `Clear search` link. With no match, the page shows an empty state.
Forum board notices are hidden during a search. `internalSupport` rejects `q`.
All of these rules are emulator choices. Tests: `LocalForumHelpSearchTests`.

## Local battle pass and expedition (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalEvents`
shows a local battle pass and expedition. `POST
/indexInternal.es?action=internalBattlePassClaim` accepts form field `tier`
(`supplier` or `engineer`). The first claim requires one successful local Shop
purchase and grants 200 local credits; the second requires one Assembly craft
and grants 300. Each can be claimed once. `POST
/indexInternal.es?action=internalExpeditionStart` starts a one-time two-minute
job and spends 100 local credits; `POST
/indexInternal.es?action=internalExpeditionClaim` grants one SG3N-B02 shield
to the shared Hangar inventory after the timer. Without Postgres, three more
emulator-defined tiers exist: `quartermaster` (3 purchases, 400 credits),
`artisan` (3 crafts, 600), and `tycoon` (10 purchases, 1000). The expedition
forms carry exactly `start=1` or `claim=1`; the battle pass form carries exactly
one `tier` of 1-64 ASCII letters, digits, `_`, or `-`. Status codes
(2026-10-24): 405 for non-POST; 401 for a missing or expired `dosid`; 403 for
a missing or invalid company; 400 for extra or repeated query parameters,
non-form bodies, unreadable forms, and missing, repeated, unexpected, or
malformed fields; 404 for an unknown tier; 409 for replays, double starts,
unreached tiers, a timer that is still running, disabled offers, too few
credits, or a full inventory stack. Each error is a small HTML page linking back
to Events. Success redirects with 303 to Events (since Menu-129 with `&claimed={tier}` or `&expedition=started|claimed`). Account state persists
in the shared equipment cache with a 30-day sliding expiry. No world entry or
real payments occur.

Hardening (2026-10-24, run menu-16): unreadable bodies, including a cut-off
multipart form, return 400 instead of an unhandled exception. A repeated or
wrong-case `action` names no Events action and returns 404. `GET internalEvents`
is `no-store`, accepts only `action`, `from`, `prc`, and `lang` (each once, at
most 64 characters, no control characters) and otherwise returns an HTML 400;
a missing session returns an HTML 401 instead of an empty one. The page shows
how many tiers are claimed, how many more purchases/crafts each unfinished tier
needs, and the expedition's minutes left.

The local HTML paths, tier rules, costs, and rewards are emulator contracts.
IL2CPP metadata establishes only `/xml/events/battlepass/` and
`/xml/events/expedition/` prefixes, the exact
`/xml/events/expedition/expedition-boxes.xml` literal, and candidate
`ExpeditionBoxRewardData` fields `RateId` and `Rewards`. No Live post-selection
request or wire payload establishes a battle pass or expedition schema. The
exact XML path now serves a provisional local shield reward; no other XML filename
was invented. Redirect client use is unverified. No Live capture was run
because the mitmproxy CA prerequisite is prohibited. The solution builds with
0 errors and 12 existing MessagePack advisory warnings; all 152
non-integration tests pass.

## Local Space Search (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalSpaceSearch` shows
five emulator planets: Mars, Venus, Earth, Jupiter, and Saturn, with owned reward
quantity and search progress. `POST
/indexInternal.es?action=internalSpaceSearchExplore` accepts exactly one form field
`planet` (`mars`, `venus`, `earth`, `jupiter`, or `saturn`; 1-32 lowercase letters)
and no query key other than `action`. Each successful search spends 100 local
credits and awards one fixed item to the shared Hangar inventory: LF-3 laser,
SG3N-B02 shield, Flax drone, LF-2 laser, or SG3N-A03 shield, respectively. A
planet can be searched once per account (durable `space-search:{planet}` grant key
in Postgres). The POST returns 303 back to Space Search on success (since Menu-128 with `&explored={planet}`); failures return
an HTML error page with `Cache-Control: no-store`: 405 for non-POST, 401 without a
session, 403 without a valid company, 400 for extra/repeated fields, non-form bodies,
or malformed planets, 404 for unknown planets, and 409 for a repeated search,
insufficient credits, a full stack, or a refused Postgres grant. (2026-10-24)

Hardening (2026-10-24, later): the explore query must be empty or exactly one
`action=internalSpaceSearchExplore` in that exact casing; other casing, a repeated
action, or extra keys return 400 before the session is checked. Unreadable forms
(cut-off multipart, I/O or bad-request errors while reading) return 400. The page
`GET internalSpaceSearch` is sent `no-store`, accepts only `action`, `from`, `prc`,
and `lang` (each once, at most 64 characters, no control characters; otherwise an
HTML 400), and answers a missing session with an HTML 401 page. No endpoint was
added or removed. These rules are emulator-defined.

Menu-56 (2026-10-24) adds two optional page parameters to the same route.
`filter` is `all` (default; empty also means `all`), `available` (not yet
searched) or `searched`, exact lowercase; any other value is HTML 400.
`planet` must be 1–32 lowercase letters (otherwise 400) and name a catalog
planet (otherwise HTML 404 `no-store`); it opens a detail panel. No endpoint was
added or removed, and the explore POST is unchanged.

The existing candidate `GET /unityApi/events/spaceSearch.php` now returns
provisional `planets`, `events`, and `rewards` string arrays from the local
catalog. `GET /unityApi/events/ssRewards.php` returns one provisional reward
object referring to that catalog. IL2CPP metadata establishes the endpoint
strings and `SpaceSearchDate`/`SpaceSearchRewardsDate` field names, but not
their wire types, values, or association with this local HTML action. No
post-selection Live capture exists, and Redirect client use is unverified.
No Live capture was run because the mitmproxy CA prerequisite is prohibited.

Menu-110 (2026-10-24): both candidates now read the signed-in account's
Space Search state from the `dosid` cookie. With a live session and valid
company, `spaceSearch.php` lists only unsearched planets and their rewards,
and `events` becomes empty once all five are searched. `ssRewards.php` takes
an optional `planet` (1–32 lowercase letters, else JSON 400; unknown planet
JSON 404) and otherwise describes the next unsearched planet. Without a
usable session both return the full catalog, as before. A repeated key, an
empty or overlong (>64) key or value, or a control character returns JSON 400
`{"error","status"}`. Responses are `application/json`, `no-store`, and
read-only. Methods, query names, and semantics are emulator-defined; no
endpoint was added or removed.
The solution builds with 0 errors and the 12 existing MessagePack advisory
warnings; all 149 non-integration tests pass.

## Local Vouchers (2026-10-22)

Menu-196 (2026-10-24): `GET /indexInternal.es?action=internalVouchers` requires exact lowercase query keys (`action`, `from`, `prc`, `lang`, `filter`, `voucher`, `redeemed`, `q`) and the exact action value `internalVouchers`. Non-canonical spellings and control characters return no-store HTML 400 before session lookup or company selection; canonical views and `internalVoucherRedeem` POST are unchanged. This is an emulator-defined contract, unverified in Redirect.

After company selection, `GET /indexInternal.es?action=internalVouchers`
shows two emulator-only practice codes, `TRAINING-500` and `HANGAR-750`.
`POST /indexInternal.es?action=internalVoucherRedeem` accepts form field
exactly one `code` (1-32 letters, digits, or hyphens; emulator-defined).
It returns 401 without a session, 403 without a valid company, 400 for
malformed or extra fields or query parameters, 404 for an unknown code, 409
for an already used code (replay or double submit), 410 for a disabled offer,
and 303 back to Vouchers on success. Error responses are HTML pages. Each code
credits the account's shared Shop/Hangar balance once; redeemed codes persist
in distributed cache with a 30-day sliding expiry. This uses no real voucher
service or payment. The local page links from the synthetic authenticated
shell. `JavascriptHelper.PostVoucherUrl(value,code)` and voucher response
types in IL2CPP metadata indicate a client flow, but do not establish its URL,
verb, or payload. No official post-selection voucher capture exists, and
Redirect client use is unverified. The solution builds with 12 existing
MessagePack advisory warnings and all 147 non-integration tests pass.

Hardening (2026-10-24, no endpoint change): `internalVoucherRedeem` returns
405 (`Allow: POST`) for non-POST requests before the session check, 400 for
a query other than none or exactly `action=internalVoucherRedeem` (exact
case), and 400 when the form cannot be read. The `internalVouchers` page is
`no-store`, accepts only `action`, `from`, `prc`, and `lang` (each once, at
most 64 characters), and returns HTML 400/401 error pages. These rules are
emulator-defined.

Menu-46 (2026-10-24, page parameter added): the `internalVouchers` page also
accepts one optional `filter` of `all`, `available`, `redeemed`, or
`unavailable` (exact case, emulator-defined). A different or differently
cased value returns HTML 404 "Unknown voucher filter." after the session and
company checks, without repeating the value; a repeated `filter` or a control
character returns 400 like the other page parameters. Each code is listed
with `data-voucher` and `data-status` markers, the admin offer name when it
differs from the code, and a one-click redeem form (hidden `code`, same
`internalVoucherRedeem` POST) only while available. Disabled offers and
grants outside 1..1,000,000 show as unavailable, matching the 410 rule. The
page totals granted and still-available rewards per asset, names the last
redeemed code, shows filter tabs with counts, and links to the Logbook
account category. The redeem POST, its 303 target, and all status codes are
unchanged. See `LocalVoucherPageTests`.

Menu-67 (2026-10-24, page parameter added): the `internalVouchers` page also
accepts one optional `voucher={id}` detail parameter (emulator-defined; the
page still rejects `code`, which belongs to the redeem POST form). A value that
is not 1-32 ASCII letters, digits, or hyphens returns HTML 400 "Invalid voucher
code." after the session and company checks. An id that does not exactly match
(ordinal case) a code in the selected `filter` returns HTML 404 "Unknown voucher
code for this list.". The detail page (`main#voucherDetail` with
`data-voucher`, `data-status`, `data-filter`) shows the admin offer name,
reward and asset, a status explanation, the local Credits balance for credit
rewards, the position within the filter, a redeem form only while available,
and Previous/Next/Close links that keep the filter. Each list row links to its
detail with `data-detail`. The redeem POST and its 303 target are unchanged.
See `LocalVoucherDetailTests`.

## Local Payment practice credits (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalPayment`
shows five synthetic, one-time packages: `starter` (1,000), `hangar`
(3,000), `fleet` (5,000), `squadron` (10,000), and `armada` (25,000) local
credits (names and amounts are emulator-defined; with Postgres the
`payment` reward offers replace this list), plus a claimed-packages summary.
The captured news link target
`GET /?action=internalPaymentProxy` opens the same package list after session
and company checks; opaque `req` and `v` values are ignored. `POST
/indexInternal.es?action=internalPaymentClaim` accepts exactly one form field
`package` (1-32 ASCII letters, digits, `-`, `_`). Errors are uncached HTML
pages: 405 with `Allow: POST` for other methods; 400 for any query other than
exactly one `action=internalPaymentClaim` (exact case), a non-form or
unreadable body, missing, repeated, or extra
fields (for example `quantity` or `amount`), or a malformed ID; 401 without
a valid session; 403 without a valid company; 404 for an unknown package
(IDs are case-sensitive); 409 for a replayed, double-submitted, or concurrent
claim, or a grant that would exceed the local credit limit; 410 for a
disabled offer or an amount outside 1..1,000,000. Success returns 303 back
to Payment with `claimed={packageId}` (Menu-120). The uncached `GET internalPayment` page accepts only `action`,
`from`, `prc`, and `lang` (once each, at most 64 characters); other query
parameters return 400. Grants share the account-scoped Shop/Hangar credit state in
distributed cache with a 30-day sliding expiry and never change inventory.
No prices, payment provider, checkout request, or real currency are involved.

The saved Live capture establishes only the pre-company Payment redirect and
the proxy link target, not a post-selection page, package, or claim contract.
This is emulator-only behavior, and Redirect client use is unverified. No new
Live capture was run because the mitmproxy CA prerequisite is prohibited.
The solution builds with 0 errors and 12 existing MessagePack advisory
warnings; all 146 non-integration tests pass.

## Local Command center (2026-10-22)

After company selection, the local shell links to `GET /indexInternal.es?action=internalCommandCenter`.
Optional `category` selects `events`, `deals`, or `news`; optional `item` selects one
of the 19 saved notice IDs. Unknown values return 404. The route requires an
existing `dosid` session (401 if missing) and redirects to company selection
before a company is chosen. Its HTML shows the captured notice ID, priority,
category, countdown snapshot, and background filename from the same catalog
used by local `/flashAPI/brNews.php` and `/unityApi/news.php`. The page does not
follow payment or external actions. This HTML route is emulator-only: the
saved Live capture established the XML/JSON data shapes, not a post-selection
Command center page or click contract. Redirect client use remains unverified;
no new Live capture was run because the mitmproxy CA prerequisite is prohibited.

Update 2026-10-24: the query now accepts only `action`, `category`, `item`,
`from`, `prc` and `lang`, each at most once and at most 64 characters, with
`category`/`item` limited to ASCII letters, digits, `_` and `-`. Unknown,
repeated, overlong or malformed parameters return 400. An unknown category or
notice, or a notice outside the selected category, returns 404. A missing or
expired session returns 401. Every error now has an HTML body; the 401 page
links to sign-in. The route and its success responses are otherwise unchanged.

## Local Account settings (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalAccount`
shows the session pilot name, game user ID, company, and emulator preferences.
`POST /indexInternal.es?action=internalAccountSave` accepts form fields
`language` (`es`, `en`, `de`, `fr`, `it`, `pl`, `tr`, or `pt`; emulator-defined),
`sound` (`true` or `false`), `notifications` (`true` or `false`), and the page's
per-account revision `rev`. A valid save returns 303 to Account. A missing session
returns 401, a missing or invalid company 403, invalid or extra values 400, and a
stale or replayed `rev` 409. Error responses are HTML pages.
Hardening (2026-10-24): the save action returns 405 with `Allow: POST` for other
methods before the session check. Its query must be empty or exactly one
`action=internalAccountSave` in that casing; anything else is 400. A form that
cannot be read (for example a cut-off multipart body) returns HTML 400 and saves
nothing. Error messages are HTML-encoded and never echo submitted values. The page
is `no-store`, accepts only `action`, `from`, `prc` and `lang` once each (at most
64 characters, no control characters; otherwise HTML 400), and a missing session
gets an HTML 401 page instead of an empty 401. These rules are emulator choices
matched to Vouchers and Payment.
Preferences are account-scoped in distributed cache with a 30-day sliding
expiry. They do not modify Unity client configuration or real account data.

The Account page and save contract are emulator-only. Saved Live sessions
establish the account-center link in `/unityApi/links.php`, but no
post-selection Settings page, request, or write schema. The local page uses
identity already present in the authenticated session. `/unityApi/upload.php`
has no proven Settings association. Redirect client use remains unverified;
no new Live capture was run because the mitmproxy CA prerequisite is prohibited.

## Local Quests and achievements (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalQuests`
shows five local goals read from Postgres `economy.reward_offers` (source
`quest`): `first_purchase` (1 purchase, 250 Credits), `field_supplier`
(3 purchases, 500), `first_craft` (1 craft, 400), and, since migration
`018_quest_offers`, `bulk_buyer` (10 purchases, 1,000) and `master_engineer`
(5 crafts, 1,500). The page shows clamped progress, a claimed/ready notice,
disabled offers as Unavailable, unfinished goals as "In progress (N more
needed)", and achievements. The page is `no-store`. Without a session it
returns HTML 401. Since 2026-10-24 it accepts only `action`, `from`, `prc`, and
`lang`, each once, at most 64 characters and without control characters; any
other, repeated, or overlong field returns HTML 400.

`POST /indexInternal.es?action=internalQuestClaim` requires exactly the form
fields `quest` and `key`. Non-POST requests return 405. The query must be
exactly one `action=internalQuestClaim` in that case; other casing, a repeated
action, or extra fields such as `from` return 400. A non-form body returns 400,
and so does a form that cannot be read, such as a truncated multipart body. A
key exactly one hour old still claims. The key is `nonce.issuedUnixSeconds.hmac`, signed
with the session dosid over the quest id, and is valid for one hour
(emulator-defined). Errors are HTML pages: missing/expired session 401;
missing or invalid company 403; extra query fields, missing, repeated, extra,
oversized, malformed, tampered, foreign-session, or future-dated fields 400;
an incomplete quest 400; an unknown quest 404; an expired form, a disabled
offer, or an already claimed reward (replay/double-submit/concurrent) 409.
Success grants the offer asset through ledger key `quest:{id}` and returns 303
to Quests. Hangar inventory is unchanged. No world entry or real payment
occurs.

These paths, goals, rewards, and achievements are emulator contracts. Saved
Live sessions contain no post-selection quest request or response, and the
existing IL2CPP network inventory establishes no quest HTTP schema. Redirect
client behavior remains unverified. No new Live capture was run because the
mitmproxy CA prerequisite is prohibited.

Since menu-47 (2026-10-24) the page also accepts one optional `filter`
of `all` (default), `ready`, `in-progress`, `claimed`, or `unavailable`
(case-exact). An unknown value returns HTML 404 "Unknown quest filter." after
the session, query, and company checks; a repeated `filter` or one with a
control character still returns 400. The filter changes only the listed rows:
the claimed/ready notice, the achievements list, and the new `#questTotals`
line (purchases, crafts, Credits claimed, Credits ready; non-Credit rewards
are not summed) always cover every quest. Each row carries `data-quest` and
`data-status` markers and a whole-percent completion bar (a zero-target
offer counts as 100%). Filter names are emulator-defined. The claim POST is
unchanged.

## Local Assembly crafting (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalAssembly`
renders three local recipes for LF-3 laser, SG3N-B02 shield, and Flax drone.
`POST /indexInternal.es?action=internalAssemblyCraft` accepts form field
`recipe` (`lf3`, `shield`, or `flax`). Each successful craft immediately spends
local components (150, 100, or 200) and credits (500, 400, or 600), then adds
one item to the shared Hangar inventory. Each account starts with 1,000 local
components; the existing local Shop/Hangar balance supplies credits. State is
account-scoped in distributed cache with a 30-day sliding expiry. No world
entry or real payment occurs.

Update (2026-10-24): the routed action reads Postgres recipes and requires
exactly `recipe` and a signed, session- and recipe-bound `key` rendered by the
page (one-hour emulator-defined lifetime). It returns 303 back to Assembly on
success; HTML 405 for non-POST; 401 without a session; 403 without a valid
company; 400 for extra query fields, non-form bodies, missing/repeated/extra
form fields, and malformed, tampered, foreign or future-dated keys; 404 for an
unknown recipe; 409 for an expired form, disabled recipe, too few inputs, or a
replayed key. Key nonces persist in `economy.craft_requests` (migration
`019_craft_requests`), so each form crafts at most once. The GET page returns
HTML 401/400 for a missing session or extra query fields.

These paths, recipe names, costs, and immediate crafting rule are emulator
contracts. Saved Live sessions contain no post-selection Assembly request or
response. The existing IL2CPP network inventory establishes no Assembly HTTP
schema. Redirect client behavior remains unverified. No new Live capture was
run because the mitmproxy CA prerequisite is prohibited.

## Local Auction (2026-10-22)

The local shell links to `GET /indexInternal.es?action=internalAuction` after
company selection. The page lists LF-3 laser, SG3N-B02 shield, and Flax drone
lots from the emulator's Shop catalog. `POST
/indexInternal.es?action=internalAuctionBid` accepts form fields `item` and
`amount` (integer local credits). The opening bid varies by item; a later bid
must increase the account's own bid by at least 10%. The first accepted bid
starts a five-minute timer. Only the increase is debited from the shared
Shop/Hangar local credit balance. `POST
/indexInternal.es?action=internalAuctionClaim` accepts `item` after the timer
ends and adds one item to the shared Hangar inventory exactly once. Auction
state is account-scoped in distributed cache with a 30-day sliding expiry.
POSTs return 303 back to Auction on success (since Menu-124 with `&placed={lot}` or `&claimed={lot}`). Since 2026-10-24, failures return
an HTML error page with `Cache-Control: no-store`: 405 for non-POST; 400 for
extra or repeated query keys, non-form bodies, missing/repeated/extra form
fields (bid: exactly `item` and `amount`; claim: exactly `item`), a lot id
outside 1–64 `[a-z0-9_-]`, or a bid that is not a plain 1–999,999,999
integer; 401 without a session; 403 without a valid company; 404 for an
unknown lot; 409 for a low bid, too few Credits, an ended, claimed or disabled
lot, a claim without a bid or before the timer ends, or a full inventory
stack. GET redirects to company selection when a session has no company. No
world entry occurs.

Since the 2026-10-24 hardening pass, an unreadable bid or claim form (cut-off
multipart body, I/O failure, or bad request while reading) returns the HTML
400 page instead of a 500, and nothing is debited or granted. The action must
be spelled exactly `internalAuctionBid` or `internalAuctionClaim`; other
casing or an empty query returns 404 before the session check. The Auction
page is sent with `Cache-Control: no-store`, accepts only `action`, `from`,
`prc`, and `lang` (each once, at most 64 characters, no control characters;
otherwise an HTML 400 page), and answers a missing session with an HTML 401
page instead of an empty 401. No endpoints were added or removed.

This is a synthetic personal auction and has no other bidders or real
payments. The saved Live captures and IL2CPP inventory do not establish an
official Auction route, request, response, or page layout. Redirect client
use remains unverified. No new Live capture was run because the mitmproxy CA
prerequisite is prohibited.

## Local Pilot Sheet and skill tree (2026-10-22)

The local shell links to `GET /indexInternal.es?action=internalPilotSheet`
after company selection. The page shows pilot name, game user ID, company,
and six emulator-defined skills: `hull`, `shields`, and `lasers` (rank 0-5),
plus `evasion`, `shieldEngineering`, and `rocketFusion` (rank 0-3, each
unlocked by rank 3 in hull, shields, or lasers). All skills share ten local
starter points. `POST /indexInternal.es?action=internalPilotSkillAllocate`
requires exactly the form fields `skill`, `rev`, and `key`. `POST
/indexInternal.es?action=internalPilotSkillReset` requires exactly `rev` and
`key` and returns all allocated points. `rev` is the state revision shown on
the page. `key` is a nonce, issue time, and HMAC bound to the session, action,
skill, and revision. Training and resets are free and change no Credits,
Uridium, or Hangar inventory.

Skill ranks are account-scoped in distributed cache with a 30-day sliding
expiry. When the entry expires, all points become available again. Corrupt or
out-of-range cached ranks are clamped, unknown skills are dropped, and an
over-budget state resets to zero. Mutations run under a per-process lock, so
concurrent requests in one process cannot exceed the point budget. Used keys
are stored for the form lifetime plus ten minutes.

Status codes: 401 HTML for a missing or expired session; 403 for a missing or
unknown company. 400 covers a non-form body, extra query parameters, and
missing, repeated, extra, or oversized fields. It also covers negative or
non-numeric `rev`, `rev` above 1,000,000, and keys that are tampered,
signed for another session, action, skill, or revision, or future-dated.
404 means an unknown skill. 409 means a replayed key, a stale revision (for
example after another tab changed skills), a form older than one hour, or a
change that is no longer allowed. Success returns 303 to Pilot Sheet (since Menu-127 with `&trained={skill}` or `&reset=1`). GET
redirects to company selection when a session has no company. No world entry
occurs.

Hardening (2026-10-24): a cut-off or malformed multipart body on either action
returns HTML 400 ("could not be read") instead of a 500, and no ranks change.
Non-POST requests that reach the handler return 405 with `Allow: POST` before
the session is checked. The GET page sends `Cache-Control: no-store`. It accepts
only `action`, `from`, `prc` and `lang`, each once, at most 64 characters, with
no control characters (emulator-defined, as on Events). Anything else returns
HTML 400. A missing session now gets an HTML 401 page instead of an empty 401.

These routes, skills, points, and page layout are local emulator contracts.
The saved Live sessions establish no post-selection profile or skill request;
the captured login payload supplies only pilot identity and a profile XML hash.
Redirect client use remains unverified. No new Live capture was run because
the mitmproxy CA prerequisite is prohibited.

## Local Clan directory and membership (2026-10-22)

Menu-197 (2026-10-24): `GET /indexInternal.es?action=internalClan` requires exact lowercase query keys (`action`, `from`, `prc`, `lang`, `filter`, `detail`, `q`, `joined`, `left`) and the exact action value `internalClan`. Wrong-case keys (`ACTION=`, `From=`, `PRC=`, `Lang=`), a wrong-case action value and control characters return no-store HTML 400 before the session lookup or company-choice redirect, with no `Location` and no echoed input. This supersedes the earlier case-insensitive matching of `from`, `prc` and `lang` described below.

Update 2026-10-24 (validation): join/leave requests that are not POST return
HTML 405 with `Allow: POST`. The query must be exactly one case-exact
`action=internalClanJoin` or `internalClanLeave`; other casing, a repeated
action, or extra fields return 400. An unreadable form body (for example a
cut-off multipart body) returns 400 instead of throwing. `clan` must be 1-64
lowercase ASCII letters or digits in hyphen-separated words (no leading,
trailing, or doubled hyphen); other values return 400 even with a matching
signed key. Error pages HTML-encode their message and never echo the input.
`GET internalClan` is `no-store` and now accepts optional `from`, `prc`, and
`lang` (each once, at most 64 characters, no control characters) like the
other local menus; anything else returns 400. The page shows an
"N of M clans recruiting" summary. Endpoints are unchanged; these rules are
emulator-defined.

Update 2026-10-24 (menu-58): `GET internalClan` also accepts optional
lowercase `detail={clanId}` (a well-formed clan id, at most 64 characters) and
`filter=all|recruiting|closed` (empty means `all`), each once. Wrong-case keys,
malformed ids or unknown filter values return 400; a well-formed id that is not
a directory clan of the pilot's company returns 404 (`LocalClan.HasDetail`).
`clan=` on the page is still rejected. The filter tabs show counts, and the
detail panel shows position, company name, members, share of company clan
pilots, size rank, recruiting state, and a join or leave form when allowed.
All of these values are emulator-defined. Covered by `LocalClanDetailTests`.

Update 2026-10-23 (deepen): signed `key` is now `nonce.unixIssued.hmac`;
keys older than one hour return 409 (reload), future-dated, legacy two-part,
or oversized values 400. Each company gains a closed, emulator-defined
"Elite" clan (join 409 "not recruiting"). Leave accepts the pilot's own
membership even when it belongs to another company or an unknown clan id.
Member counts are emulator-defined display data. Endpoints are unchanged.

Update 2026-10-23: `GET internalClan` now requires exactly the `action`
query field and returns an HTML 400/401/403 for malformed query, missing or
expired session, or invalid company. Join and leave require exactly one `clan`
and one signed `key` form field, bound to the local session, action, and clan.
Unknown clan returns 404, another company's clan 403, malformed or tampered
forms 400, and stale or replayed forms 409. Successful POSTs still redirect
with 303. Membership and consumed action keys are now account-locked Postgres
state; a valid old cache membership imports once. The new Traders entries for
MMO, EIC, and VRU, their names and descriptions, and all form/error behavior
are emulator-defined. Clan actions neither debit Credits nor grant Hangar
inventory. No new official Clan evidence or Redirect verification was obtained.

The local shell links to `GET /indexInternal.es?action=internalClan` after
company selection. It displays fixed, synthetic clans for the pilot's company
and the pilot's current membership. `POST
/indexInternal.es?action=internalClanJoin` accepts form field `clan` (one of
the directory IDs for that company) and signed `key`; `POST
/indexInternal.es?action=internalClanLeave` accepts the current `clan` and
signed `key`. A pilot may join one clan at a time and leave before joining
another. Postgres holds account-scoped membership and used keys. Both POSTs
return 303 back to Clan on success (since Menu-126 with `&joined={clan}` or
`&left={clan}`). No real payments or world
entry occur.

These routes, clan IDs, names, descriptions, and membership rules are local
emulator contracts. Saved Live sessions have no post-selection Clan traffic.
The IL2CPP clan-logo path literal on `darkorbit-22.bpsecure.com` does not
establish a Clan API or page schema. Redirect client use is unverified; no
Live capture was run because the mitmproxy CA prerequisite is prohibited.

## Local Tech Factory (2026-10-22)

Update 2026-10-23: `GET` still lists local timed recipes, now including
emulator-defined `tech_speed` Speed boost (250 components, five minutes) and
`tech_energy` Energy charge (300 components, six minutes). The existing three recipe costs and durations
are also emulator-defined. Each start form submits exactly `recipe` and a
session-bound signed `key`; each claim submits exactly the persisted `job` UUID
and its signed `key`. No quantity field is accepted: a job always produces the
recipe's configured output quantity. Extra, repeated, missing, or tampered
fields return HTML 400; unknown recipe returns 404; missing or expired session
returns 401; invalid or unchosen company returns 403; active job, early claim,
stale job ID, or replay returns 409. Successful POSTs redirect with 303.
Job ID, recipe snapshot, ready time, and consumed action keys now persist in
Postgres rather than the 30-day cache. Start atomically debits the shared
`tech_components` balance and creates the job; claim atomically credits the
configured resource and removes it. Account locking serializes concurrent
menu actions and shared wallet changes. An existing cached job is imported
once on first read, without a second debit; an isolated Postgres test covers
cache expiry and this import. These are local contracts without a
post-selection Live request or Redirect verification.

The local HTML shell links to `GET /indexInternal.es?action=internalTechFactory`.
It requires a `dosid` session and company. The initial component balance is
1,000. One job may run at a time. The original local recipes are
`tech_targeting` Targeting boost (100 components, two minutes), `tech_shield`
Shield boost (150, three minutes), and `tech_repair` Repair charge (200, four
minutes). No world entry or real payment occurs.

The page accepts only `action`, `from`, `prc`, and `lang`, each once and at
most 64 characters; other queries return 400. Each recipe row shows its status
(available, slot busy, or the component shortfall), and a running job shows
the time left. `internalTechFactoryStart` and `internalTechFactoryClaim` return
405 for non-POST requests. Their form keys carry a signed issue time: keys
older than one hour return 409, while keys dated more than five minutes ahead,
old-format keys, and altered keys return 400. `recipe` must be 1–64 lowercase
ASCII letters, digits, `_`, or `-`, and `job` must be 32 lowercase hex digits.
A busy slot, a component shortfall (with needed and available amounts), an
early claim (with the ready time), or a claim without a matching job returns
409 with a specific message.

These paths, recipes, prices, and timing rules are emulator-only. The saved
Live sessions contain no post-selection Tech Factory request or response, and
the IL2CPP network inventory establishes no Tech Factory HTTP contract. The
local HTML shell supplies the navigation pattern. Redirect client use is
unverified; no Live capture was run because the mitmproxy CA prerequisite is
prohibited.

## Local Skylab collectors (2026-10-22)

The local HTML shell links to `GET /indexInternal.es?action=internalSkylab`.
It requires a `dosid` session and valid company and shows account-scoped
Prometium, Endurium, and Terbium collectors, with throughput and upgrade
notices. `POST /indexInternal.es?action=internalSkylabUpgrade` requires exactly
`collector`, `level`, and a signed session-bound `key`; it spends
`100 * current level` units of that collector's shared resource balance and
raises its level, to a maximum of 10. `POST
/indexInternal.es?action=internalSkylabCollect` requires exactly `time` and
signed `key`; it credits 10 units per level for each whole elapsed minute,
capped at 60 minutes per action. Each account starts at level 1 with 500 units
of each resource. The shared wallet, collector levels, timing, and used action
keys persist in Postgres; legacy cache state imports once. Account locking and
one transaction cover balance changes and progress, so concurrent submissions
and cache expiry cannot reset levels or make balances negative. GET and POST
return HTML 401 for missing/expired sessions and 403 for wrong/missing company;
malformed, extra, duplicate, or tampered fields return 400, unknown collectors
404, stale/replayed forms 409, and success 303 to Skylab. No payment or world
entry occurs.

Hardening (2026-10-24): the GET page accepts only `action`, `from`, `prc` and
`lang`, each once and at most 64 characters, and is sent `no-store`. It shows
the minutes ready to collect, pending units per collector, a countdown to the
next whole minute, a storage-full notice at 60 minutes, and disables Collect
when nothing is ready. Form keys are `{nonce}.{issuedUnixSeconds}.{hmac}`
(at most 128 characters), signed with the session over the target, expected
value, nonce and issue time. Keys older than one hour return 409; keys dated
more than 5 minutes ahead, old two-part keys, or a changed issue time return
400. A non-POST returns 405 and an unreadable form 400. Conflicts now explain
the cause with 409: not enough resources, maximum level, nothing to collect
yet (under one whole minute), expired form, or stale/replayed form. Both the
cache and Postgres stores report these reasons; a collect that would overflow
a stock returns 409 instead of a 500. The form lifetime, skew and limits are
emulator-defined.

These routes, quantities, timing rules, and notices are emulator-defined. The saved Live
sessions contain no post-selection Skylab request, response, or assets; the
existing IL2CPP network inventory does not establish a Skylab HTTP contract.
The local HTML shell supplies the navigation pattern. Redirect client use is
unverified. No new Live capture was run because the mitmproxy CA prerequisite
is prohibited.

## Local Galaxy Gates builder (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalGalaxyGates`
renders a local Alpha, Beta, Gamma, Delta, and Epsilon gate builder. Each account starts with
250 local energy and zero parts; the local part totals are 34, 48, 82, 96, and 120.
`POST /indexInternal.es?action=internalGalaxyGateSpin` accepts form fields
exactly `gate` (one catalog ID), `amount` (integer 1–10), and a session-bound signed `key`. It spends that
many energy units and advances part progress; a spin larger than the remaining
part count is rejected without spending energy. A completed
gate can be marked ready with `POST
/indexInternal.es?action=internalGalaxyGateActivate`, form fields `gate` and signed `key`.
Activation does not enter the game world. GET and POST require an existing
`dosid` session and valid company, returning HTML 401/403 otherwise. GET accepts
only `action`, `from`, `prc`, and `lang` (each once, at most 64 characters) and
returns 400 otherwise; pages and errors are `no-store`. Non-POST actions return 405.
`gate` must be 1–32 lowercase letters and `amount` plain ASCII digits 1–10. Keys are
`{nonce}.{unixSeconds}.{hmac}`: older than one hour (emulator-defined) returns 409,
more than 5 minutes in the future, legacy, overlong, or tampered returns 400.
POST returns HTML 400 for missing, repeated, extra, malformed, or unreadable fields;
404 for an unknown gate; 409 with a specific reason for replay, activated or completed
gates, excess parts, or premature activation; and 400 with needed/available amounts
for insufficient energy. Error pages never echo input. Success redirects
303 to the menu (since Menu-125 with `&spun={gate}` or `&activated={gate}`). Gate parts and activation persist in Postgres alongside the energy
ledger; the account lock serializes simultaneous writes across API instances. Cache
expiry cannot restore spent energy or erase progress. The resource is separate from
Credits and Hangar inventory; cached legacy progress imports once.

This HTML and POST contract is emulator-only. The saved Live request for
`internalGalaxyGates` returned 302 to company selection, so no post-selection
page, spin response, gate totals, or energy amount was observed. IL2CPP metadata
contains binary `GalaxyGateBuilderInfoRequest`, `MultiEnergyRequest`, and
`SetupGateRequest` types, among others, supporting the feature vocabulary but
not this HTTP contract. The candidate `GET /unityApi/guide/ggRewards.php` now
returns provisional `{"data":["alpha","beta","gamma","delta","epsilon"]}` from the same local
gate catalog as the builder. These identifiers are local data, not captured
rewards; Delta, Epsilon, every part total, energy amount, notices, and form rules are
emulator-defined. Gate activation still grants no reward or world entry. The response
schema and Redirect client use remain unverified. No new Live capture was run
because the mitmproxy CA prerequisite is prohibited.

## Local Shop catalogue and purchases (2026-10-22)

The local HTML shell links to `GET /indexInternal.es?action=internalShop`, which
requires a `dosid` session and company. Optional `category` filters the
Equipment, Drones, and PET catalogue; optional `item` selects a detail page.
Unknown categories or item IDs return 404. `POST
/indexInternal.es?action=internalShopBuy` accepts form fields `item` (one of the
four local starter IDs) and `quantity` (integer 1–100). Missing session returns
401, missing company 403, and invalid or unaffordable orders 400. A successful
purchase returns 303 to the item detail page. The account starts with 10,000
local credits. Prices are 1,500 for LF-3, 1,000 for SG3N-B02, 500 for Flax,
and 3,000 for P.E.T. 10. Purchases debit the local balance and add to the same
inventory used by Hangar equipment. Balance and inventory live in the existing
distributed cache under the account ID with a 30-day sliding expiry. The
emulator makes no payment request.

All Shop paths, descriptions, prices, and the balance are emulator contracts.
The saved Live sessions have no post-selection Shop request or response. The
existing synthetic Hangar inventory supplied the item IDs; no official Shop
HTML/IL2CPP request builder or price contract was established. Redirect client
use remains unverified. The solution build passed with no warnings or errors;
all 111 non-integration tests passed.

## Local Shop purchase hardening (2026-10-24)

`POST /indexInternal.es?action=internalShopBuy` now follows the same rules as
the hardened Skylab, Tech Factory, Clan, and Dock actions:

- Non-POST requests return 405 with `Allow: POST`. The query must be exactly
  one `action=internalShopBuy`; any other parameter, a repeat, or a different
  case returns 400. A body that is not a readable form returns 400.
- `quantity` must be 1–3 ASCII digits with no sign, padding, or leading zero,
  in 1–100. `item` and `currency` are 1–100 characters with no control
  characters or surrounding spaces.
- `idempotencyKey` is `{32-hex nonce}.{issued unix seconds}.{HMAC-SHA256}`,
  keyed by the session `dosid` over the listing ID, nonce, and issue time. It
  is valid for one hour and up to 5 minutes into the future. Altered,
  foreign-session, legacy two-part, or future keys return 400. Expired keys
  return 409 before any database work.
- Error pages are `text/html`, `no-store`, headed `Shop request failed
  ({status})`, and never echo submitted values. The success `Location`
  URL-escapes the listing ID.

These are emulator contracts. No Live Shop purchase request was captured, and
Redirect client use remains unverified.

## Local Shop page hardening (2026-10-24)

`GET /indexInternal.es?action=internalShop` now matches the hardened Auction,
Vouchers, and Account pages:

- Responses are `Cache-Control: no-store`. The query is checked before the
  session.
- Parameter keys must use their exact lowercase spelling, and `action` must be
  exactly `internalShop`. Mixed-case keys or action values return HTML 400
  before the session lookup.
- Allowed parameters are `action`, `from`, `prc`, and `lang` (each at most 64
  characters, no control characters), plus `category` and `item` filters
  (1–100 characters, no control characters or surrounding spaces, the same
  rule as purchase form fields). Each may appear once. Unknown parameters
  such as `quantity`, repeats, or empty filters return 400.
- 400, 401, 403, and 404 use the HTML-encoded `Shop request failed ({status})`
  page with a link back to Shop.
- `POST internalShopBuy` with a cut-off multipart body or a failing body
  stream returns the 400 "could not be read" page without debiting; a missing
  session returns 401 before the body is read.

The parameter rule is emulator-chosen. Redirect client use remains unverified.

## Local Assembly page and craft hardening (2026-10-24)

`GET /indexInternal.es?action=internalAssembly` now matches the hardened
Auction and Shop pages:

- Responses are `Cache-Control: no-store`. The query is checked before the
  session.
- Allowed parameters are `action`, `from`, `prc`, and `lang`, each once, at
  most 64 characters, no control characters. Before, any parameter besides
  `action` returned 400, so shell links carrying `from`/`prc`/`lang` failed.
  Unknown parameters such as `recipe` or `key`, and repeats, return 400.
- Order: 400 (query), then 401 (session), then the company-choice redirect.
- `POST internalAssemblyCraft` requires a query of exactly one
  `action=internalAssemblyCraft` in that casing; other casing or extra
  parameters return 400 before the session is checked. A cut-off multipart body
  or failing body stream returns the 400 "could not be read" page without
  crafting; a missing session returns 401 before the body is read.

The parameter rule is emulator-chosen. Redirect client use remains unverified.

## Local Hangar equipment deepening (2026-10-23)

The equipment GET now caps saved assignments against the current owned inventory
before rendering. This covers stock changed by another local economy menu or an
admin edit while the assignment cache remains alive; an item-count notice shows
the resulting selected configuration. The next successful equipment change saves
the reconciled assignments. Cache expiry still clears assignments, while
Postgres-backed credits and inventory persist. This reconciliation and notice are
emulator-defined. The routes and form fields did not change.

The local Dock POST contracts now reject duplicate, missing, and extra form fields,
out-of-range slots/configurations, negative or oversized quantities, and unknown
items. An absent or expired session returns an HTML 401 page; a missing or
unknown company returns 403; invalid fields return 400; an unchanged selection
or assignment, or an allocation beyond shared stock, returns 409. Successful
changes return 303. Equipment now shows available stock after the other ship's
reservations. Cache read/modify/write operations in the shared Hangar/Shop state
are serialized across store instances within one API process. The same behavior
is not atomic across multiple API processes. On cache expiry, local assignments
reinitialize; Postgres-backed inventory and balances remain durable. Equipment-only LF-2, SG3N-A03, and Iris
entries extend the local inventory; their names and quantities are
emulator-defined. These validation rules, notices, stock
display, and status choices are emulator-defined, with no new Live evidence or
Redirect client verification.

## Local Hangar equipment (2026-10-22)

The local Dock now links each owned ship to `GET /indexInternal.es?action=internalDockEquipment&slot={1|2}&configuration={1|2}`. The page shows two alternative configurations and starter equipment in Equipment, Drones, and PET categories. `POST /indexInternal.es?action=internalDockEquipmentSet` accepts form fields `slot` (1 or 2), `configuration` (1 or 2), `item` (one of the local starter IDs), and `quantity` (nonnegative integer). A valid `dosid` and company are required; missing session returns 401, missing company 403, and invalid item, quantity, or slot returns 400. A successful change returns 303 to the same equipment page. Assignments are keyed by account in the existing distributed cache with a 30-day sliding expiry. The two configurations of one ship may reuse equipment, but simultaneous allocations across ships cannot exceed owned quantities. The route uses the existing gateway `/indexInternal.es` mapping. Since 2026-10-24 the page and the set action require canonical, exact-case queries (page: optional `from`/`prc`/`lang`; set: only `action`), the set action answers non-POST with 405, and form numbers must be plain digits (`slot`/`configuration` `1`|`2`, `quantity` 0-999); see the Hangar equipment row in [menu-coverage.md](menu-coverage.md).

This is a local emulator contract. The saved Live response contains only a nine-field hangar summary, and the IL2CPP inventory does not establish an equipment request or response body. Starter item names, quantities, and interactions are synthetic; no Live capture was run because the mitmproxy CA work is prohibited. Redirect client behavior remains unverified.

## Local Dock selection (2026-10-22)

After company selection, `GET /indexInternal.es?action=internalDock` now
renders two local hangar slots and a form to activate the inactive ship.
`POST /indexInternal.es?action=internalDockSelect` accepts form field `slot`
as integer `1` or `2` only for an existing `dosid` session with a company;
it returns `303` to Dock. Missing session returns `401`, missing company
`403`, and invalid slot `400`. The active slot is stored under the account ID
in the existing distributed cache with a 30-day sliding expiry. On the next
`GET /unityApi/login.php`, `hangars.data.hangars` includes both slots and
sets exactly one `hangarActive` field. Each entry has the nine fields observed
in the saved Live login response. Slot 1 retains the existing Pusat Plus
sample and relative captured icon path; slot 2 Phoenix, its model ID and icon
path are synthetic. The Dock HTML and POST action are local contracts, not
captured post-selection behavior. No Live capture was run because installing
the mitmproxy CA is prohibited. Local tests cover account isolation, validation,
HTML selection controls, and login JSON consistency; client use is unverified.

### Dock selection hardening (2026-10-24)

`GET /indexInternal.es?action=internalDock` (with a session and a company)
accepts only `action`, `from`, `prc` and `lang`. Each may appear once and be
at most 64 characters; anything else gets a `400` HTML error page. The
response is `no-store`. Without a session or company it still returns the
captured pre-company `302`. Each Dock card shows the ship ID and model ID. The
inactive slot's form posts `slot`, `current` (the slot active when the page
was rendered) and `key`. The key is an HMAC signature over the session, the
target slot and the current slot.

`POST /indexInternal.es?action=internalDockSelect` returns these errors:

| Status | Cause |
| --- | --- |
| `405` | The method is not POST. |
| `400` | Any query parameter besides one `action`, or the `action` value is repeated. |
| `401` / `403` | No session, or no company selected. |
| `400` | The body is not a form or cannot be read. |
| `400` | The form fields are not exactly one `slot`, one `current` and one `key`. |
| `400` | A slot value is not exactly `1` or `2` (`02`, `+2` and ` 2` are rejected), or `slot` equals `current`. |
| `400` | The key is malformed, tampered with or signed for another session, or its issue time is more than 5 minutes in the future. |
| `409` | The key is older than 1 hour. |
| `409` | The active slot is no longer `current`, for example a second tab or a replayed form. |

The store applies the `current` check and the write together under its
existing lock. Success still returns `303` to Dock. These limits are defined
by the emulator; no official Dock form was captured.

### Dock activation notice (menu-117, 2026-10-24)

A successful `POST /indexInternal.es?action=internalDockSelect` now returns
`303` to `/indexInternal.es?action=internalDock&activated={slot}`, where `slot`
is the slot that was just activated. Failed selections (`400`/`401`/`403`/
`405`/`409`) still send no `Location`. `GET internalDock` accepts `activated`
in addition to the shell parameters. The key must be exactly lowercase
`activated`, appear once, and have the value `1` or `2`. A value such as `0`,
`3`, `02`, `+2`, a padded value, non-ASCII digits, an empty value, a repeat, or
the key spelled `ACTIVATED` or `Activated` returns the existing HTML `400`
"Invalid Hangar query." page. If the named slot is still active, the page
shows `<p id="dock-notice" role="status">{Ship} is now the active ship (slot
N).</p>` above the cards. If another slot is active now, for example after a
second tab or a reload of an old redirect, the notice says "The active ship
has changed since slot N was activated." and carries `data-stale="true"`.
Without `activated` the page is unchanged. This is an emulator contract;
coverage is in `ThDarkOrbitEmulator.Tests/ClientTesting/LocalHangarActivatedNoticeTests.cs`.

## Captured news catalogue alignment (2026-10-22)

The saved successful Live login (`20261021-224636-login`, decoded flows 84 and
86) contains 19 `unityApi/news.php` items. The emulator now returns those
captured public `newsId` strings and integer `seconds` values in their observed
order instead of one synthetic item. Its breaking-news XML contains a matching
layer for each ID, with the captured priority, category, and background asset
filename. All 19 backgrounds resolve to files in the existing captured command
center manifest. `extract_safe_news_catalog.py` reproduces this allowlisted
projection from the decoded files without opening the credential HAR or
printing links, request fields, or account data.

The layer actions remain local navigation stubs. Other captured layer content,
including labels and event-specific links, is not reproduced; countdowns are
the saved snapshot, not live event state. Tests cover the 19-item shape, unique
IDs, JSON-to-XML references, and manifest asset references. The solution build
passed without warnings or errors and all 102 non-integration tests passed.
Client use after company selection remains unverified; the synthetic
`internalWebGL` page still does not launch native gameplay. No new capture was
run because this task prohibits installing the required mitmproxy CA.

## Expedition boxes path (2026-10-22)

The installed IL2CPP metadata contains the exact path
`/xml/events/expedition/expedition-boxes.xml` (literal 1793, offset
`0x51661`). ApiService now registers GET at that path, and the gateway routes
it to ApiService. The response is a 200 XML local catalogue with `no-store`,
one `RateId`, and a `Rewards` entry for the shield granted by the local Events
menu. This is an emulator contract, not an observed official catalogue.
Endpoint discovery, gateway route, and response tests cover the path and
reward consistency. No capture establishes whether the client requests it,
its method, or its XML schema. Native gameplay use remains unverified. The
larger post-selection blocker is
still the synthetic `internalWebGL` page, which does not launch the native game.
The solution build passed with no warnings or errors; all 102 non-integration
tests passed. No capture was run because the mitmproxy CA prerequisite is
prohibited in this task.

## Payment proxy link target (2026-10-22)

The saved Live `flashAPI/brNews.php` XML includes `payment` package links to
`/?action=internalPaymentProxy` with opaque `req` and `v` query values. No
request to that target or response from it was captured. The emulator now
handles the action at the game-host root before serving the public login page.
It requires an existing `dosid` session, redirects an account without a
company to local company selection, and returns a local practice-credit page
after selection. It does not read or reuse the captured opaque query values or
perform a purchase. The local-only claim contract is described above. Exact numbered game hosts are accepted through the
validated `X-Original-Host` redirect header or as the direct request host;
the public `www.darkorbit.com` root remains the login page. Four local tests
cover host and action matching plus each response branch. The destination
behavior is synthetic, and Unity client use is unverified. No new capture was
run because the mitmproxy CA prerequisite is prohibited in this task. The
solution build passed with 0 warnings and errors; all 99 non-integration tests
passed.

## Login payload forum and help link targets (2026-10-22)

The local `unityApi/login.php` payload advertises `boardLink` and `helpLink`
at `indexInternal.es?action=internalForum` and `internalHelp` on the
client-facing game host. Those exact actions now return distinct local Forum
and Help HTML pages for an existing `dosid` session; an absent or expired
session returns 401. Previously both actions fell through to the game-page
redirect. Tests cover the advertised URLs, action handling, status, content
type, distinct page bodies, and return links. The login fields are present in
the saved Live response, but their local target pages and client use have not
been captured. The pages are synthetic emulator behavior. The solution build
passed with 0 warnings and errors, and all 95 non-integration tests passed.
No client capture was run because this task forbids the mitmproxy CA work that
the capture script requires.

## Local support and account-center link targets (2026-10-22)

The local `support` and `accountCenter` values in `GET /unityApi/links.php`
point to `indexInternal.es?action=internalSupport` and `internalAccount` on the
client-facing game host. Those actions now return distinct local HTML pages for
an existing `dosid` session, with a link back to the game; an absent or expired
session returns 401. Previously both actions fell through to the game-page
redirect. Tests cover the link targets, response bodies, status, and content
type. The real Live destinations are authenticated external account-center URLs,
so these local pages are emulator behavior, not captured response contracts.
Their use by the Unity client remains unverified.
The solution build passed with 0 warnings or errors, and all 92
non-integration tests passed. No client capture was run because the capture
script requires the prohibited mitmproxy CA installation.

## Captured Unity link destinations and local forum action (2026-10-22)

The decoded successful Live `20261021-224636-login` flow 83 gives more than the
six `unityApi/links.php` field names. The response has an external Bigpoint legal
imprint URL, an empty `credits` string, a relative
`/GameAPI.php?action=portal.redirectToBoard` forum URL, and an external Spanish
DarkOrbit board URL for `help`. The emulator now emits those four captured
static values. Its `support` and `accountCenter` values remain local game-host
placeholders: the captured URLs point to `accountcenter.bpsecure.com` and carry
`authUser` and `token` query parameters, which cannot be copied from a saved
session. The placeholders neither send local credentials to the official site
nor claim parity with those authenticated external destinations.

The gateway now routes GET `/GameAPI.php` to ApiService. For the captured forum
action, an absent or expired local `dosid` session returns 401, consistent with
the earlier failed-login Redirect request to `/GameAPI.php`; a valid session
receives a synthetic local forum page with a return link. Other actions return
404. The captured link proves the action URL, but no successful Live forum
request or response was saved, so the local page and its success status are
emulator choices. The solution build passed with 0 warnings and errors; all 84
non-integration tests passed. No new client capture was run because the capture
script requires the prohibited mitmproxy CA installation.

## Menu-185: Unity links session cookie and length (2026-10-24)

`GET /unityApi/links.php` now runs through `UnityLinksAction`. The saved Live
response (20261021-224636-login) sent the client's `dosid` cookie, an exact
`Content-Length`, and a refreshed `dosid` cookie (`path=/; samesite=none;
secure; HttpOnly`). The local response now sends an exact UTF-8
`Content-Length`. For a live session with a `dosid` of at most 128 characters,
it refreshes exactly one cookie with the same attributes. Missing, blank,
unknown, logged-out or oversized cookies (never looked up) still get the public
200 links without `Set-Cookie`. Anonymous Live behavior is unknown, and the
links carry no account data. The headers, six string keys in captured order and
the game-host resolution, which never echoes a foreign `X-Original-Host`, are
unchanged and now endpoint-tested in `UnityLinksEndpointTests` (13 cases). No
Live capture was run.

## Unity links client-facing host (2026-10-22)

`GET /unityApi/links.php` uses the same validated game-host resolver as
`unityApi/login.php` for its local `support` and `accountCenter` placeholders.
When the gateway receives a redirected request with an exact numbered
`gblN.darkorbit.com` `X-Original-Host`, those two links use the game host rather
than exposing `localhost:60952`. Direct local requests retain their local scheme
and host; unrecognized or lookalike headers do not become link hosts. The
other four fields now use the captured static values documented above.

## Legacy command center XML path (2026-10-22)

The installed IL2CPP metadata contains `/xml/CommandCenter.xml` (literal
35425, file offset `0x13c576`). The emulator now serves its existing synthetic
breaking-news command center XML at this exact GET path and routes it through
the gateway. Both `/flashAPI/brNews.php` and the new compatibility path use one
response writer, preserving the existing XML body and headers. Endpoint catalog,
gateway route, and response tests cover the addition. This is an inferred alias:
no saved capture requests `/xml/CommandCenter.xml`, and the native client's
expected response schema at that path is unknown. The client has not been
recaptured because this run forbids the CA installation required by the capture
script. The solution build passed with 0 warnings or errors, and all 80
non-integration tests passed.

## Fourth local company (2026-10-22)

The emulator now offers TST (`tst`, display name `Test Company TEST`) as its
fourth company. `CompanyCatalog` is the shared source for the four codes,
display names, numeric faction IDs (MMO 1, EIC 2, VRU 3, TST 4), and starter
maps. The synthetic `internalCompanyChoose` page renders four forms from that
catalog. Confirmation saves `tst` to the account and session; the confirmation
and game pages display the catalog name. A later login restores the company.
Migration `005_add_test_company` expands the check constraint from migration
004 without changing that applied migration. TST uses map ID `1` (1-1) for
both `mapID` and the active hangar's `mapId`.

The local `unityApi/login.php` contract contains no company name or numeric
faction ID field. It emits `hangars.data.faction = "tst"`, following the existing
string field, and does not add a new wire field for numeric ID 4. The choice,
faction value, and map are emulator extensions; no official TST client contract
exists. The installed client's IL2CPP metadata inventory identifies login and
hangar data types but does not establish support for a fourth faction. Native
gameplay may reject the new string or numeric faction because its own enums,
assets, and map rules were built for the official three companies.

## Legacy command center categories path (2026-10-22)

The installed IL2CPP metadata contains `/xml/Categories.xml` (literal 35424,
file offset `0x13c564`). The emulator now serves the captured public command
center categories XML at that exact GET path, alongside
`/resources/command-center/categories.xml`, through a shared response writer.
The gateway routes only this GET path to ApiService. This is a compatibility
alias based on the metadata path and the available categories asset, not a
captured wire contract: no saved flow requests `/xml/Categories.xml`, and its
actual response may have a different schema. Endpoint catalog and gateway tests
cover the route; the existing XML contract test covers the shared body. Client
use remains unverified because capture requires prohibited CA installation.
The solution build passed with 0 warnings or errors, all 44 client testing
tests passed, and all 73 non-integration tests passed.

## Local company display names (2026-10-22)

The synthetic `internalCompanyChoose` page, the page reached after a successful
local `internalCompanyConfirm` redirect, and the local `internalWebGL` game page
now render the selected company's full display name with a ` TEST` suffix.
All three pages read the names from `LocalCompanySelection.DisplayName`. The
confirmation view links to the game page. The `internalCompanyConfirm` action
still persists the same `mmo`/`eic`/`vru` value and returns a 302. The
`unityApi/login.php` response has no company-name field, so its faction values
and the existing numeric starter-map mapping are unchanged. Unit tests cover
all three rendered names on each page and the existing faction and map values.
The confirmation view is local emulator behavior; no official confirmation
page contract has been captured.

## Legacy command center resource path (2026-10-22)

The installed IL2CPP metadata contains `/xml/resource_command_center.xml`
(literal 35430, file offset `0x13c5cf`), while the successful Live login
requested `/resources/resource_command_center.xml` and received a manifest
with 609 files and three locations. The emulator now serves the same captured
manifest at the legacy `/xml/` path and routes that exact GET through the
gateway. This is an inferred compatibility alias: no saved capture shows the
client requesting the `/xml/` path, and its actual response contract may
differ. The client has not been run against this change because the required
mitmproxy CA work is prohibited. Tests cover route registration, gateway
matching, and the captured manifest shape.
The solution build completed with 0 errors, and all 70 non-integration tests
passed. The tests do not establish that the installed client selects this path.

## Command center categories asset (2026-10-22)

The captured command center manifest lists `categories` at
`resources/command-center/categories.xml` with a 32-character version hash.
Fetching that versioned public CDN URL returned 355 bytes of XML: a `root`
containing five `category` elements (`sales`, `events`, `news`, `deals`, and
`upselling`). Each has string `id` and `textKey` attributes and an integer
`prio`; only `sales` has `isSpecial="true"`. The fetched body contains no
account fields. The snapshot is now embedded in ApiService and served as
`application/xml` from `GET /resources/command-center/categories.xml`.
Redirect mode sends only that exact path on `darkorbit-22.bpsecure.com` to the
local gateway, leaving other CDN assets upstream. Tests cover the XML and
manifest relationship, endpoint catalog entry, and path-specific routing in
both addons. This is an asset fetch and local contract test, not evidence that
the installed client requested the path. The CDN snapshot can become stale.
The solution build passed with 0 errors, all 68 non-integration tests passed,
and the three offline redirect addon tests passed. No client capture was run
because the mitmproxy CA work it requires is prohibited.

## Local post-company menu navigation (2026-10-22)

After a local company choice, the synthetic game page linked to
`internalDock`, `internalGalaxyGates`, and `internalPayment`, but each action
fell through to a redirect back to `internalWebGL`. The three actions now return
distinct local HTML pages (Hangar, Galaxy Gates, and Payment) with a return
link. The Payment page has no purchase form or payment operation. Before
company selection, all three actions still redirect to
`internalCompanyChoose`, as observed in the saved Live menu capture. Tests
cover the action set, distinct page content, return links, and case-insensitive
action handling. These post-selection bodies are synthetic: the saved Live
capture never passed the official company choice gate, so official page
contracts and client rendering remain unverified. No capture was run because
the task prohibits the mitmproxy CA work required by the capture script.
The solution build passed with 0 errors, and all 67 non-integration tests pass.

## Breaking news asset filename (2026-10-22)

The saved Live `flashAPI/brNews.php` XML contains 526 distinct background and
overlay `assetID` values. Each includes its file extension and resolves to a
`file` in the captured command center manifest as `id.type`. The local
synthetic layer previously used the manifest ID alone, so its background did
not follow the captured reference convention. It now uses
`sale_general_s_2018.jpg`, which resolves to the manifest entry with ID
`sale_general_s_2018` and type `jpg`. The cross-response test checks the full
filename. This is a static contract correction; redirected-client asset loading
remains unverified under the current capture restriction.

## Breaking news ID and asset consistency (2026-10-22)

The saved Live login's `unityApi/news.php` response has 19 items. Every `newsId`
is `BE_` followed by the name of a layer in the captured `flashAPI/brNews.php`
XML. The XML layer's `countdown` attribute is a `0`/`1` flag, separate from the
positive `seconds` value in the JSON response. The local synthetic news item
now uses the captured ID convention, points to its corresponding XML layer,
and that layer's background asset ID exists in the served command center
manifest. The synthetic seconds value is within the range seen in the saved
response. A cross-response test checks the ID and asset references. The
specific local news content is still synthetic, and a redirected client has
not requested these endpoints in the available captures.

## Captured command center resource manifest (2026-10-22)

`GET /resources/resource_command_center.xml` now serves the static manifest
from the successful Live `20261021-224636-login` capture (flow 85) as an
embedded API service resource. The captured XML contains three relative asset
locations and 609 file entries with IDs, names, types, versions, and 32-character
hex hashes. It replaces the one-entry synthetic response, so the client can
resolve the captured command center asset IDs and cache versions against the
upstream asset host. This is a snapshot of upstream content and can become
stale as assets change; it contains no account or session fields. Tests verify
the location references, entry count, hash shape, and XML contract. The
solution build and all 63 non-integration tests pass. Runtime use of the
manifest in a redirected client remains unverified because this task prohibits
the mitmproxy CA work needed for another capture.

## Numbered game host routing (2026-10-22)

Both Redirect addons now route any exact `gblN.darkorbit.com` host, where `N`
is a positive decimal integer, to the local gateway and retain that original
host in `X-Original-Host`. The login payload uses the same host in its
client-facing `host`, `chatHost`, links, and image base. Offline tests cover
the captured `gbl3`/`gbl6` hosts, a prospective `gbl12` host, and unrelated or
lookalike hosts that must stay upstream. This closes the configuration gap
for a numbered game host selected by a future login redirect. Only `gbl3` and
`gbl6` have been seen in saved captures; use of another number and native
gameplay remain unverified. No new capture was run because this task prohibits
the required mitmproxy CA work.

## Active hangar icon cache version (2026-10-22)

The allowlisted shape inspection of saved Live `20261021-224636-login` flow 82
found that the active hangar's relative icon path ends with a single `__cv`
query value containing 32 hexadecimal characters. The local login payload now
emits that captured asset version with the synthetic hangar's icon path.
`extract_login_asset_hashes.py` validates the exact path, query key, and hash
shape before copying this one asset version; it does not copy account or session
fields. A payload test checks the relative path and query shape. This is a
snapshot of an upstream asset version, and client behavior after the change
remains unverified. The capture script cannot be run in this task because it
requires the prohibited mitmproxy CA.

Hardcoded URL literals, metadata indices, redirect candidates, and analysis
limits are documented in [client hardcoded hosts](client-hardcoded-hosts.md).

## Active hangar path and ship ID shape (2026-10-22)

The allowlisted projection of saved Live `20261021-224636-login` flow 82
confirmed that the active hangar's `tooltipIcon` is a relative `/do_img/…`
path, with no scheme or host; all 33 hangars use relative paths. Its `shipId`
has a `ship_` prefix followed by a hyphenated name and is 15 characters long.
The local response previously used
an absolute CDN URL and omitted that prefix. It now emits the relative path
and the synthetic `ship_pusat-plus` identifier inferred from the path. The
captured ship ID itself was not copied. The existing numeric hangar ID and
model ID remain covered by the payload test. The solution build passed and all
63 non-integration tests passed. Client behavior after this change remains
unverified because capture requires the prohibited mitmproxy CA.

## Synthetic hangar wire shape (2026-10-22)

The allowlisted projection of saved Live `20261021-224636-login` flow 82
shows 33 hangars, one active; all `hangarId` values are six digit numeric
strings and all `tooltipIcon` values are relative ship-image paths. The active
hangar has `modelId=534` and an icon path ending in
`/do_img/global/items/ship/pusat-plus_100x100.png`. The latest Redirect
`20261022-103322-redirect-company` response has one active synthetic hangar,
but its `hangarId` is nonnumeric and its tooltip is a plain placeholder.

`UnityLoginPayloadBuilder` now gives the local hangar a stable numeric ID
derived from the local game user ID, the captured active model number, and a
relative icon path with the captured suffix. Its `shipId` and name are synthetic
choices inferred from that image path; the Live ship identifier was not
copied. The local response still provides one hangar because the emulator has
no account inventory. Unit tests cover the ID, icon path, model, and account
isolation. The solution build passed and all 62 non-integration tests passed.
This improves the login response shape; native hangar selection and gameplay
after the change remain unverified. No client capture was run because this
task forbids work requiring the mitmproxy CA.

## Redirect parity pass (2026-10-22, 10:31-10:34 UTC-5)

AppHost answered the local gateway health check before two bounded Redirect
AutoLogin sessions. `20261022-103128-redirect` ended on idle with 90 HAR entries;
the client reached the synthetic company choice page and all three menu actions
returned 302 to `internalCompanyChoose`, then 200. The second session,
`20261022-103322-redirect-company`, used the existing local company choice
automation, ended on idle with 68 HAR entries, and reached `unityApi/links.php`
and `unityApi/login.php` after VRU selection; both returned 200. Both sessions
exited 0 and restored the Vuplex host. The first session made no Unity API
requests before company selection.

REA retained capture evidence: first Redirect
`ev_b57d965f4d7e8fc0d4acfc537ade1c670b598943a6d88cedaf49b06d51f8ce70`,
second Redirect `ev_bf7fbbceea6f584bc20c17af29273ac552409ba816bbfcd9d70e1118bbef511b`,
newest successful Live menu session `20261022-003627-menus`
`ev_151d19de3a15b6d5cadb011fa17ea446a79cd3a248601a4ff89cb17e966cd87c`,
and the successful Live login sample `20261021-224636-login`
`ev_9f9edfb59fdaba907995c66e7ab6a263e1251c1ff2ec826721a5139fa1a3a30d`.
Only endpoint/status views and allowlisted login field projections were used;
credential submissions and session values were not inspected or copied.

| Flow or response | Redirect observation | Live observation | Status |
| --- | --- | --- | --- |
| SAS login → ProjectApi twice → `internalStart` → `internalWebGL` | 302 through the redirect chain, then 200 HTML in both sessions | Same status sequence on the game host | Status parity; local company page is synthetic. |
| Pre-company menu (`internalDock`, `internalGalaxyGates`, `internalPayment`) | Each returned 302 to `internalCompanyChoose`, then 200 | Newest Live menu session navigated back to its company choice page | Navigation parity at status/action level; Live HTML loads many CDN assets absent from the local page. |
| `unityApi/login.php` after local company choice | 200; captured startup flags, client mode values, CDN URL shape, and 32-character asset hash shapes match the saved Live sample | 200 in the successful Live login sample | Redirect `host` was `https://localhost:60952` against Live `https://gbl3.darkorbit.com`. The builder now uses the recognized original game host for redirected requests; this change has unit coverage but was made after the second capture. |
| Hangars in `unityApi/login.php` | At capture time: one synthetic active hangar with a nonnumeric ID and placeholder tooltip; the current builder emits two local slots with numeric IDs and relative image paths | 33 hangars in the saved Live account sample, with six-digit numeric IDs and relative tooltip paths | Open: account inventory and native gameplay behavior are not established by the emulator sample. |
| `unityApi/links.php` | 200 JSON after local company choice | 200 JSON in the Live login sample | Key/type shape matches; URL contents are synthetic. |
| `flashAPI/brNews.php`, `resources/resource_command_center.xml`, `unityApi/news.php` | No request in either new Redirect session | All three returned 200 in the successful Live login sample | Open: trigger conditions and runtime response parity. |

The newest Live menu session used `gbl6.darkorbit.com`, while the saved Live
Unity login sample used `gbl3.darkorbit.com`. The redirected login builder
accepts either captured game host from `X-Original-Host`; a direct local request
still reports its local host. The host correction is an emulator response change,
not a new client observation. No third capture was run because this pass was
limited to two attempts.

## Unity login client mode fields (2026-10-22)

A field-only projection of the saved successful Live login response
(`20261021-224636-login`, flow 82) found `browser="Unknown"` and
`gameclientAllowedInitDelay="10"`. The latest Redirect capture
(`20261022-055853-company-final-verify`, flow 61) emitted `"unity"` and `"0"`.
`UnityLoginPayloadBuilder` now emits the two captured string values. The
projection script prints only allowlisted client mode fields and never prints
account, session, or credential fields. The Redirect response also had
`requestScheme="http"` while the intercepted client request used HTTPS. For
recognized redirected game hosts, the builder now emits client-facing `https`
in both `requestScheme` and `host`; direct local HTTP requests retain `http`.
Tests cover these values. The solution builds and all 60 non-integration tests
pass.

The latest Redirect capture predates this correction and the asset hash and
startup flag corrections below. Client startup after these changes remains
unverified. A new capture was not run because the capture script requires the
mitmproxy CA, which this task forbids using.

## Company-consistent local starter map (2026-10-22)

The selected company changed `hangars.data.faction`, but the local
`unityApi/login.php` response still assigned map `1` to both `mapID` and the
synthetic hangar's `mapId`. The payload now selects `1` for MMO, `5` for
EIC, and `9` for VRU, with `1` retained before a company is selected.
Tests cover both map fields and the faction for each choice, plus isolation
between selected and unselected sessions. These starter maps are an emulator
mapping; a captured official response after company confirmation has not
established the map values. The installed client's behavior with this change
also remains unverified. No new capture was run because the capture script
requires the mitmproxy CA, which this task forbids using.

## Unity login asset and startup fields (2026-10-22)

The successful Live `20261021-224636-login` login response had `useHash=1`,
`autoStartEnabled=0`, `display2d=2`, and `fullscreen=1`. The latest Redirect
response (`20261022-055853-company-final-verify`) emitted `0`, `1`, `1`, and
`0`, respectively. A field-only inspection also found twelve 32-character
hex asset hashes in Live, where Redirect emitted the nine-character
`synthetic` placeholder. The response's `cdn` still names
`darkorbit-22.bpsecure.com` and is left upstream by the Redirect addon.

`UnityLoginPayloadBuilder` now emits the four observed startup flags and the
twelve asset content hashes from the captured login response. `display2d` was
later changed to `0`: the client maps it to `Settings.UserType`
(`GameLoadingManager.InitSetting`), where `2` forces 2D on every start and `0`
keeps the locally saved `force2D` PlayerPref. The allowlisted
`extract_login_asset_hashes.py` script generated `CapturedAssetHashes.cs` from
the decoded response, validating every hash as 32 hex characters and copying
no account or session fields. The login payload test checks the emitted flags,
hash shapes, and existing field names; the solution build and all 56
non-integration tests pass. This narrows a likely asset-loading mismatch but
does not establish that the installed client requests those assets or enters
native gameplay after company selection. The captured hashes are a snapshot
and must be refreshed when upstream asset content changes. No new client
capture was run because this task forbids work requiring the mitmproxy CA.

## Local company choice across logins (2026-10-22)

The local `internalCompanyConfirm` action now writes a validated `mmo`, `eic`,
or `vru` choice to the authenticated account in `identity.accounts.company`
before updating the current game session. SQL migration `004_add_company`
adds the nullable column with a database check constraint. A subsequent SAS
login reads that account choice into its new `dosid` session, so
`internalWebGL` shows the local navigation page and `unityApi/login.php`
reports the corresponding faction without another choice. Accounts with no
choice still see the company selection page. The confirmation request and
faction mapping remain **local emulator behavior**, not a captured official
contract.

The identity-to-session relogin path, invalid choices, and unknown accounts
are covered by local tests using a fresh identity read context. The solution
build and 55 non-integration tests pass. The PostgreSQL migration test was
updated for the fifth migration and new column but was not run because its
container setup may write outside the project boundary. No Redirect capture
was attempted because the capture script requires the mitmproxy CA, and this
run forbids work needing it. Database migration behavior and client relogin
remain runtime verification gaps.

## Session-bound local company choice (2026-10-22)

The local `internalCompanyConfirm` action now requires a live `dosid` session.
It records the selected `mmo`, `eic`, or `vru` in the session cache through the
session's existing expiration, and returns 401 without a session or 400 for an
invalid company. The browser `emulatorCompany` cookie is no longer used to
authorize progression. Subsequent local `internalWebGL` and menu requests read
the same session, and `unityApi/login.php` reports the selected company in
`hangars.data.faction` (`mmo`, `eic`, or `vru`; `0` before selection). This mapping is
local emulator behavior; the official confirmation method and faction values
for each company have not been captured. The current session still expires
after two hours; later sessions now inherit the account choice.

The session store tests cover invalid and unknown sessions, isolation between
two sessions, and the login payload's faction after selection. A Redirect
client capture has not yet verified the changed endpoint behavior.

## Local company selection step (2026-10-22)

The latest Redirect screenshot (`20261022-053738-cdn-verify/screenshots/02-logged-in.png`)
was entirely white: the emulator's `internalWebGL` response contained an empty
`main` element. The earlier Live screenshot showed company selection, and its
redacted CDP DOM contained `companySelection`, `confirm_mmo`, `confirm_eic`, and
`confirm_vru`. The local `internalWebGL` and `internalCompanyChoose` pages now
render a synthetic three-company choice page. Each form submits to the new
**local-only**, unverified `GET /indexInternal.es?action=internalCompanyConfirm&company=…`
action. It accepts only `mmo`, `eic`, or `vru`, stores the selection in a secure,
HTTP-only cookie, and redirects to a simple local navigation page. Invalid
values return 400. Captured pre-selection menu redirects still lead to company
selection; after a local selection, those actions return to the local page.

In bounded Redirect AutoLogin capture `20261022-054858-company-choice-verify`,
AutoLogin exited 0, the capture ended on idle with 82 entries, and
`screenshots/02-logged-in.png` visibly shows all three local controls in the
Vuplex page. The three configured menu navigations again redirected to
`internalCompanyChoose`. The script restored the original Vuplex host. The
capture HAR was not opened because it includes the credential submission.

This removed the blank browser state. At the time of this capture, it did not
reproduce the official confirmation request, persist company choice in the
game account, or launch native gameplay. A second bounded Redirect capture,
`20261022-055311-company-click-verify`, used
`scripts/client-testing/local-company-choice.config.json` and reported that
`#confirm_vru` was clicked. AutoLogin exited 0 and the capture ended on idle
with 62 entries. Its follow-up screenshot still showed the choice page. A
route-only probe of the mitmproxy `flow` file showed that
`internalCompanyConfirm` returned **204**: FastEndpoints had replaced the
intended 302 because the new handler branch had not completed the response.
The branch now calls `CompleteAsync`, and the automation refreshes the CDP
target and requires the expected destination after a click.

The final bounded Redirect capture, `20261022-055853-company-final-verify`,
exited 0 with 61 entries. Its safe route-only projection shows
`internalCompanyConfirm` **302**, followed by `internalWebGL` **200** and both
Unity JSON requests **200**. The follow-up screenshot visibly shows the local
navigation page. The capture script restored the Vuplex host, and the AppHost
and its child processes were stopped. These results verify the local company
selection path through the client. The official company confirmation contract,
account persistence, and native gameplay remain open.

## Redirect AutoLogin verification (2026-10-22)

Fresh AppHost startup after the Keycloak 25 bootstrap-variable fix created the
`thdarkorbit` realm: `GET http://localhost:8081/realms/thdarkorbit` returned
200. The gateway `/health` returned 200, and an empty registration request
returned 400 instead of the previous 503. No credentials were used in that
registration probe.

Two bounded Redirect AutoLogin captures used the capture script's in-memory
Credential Manager lookup and `-ProvisionLocalAccount`. In
`20261022-050917-redirect-verify-1`, provisioning returned 200 and the SAS
login passed, but automation missed its game-host success marker. The capture
reached `indexInternal.es` and `unityApi/login.php`/`links.php` (all 200). The
redirect addon had replaced the request host with localhost, causing the
emulator to issue relative cross-host redirects. Those locations would keep
the browser on the SAS host rather than following the Live chain through
`www.darkorbit.com` and `gbl3.darkorbit.com`.

Both Redirect addons now send `X-Original-Host` to the local gateway. The
emulator uses that header only to select the production absolute redirect
locations for intercepted requests; direct localhost requests retain relative
locations. The follow-up `20261022-051442-redirect-verify-2` completed with
automation exit 0 and 71 HAR entries. Provisioning returned 409 for the
already-created account after refreshing its local password. The client reached
the logged-in `internalWebGL` page. The capture script restored the Vuplex host.

REA `inspect-web-network-capture` and retained `api-surface`/record views were
used on both the successful Redirect HAR (parent Evidence
`ev_3fa6d2deaf3a1dc3a0158b7b4fe297c05849808c05c4f7e8b23c56756164d491`)
and the successful Live `20261021-224636-login` HAR (parent Evidence
`ev_9f9edfb59fdaba907995c66e7ab6a263e1251c1ff2ec826721a5139fa1a3a30d`).
The Redirect HAR records rewritten `localhost:60952` destinations; the
absolute `Location` headers and the CDP success marker establish the browser's
cross-host progression. Query values, form bodies, cookies, and tokens are
omitted here.

| Post-login response | Redirect verification | Live comparison | Remaining gap |
| --- | --- | --- | --- |
| SAS → two ProjectApi redirects → `internalStart` → `internalWebGL` | 302, 302, 302, 302, then 200 HTML | Same status sequence and HTML content type | Local `internalWebGL` now renders a synthetic company selection page, visually verified in the Redirect screenshot. |
| `GET /unityApi/login.php` | 200 JSON; outer `data` is a JSON string; 45 nested names and their JSON types match Live | 200 JSON with the same nested names and types | The local `cdn` names the captured `darkorbit-22.bpsecure.com` asset host. The four startup flags and twelve asset hashes were aligned afterward, without a new capture. One synthetic hangar remains versus 33 in the Live sample; native game behavior is unverified. |
| `GET /unityApi/links.php` | 200 JSON with six matching string keys | 200 JSON with the same six keys | Four static values now match the captured response; `support` and `accountCenter` remain local placeholders because the captured URLs carry authentication parameters. |
| `GET /flashAPI/brNews.php`, `/resources/resource_command_center.xml`, `/unityApi/news.php` | Not requested in the successful Redirect capture | All three requested and returned 200 | Runtime parity and trigger conditions remain unknown; synthetic endpoint bodies have only shape-level tests. |
| `internalDock`, `internalGalaxyGates`, `internalPayment` | Each navigation redirected to `internalWebGL` | Pre-faction Live navigation redirected to `internalCompanyChoose` | Menu and company selection behavior differs. |

The menu row records the earlier Redirect verification. The ApiService handler
now has synthetic 302 stubs for those three captured actions; the later
`20261022-053738-cdn-verify` capture reached each stub and followed its
redirect to the local company-choice page.

The local response DTOs remain in their matching `Features/UnityApi/*/Contracts`
and `Features/FlashApi`/`Features/Resources` directories. The observed
`unityApi/login.php` type agreement establishes only this captured sample;
optional fields and binary game traffic remain unverified. No new client
payload fields were inferred from the redirect fix.

The Live login response's `cdn` URL points to `darkorbit-22.bpsecure.com/`,
while its `host` points to the game host. A field-specific shape probe of the
already decoded response (`scripts/client-testing/inspect_login_asset_shape.py`)
confirmed the CDN hostname without exposing session fields or hash values.
The local login payload now preserves that CDN/game-host distinction and the
CDN URL's trailing slash. In a bounded Redirect AutoLogin capture,
`20261022-053738-cdn-verify`, the local login response pointed to the captured
CDN hostname and the browser reached the logged-in marker. Its CDN URL lacked
the Live trailing slash; this was corrected afterward and is covered by the
login payload test, but that final byte has not been recaptured. The same
capture recorded 84 flows and all three menu actions redirected to the local
company-choice page. The script restored the Vuplex host, and the AppHost and
child processes were stopped.

The Redirect addon leaves the CDN upstream, so this is an asset URL correction,
not yet a fully local asset implementation. The captured 32-character asset
hashes are now emitted locally as described above. Asset requests and game
behavior after the CDN and hash changes remain unverified.

## Local account provisioning for Redirect AutoLogin (2026-10-22)

The previous Redirect capture stopped at the local SAS login-error branch because
the emulator requires a matching local identity. `Start-CaptureSession.ps1` now
accepts `-ProvisionLocalAccount` with `-Mode Redirect -AutoLogin`. After the
existing credential lookup, `automate-login.mjs` sends the credentials in memory
to the loopback gateway's existing `POST /api/v1/identity/register` contract
before submitting the embedded login form. The endpoint returns 200 for a new
account or 409 for an existing account after refreshing its local Keycloak
password. The helper accepts only a loopback gateway and reports HTTP status
without printing a request or response body. This is an emulator setup request,
not an endpoint observed in the official client capture. The client still uses
the captured `POST /Sas/Authentication/Bigpoint` route for login.

The helper's local HTTP test covers new and existing accounts, request shape,
loopback restriction, and error status. In `20261022-002004-provisioned-login`,
AutoLogin obtained credentials from Credential Manager and reached this setup
request, but registration returned 503 before the client submitted its login
form. The capture contains only three unrelated HTTP entries. No credentials
or registration body were saved in the capture output.

The local `thdarkorbit` realm was absent in that earlier run (GET `/realms/thdarkorbit` returned
404), and the running Keycloak 25 container logged `user_not_found` for the
configured admin username. The AppHost has been changed to Keycloak 25's
`KEYCLOAK_ADMIN` / `KEYCLOAK_ADMIN_PASSWORD` bootstrap variables. That source
change compiled, and the fresh AppHost verification above now confirms it;
the old AppHost and ApiService binaries remained running and locked their
normal build outputs after the launcher session ended. The redirected Unity
login now succeeds as described above; full post-login response parity remains open.

## Redirect host coverage (2026-10-22)

The Redirect capture addon now routes the captured game/API destinations
`170.33.13.202`, `www.darkorbit.com`, `sas.bpsecure.com`,
`gbl3.darkorbit.com`, and `gbl6.darkorbit.com` to the local Aspire gateway.
Both selectable addons (`launch_redirect.py` and `redirect_to_local.py`) use the
same exact host allowlist. HTTPS goes to `localhost:60952`; HTTP goes to
`127.0.0.1:60953`. CDN and other hosts remain upstream. The offline
`test_redirect_hosts.py` checks both schemes, all five targets, and representative
excluded hosts. This closes the capture configuration gap where named game
hosts could bypass the IP-only redirect, but the new routing has not yet been
verified in a client capture. Local account authentication remains the first
observed runtime blocker before post-login response comparison.

## Redirect capture (2026-10-22)

The local Aspire gateway answered `/health` over both `http://localhost:60953` and `https://localhost:60952` before the client started. `20261022-000559-redirect` captured 104 HTTP entries, including four requests to the local gateway. AutoLogin submitted the form but exited 6 because it did not see the logged-in marker. The capture script restored the Vuplex host. The AppHost and its .NET child processes were stopped after the capture.

| Flow | Redirect request | Redirect response | Latest successful Live comparison |
| --- | --- | --- | --- |
| 4 | `GET /` | 200 `text/html`; HTML body (42,065 bytes) | Public page: 200 `text/html`; HTML body (9,846 bytes). The emulator page is structurally different, though AutoLogin found and submitted its form. |
| 68 | `POST /Sas/Authentication/Bigpoint` | 302 with no content type or body, to `/?loginError=…` | Flow 73: 302 `text/html`, empty body, to `/ProjectApi/Authentication` |
| 69 | `GET /?loginError=…` | 200 `text/html`; HTML body (42,065 bytes) | Successful Live login did not enter this path |
| 101 | `GET /GameAPI.php` | 401 with no content type or body | No matching request in the successful Live login capture; likely follows the failed local login |

The latest successful Live login (`20261021-224636-login`) continued from SAS through two `/ProjectApi/Authentication` redirects, `/indexInternal.es` (302 then 200 `text/html`), and the five Unity JSON/XML endpoints listed below (all 200). The later Live menu capture (`20261021-233244-menus`) also reached `internalCompanyChoose` (200 `text/html`). The Redirect capture did not reach any of them, so their body shapes were not compared at runtime. The previous synthetic response tests cover field and element names only.

The local SAS handler looks up an existing emulator account and verifies its password through local Keycloak. A failed lookup or verification takes the observed login-error branch. The capture does not reveal which check failed, and credentials were not inspected. Provisioning or signing in with a matching local account is needed for an end-to-end Redirect comparison. No authentication bypass was added. The missing SAS redirect content type was fixed in ApiService; the same `text/html; charset=UTF-8` contract is now applied to its ProjectApi and indexInternal redirects. This change is covered by a response-header test but was not recaptured through Unity.

## Menu capture status (2026-10-21)

### CDP menu discovery (2026-10-22)

The bounded Live `20261022-002943-menu-discovery` login reached
`internalCompanyChoose` on `gbl6.darkorbit.com`. The redacted CDP dump in
`menu-dom.json` includes `Page.getFrameTree` and isolated-world DOM summaries
for the page and both child frames. The page contains a visible
`#companySelection` area and `#confirm_vru`; the child frames are blank.
The eight `text=` menu labels were absent because this account was still at
company selection. This is a stronger explanation than the earlier Unity
rendering hypothesis. No company choice was submitted during discovery.

The automation now supports action-only `Page.navigate` menu steps on the
logged-in game host, with a five-second load bound and screenshots. Its
configured actions are `internalDock`, `internalGalaxyGates`, and
`internalPayment`, which appeared as links in the earlier decoded news XML.
The first full attempt (`20261022-003514-menus`) stopped before issuing any menu
request because the CDP session retained the public login URL after redirect;
the session cache now refreshes its target metadata. The retry
(`20261022-003627-menus`) saved `03-menu-hangar-dock.png`,
`04-menu-galaxy-gates.png`, and `05-menu-payment.png`. Its 218-entry HAR was
inspected with REA `inspect-web-network-capture`. The three action requests
were captured, but each returned 302 to `internalCompanyChoose`; all four
company-choice responses were 200. The screenshots still show company
selection, so **none of the three menu pages was reached**. Automation now
reports a redirect instead of treating a screenshot as proof of arrival.
Confirming a faction is the remaining gate. Routes for Shop, Profile,
Settings, Skylab, Clan, and Auction are not established by the captures.

`decode_flow_bodies.py` was run on `20261021-233244-menus`, `20261021-234021-menu-discovery`, `20261021-232955-menu-auto-discovery`, and `20261021-232442-menu-discovery`. Each yielded zero JSON/XML responses. Its redacted endpoint index records two HTML requests in `20261021-233244-menus`: `internalStart` (302) and `internalCompanyChoose` (200), both on `gbl6.darkorbit.com`. The action names were checked with `list-menu-actions.mjs`, which prints only action names. The eight configured `text=` menu selectors missed. No ajax/json menu endpoint or menu page beyond `internalCompanyChoose` was observed.

The later `20261021-235008-menu-discovery` stopped before client launch with prerequisite exit code 2 because the capture script reported that its mitmproxy CA was not trusted. It wrote no flow or DOM dump. The decoded news XML contains candidate links to `internalDock`, `internalGalaxyGates`, `internalPayment`, and `internalPaymentProxy`; none is evidence that those pages were requested.

Source: live capture `_reversing/Requests/sessions/20261021-224636-login/flow.har`, inspected with REA `inspect-web-network-capture` (HAR), and the same session's `flow` decoded with `scripts/client-testing/decode_flow_bodies.py` using mitmproxy `response.get_content()`. The redacted response bodies and endpoint index are in the session's `decoded/` directory. The capture contains 92 HTTP entries. One automated login attempt completed; the SAS request was followed by game-host redirects and Unity API responses. The automation saved only pre-submit screenshots, so the network sequence is the evidence for login progress. Request values, credentials, cookies, session IDs, and redirect tokens are deliberately omitted.

The table covers game and API traffic observed during login and the one newly observed company choice page. Static assets, ads, analytics, telemetry, DNS-over-HTTPS, and captcha assets are excluded. A dash means no structured body was observed. **Verified** means the method, endpoint, and response were seen in a capture; it does not claim emulator response parity or required fields across accounts.

| Method | URL (query values omitted) | Request / response content type | Response shape | HTTP status | Capture status | Emulator feature | Emulator status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| GET | `https://www.darkorbit.com/` | — / `text/html` | Public login page; no DTO | 200 | Verified: login | `Features/Mocks/WwwDarkorbitCom` | Existing mock |
| POST | `https://sas.bpsecure.com/Sas/Authentication/Bigpoint?authUser=…&token=…` | `application/x-www-form-urlencoded` / redirect | `BigpointAuthenticationFormRequest`; no response DTO | 302 | Verified: login | `Features/Sas/Authentication` | Existing auth route |
| GET | `https://www.darkorbit.com/ProjectApi/Authentication?authUser=…&token=…` | — / redirect | No structured body | 302 | Verified: login | `Features/ProjectApi/Authentication` | Existing redirect |
| GET | `https://gbl3.darkorbit.com/ProjectApi/Authentication?authUser=…&token=…` | — / redirect | No structured body | 302 | Verified: login | `Features/ProjectApi/Authentication` | Existing redirect |
| GET | `https://gbl3.darkorbit.com/indexInternal.es?action=internalStart&prc=…` | — / `text/html` | Redirect; no body DTO | 302 | Verified: login and menus | `Features/IndexInternal/InternalStart` | Existing redirect |
| GET | `https://gbl3.darkorbit.com/indexInternal.es?action=internalWebGL&from=internalStart` | — / `text/html` | Logged-in page; no body DTO | 200 | Verified: login | `Features/IndexInternal/InternalStart` | Synthetic company selection when no local choice exists |
| GET | `https://gbl6.darkorbit.com/indexInternal.es?action=internalCompanyChoose&from=…` | — / `text/html` (gzip) | Company choice page; no structured body DTO | 200 | Verified: menus, flow 54 | `Features/IndexInternal/InternalStart` | Synthetic four-company choice page (TST is local only); local route requires a session (401) and redirects after selection (302) |
| GET | `/indexInternal.es?action=internalCompanyConfirm&company=…` | — / redirect | Local-only company selection; requires a live `dosid` session | 302 | No official capture | `Features/IndexInternal/InternalStart` | Persists the first choice; same-company retry redirects; 400 invalid, 401 no session, 409 conflicting choice |
| GET | `https://gbl6.darkorbit.com/indexInternal.es?action=internalDock` | — / redirect | Redirects to company choice before faction confirmation | 302 | Verified: `20261022-003627-menus` | `Features/IndexInternal/InternalStart` | Pre-company redirect; distinct synthetic HTML page after local choice |
| GET | `https://gbl6.darkorbit.com/indexInternal.es?action=internalGalaxyGates` | — / redirect | Redirects to company choice before faction confirmation | 302 | Verified: `20261022-003627-menus` | `Features/IndexInternal/InternalStart` | Pre-company redirect; distinct synthetic HTML page after local choice |
| GET | `https://gbl6.darkorbit.com/indexInternal.es?action=internalPayment` | — / redirect | Redirects to company choice before faction confirmation | 302 | Verified: `20261022-003627-menus` | `Features/IndexInternal/InternalStart` | Pre-company redirect; distinct synthetic HTML page after local choice, no payment operation |
| GET | `https://gbl3.darkorbit.com/unityApi/login.php` | — / `application/json` | `UnityLoginCapturedResponse.data` is a JSON encoded string containing `UnityLoginData` | 200 | Verified: login, flow 82 | `Features/UnityApi/Login` | Synthetic account/hangar data; session required; local company choice changes faction and local starter map; captured asset hashes and four startup flags |
| GET | `https://gbl3.darkorbit.com/unityApi/links.php` | — / `application/json` | `UnityLinksResponse`: six string links | 200 | Verified: login, flow 83 | `Features/UnityApi/Links` | Four captured static values; two local authenticated-link placeholders |
| POST | `/unityApi/setLang.php` | form field `lang` (client code, e.g. `en`, `en_US`, `pt_BR`) / empty | Sent by `LaunchShow.GetUnityPostLanguage` when the launch-screen language dropdown changes | 200 saved or unchanged; 400 unknown/disabled code or non-form body; 401 no valid `dosid` cookie | No official capture; observed from the patched 1.1.93 client | `Features/UnityApi/Login/UnityConfigRoutes.cs` and `SetLangEndpoint.cs` | Stores the exact client code as the account language, so the next `login.php` `lang` keeps the choice. The 27 client codes come from `DarkOrbit.Settings` (migration `045_client_languages_unity`) |
| GET | `/indexInternal.es?action=internalSupport` or `internalAccount` | — / no local-target Live response captured | Local link targets from `unityApi/links.php` | — | Emulator-only destinations | `Features/IndexInternal/InternalStart` | Session-gated Support placeholder and local Account page with persisted preferences; client use unverified |
| POST | `/indexInternal.es?action=internalAccountSave` | form fields `language`, `sound`, `notifications`, `rev` / redirect | Emulator-only preferences action | 303 on valid save (`saved={rev}`), 303 to `internalAccount&unchanged=1` when the values already match (no revision bump, menu-187) | No official capture | `Features/IndexInternal/InternalStart` | Account-scoped local settings; invalid input 400, no session 401, no or invalid company 403, stale or replayed `rev` 409, non-POST 405, unreadable form 400; HTML error pages |
| GET | `/indexInternal.es?action=internalForum` or `internalHelp` | — / no local-target Live response captured | Link targets from local `unityApi/login.php` `boardLink` and `helpLink` | — | Emulator-only destinations | `Features/IndexInternal/InternalStart` | Session-gated synthetic Forum and Help HTML pages with 400/401/403/404 validation, board notices, and an optional `category` filter (menu-44); client use unverified |
| GET | `https://gbl3.darkorbit.com/GameAPI.php?action=portal.redirectToBoard` | — / no successful response captured | Forum action from captured links response | — | Link target only; failed-login Redirect request returned 401 | `Features/GameAuth/GameApiForumEndpoint` | Session-gated synthetic local forum page with the same validation as `internalForum`; exact-case single `action` and 128-character `dosid` cap (menu-104); successful client use unverified |
| GET (inferred) | `https://gblN.darkorbit.com/?action=internalPaymentProxy` | — / no response captured | Link target in captured breaking news XML; opaque query values excluded | — | Link target only | `Features/Mocks/WwwDarkorbitCom/HomeEndpoint` | Session-gated synthetic local Payment page after company choice; no purchase operation |
| GET | `https://gbl3.darkorbit.com/flashAPI/brNews.php` | — / `text/xml` (gzip) | `BreakingNewsXmlResponse` | 200 | Verified: login, flow 84 | `Features/FlashApi/BrNews` | 19 layers use captured IDs, priority, category, and backgrounds; remaining content and actions are synthetic |
| GET | `https://gbl3.darkorbit.com/resources/resource_command_center.xml` | — / `application/xml` (gzip) | `CommandCenterResourcesXmlResponse` | 200 | Verified: login, flow 85 | `Features/Resources/CommandCenter` | Synthetic XML stub |
| GET | `https://gbl3.darkorbit.com/unityApi/news.php` | — / `application/json` | `UnityNewsResponse.data[]` with `newsId: string`, `seconds: integer` | 200 | Verified: login, flow 86 | `Features/UnityApi/News` | 19 captured IDs; `seconds` count down live from the capture snapshot (2026-10-21T22:46:36-05:00) and restart with the captured duration at zero (emulator choice) |

`internalPaymentProxy` remains an **unverified link target**, not an observed
request. Its local game-host root handler is session-gated and synthetic. The
three observed menu actions above only establish their
pre-company-selection redirects; their post-selection page contracts are
unknown. `PreCompanyMenuRedirect` types the captured action and synthetic
redirect location; no response body DTO is applicable. No DTO can be based on
the link alone. No ajax/json menu endpoint was found in these sessions.

## Additional static client endpoint candidates (2026-10-22)

These paths are IL2CPP v31 string literals in the installed metadata, **not
captured requests**. HTTP method, host chosen at runtime, query/body, response
schema, and whether the installed build uses each path are unverified. The
`DarkOrbit.Settings` host/suffix fields and `DarkOrbit.Init.UpdateHost` suggest
runtime URL construction, but native caller and string xrefs are unavailable.
The complete [metadata inventory](../_reversing/Analysis/il2cpp-network-types.md)
records declaring types, fields and named method parameters. None of these
rows establishes a wire contract. Provisional emulator routes now cover these
paths so a redirected client can reach them. GET is assumed for read-oriented
paths; POST is assumed for upload. JSON field names and types below are inferred
from client metadata or selected as synthetic placeholders, and must be replaced
if a safe capture establishes the actual method and body.

| HTTP method | Path or URL literal | Metadata evidence | Candidate client type / shape | Capture status | Emulator status |
| --- | --- | --- | --- | --- | --- |
| Unknown (GET assumed) | `/unityApi/events/spaceSearch.php` | Literal 1772, `0x51516`; relative 34625; `//` variant 1683, `0x50ffa` | `SpaceSearchDate`: `Planets`, `Events`, `Rewards` | Unverified | Synthetic 200 JSON with populated `planets`, `events`, `rewards` string arrays from the local catalog; field types provisional |
| Unknown (GET assumed) | `/unityApi/events/ssRewards.php` | Literal 1773, `0x51536`; relative 34626; `//` variant 1684, `0x5101b` | `SpaceSearchRewardsDate`: `Name`, `Special`, `Time`, `Item`, `Title`, `Package` may be related | Unverified | Synthetic 200 JSON with one catalog-linked reward object; relationship and types provisional |
| Unknown (GET assumed) | `/unityApi/guide/ggRewards.php` | Literal 1774, `0x51554`; variant 34627 | `GalaxyGateBuilderAssets` has reward accessors; response unestablished | Unverified | Synthetic 200 JSON with local gate IDs in `data`; shape and reward semantics unverified |
| Unknown (GET assumed) | `/unityApi/payment.php` | Literal 1775, `0x51571` | `JavascriptHelper` has `PostUrl(category,packageID)` and related payment methods; direct link unproved | Unverified | Synthetic 200 JSON with local `url`; placeholder shape, no payment operation |
| Unknown (POST assumed) | `/unityApi/upload.php` | Literal 1776, `0x51586` | `UploadHelper.RequestPic(imageData,imageName,type,url)` and `UploadData` (`Result`, `Type`, `Error`, `Status`) are candidates | Unverified | Typed JSON failure, 501; no image accepted or stored |
| Unknown (GET assumed) | `https://www.darkorbit.com/index.es?action=externalDownload` | Literal 24854, `0x10998f` | Public web/launcher action; no body type | Unverified | Local GET action serves an emulator client/launcher page linked from the menu, with sign-in, menu, and Help navigation for an already installed client, plus optional emulator-defined `section` pages (menu-45); no installer, updater, Live response, or Redirect client use captured |
| GET asset (client use unverified) | `/resources/command-center/categories.xml` | Literal 30130, `0x123468`; versioned entry in captured manifest; public CDN XML fetched 2026-10-22 | `root/category` with `id`, `prio`, `textKey`, optional `isSpecial` | Client request unverified | Captured public asset snapshot served as XML; exact CDN path routed locally |
| Unknown (GET likely) | `/xml/Categories.xml` | Literal 35424, `0x13c564` | Category/configuration XML candidate; parser binding unknown | Unverified | GET compatibility alias to captured public command center categories XML; response parity unverified |
| Unknown (GET likely) | `/xml/CommandCenter.xml` | Literal 35425, `0x13c576` | `CommandCenterPattern.Parse(root,patterns)` is a candidate consumer | Unverified | GET compatibility alias to synthetic breaking news XML; response parity unverified |
| Unknown (GET likely) | `/xml/maps.php` | Literal 35428, `0x13c5b7` | `Settings.get_MapXmlUrl()` and `MapsPattern.Parse(root,patterns)` are candidates | Unverified | GET route serves a provisional local map catalogue; response parity unverified |
| Unknown (GET likely) | `/xml/maps.xml` | Literal 35429, `0x13c5c3` | `MapsPattern` (`GameServerIP`, `ID`) and `MapsConfigPattern` are candidate models | Unverified | GET route serves the same provisional local map catalogue; response parity unverified. Both routes send `application/xml; charset=utf-8` with an XML declaration, `no-cache`, `nosniff` and no cookie, and ignore the query (menu-101, `LocalMapsXmlResponseTests`) |

| Unknown (GET likely) | `/xml/resource_command_center.xml` | Literal 35430, `0x13c5cf` | `CommandCenterResourcesPattern` is a candidate parser; distinct from captured `/resources/resource_command_center.xml` | Unverified | GET compatibility alias to captured manifest; response parity unverified |
| Unknown (GET likely) | `/xml/events/battlepass/…` | Prefix literals 1792, `0x5164a`, and 35426, `0x13c58b`; `//` variant 1685 | Asset path prefix only; filename and XML shape unknown | Unverified | No dedicated route established |
| Unknown (GET likely) | `/xml/events/expedition/…` | Prefix literal 35427, `0x13c5a1` | Asset path prefix only; filename and XML shape unknown | Unverified | No dedicated route established |
| Unknown (GET likely) | `/xml/events/expedition/expedition-boxes.xml` | Literal 1793, `0x51661`; `//` variant 1686, `0x51052` | `ExpeditionBoxRewardData` (`RateId`, `Rewards`) is a candidate model, not a proven XML binding | Unverified | GET route returns 200 with a provisional local XML shield reward; structure and values are emulator choices |

The local-only `GET /indexInternal.es?action=internalMapOverview` is linked from the synthetic authenticated shell. It renders the selected company's saved starter map and the shared `/xml/maps.xml` catalogue without a world-entry action. An optional `map={id}` shows one catalogue map with its `{faction}-1` name, home company and starting companies. Only `action`, `map`, `from`, `prc` and `lang` are accepted, once each and at most 64 characters; `map` must be 1–4 ASCII digits without a leading zero. Failures return small HTML pages that do not echo input: 400 for bad queries, 404 for an unknown map, and 401 with a sign-in link for a missing session. A session without a company still redirects to company choice. No official menu route or XML body is established by the client literals. Menu-43 (2026-10-24): values with control characters also return 400; the overview shows a map count and a `data-map` marker per card; a detail page has its own `<title>`, Previous/Next links in catalogue order (none before the first or after the last map) and, on the pilot's own starter map only, an Open Hangar link to `internalDock`. Routes and status codes are unchanged.

The local-only `GET /indexInternal.es?action=internalHallofFame` (optional `category`, `company`, `page`, `pilot`, `q`) is linked from the synthetic authenticated shell. It renders read-only, emulator-defined leaderboards. Invalid query input (unknown, repeated, or wrong-case keys; a non-exact `action`; values over 64 characters or with control characters; a non-canonical page; `q` outside 2-32 ASCII letters, digits or hyphens) returns HTML 400, an unknown category or page returns 404, a missing session returns an HTML 401, and no company redirects to `internalCompanyChoose`. `q` filters pilot names case-insensitively within the company; a pilot detail outside the result returns 404. Responses are `Cache-Control: no-store`. No official ranking route, parameter set, or body was captured.

The local-only `GET /indexInternal.es?action=internalLogbook` (optional `category`, `page`, `entry={gameUserId}-{n}`) is linked from the synthetic authenticated shell. It renders a read-only, emulator-defined Logbook / History. A missing or expired session returns HTML 401, an invalid company or a foreign-account entry returns 403, invalid or repeated query input returns 400, and an unknown category, page, or entry returns 404. With a valid session but no company, it redirects to `internalCompanyChoose`. No official logbook route has been captured.

Menu-42: `internalLogbook` also renders the account's newest 200 `economy.ledger` rows (read-only, `EconomyStore.LedgerAsync`) as entries `{gameUserId}-{32 + per-account ledger sequence}`; demo entries 1-32 are unchanged. Menu-62: the entry number now accepts canonical positive 64-bit decimal values, so ledger rows beyond sequence 9,967 remain visible. An overflow, leading zero, empty category/page, wrong-case query key/action, repeated key, control character, or value over 64 characters returns HTML 400. Well-formed but absent entries still return 404, and foreign account IDs return 403. No Live or Redirect Logbook request was captured; these are emulator-defined rules.

The local-only Messaging / Mail / Friends menu `GET /indexInternal.es?action=internalMessages[&message={id}]` is linked from the synthetic authenticated shell. Its forms post to local-only `POST /indexInternal.es?action=internalMessageSend` (`to`, `subject`, `body`, `rev`, `key`), `internalMessageDelete` (`message`, `rev`, `key`), `internalFriendAdd` and `internalFriendRemove` (`pilot`, `rev`, `key`), which answer 303 to the menu on success (since Menu-130 with a confirmation key: `&sent={id}`, `&deleted={id}`, `&added={pilot}`, `&removed={pilot}`, or `&readall=1` for `internalMessageReadAll`), and 405 (`Allow: POST`) for any other method. The page accepts only `action`, `message`, `from`, `prc`, and `lang`, each once. No captured or IL2CPP evidence exists for an official messaging contract; status codes and fields are documented in [menu-coverage.md](menu-coverage.md).

The `//` literals are alternate path spellings in metadata; they do not prove
that the client sends a double-slash request. Likewise, the XML path prefixes
do not identify a complete requested file. `Settings.get_CommandCenterUrl()`,
`get_CommandCenterCategoriesUrl()`, `get_CommandCenterResourceUrl()`, and
`get_MapXmlUrl()` name likely URL builders, but their native expressions and
callers are unavailable. `JavascriptHelper.PostVoucherUrl(value,code)`,
`PostPackageUrl(paymentCategory,paymentPackage)`, and `ResData.EvoucherRequest`
add voucher/payment request candidates without establishing an endpoint.
The metadata also has `WebGlData` (`Success`, `Message`) and
`JavascriptVouchersRedeemData` (`Result`, `Res`, `Status`) as possible response
models; neither can be assigned to a captured response. HTTP verbs above are
inferred from asset paths only. See the
[expanded inventory](../_reversing/Analysis/il2cpp-network-types.md).

The captured `/unityApi/login.php` response names match
`DarkOrbit.LoginNodeData` (`Host`, `ChatHost`, `Cdn`, `BasePath`, `ServerInfo`,
`Hangars` and asset hashes), with `LoginDataNew.FromJson(json)` as a wrapper
candidate and `HangarWebData` as a hangar item candidate. `DarkOrbit.LinkUrl`
has accessors for the six captured `/unityApi/links.php` keys, and
`DarkOrbit.Datum` has `NewsId` and `Seconds` for `/unityApi/news.php` items.
`DarkOrbit.CommandCenterPattern` and `CommandCenterResourcesPattern` have
fields matching parts of the captured `/flashAPI/brNews.php` and
`/resources/resource_command_center.xml` documents.
These are static class and field matches to the captured shapes, not verified
serializer bindings. `LaunchShow.GetUnityPost(functionId)`,
`GetUnityPostLanguage(lang)`, `GetNewsRequest()`, and `GetRequest(url)` are
possible fetch sites; the binary does not currently yield their bodies.

## Login sequence

1. `GET www.darkorbit.com/` loads the public login page.
2. `POST sas.bpsecure.com/Sas/Authentication/Bigpoint` submits form fields `username` and `password` and returns 302. The query contains `authUser` and `token`; values are `<redacted>`.
3. `GET www.darkorbit.com/ProjectApi/Authentication` returns 302.
4. `GET gbl3.darkorbit.com/ProjectApi/Authentication` returns 302.
5. `GET gbl3.darkorbit.com/indexInternal.es?action=internalStart&prc=100` returns 302 to `action=internalWebGL`.
6. `GET gbl3.darkorbit.com/indexInternal.es?action=internalWebGL&from=internalStart` returns 200 HTML.
7. The Unity client fetches `/unityApi/login.php`, `/unityApi/links.php`, `/flashAPI/brNews.php`, `/resources/resource_command_center.xml`, and `/unityApi/news.php`; each returned 200.

## Captured body contracts

The SAS body is form encoded. Its observed fields are `username: string` and `password: string`. The existing endpoint parser also accepts captcha fields, but neither appeared in this captured submission. No credential values are stored in the contracts or this document.

`/unityApi/login.php` is JSON (`decoded/0082.json`) with a `data: string` property. That string contains another JSON object, represented by `UnityLoginData`. It contains string identifiers and asset hashes, integer flags, a boolean `showAdvertisingHint`, `serverInfo` with integer timestamps, and a `hangars.data.hangars` array. `languageXmlHash` was JSON `null` and is nullable in the contract. All 33 captured hangar entries had the same nine fields. The nested records are `UnityHangarsEnvelope`, `UnityHangarsData`, `UnityHangar`, and `UnityServerInfo`.

`/unityApi/links.php` is JSON (`decoded/0083.json`) with six string fields: `imprint`, `credits`, `forum`, `support`, `help`, and `accountCenter`. `/unityApi/news.php` is JSON (`decoded/0086.json`) with a `data` array of 19 items; each item has `newsId: string` and `seconds: integer`. The Unity API DTOs use exact wire-name `JsonPropertyName` attributes; XML serializer attributes do not apply to these three responses. Synthetic redacted fixtures under `ThDarkOrbitEmulator.Tests/ClientTesting/Fixtures` verify deserialization and JSON field-name round trips.

The two XML responses were compressed in the flow and base64 encoded in the HAR. Decoding the flow established their XML shape: `/flashAPI/brNews.php` (`decoded/0084.xml`) has a `root` with 694 `layer` elements. Layers contain `config`, `assignments`, and `actions`, with optional `event`, `overlay`, `button`, `onclick`, `payment`, and `link` collections. `categoryOverrideKey` appeared on only 12 layers and is nullable. `/resources/resource_command_center.xml` (`decoded/0085.xml`) has a `filecollection` with three `location` elements and 609 `file` elements. The XML contracts use `XmlRoot`, `XmlElement`, and `XmlAttribute` alongside wire-name `JsonPropertyName` attributes.

These DTOs describe the observed form, JSON, and XML field types. Their files are under the matching feature's `Contracts` folder. The Unity JSON handlers and the breaking news XML handler emit synthetic responses with captured field and element names; the breaking news XML stub contains one representative layer. The command center resource endpoint serves the captured static manifest. The gateway already routes these paths to ApiService.

## Open questions

- The decoded XML establishes element and attribute shapes for this capture. Response variation across accounts or events remains unknown.
- A single successful login shows presence and types, but does not establish whether most JSON fields are required across users, servers, or client versions. Only the observed `languageXmlHash` null is marked nullable.
- The synthetic JSON responses and reduced breaking news XML have not been validated in a redirected Unity client session. The captured command center manifest also lacks redirected client verification. The unverified menu links and ajax endpoints have no captured method/body contract, so they remain unmapped.
- HTTP HAR has no binary game traffic. REA's native mitmproxy decoder reported `capability_unavailable` on this Windows host because it requires Linux and caller-supplied mitmdump 12.2.3. Binary TCP/WebSocket message contracts remain unexamined.
- The automation produced no post-submit screenshot. The 302 chain, logged-in page response, and subsequent Unity API calls establish network progress, but visual UI state is not independently verified.
- REA's managed string inventory for `DarkorbitLauncher.dll` includes a DarkOrbit CDN URL but no observed login API path. The IL2CPP metadata table parse now identifies candidate methods and response classes, but literal-to-native-caller correlation remains open without a configured disassembler. Captured method, status, and body claims in the verified table remain grounded in the HAR/flow.

## Local Skylab page and action hardening (2026-10-24)

`GET /indexInternal.es?action=internalSkylab` now matches the hardened
Assembly, Auction and Shop pages:

- Responses are `Cache-Control: no-store`. The query is checked before the
  session: 400 (query), then 401 (session), then 403 (no valid company).
- Allowed parameters stay `action`, `from`, `prc`, and `lang`, each once, at
  most 64 characters; values with control characters now also return 400.
- `POST internalSkylabUpgrade` / `internalSkylabCollect` require a query of
  exactly one `action` key in that casing. `ACTION=…` or a lowercased value
  returns 400 before the session is checked.
- Any unreadable form (cut-off multipart body, failing body stream) returns the
  HTML 400 page and changes no collector or resource. A missing session
  returns 401 before the body is read.
- Error pages read "Skylab request failed (status)" and HTML-encode the
  message. These rules are emulator-defined; no Skylab traffic was captured.
- Later the same day, the page gained an optional lowercase `detail={collectorId}`
  parameter: lowercase ASCII letters, 1–32 characters, sent once. Any other value
  shape, an empty value, or `DETAIL=` returns 400, and `collector=` is still
  rejected. An unknown collector returns 404 after the session and company checks.
  The detail view adds no endpoint; its upgrade form posts to the existing
  `internalSkylabUpgrade` action.

## Local Galaxy Gates page and action hardening (2026-10-24)

`GET /indexInternal.es?action=internalGalaxyGates` now matches the hardened
Skylab and Assembly pages. No endpoints were added or removed.

- The query is checked before the session: 400 (query), then 401 (session),
  then 403 (no valid company). Responses stay `Cache-Control: no-store`.
- Allowed parameters stay `action`, `from`, `prc`, and `lang`, each once, at
  most 64 characters; values with control characters now also return 400.
- `POST internalGalaxyGateSpin` / `internalGalaxyGateActivate` require a query
  of exactly one `action` key in that casing. `ACTION=…` was accepted before;
  it and lowercased values now return 400 before the session is checked.
- Any unreadable form (cut-off multipart body, failing body stream) returns the
  HTML 400 page and spends no Galaxy Gate energy. A missing session returns 401
  before the body is read.
- Error pages read "Galaxy Gates request failed (status)" and HTML-encode the
  message.

## Local Galaxy Gates detail view (2026-10-24)

`GET /indexInternal.es?action=internalGalaxyGates` accepts an optional
`gate={gateId}` parameter. No new endpoint was added; the panel's forms post to
the existing `internalGalaxyGateSpin` and `internalGalaxyGateActivate` actions.

- `gate` must use that exact casing, appear once, and be 1–32 lowercase ASCII
  letters. Otherwise the page returns 400, still before the session check.
  A well-formed id that is not in the catalog returns HTML 404 "That Galaxy
  Gate does not exist." after the session and company checks.
- The panel shows the gate name, "Gate N of 5", the id, parts owned/required
  with a percentage and missing count, and the status. An energy forecast
  (emulator-defined, one energy per part) shows either the energy left after
  completion or the shortfall. It then shows the Spin or Activate form, or none
  once activated, plus Previous/Next gate and Close links.
- Every gate card name links to its detail view. The selected card is
  highlighted and omits its form, so the page shows each form only once.
- Tests: `LocalGalaxyGateDetailTests`. Redirect-client use is unverified; no
  Live post-company Galaxy Gates capture exists.

## Local Tech Factory page and action hardening (2026-10-24)

`GET /indexInternal.es?action=internalTechFactory` and its two actions now
match the hardened Galaxy Gates, Skylab and Assembly menus. No endpoints were
added or removed.

- The page already checked the query first: 400 (query), then 401 (session),
  then 403 (no valid company), with `Cache-Control: no-store`. Values with
  control characters now also return 400.
- `POST internalTechFactoryStart` / `internalTechFactoryClaim` require a query
  of exactly one `action` key in that casing. `ACTION=…` was accepted before;
  it and lowercased values now return 400 before the session is checked.
- Any unreadable form (cut-off multipart body, failing body stream) returns the
  HTML 400 page and starts or claims nothing. A missing session returns 401
  before the body is read.
- Error pages read "Tech Factory request failed (status)" and HTML-encode the
  message. Covered by `LocalTechFactoryHardeningTests`.

## Local Events action hardening (2026-10-24)

No endpoints changed. `POST /indexInternal.es?action=internalBattlePassClaim|internalExpeditionStart|internalExpeditionClaim` now requires the query to be exactly one `action` key in canonical lowercase casing (`LocalEventsAction.ValidActionQuery`). Before, `?ACTION=internalBattlePassClaim` passed because query-key lookup ignores case. A non-canonical key returns the HTML 400 "Invalid Events action." page before the session is checked. Wrong-case action values still return 404, as before. Form-read failures now also catch `InvalidOperationException` and return the HTML 400 "could not be read" page with no state change. A missing session still returns 401 before the body is read. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalEventsActionHardeningTests.cs`. These rules are emulator choices; no official Events action contract was captured.

## Local Pilot Sheet action query hardening (2026-10-24)

No endpoints changed. `POST /indexInternal.es?action=internalPilotSkillAllocate|internalPilotSkillReset` now requires the query to be exactly one `action` key in canonical lowercase casing (`LocalPilotSkillAction.ValidActionQuery`). Before, `?ACTION=internalPilotSkillAllocate` passed because query-key lookup ignores case. A non-canonical key returns the HTML 400 "Invalid Pilot Sheet action." page before the session is checked; non-POST methods still return 405 first, and wrong-case action values still return 404. Form-read failures now also catch `InvalidOperationException` and return the HTML 400 "could not be read" page with no rank or revision change. A missing session still returns 401 before the body is read. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalPilotSkillActionQueryTests.cs`. These rules are emulator choices; no official skill-tree contract was captured.

## Local Clan action query hardening (2026-10-24)

No endpoints changed. `POST /indexInternal.es?action=internalClanJoin|internalClanLeave` now requires the query to be exactly one `action` key in canonical lowercase casing (`LocalClanAction.ValidActionQuery`). Before, `?ACTION=internalClanJoin` passed because query-key lookup ignores case. A non-canonical key returns the HTML 400 "Invalid Clan action." page before the session is checked; non-POST methods still return 405 first, and wrong-case action values still return 400 as before. Form-read failures now also catch `InvalidOperationException` and return the HTML 400 "could not be read" page with membership unchanged. A missing session still returns 401 before the body is read. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalClanActionHardeningTests.cs`. These rules are emulator choices; no official Clan contract was captured.

## Local Messaging action query hardening (2026-10-24)

No endpoints changed. `POST /indexInternal.es?action=internalMessageSend|internalMessageDelete|internalFriendAdd|internalFriendRemove` now requires the query to be exactly one `action` key in canonical lowercase casing (`LocalMessagingAction.ValidActionQuery`). Before, `?ACTION=internalFriendAdd` passed because query-key lookup ignores case. A non-canonical key returns the HTML 400 "Invalid Messages action." page before the session is checked; non-POST methods still return 405 first, and wrong-case or repeated action values still return 404 as before. Form-read failures now also catch `InvalidOperationException` and return the HTML 400 "could not be read" page with mailbox, friends, and revision unchanged. A missing session still returns 401 before the body is read. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalMessagingActionQueryTests.cs`. These rules are emulator choices; no official Messaging contract was captured.

## Local Hangar action query hardening (2026-10-24)

No endpoints changed. `POST /indexInternal.es?action=internalDockEquipmentSet` and `POST /indexInternal.es?action=internalDockSelect` now require the query to be exactly one `action` key in canonical lowercase casing (`LocalHangarError.ValidActionQuery`). Before, `?ACTION=internalDockEquipmentSet` passed because query-key lookup ignores case. A non-canonical key returns the HTML 400 "Invalid equipment action." / "Invalid hangar action." page before the session is checked; non-POST methods still return 405 first. Equipment Set still requires the exact `internalDockEquipmentSet` value; Dock Select keeps its existing case-insensitive value routing. Form-read failures in both actions now also catch `InvalidOperationException` and return the HTML 400 "could not be read" page (Equipment Set previously said "Expected a form submission.") with assignments and the active slot unchanged. A missing session still returns 401 before the body is read. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalHangarActionQueryTests.cs`. These rules are emulator choices; no official Hangar mutation contract was captured.

## Local Shop buy and Auction action query hardening (2026-10-24)

No endpoints changed. `POST /indexInternal.es?action=internalShopBuy` now requires the query to be exactly one `action` key in canonical lowercase casing (`LocalShopPurchase.ValidActionQuery`). Before, `?ACTION=internalShopBuy` passed because the query had one key and key lookup ignores case. A non-canonical or repeated key returns the HTML 400 "Invalid Shop action." page before the session is checked; non-POST methods still return 405 first. Form-read failures in Shop buy and in `internalAuctionBid` / `internalAuctionClaim` now also catch `InvalidOperationException` and return the HTML 400 "could not be read" page without echoing the exception, so no purchase, bid, or claim is applied and the database is not touched. Auction already compared the query key ordinally, so only its catch changed. A missing session still returns 401 before the body is read. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalShopAuctionActionQueryTests.cs`. These rules are emulator choices; no official Shop or Auction mutation contract was captured.

## Local Quest, Voucher, Payment and Space Search action query hardening (2026-10-24)

No endpoints were added or removed. `POST internalQuestClaim`, `internalVoucherRedeem`, `internalPaymentClaim` and `internalSpaceSearchExplore` now require the query key to be spelled exactly `action`; `?ACTION=…`, `?Action=…` or a repeated mixed-case key returns HTML 400 (`no-store`) before the session lookup. A non-POST request still returns 405 first; Quest's 405 now includes `Allow: POST` like the other three. A form read that throws `InvalidOperationException`, `IOException` or `InvalidDataException` returns 400 "could not be read" without echoing the error and changes no state. A missing session still returns 401 before the body is read. Voucher, Payment and Space Search still accept an empty query (the dispatcher may route by form). Tests: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalRewardActionQueryTests.cs`.

### Account save hardening (menu-35, 2026-10-24)

`POST /indexInternal.es?action=internalAccountSave` now follows the same action-query and form-read rules as Quest claim, Voucher redeem, Payment claim and Space Search explore: the only accepted query key is the exact lowercase `action` (an empty query is still accepted for compatibility), and an `InvalidOperationException` while reading the form returns HTML 400 instead of a 500. No endpoints were added or removed. Tests: `LocalAccountSettingsActionHardeningTests`.

### Client and launcher query validation (menu-36, 2026-10-24)

No endpoints were added or removed. `GET /index.es?action=externalDownload` is now resolved by `LocalLauncherDownload.Resolve`. Any `action` value equal to `externalDownload` in any casing, under any key casing, reaches the launcher handler. Before, a request such as `?ACTION=externalDownload`, `?action=EXTERNALDOWNLOAD` or a repeated `action` fell through to the wildcard sign-in capture, which mints a new `dosid` cookie. Only the exact `action=externalDownload` returns the page. A wrong-case key or value, a repeated parameter, a key other than `action`/`from`/`prc`/`lang` (exact case), a value over 64 characters or a control character returns an HTML 400 `no-store` error page that links back to the canonical page. Neither response sets a cookie. Tests: `LocalLauncherDownloadValidationTests`.

### Auction signed form key (menu-37, 2026-10-24)

No endpoints were added or removed. `POST /indexInternal.es?action=internalAuctionBid` now takes exactly `item`, `amount` and `key`; `internalAuctionClaim` takes exactly `item` and `key`. Both Auction pages (the cache-backed `LocalAuction.Page` and the Postgres-backed `PageFromDatabase`) emit `key` as a hidden field made by `LocalAuctionAction.CreateFormKey(dosid, "bid"|"claim", lotId)`: `{nonce}.{unixSeconds}.{hex HMAC-SHA256(dosid, "auction:{operation}:{lot}:{nonce}:{issued}")}`, the same shape as the Quest claim key. The check runs after the session, field-set, lot-shape and bid-amount checks. An invalid, tampered, other-session, other-lot, wrong-operation or future-dated (more than 5 minutes) key returns HTML 400. A key older than one hour returns 409. These are emulator choices; the official Auction contract is unknown. Tests: `LocalAuctionFormKeyTests`. Older Auction tests sign their bodies through `AuctionTestForms.WithKey`.

### Support client links and navigation (menu-41, 2026-10-24)

No endpoints were added or removed. `GET /indexInternal.es?action=internalSupport` accepts a new `section=client-links` value. It lists the six `/unityApi/links.php` keys in captured order (`imprint`, `credits`, `forum`, `support`, `help`, `accountCenter`), using the same `ClientStubResponses.Links` values the JSON endpoint serves. Local destinations stay relative. External ones (imprint, board) get `rel="noopener noreferrer"`. The empty captured `credits` value is shown as not provided, not as a link. Section detail pages now carry `data-section`, a per-section `<title>`, and previous/next links in section order (no wrap-around). Validation is unchanged: the shared `LocalLinksRequest` 400/401/403/404 rules. Covered by `LocalSupportHardeningTests`.

### Launcher sections (menu-45, 2026-10-24)

No endpoints were added or removed. `GET /index.es?action=externalDownload` accepts an optional `section` of `overview`, `requirements`, `connection` or `troubleshooting`. The ids are exact, lowercase and emulator-defined. `overview` and no section both return the default page. The Connection section lists the captured client paths `/unityApi/login.php`, `/unityApi/links.php`, `/unityApi/news.php` and `/flashAPI/brNews.php`, plus each game server from `LocalMapsXml.Maps`. An empty, repeated or control-character section, or a `Section` key, returns 400. An unknown or wrong-case section returns 404 and is not repeated in the error page. All responses are still HTML `no-store` and set no cookie.

### Support session details (menu-49, 2026-10-24)

No endpoints or parameters were added or removed. `GET /indexInternal.es?action=internalSupport&section=account` and `section=security` now add a `<dl id="supportSession">` list built from the validated local session: pilot name (HTML-encoded), pilot ID and company display name. If no company is chosen, the list links to `internalCompanyChoose`. `security` also shows the sign-in and expiry times in UTC. The dosid and account GUID are never rendered. Other sections and the index are unchanged. This is emulator-defined content; no official Support page body was captured.

### Pilot Sheet skill details (menu-50, 2026-10-24)

No endpoints were added or removed. `GET /indexInternal.es?action=internalPilotSheet` accepts an optional `detail={skillId}` (exact, case-sensitive ids: `hull`, `shields`, `lasers`, `evasion`, `shieldEngineering`, `rocketFusion`). The key must be lowercase `detail`. An empty, repeated, differently cased (`Detail=`), over-64-character or control-character value returns HTML 400 before the session check. An unknown or differently cased id returns HTML 404 after the 401 session check and the company redirect, and the error page does not repeat the value. `skill=` on the page is still rejected (400). The detail panel `#pilotSkillDetail` shows rank, effect per rank, current and next-rank bonus, prerequisite (met/not met), skills it unlocks, a status, and the same signed allocate form as the table. Rows carry `data-skill`/`data-status` (`maxed`, `locked`, `no-points`, `available`), a Bonus column, and `#pilotSkillBonuses` lists active bonuses. Effect values are emulator-defined (+2% per rank for base skills, +1% evasion, +3% shield regeneration, +2% rocket damage); no Live Pilot Sheet contract was captured. Tests: `LocalPilotSheetDetailTests`.

### Hall of Fame pilot view and company standings (menu-51, 2026-10-24)

No endpoints were added or removed. `GET /indexInternal.es?action=internalHallofFame` accepts an optional `pilot={name}` (key must be lowercase `pilot`; value 1–32 ASCII letters, digits or hyphens, exact case, for example `Nova-MMO`). An empty, repeated, wrong-case-key, malformed or overlong value returns HTML 400. An unknown pilot, including `-TST` or wrong-case names, returns 404. `category`, `company` and `page` are validated first, and the view's Back to ranking link keeps them. The pilot view `#localHallOfFamePilot` lists the pilot's company and, per category (`data-category`, current one marked `class="current"`), overall position, in-company position, level and score. Every board row links its pilot name to this view, and each board adds `#rankingCompanyStandings` (MMO/EIC/VRU by total category score with each company's top pilot; the filtered company has `class="own"`). Session, 401, company-redirect and `no-store` handling are unchanged. All names and scores are emulator-defined; no Live ranking contract was captured. Tests: `LocalRankingsPilotTests`.

### Account center summary and reset (menu-52, 2026-10-24)

No endpoints were added or removed, and the `internalAccount` page query and `internalAccountSave` rules are unchanged. The Account page now sets `<html lang>` to the saved language (falling back to `es` for an unknown stored code), shows the company display name and code (or a Choose a company link when the session has none), and lists the current language name, sound, notifications and save count (`Defaults (never saved)` for revision 0, otherwise `Saved N time(s)`). When the preferences differ from the defaults (`es`, sound on, notifications on), a second form posts exactly `language=es`, `sound=true`, `notifications=true` and the current `rev` to `internalAccountSave`; a replayed reset returns 409 like any stale save. The page also links to Pilot Sheet, Logbook and `internalSupport&section=account`. It never shows the dosid or account GUID. Covered by `LocalAccountSettingsPageTests`. All of this is emulator-defined; no Live account-center response was captured.

### Payment package details (menu-53, 2026-10-24)

No endpoints were added or removed, and the `internalPaymentClaim` rules are unchanged. `GET /indexInternal.es?action=internalPayment` accepts an optional `detail={packageId}`. The key must be lowercase `detail` and the value a well-formed package id (1-32 ASCII letters, digits, `-`, `_`). An empty, repeated, differently cased (`DETAIL=`), malformed or overlong value returns HTML 400, like any other disallowed key; `package=` on the page is still 400. An unknown or differently cased id returns uncached HTML 404 after the session and company checks, without repeating the value. Ids come from the built-in catalog, or from the Postgres `payment` reward offers when present. The panel `#packageDetail` (`data-package`, `data-status` = `available`, `claimed` or `unavailable`) shows the package position (`N of M`), grant and asset (`Credits`, `Uridium` or the raw asset id), status, and for Credit packages the current balance (never below zero) and, if claimable, the balance after the claim. It has the same claim form as the card when available, Previous/Next links in catalog order, and a Close link. Each card carries `id="package-{id}"`, `data-status` and `data-asset`, and its name links to the detail view. `#paymentRemaining` totals the still-claimable packages per asset, or says that none are left. The page links to Vouchers and the economy Logbook. All package data is emulator-defined; no Live payment page was captured. Tests: `LocalPaymentDetailTests`.

### Command center sort order and countdown urgency (menu-54, 2026-10-24)

No endpoints were added. `GET /indexInternal.es?action=internalCommandCenter` now also accepts an optional `sort` (allowed keys: `action`, `category`, `item`, `from`, `prc`, `lang`, `sort`). Allowed values are lowercase `priority` (default; also used when the value is empty), `ending` (shortest captured countdown snapshot first, then priority, then id), and `name` (case-insensitive id). Any other value, including `Ending`, a trailing space, or a repeated `sort`, returns HTML 400 without repeating the value. A non-default sort is kept in the category tabs, card links, detail Back link, and Previous/Next links. The list view has a `#commandCenterSort` nav (current order marked `aria-current="true"`) and a `#commandCenterSummary` line naming the notice that ends soonest and counting notices ending within a day and permanent notices; both are omitted in the detail view, and the summary is omitted for an empty category. Each card has `data-urgency` (`ending-soon` < 1 day, `this-week` < 7 days, `running` < 365 days, otherwise `permanent`) and a matching label. The groups come from the captured countdown snapshots (the 755550769-second notices are treated as permanent); the thresholds are emulator-defined. The detail view shows `#commandCenterPosition` ("Notice N of M in {category}") in the selected order. Tests: `LocalCommandCenterSortTests`.

### Assembly status filters and recipe details (menu-55, 2026-10-24)

No endpoints were added, and the `internalAssemblyCraft` rules are unchanged. `GET /indexInternal.es?action=internalAssembly` now also accepts an optional `status` and `detail` (allowed keys: `action`, `from`, `prc`, `lang`, plus lowercase-only `status` and `detail`). `status` must be exactly `all` (default), `ready`, `missing` or `unavailable`; `detail` must be a well-formed recipe id (1-64 lowercase letters, digits, `_`, `-`). An empty, repeated, wrong-case or malformed value, or a wrong-case key (`Status=`, `DETAIL=`), returns HTML 400; `recipe=` on the page is still 400. An unknown `detail` id returns uncached HTML 404 after the session and company checks, without repeating the value. `#assembly-filters` shows per-status counts and marks the current filter with `aria-current="page"`; `#assembly-notice` still counts ready recipes across all of them, and `#assembly-empty` appears when nothing matches. Each row adds a Can craft column (the scarcest input divided by its need; 0 when disabled, `Unlimited` without inputs), and the recipe name links to `detail={id}`, keeping a non-default filter. The `#assembly-recipe` panel lists each input with Kind, Need, Have and Short, plus the output, status, `#assembly-max`, the signed craft form (none when disabled) and `#assembly-back`, which keeps the filter. All of this is emulator-defined; no Live Assembly response was captured. Tests: `LocalAssemblyViewTests`.

### Auction lot details (menu-68, 2026-10-24)

No endpoints were added, and the `internalAuctionBid`/`internalAuctionClaim` rules are unchanged. `GET /indexInternal.es?action=internalAuction` now also accepts an optional `lot={lotId}` (emulator-defined). The key must be exactly lowercase `lot`, appear once, and match the lot-id shape (1-64 `[a-z0-9_-]`); otherwise the page returns HTML 400 before the session check. An id that does not exactly (case-sensitive) match a Postgres lot returns HTML 404 after the session and company checks. The detail panel shows the lot name, position ("Lot N of M"), lot and item ids, opening, current and minimum bids, status, Credits, Credits left after the minimum bid (a raise only debits the difference above the held bid), an explanation line, the bid or claim form (none for disabled or claimed lots), and Previous/Next/Close links. Each list row links to its detail. Covered by `LocalAuctionDetailTests`.

## Menu detail-view routing order (menu-70, 2026-10-24)

`LocalMenuDetailRoutingTests` runs the real `GET /indexInternal.es` endpoint (`IndexInternalStartEndpoint`, built with FastEndpoints `Factory.Create` over an in-memory cache) for the detail views of Galaxy Gates (`gate`), Auction (`lot`), Vouchers (`voucher`), Assembly and Quests (`detail`), Skylab (`detail`) and Map overview (`map`). Only error paths are covered, because the 200 pages read Postgres balances. Behaviour changes:
- `internalVouchers`: a malformed `voucher` now returns HTML 400 **before** the session check (was 401 without a session). Wrong-case `VOUCHER`/`Filter` keys now return 400; before, they passed validation and were read as the parameter.
- `internalQuests`: the page query and a malformed `detail` are now checked **before** the session (400, then 401). Before, a missing session returned 401 first.
- Confirmed unchanged: Galaxy Gates, Auction, Assembly and Skylab return 400 for a malformed, repeated or wrong-case detail key without a session. A well-formed id without a session returns 401. Without a company, Galaxy Gates and Skylab return 403. Auction, Assembly, Vouchers, Quests and Map overview return `302` to `internalCompanyChoose`. An unknown well-formed gate returns 404 after the company check, without echoing the id or changing gate state. Map overview still resolves its query only for a signed-in pilot (401 first).

## Map overview routing order (menu-71, 2026-10-24)

`GET /indexInternal.es?action=internalMapOverview` now follows the same order as the other detail views: query validation (400), then session (401), then company (`302` to `internalCompanyChoose`), then map lookup (404). The session-independent checks moved into `LocalMapOverview.Validate`: only `action`, `map`, `from`, `prc` and `lang` are allowed, each once, at most 64 characters, no control characters, and `map` must be 1–4 ASCII digits with no leading zero. Before this change, a malformed or hostile query without a session got 401, and with a session but no company it got the redirect instead of 400. An unknown well-formed map (`4`, `999`) still returns a non-echoing 404, but only after the company check. Map overview keys stay case-insensitive by design (`MAP=5` → 200, asserted by `LocalMapOverviewValidationTests`), but a wrong-case `MAP` is still validated before the session. Covered by `LocalMenuDetailRoutingTests`; no Redirect-client or Live verification.

Menu-109: `internalMapOverview` accepts an optional `company={code}` filter (lowercase ASCII letters, at most 8; malformed 400 before the session, unknown 404 after it). The overview lists only that company's starter map and shows company tabs with map counts; `company` combined with a `map` that is not that company's starter map returns 404. Every map card shows `Starting companies: N`. No new route; emulator-defined, unverified in Redirect.

Menu-189: `internalMapOverview` query keys and the action value now match exactly. `MAP=`, `Company=`, `From=`, `PRC=`, `Lang=`, `ACTION=` and a wrong-case action value return the HTML 400 page before the session and company checks, without echoing the input. The route and successful responses are unchanged.

Menu-191: `internalDock` page query keys and the action value now match exactly. `ACTION=`, `From=`, `PRC=`, `Lang=`, a wrong-case action value and control characters in any value return the Hangar HTML 400 page (`no-store`, no `Location`). This happens before the session check and the pre-company `302`, without echoing the input. Canonical queries keep the captured pre-company `302` and the local page.

Menu-193: `internalAssembly` page query keys and the action value now match exactly. `ACTION=`, `From=`, `PRC=`, `Lang=` and a wrong-case action value (`action=INTERNALASSEMBLY`, `action=internalassembly`) return the Assembly HTML 400 page (`no-store`, no `Location`). This happens before the session check and the company-choice redirect, and the input is not echoed. Canonical queries, the view keys (`status`, `detail`, `crafted`) and `POST internalAssemblyCraft` are unchanged.

Menu-203 (2026-10-24): `GET /indexInternal.es?action=internalAssembly` also accepts an optional emulator-defined `q` (exact lowercase key, once). It is trimmed; a nonblank value must be 2-40 characters and cannot be combined with `detail`, otherwise HTML 400 before the session check. Empty or blank `q` shows the full list. No other route changed.

Menu-195: `internalQuests` page query keys and the action value now match exactly. `ACTION=`, `From=`, `PRC=`, `Lang=`, `Filter=` and a wrong-case action value (`action=INTERNALQUESTS`, `action=internalquests`) return the Quests HTML 400 page (`no-store`, no `Location`). This happens before the session check and the company-choice redirect, and the input is not echoed. Before, the shell keys were matched case-insensitively (`FROM=internalStart` was accepted). Canonical queries, the view keys (`filter`, `detail`, `claimed`) and `POST internalQuestClaim` are unchanged. Emulator-defined rule; Redirect client use is unverified.

## Command Center routing order (menu-72, 2026-10-24)

`GET /indexInternal.es?action=internalCommandCenter` now checks the query before the session, like Map overview and the other detail views: 400 (malformed query), then 401 (no or expired session), then a 302 to `internalCompanyChoose` (no company), then 404 (unknown category or notice, or a notice outside the selected category). `LocalCommandCenter.Validate` runs the session-free checks: known keys only, each once, at most 64 characters, no control characters (new: `from`/`prc`/`lang` such as `from=a%0Db` used to pass), `category`/`item` made of ASCII letters, digits, `_` or `-`, and `sort` exactly `priority`, `ending` or `name`. Before this change, a malformed query without a session got 401, and with a session but no company it got the redirect instead of 400. Every Command Center response (200, 400, 401, 404) is now `Cache-Control: no-store`. Keys stay case-insensitive (`SORT=ending` → 200). Covered by `LocalMenuDetailRoutingTests`; no Redirect-client or Live verification.

## Hall of Fame and Logbook routing order (menu-73, 2026-10-24)

`GET /indexInternal.es?action=internalHallofFame` and `action=internalLogbook` now check the query before the session, like Command Center and Map overview: 400 (malformed query), then 401 (no or expired session), then a 302 to `internalCompanyChoose` (no company), then 404 (Hall of Fame: unknown category, page or pilot; Logbook: unknown category, page or entry, or 403 for another account's entry). `LocalRankings.Validate` and `LocalLogbook.Validate` hold the session-free checks; keys stay case-sensitive. Before, a malformed query without a session got 401, and with a session but no company it got the redirect. Logbook skips the ledger read for an invalid query and now sends `Cache-Control: no-store` like Hall of Fame. Covered by `LocalMenuDetailRoutingTests`; no Redirect-client or Live verification.

## Logbook refusals before the ledger read (menu-74, 2026-10-24)

No endpoint changed. In `GET /indexInternal.es?action=internalLogbook`, after the 400, 401 and company checks, the endpoint now returns 404 for an unknown `category` and 403 for an `entry` whose `{gameUserId}` prefix is not the session's, without reading the economy ledger. Only then does it read the ledger for the list and detail pages and the remaining 404s (unknown own entry, entry not on `page`, page out of range). Expired sessions still get 401 first.

## Hangar equipment routing order (menu-76, 2026-10-24)

No endpoint changed. `GET /indexInternal.es?action=internalDockEquipment&slot={1|2}&configuration={1|2}` now validates the query before the session: malformed query 400 (also without a session or company), then 401, then 403 without a company, then the 200 page. All responses are `Cache-Control: no-store`. Tests: `LocalMenuDetailRoutingTests` (DockEquipment_*).

## Postgres-backed menu pages through the endpoint (menu-77, 2026-10-24)

No route changed. Bug fix: `EconomyStore.CraftCountAsync` cast Postgres `sum(bigint)` (a `numeric`, read as `decimal`) to `long` and threw `InvalidCastException`. `LocalHangarEquipmentStore.GetAsync` calls it, and `WriteEconomyPageAsync` calls that store for every Postgres-backed 200 menu page. The query is now `LEAST(COALESCE(sum(quantity),0), 9223372036854775807)::bigint`. New Testcontainers fixture `Integration/LocalMenuPagesPostgresTests` (Category `Integration`) drives the real `GET /indexInternal.es` with a migrated Postgres for these cases:
- `internalDockEquipment` 200 for all four slot/configuration pairs, with and without `from`/`lang`.
- `internalMessages` 200 for each folder and for opening seeded messages 1–3.
- `internalLogbook` page 1 returns 200. A page past the end and the caller's own missing entry return 404 after the ledger read.
- All 200 responses are `no-store` and carry the `economyBalances` wallet header.

Menu-78 (2026-10-24) extends the same fixture to five more menus, again with no route change. The fixture now also builds `LocalSkylabStore` and `LocalTechFactoryStore` on top of Postgres, and every case runs against the catalog that the migrations seed:
- `internalShop` returns 200 for all items, `category=Equipment`, lowercase `category=drones`, `item=pet_10`, and `category=Drones&item=drone_iris`. The last one shows the Iris limit (2 per account) and its low-stock notice. An `item` filter shows only that card. An unknown category, an unknown item, or an item outside the given category returns 404.
- `internalAuction` returns 200 for the lot list and for `lot=laser_lf3` / `lot=drone_flax`. `lot=pet_10` is not an auction lot and returns 404.
- `internalAssembly` returns 200 for `status=missing`, `status=ready` and `detail=lf3`/`shield`. `missing` shows the empty-filter row because the starter wallet covers all 3 seeded recipes. `tech_*` recipes never appear; `detail=tech_speed` and unknown ids return 404.
- `internalSkylab` returns 200 for the list and for `detail=prometium`/`terbium`. An unknown collector returns 404.
- `internalTechFactory` returns 200 for the list, `filter=affordable`/`shortfall` and `detail=tech_speed`/`tech_energy`. The Assembly recipe `lf3` and unknown `tech_*` ids return 404.
- Every 404 is `no-store` and does not render the wallet header.

Menu-79 (2026-10-24) covers the last five menus that had no endpoint test against Postgres. No route changed. The fixture now also builds `LocalClanStore` and `LocalPilotSkillStore`, so the only store it leaves out is account preferences:
- `internalGalaxyGates` returns 200 for the list (`Activated gates: 0 / 5`) and for `gate=alpha`/`epsilon`. `gate=zeta` returns 404.
- `internalClan` returns 200 for the list, `filter=recruiting`/`closed`, `detail=mmo-orbit` and `filter=closed&detail=mmo-elite`, and an MMO pilot never sees another company's clans. `detail=eic-orbit` (another company) and an unknown clan return 404.
- `internalPilotSheet` returns 200 for the list and `detail=hull`/`rocketFusion`. `detail=ammo` returns 404.
- `internalQuests` returns 200 for the list, `filter=in-progress` and the details of the migration-seeded offers `first_purchase`/`master_engineer`. A new pilot has every quest in progress, so an unknown quest, `filter=claimed&detail=first_purchase` and `filter=ready&detail=first_craft` return 404.
- `internalVouchers` returns 200 for the list, `filter=available` (`Redeemed 0 of 2 codes`), `filter=redeemed` and the seeded `TRAINING-500`/`HANGAR-750` details. An unknown code, the lowercase `training-500` (codes match exactly), and `filter=redeemed&voucher=TRAINING-500` return 404.
- As before, every 200 page is `no-store` and renders the wallet header, and every 404 is `no-store` and does not.

Menu-80 (2026-10-24) tests the reward POST handlers against migration-seeded Postgres offers (`LocalRewardRedeemPostgresTests`). No route changed:
- `internalVoucherRedeem` with `TRAINING-500`/`HANGAR-750` returns 303 to `internalVouchers` and credits 500/750 once, with one `voucher:{code}` ledger row. A repeat returns 409.
- A lowercase code is accepted for redeem (only the page `voucher=` detail matches exactly) and is keyed as the canonical `voucher:TRAINING-500`.
- After the menu cache is lost, the state read from the ledger still marks the code redeemed and a retry returns 409. Four concurrent redeems from separate caches give one 303 and three 409s.
- `code=UNKNOWN-1` and the payment id `code=starter` return 404, and a disabled voucher offer returns 410. None of them writes a ledger row.
- `internalPaymentClaim` with `starter`/`fleet` returns 303 to `internalPayment&claimed={id}` (Menu-120) and credits 1,000/5,000 once. A repeat from a fresh cache returns 409. `package=TRAINING-500` returns 404.

Menu-81 (2026-10-24) tests the Auction POST handlers against the migration-seeded Postgres lots (`laser_lf3` 700, `shield_sg3n_b02` 450, `drone_flax` 200, 300 s each) in `LocalAuctionPostgresTests`, using a controllable clock. No route changed:
- `internalAuctionBid` at the opening price returns 303 to `internalAuction&placed=laser_lf3` (Menu-124), debits the opening price, stores the bid and writes one `auction_bid` ledger row.
- A bid below the opening price, a raise under 10% (769 after 700), a bid over the wallet, and a bid on a disabled lot return 409 and write no ledger row. A raise to 770 debits only 70.
- `no_such_lot` returns 404 for both bid and claim.
- `internalAuctionClaim` returns 409 while the lot runs, without a bid, and when repeated. A bid after the lot ends (claimed or not) returns 409. A claim after the end grants one inventory item, also when served from a fresh menu cache.
- Four concurrent opening bids from separate caches give one 303 and three 409s, with a single debit. An expired form key returns 409 without a debit.

Menu-82 (2026-10-24) tests the Skylab POST handlers against migrated Postgres in `LocalSkylabPostgresTests`, using a controllable clock. No route changed:
- The first read imports 500 units and a level-1 `economy.skylab_collectors` row for each of `prometium`, `endurium` and `terbium`. All three rows share one `last_collected`.
- `internalSkylabUpgrade` at the current level returns 303 to `internalSkylab&upgraded={collector}` (Menu-121; plain `internalSkylab` before). It debits 100 × level of that collector's own resource and writes one `skylab_upgrade` ledger row. Two upgrades leave 200 endurium, so the level-3 upgrade (300) returns 409 "not enough resources".
- These upgrades return 409 with nothing charged: a stale level, a collector already at level 10, a replayed form key (also from a fresh cache), and an expired form key. `xenomit` returns 404. Four concurrent upgrades from separate caches give one 303 and three 409s, with a single debit.
- `internalSkylabCollect` returns 409 "Nothing to collect" before one whole minute. Otherwise it credits 10 × level per whole minute to each collector and moves `last_collected` forward by those minutes only, so partial minutes stay pending. One collect covers at most 60 minutes. A second form that still carries the old timestamp returns 409. After the menu cache is lost, the levels and timestamp are read back from Postgres and a collect still works.


Menu-83 (2026-10-24) tests the Tech Factory POST handlers against migrated Postgres in `LocalTechFactoryPostgresTests`, using a controllable clock. No route changed:
- The first read imports 1000 `tech_components`; the five seeded `tech_*` recipes cost 100-300 components and take 2-6 minutes.
- `internalTechFactoryStart` returns 303 to `internalTechFactory`, debits the recipe cost with one `tech_factory_start` ledger row and stores one `economy.tech_factory_jobs` row with `ready_at` = now + craft time.
- Start returns 409 with no debit while the slot is busy, with too few components ("N needed, M available"), for an expired form key, and for a replayed key even after the earlier job was claimed (from a fresh cache). Four concurrent starts from separate caches give one 303 and three 409s. `tech_xenomit` and unprefixed `targeting` return 404; a key signed for another recipe returns 400.
- `internalTechFactoryClaim` returns 409 before `ready_at` ("not ready until …") and for a job id that is not the active job. A ready claim credits the output resource once (`tech_factory_claim` +1), frees the slot, and works after menu-cache loss. Replayed and four concurrent claims credit once.

Menu-84 (2026-10-24) tests the Quests claim POST handler against migrated Postgres in `LocalQuestClaimPostgresTests`. No route changed:
- Progress comes from real `economy.purchases` (500-Credit `drone_flax` orders) and `economy.crafts` (`shield` recipe). Seeded quests: `first_purchase` (1 purchase, 250 Credits), `field_supplier` (3, 500), `bulk_buyer` (10, 1000), `first_craft` (1 craft, 400), `master_engineer` (5 crafts, 1500).
- `internalQuestClaim` at target returns 303 to `internalQuests&claimed={id}` (`no-store`; Menu-119) and writes one ledger row keyed `quest:{id}`. One short, or a new pilot, returns 400 "not complete" with no grant.
- A second claim, a claim from a fresh cache, and concurrent claims from four separate caches (one 303, three 409) return 409 "already claimed" without a second grant. A disabled offer returns 409 "unavailable" even when complete and claims normally once re-enabled.
- `unknown_quest` and the battle-pass ID `supplier` return 404. A key signed for another quest returns 400; an expired key returns 409. A claim leaves purchase and craft counts unchanged.

Menu-85 (2026-10-24) tests the Events battle pass and expedition POST handlers against migrated Postgres in `LocalEventsPostgresTests`. No route changed; migration `020_battle_pass_offers` was added:
- Before 020, Postgres seeded only the `supplier` and `engineer` battle pass offers, so the cache-only tiers `quartermaster` (3 purchases, 400 Credits), `artisan` (3 crafts, 600) and `tycoon` (10 purchases, 1000) returned 404 and the page showed 2 tiers. Migration 020 seeds them; the seeded offers now match `LocalEvents.Tiers` (names, Credits rewards, targets).
- `internalBattlePassClaim` at target returns 303 to `internalEvents` (`no-store`) and writes one ledger row keyed `battlepass:{tier}`. One short returns 409 "not ready" with no grant. A second claim, a claim from a fresh cache, and four concurrent claims from separate caches (one 303, three 409) grant once. A disabled tier returns 409 "not available" even when complete. The quest ID `first_purchase` and `unknown` return 404.
- `internalExpeditionStart` debits 100 Credits once (`expedition:start`); a repeat, a fresh-cache repeat, and three of four concurrent starts return 409. With fewer than 100 Credits it returns 409 "enough local credits" with no ledger row. `internalExpeditionClaim` returns 409 "not ready" before 2 minutes; after that it grants one SG3N-B02 shield once (`economy.item_grants` key `expedition:reward`), also from a fresh cache, where the timer is restored from the start row's database timestamp.

Menu-86 (2026-10-24) aligns the pre-company answer of six GET menu pages with Live. No route was added or removed.
- `internalShop`, `internalGalaxyGates`, `internalSkylab`, `internalTechFactory`, `internalClan` and `internalDockEquipment` used to return an HTML 403 page for a session without a company. They now return `302` with `Location: /indexInternal.es?action=internalCompanyChoose`, `no-store` and an empty body. That matches `PreCompanyMenuRedirect` (the Live gbl6 capture for `internalGalaxyGates`, `internalDock` and `internalPayment`) and the other local menus. The order is unchanged: malformed query 400, then missing session 401, then the redirect. POST actions still return 403 without a company.
- `EconomyPostgresTests.ConcurrentPurchases_DoNotOversell_AndRetryDoesNotDebitAgain` had a stale Flax count (3) from before `019_craft_requests` refused replayed crafts. It now expects 2 and checks that there is exactly one `economy.craft_requests` row.

Menu-87 (2026-10-24) adds Postgres integration tests for the Clan `internalClanJoin` and `internalClanLeave` POST handlers (`LocalClanPostgresTests`). No route or response changed.
Menu-88 (2026-10-24) adds Postgres integration tests for the Galaxy Gates `internalGalaxyGateSpin` and `internalGalaxyGateActivate` POST handlers (`LocalGalaxyGatePostgresTests`). No route or response changed.
Menu-89 (2026-10-24) moves Pilot Sheet skill state for `internalPilotSkillAllocate` and `internalPilotSkillReset` to Postgres (migration `021_pilot_skills`) and adds `LocalPilotSkillPostgresTests`. No route or response changed.
Menu-90 (2026-10-24) adds `LocalSpaceSearchPostgresTests` for `POST /indexInternal.es?action=internalSpaceSearchExplore` against migrated Postgres (single debit and grant, replica replay and concurrency, credit boundary, disabled reward item, invalid planets). No route or response changed.
Menu-91 (2026-10-24) moves Messages state for `internalMessageSend`, `internalMessageDelete`, `internalFriendAdd`, and `internalFriendRemove` to Postgres (migration `022_messaging`) and adds `LocalMessagingPostgresTests`. No route or response changed.
Menu-92 (2026-10-24) moves Account center preferences for `internalAccountSave` (and the `internalAccount` page and Unity login language that read them) to Postgres (migration `023_account_preferences`) and adds `LocalAccountPreferencesPostgresTests`. No route or response changed.

Menu-93 (2026-10-24) moves the Dock active hangar slot for `internalDockSelect` (and the `internalDock` page and Unity login hangar data that read it) to Postgres (migration `024_hangar_selection`) and adds `LocalHangarSelectionPostgresTests`. No route or response changed.

Menu-94 (2026-10-24) moves Dock equipment assignments for `internalDockEquipmentSet` (and the `internalDockEquipment` page that reads them) to Postgres (migration `025_hangar_equipment`) and adds `LocalHangarEquipmentPostgresTests`. No route or response changed.

Menu-95 (2026-10-24) makes the company choice behind `internalCompanyConfirm` atomic across API instances: on Postgres, `SetCompanyAsync` uses one conditional `UPDATE` so only one first choice is stored. Adds `CompanyChoicePostgresTests`. No route or response changed.

Menu-96 (2026-10-24) runs the Logbook's starter-balance import before its ledger read, so the first `internalLogbook` visit already lists the import rows it writes. No route, query rule, or status code changed. Covered by `Integration/LocalLogbookLedgerPostgresTests`.
Menu-97 (2026-10-24) adds cross-instance Postgres tests for Assembly crafting (`internalAssemblyCraft` store path): concurrent crafts never overspend inputs and one form key crafts once. No route, query rule, or status code changed. Covered by `Integration/LocalAssemblyCraftPostgresTests`.

## Unity news countdowns (Menu-99, 2026-10-24)

No endpoint was added or removed. `GET /unityApi/news.php` no longer returns the
frozen Live snapshot: each item's `seconds` is its captured value minus the time
elapsed since the capture session started (2026-10-21T22:46:36-05:00; the Live
request came within its first minute). An item that reaches zero restarts with
its captured duration, so the list keeps all 19 IDs in step with the
`flashAPI/brNews.php` layers. Live presumably rotates notices instead; that
restart rule is an emulator choice. A clock before the snapshot returns the
captured values. The query string and cookies are ignored, no cookie is set, and
each header is sent once with `no-store`. Tests: `UnityNewsEndpointHardeningTests`.

## Command center live countdowns (Menu-100, 2026-10-24)

No endpoints were added. `GET /indexInternal.es?action=internalCommandCenter` now computes each notice's countdown with the same rule as `/unityApi/news.php` (`CapturedNewsCatalog.RemainingSeconds` at the request time from the injected `TimeProvider`), so the page and `news.php` agree at the same instant. Each card has `data-seconds="{remaining}"`, the label reads `Countdown: {d}d {h}h {m}m ({n} seconds)`, and urgency, the "Ending soonest" summary, `sort=ending` and detail navigation use the live values. Calls without a clock (unit tests) still render `Countdown snapshot:` with the captured values. Tests: `LocalCommandCenterLiveCountdownTests`. Not checked against a Live page (none was captured).

## Launcher sign-in card (Menu-107, 2026-10-24)

No endpoints were added or removed. `GET /index.es?action=externalDownload` (every section) now reads an existing `dosid` cookie after the query passes validation and adds an emulator-defined `#launcherSession` card: `signed-out`, `no-company` (link to `internalCompanyChoose`) or `ready` (company name and local menu link). 400/404 responses skip the session lookup. The route still never sets a cookie and stays `Cache-Control: no-store`. Tests: `LocalLauncherDownloadSessionTests`.

Menu-108 (2026-10-24): `GET /indexInternal.es?action=internalLogbook` list pages add entry counts to the category tabs and a per-asset Local ledger summary (`#logbookSummary`: entries, signed net change, latest balance). This is emulator-defined HTML only; no route, query rule, or status code changed.

## Messages read state and Mark all read (Menu-112, 2026-10-24)

New local-only `POST /indexInternal.es?action=internalMessageReadAll` (form: exactly `rev`, `key`; key signed for action `internalMessageReadAll` with an empty target). It marks every received message read and redirects 303 to `internalMessages`. It returns 409 when nothing is unread, on a replay or stale revision, or when the form has expired, and 405/401/403/400 by the same rules as the other Messages actions. `GET internalMessages&message={id}` now marks an opened received message read (revision unchanged). The page shows `N unread` beside the Inbox tab and `class="unread"` rows. These are emulator-defined; no official contract was captured. Tests: `LocalMessagingReadStateTests`.

## Account save confirmation (Menu-113, 2026-10-24)

A successful `POST /indexInternal.es?action=internalAccountSave` (including the Reset to defaults form and legacy forms without `rev`) now redirects 303 to `/indexInternal.es?action=internalAccount&saved={revision}`, where `revision` is the account's revision after the save (1 after the 1,000,000 wrap). Conflicts (409) and errors set no `Location`. `GET internalAccount` accepts the new optional `saved` key: exactly lowercase `saved`, once, 1-7 ASCII digits from 1 to 1,000,000 (no sign, spaces, decimals, non-ASCII digits or zero); `SAVED`, `Saved`, a repeat or any other value is HTML 400 before the session check. When `saved` equals the current revision the page shows `#accountSaved` ("Preferences saved (change N)."); otherwise (another tab saved since, or an old bookmarked URL) it shows the neutral `#accountSavedStale` notice and never claims a save. Without `saved` there is no notice. Emulator-defined HTML only; covered by `LocalAccountSettingsSavedNoticeTests`.

## Voucher redeem confirmation (Menu-114, 2026-10-24)

No endpoints were added or removed. A successful local-only `POST /indexInternal.es?action=internalVoucherRedeem` now redirects 303 to `/indexInternal.es?action=internalVouchers&redeemed={id}` (canonical code id; plain `internalVouchers` if the id is not a well-formed code). `GET /indexInternal.es?action=internalVouchers` accepts an optional case-exact `redeemed` parameter: malformed, repeated, wrong-case key, or combined with `voucher` returns HTML 400 before the session check; an unknown or wrong-case code returns 404; otherwise the list shows `#voucherRedeemedNotice` (`data-redeemed="true"` with reward and Credits balance, or a neutral `false` notice when the code is not redeemed on this account). Emulator-defined; no official voucher endpoint or confirmation was captured. Tests: `LocalVoucherRedeemedNoticeTests`.

## Command center urgency filter (Menu-115, 2026-10-24)

No endpoints were added. `GET /indexInternal.es?action=internalCommandCenter` now accepts an optional `urgency` (allowed keys: `action`, `category`, `item`, `from`, `prc`, `lang`, `sort`, `urgency`). Allowed values are exactly `ending-soon`, `this-week`, `running` and `permanent` (the existing card `data-urgency` groups at the request time); an empty value means no filter. Any other value (wrong case, `ending_soon`, trailing space, control characters, repeated key) and `urgency` together with `item` return HTML 400 from `LocalCommandCenter.Validate`, before the session check, without echoing the value. The list view has a `#commandCenterUrgency` nav ("Show: Any (N)" plus one link per group with its count in the current category, current choice marked `aria-current="true"`); it keeps the category and sort. A selected filter is kept in the category tabs and sort links, not in card/detail links. A group with no notices shows "No notices match this urgency filter."; the empty-category message is unchanged. Unknown category still returns 404. The filter is emulator-defined (no Live page was captured). Tests: `LocalCommandCenterUrgencyFilterTests`.

## Support related Help topics (Menu-116, 2026-10-24)

No endpoints or parameters were added or removed; `internalSupport` still rejects `q` and `category`. The `getting-started`, `account`, `community`, `payment` and `security` sections of `GET /indexInternal.es?action=internalSupport&section=…` now render a `<ul id="supportRelatedTopics">` list of `<li data-topic="{id}">` links to the existing `GET /indexInternal.es?action=internalHelp&topic={id}` pages (emulator-defined mapping, see `LocalSupport.RelatedHelpTopics`). No Live Support page was captured; Redirect client use is unverified.

## Hangar equipment saved notice and loadout summary (Menu-118, 2026-10-24)

A successful `POST /indexInternal.es?action=internalDockEquipmentSet` (including `quantity=0`) now answers `303` to `/indexInternal.es?action=internalDockEquipment&slot={1|2}&configuration={1|2}&saved={item}` instead of the page without `saved`. `GET …internalDockEquipment` accepts an optional lowercase `saved` key, once, with the same 1-64 character `[A-Za-z0-9_.-]` pattern as the form's `item`; an empty, repeated, differently cased (`SAVED`, `Saved`) or malformed value returns HTML 400 before the session check, without echoing it. When `saved` names an owned item, the page shows `<p id="equipment-notice" role="status" data-item data-quantity>` with the item's current quantity in that configuration ("Saved: 3 × LF-3 laser equipped in configuration 2." or "… is no longer equipped in configuration N."); an unowned or unknown id shows no notice. Failed changes (400/409) still have no `Location`. Every page also renders `<p id="loadout-summary">` with equipped units per inventory category (Equipment, Drones, PET) for the selected slot and configuration. Text and markup are emulator-defined; no Live equipment page was captured and Redirect client use is unverified. Tests: `LocalHangarEquipmentSavedNoticeTests`.

## Quest claim confirmation (Menu-119, 2026-10-24)

A successful `POST /indexInternal.es?action=internalQuestClaim` now answers `303` to `/indexInternal.es?action=internalQuests&claimed={questId}` (previously plain `internalQuests`). Failed claims still return their HTML error page without `Location`.

`GET /indexInternal.es?action=internalQuests` accepts one new optional key, `claimed` (exact lowercase key, once, non-empty, lowercase ASCII letters, digits and `_`, at most 64 characters). An empty, repeated, wrong-case or malformed value returns HTML 400 (`no-store`) before the session lookup and is not echoed. When the pilot has claimed that quest the page shows `#questClaimed` (`role="status"`, `data-quest`) with the quest name and catalog/offer reward; an unknown, unclaimed or other-account quest renders nothing. Route, key, and text are emulator-defined; no Live quest page was captured. Tests: `LocalQuestClaimedNoticeTests`.

## Payment claim confirmation (Menu-120, 2026-10-24)

A successful `POST /indexInternal.es?action=internalPaymentClaim` now answers `303` to `/indexInternal.es?action=internalPayment&claimed={packageId}` (previously plain `internalPayment`). Failed claims (400/401/403/404/405/409/410) still return their HTML error page without `Location`.

`GET /indexInternal.es?action=internalPayment` accepts one new optional key, `claimed`, with the same rules as `detail`: exact lowercase key, once, a well-formed package id (1-32 ASCII letters, digits, `-`, `_`). An empty, repeated, wrong-case or malformed value returns HTML 400 like any other disallowed key, and is not echoed. When the account has claimed that package, the page shows `#paymentClaimed` (`role="status"`, `data-package`) above the remaining summary: "Package claimed: {name} · {amount} {asset} added to your account.", plus "Balance: N local credits." (never below zero) for Credit packages. Name and amount come from the built-in catalog or the Postgres `payment` offer, never from the link, and the notice still appears if the offer is disabled later. An unknown, unclaimed, differently cased or other-account id renders nothing (no 404). It combines with `detail` and also renders on the proxy page. Route, key and text are emulator-defined; no Live payment page was captured. Tests: `LocalPaymentClaimedNoticeTests`.

## Skylab action confirmation (Menu-121, 2026-10-24)

A successful `POST /indexInternal.es?action=internalSkylabUpgrade` now answers `303` to `/indexInternal.es?action=internalSkylab&upgraded={collector}`, and a successful `internalSkylabCollect` answers `303` to `/indexInternal.es?action=internalSkylab&collected=1` (both previously plain `internalSkylab`). Failed actions (400/401/403/404/405/409) still return their HTML error page without `Location`.

`GET /indexInternal.es?action=internalSkylab` accepts two new optional, mutually exclusive keys. `upgraded` follows the `detail` rules: exact lowercase key, used once, 1-32 lowercase ASCII letters. `collected` must be exactly `1`. Wrong-case, repeated, empty or malformed values, or both keys together, return HTML 400. The page renders `#skylabActionNotice` (`role="status"`) above the detail panel, using persisted collector state only. An upgrade shows the level, output and stock (and "Maximum level reached." at level 10). A collect lists the current stock of all three collectors. An unknown collector, or one still at level 1, renders nothing (no 404). Both keys combine with `detail`. Route keys and text are emulator-defined; no Live Skylab page was captured. Tests: `LocalSkylabActionNoticeTests`.

### Menu-122: Assembly craft confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalAssemblyCraft` now answers 303 with `Location: /indexInternal.es?action=internalAssembly&crafted={recipeId}`. `GET ...?action=internalAssembly` accepts an optional lowercase `crafted` key validated like `detail`; when the account has crafted that recipe at least once (Postgres `economy.crafts`), `#assembly-crafted` confirms the recipe name, outputs, total crafts and remaining crafts. Emulator-defined; no Live Assembly response was captured. Tests: `LocalAssemblyCraftedNoticeTests`.

### Menu-123: Tech Factory start and claim confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalTechFactoryStart` now answers 303 with `Location: /indexInternal.es?action=internalTechFactory&started={recipeId}`, and a successful `internalTechFactoryClaim` with `...&claimed={recipeId}` (the claimed job's recipe; a legacy job id that fails the recipe-id rules falls back to the plain page). `GET ...?action=internalTechFactory` accepts at most one of `started` or `claimed`, validated like `detail`. `#techFactoryStarted` appears only while the saved job matches the recipe; `#techFactoryClaimed` only when the account owns produced output of that recipe. Emulator-defined; no Live Tech Factory response was captured. Tests: `LocalTechFactoryActionNoticeTests`.

### Menu-124: Auction bid and claim confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalAuctionBid` now answers 303 with `Location: /indexInternal.es?action=internalAuction&placed={lotId}`, and a successful `internalAuctionClaim` with `...&claimed={lotId}`; failed actions keep the HTML error page and no `Location`. `GET ...?action=internalAuction` accepts at most one of exact-case `placed` or `claimed`, each once, validated like `lot` (1–64 `[a-z0-9_-]`), otherwise 400. `#auctionPlaced` appears only when the account's saved Postgres bid on that lot is above 0 and unclaimed (held bid, timer, next minimum, credits left); `#auctionClaimed` only when the saved lot is claimed (price paid, item moved to the Hangar). Text comes from saved state, never the link, and is HTML-encoded. Emulator-defined; no Live Auction response was captured. Tests: `LocalAuctionActionNoticeTests`.

### Menu-125: Galaxy Gates spin and activation confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalGalaxyGateSpin` now answers 303 with `Location: /indexInternal.es?action=internalGalaxyGates&spun={gateId}`, and a successful `internalGalaxyGateActivate` with `...&activated={gateId}`; failed actions keep the HTML error page and no `Location`. `GET ...?action=internalGalaxyGates` accepts at most one of exact-case `spun` or `activated`, each once, validated like `gate` (1–32 lowercase ASCII letters), otherwise 400; they combine with `gate`. A well-formed id outside the catalog renders the page without a notice. `#gateSpun` appears only while the saved gate is not activated (parts owned/required, missing parts or "Activate the gate now", energy left); `#gateActivated` only when the saved gate is activated (activated count of 5). Text comes from saved state, never the link. Emulator-defined; no Live Galaxy Gates response was captured. Tests: `LocalGalaxyGateActionNoticeTests`.

### Menu-126: Clan join and leave confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalClanJoin` now answers 303 with `Location: /indexInternal.es?action=internalClan&joined={clanId}`, and a successful `internalClanLeave` with `...&left={clanId}`; failed actions keep the HTML error page and send no `Location`. `GET internalClan` accepts `joined` or `left` (exact lowercase keys, once each, never both, same well-formed clan-id rule and 64-character limit as `detail`; combinable with `detail` and `filter`), anything else is the HTML 400 page. The notice (`#clan-notice`, `role="status"`, `data-notice="joined|left"`) is built from the saved membership, not from the link: "You joined [MG] Mars Guard. Members: 43." appears only while the pilot belongs to that clan of their current company; "You left [MG] Mars Guard." appears only once the membership differs, adding "You can join a recruiting clan now." when the pilot has no clan, and naming "your previous clan" for ids outside the directory. These parameters and texts are emulator-defined; no Live Clan form was captured.

### Menu-127: Pilot skill training and reset confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalPilotSkillAllocate` now answers 303 with `Location: /indexInternal.es?action=internalPilotSheet&trained={skillId}`, and a successful `internalPilotSkillReset` with `...&reset=1`; failed actions (400/401/403/404/405/409) keep the HTML error page and send no `Location`. `GET internalPilotSheet` accepts `trained` (exact lowercase key, a lowercase ASCII letter followed by ASCII letters or digits, max 64 characters) or `reset` (exactly `1`), once each and never both, combinable with `detail`; anything else is the HTML 400 page. The notice (`#pilotSkillTrained` with `data-skill`, or `#pilotSkillReset`, both `role="status"`) is built from the saved ranks, not from the link: the training notice names the skill, rank, bonus and points left, adds "Maximum rank reached." at the cap and "Unlocked: {skill}." when the rank equals a dependent skill's requirement, and appears only while the skill has at least one rank; well-formed unknown ids render no notice. The reset notice appears only while no points are spent. Parameters and texts are emulator-defined; no Live Pilot Sheet form was captured and Redirect client behavior is unverified.

### Menu-128: Space Search explore confirmation (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalSpaceSearchExplore` now answers 303 with `Location: /indexInternal.es?action=internalSpaceSearch&explored={planetId}`; failed explores (400/401/403/404/405/409) keep the HTML error page and send no `Location`. `GET internalSpaceSearch` accepts `explored` (exact lowercase key, once, 1-32 lowercase ASCII letters), combinable with `filter` and `planet`; other casing, repeats or malformed values are the HTML 400 page. The notice (`#spaceSearchExplored`, `role="status"`, `data-planet`) is built from the saved search state, not from the link: it names the planet, the reward and owned quantity, the cost and balance, and the planets left (with the credit shortfall for the next search, or "Every planet has been searched."). Well-formed unknown or unsearched planets render no notice. Parameters and texts are emulator-defined; Redirect client behavior is unverified.

### Menu-129: Events battle pass and expedition confirmations (2026-10-24)

No endpoints were added. A successful `POST /indexInternal.es?action=internalBattlePassClaim` now answers 303 with `Location: /indexInternal.es?action=internalEvents&claimed={tierId}`, `internalExpeditionStart` with `...&expedition=started`, and `internalExpeditionClaim` with `...&expedition=claimed`; failed actions (400/401/403/404/405/409) keep the HTML error page and send no `Location`. `GET internalEvents` accepts `claimed` (exact lowercase key, once, tier-shaped: 1-64 ASCII letters, digits, `_` or `-`) or `expedition` (exact lowercase key, once, exactly `started` or `claimed`), never both, combinable with `detail`, `from`, `prc` and `lang`; other casing, repeats or malformed values are the HTML 400 page. The notice (`#eventsNotice`, `role="status"`, with `data-tier` or `data-expedition`) is built from the saved event state, not from the link: the claim notice names the tier, its reward (credits, or the admin offer's amount and asset), the balance and "Tiers claimed: N of M", then "Next ready: {tier}." or "Every tier has been claimed."; it appears only while the tier is claimed. The launch notice shows the cost, the ready time and minutes left (or "It is ready to claim now.") and the balance, only while an unclaimed expedition is running or ready. The reward notice shows the shield and owned quantity, only once the expedition is claimed. Well-formed unknown or unclaimed tiers render no notice. Parameters and texts are emulator-defined; no Live Events form was captured and Redirect client behavior is unverified.

### Menu-130: Messaging confirmations (2026-10-24)

No endpoints were added. A successful Messages action now answers 303 with a confirmation key on `/indexInternal.es?action=internalMessages`: `internalMessageSend` adds `&sent={newMessageId}`, `internalMessageDelete` `&deleted={messageId}`, `internalFriendAdd` `&added={pilotId}`, `internalFriendRemove` `&removed={pilotId}`, and `internalMessageReadAll` `&readall=1`. Failed actions (400/401/403/404/405/409) keep the HTML error page and send no `Location`. `GET internalMessages` accepts at most one of these keys (exact lowercase, once; ids canonical 1-9 digits without sign or leading zero, `readall` exactly `1`), combinable with `folder`, `message`, `from`, `prc` and `lang`; two keys together, other casing, repeats or malformed values are the HTML 400 page. The notice (`#messagingNotice`, `role="status"`) is built from the saved mailbox, never from the link, and is HTML-encoded: "Mail sent to {pilot}: "{subject}". Sent folder: N. Mailbox: N / 50." (plus a full-mailbox warning) only while that sent message exists; "Message deleted. Inbox: N, Sent: N. Mailbox: N / 50." only while the id was issued and is gone; "{pilot} ({COMPANY}) added to your friends. Friends: N / 20. You can now send {pilot} mail." only while the pilot is a friend (plus "Your friend list is full." at 20); "{pilot} removed from your friends. Friends: N / 20." only while the pilot is not a friend (plus "Add a friend to send mail." at zero); "All N received messages marked read." only while nothing is unread. Parameters and texts are emulator-defined and Redirect client behavior is unverified. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalMessagingNoticeTests.cs`.

### Menu-131: Company confirmation welcome page and shell 401 body (2026-10-24)

No endpoints or routes were added. `GET /indexInternal.es?action=internalWebGL&from=internalCompanyChoose` (the redirect target of a successful `internalCompanyConfirm`) now renders a welcome page (`#companyConfirmation`, `data-company`) built from the session's stored company, never from the link: display name, "Welcome to {CODE}" with the permanence notice, a fact list (`#companyFacts`: code, faction number, starter map as `{faction}-1 (map {id})`, game server from the `/xml/maps.php` catalogue), a TST-only `#companyTestNotice`, and `#companyNextSteps` links to `internalDock`, `internalShop`, `internalMapOverview&map={starterMap}` and `internalHelp&topic=company`, plus `Continue to game` (`internalWebGL&from=internalStart`, now HTML-escaped). An `internalWebGL` request without a live session still returns 401, now with an HTML `text/html; charset=UTF-8` body (`#sessionExpired`) linking to `/index.es?action=externalHome` instead of an empty body; it stays `no-store`. Texts are emulator-defined; Redirect client behavior is unverified. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalCompanyConfirmationPageTests.cs`.

### Menu-132: Main menu shell pilot card and grouped navigation (2026-10-24)

No endpoints or routes were added. The authenticated shell (`GET /indexInternal.es?action=internalWebGL` with a live session and selected company, not `from=internalCompanyChoose`) now shows a pilot card (`#pilotCard`, `data-company`) above the menu. The card has the HTML-encoded session username (`#pilotName`, `Pilot` when blank), the game ID (`#pilotGameId`, only when positive), the company code (`#pilotCompany`) and the home map as `{faction}-1 (map {id})`, linked to `internalMapOverview&map={starterMap}` (`#pilotHomeMap`). Every value comes from the session and the stored company. The menu (`<nav id="mainMenu" data-count="25">`) is grouped into five emulator-defined sections (`.menuSection#menu_{hangar|activities|pilot|info|account}`, each with `data-count`). It now also links `internalForum` and `internalHelp`, which were reachable before but missing from the shell. Every href stays in the `href="/indexInternal.es?action={action}"` form, each menu is linked exactly once, and Log out is last. The grouping and texts are emulator-defined; the official shell layout is uncaptured and Redirect client behavior is unverified. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalMainMenuShellTests.cs` (12 tests). The rendered page is tested on its own, not through the endpoint, because that path needs the equipment and economy stores.

### Menu-133: Dock ship detail view (2026-10-24)

No new action. `GET /indexInternal.es?action=internalDock` accepts an optional `ship={1|2}` (exact lowercase key, once, canonical value; it may be combined with `activated`, `from`, `prc` and `lang`). Anything else returns the existing HTML 400 `no-store` page before the page renders. With a valid `ship`, the card list is replaced by `<section id="dock-ship" data-slot data-active>`. It holds the slot's tooltip icon, a `#dock-ship-specs` table (`data-spec` = `hit-points`, `speed`, `cargo`, `lasers`, `generators`, `extras`, `hangar`, `map`, `model`) and either `data-status="active"` or `data-status="inactive"` with the same session-signed `internalDockSelect` form. It also links to `internalDockEquipment&slot={n}&configuration=1`, to the other slot and to the list. Each list card now has a `Details` link. Hangar ID, ship ID, model ID and icon come from the same `UnityHangar` data as `/unityApi/login.php`. Hit points, speed, cargo and slot counts are emulator-defined, because no Live ship stats were captured. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalHangarShipDetailTests.cs`.

### Menu-134: Equipment slot limits (2026-10-24)

No routes or query rules changed. `POST /indexInternal.es?action=internalDockEquipmentSet` now also returns `409` when the change would put more laser units than `LaserSlots`, or more shield units than `GeneratorSlots`, into one ship configuration (`LocalHangars.Spec`: slot 1 5/12, slot 2 1/1; emulator-defined). Lowering an assignment is always accepted. `GET internalDockEquipment` adds `<p id="slot-capacity">` with one `<span data-slot-kind="laser|shield" data-used data-capacity>` per kind, `<p id="slot-warning" role="alert">` when over capacity, and per-row input `max` capped at the free slots. `LocalEquipmentItem` gains an optional `Type` (catalog type); the Postgres read path fills it. Coverage: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalHangarSlotCapacityTests.cs`.
### Menu-140: local Voucher search (2026-10-24)

`GET /indexInternal.es?action=internalVouchers` accepts optional `q` to search voucher IDs and admin offer names without changing redemption. It is case-insensitive and combines with `filter` and `voucher`; list tabs, detail navigation, and Close preserve the search. The exact lowercase key is accepted once, with 1–32 ASCII letters, digits, spaces, or hyphens and no leading or trailing spaces. A malformed, repeated, or wrong-case key returns the HTML 400 page before the session check. A valid search with no matches renders the empty list; a detail outside the search returns 404. This is an emulator-defined page parameter with no captured official post-company request. Covered by `LocalVoucherSearchTests`.

### Menu-143: local Clan search (2026-10-24)

`GET /indexInternal.es?action=internalClan` accepts optional `q` to search the pilot's company clans by id, tag, name, or focus text, case-insensitively. No action changed. The exact lowercase key is accepted once, with 1–32 ASCII letters, digits, spaces, or hyphens and no leading or trailing spaces. Wrong-case, repeated, empty, or malformed `q` returns the HTML 400 page before the session check. A `detail` clan outside the results returns 404. The search combines with `filter`. Tab counts, row links, the detail panel position (`Clan N of M`), Previous/Next, and Close use the search results; share and size rank still compare all company clans. The page has a GET search form (`#clan-search`), a result line (`#clan-search-result`), and a Clear search link that keeps the filter. With no results, the page shows "No clans match this search." Covered by `LocalClanSearchTests` and `LocalMenuDetailRoutingTests`. The search contract is emulator-defined.

### Menu-144: local Auction status filter (2026-10-24)

`GET /indexInternal.es?action=internalAuction` accepts an optional exact-case `filter`: `all` (default), `open`, `claimable`, `claimed`, `ended` (timer ended without a bid), or `unavailable` (lot disabled). No POST action changed. Each lot has one status: disabled first, then claimed, then the timer. A repeated, wrong-case, empty, or malformed `filter` (not 1–16 lowercase letters or hyphens) returns the HTML 400 page before the session check. A well-formed unknown filter returns 404 after the session and company checks and is not echoed. The page shows counted tabs (`#auction-filters`, `data-filter`, current tab `aria-current="page"`), and rows carry `data-lot` and `data-status`. An empty filter shows "No lots match this filter." (`#auction-empty`). A `lot` outside the filter returns 404. Detail links, the panel position (`Lot N of M`), Previous/Next, and Close keep the filter. The Credits line and the `placed`/`claimed` notices still use every lot. Covered by `LocalAuctionFilterTests` and `LocalMenuDetailRoutingTests`. The filter contract is emulator-defined.

### Menu-145: local Galaxy Gates status filter (2026-10-24)

`GET /indexInternal.es?action=internalGalaxyGates` accepts an optional exact-case `filter` (emulator-defined): `all` (default), `activated`, `ready` (all parts collected, not yet activated), or `incomplete` (parts missing, with or without energy). No action changed. A malformed value (empty, repeated, wrong-case key or value, anything but 1–16 lowercase ASCII letters) returns 400 before the session check. A well-formed unknown filter, or a `gate` detail outside the selected filter, returns HTML 404 without echoing input. The page shows counted tabs (`#gate-filters`, `data-filter`/`data-count`, counts over all five gates, current tab `aria-current="page"`), lists only matching cards, and shows `#gate-empty` "No gates match this filter." when none match. Card, Previous/Next and Close links keep a non-`all` filter, and the detail position ("Gate N of M") and navigation follow the filtered list. Covered by `LocalGalaxyGateFilterTests` and `LocalMenuDetailRoutingTests`.

### Menu-146: local Payment status filter (2026-10-24)

`GET /indexInternal.es?action=internalPayment` accepts an optional exact-case `filter` (emulator-defined; no Live payment list was captured): `all` (default), `available`, `claimed` (claimed by this account, even if the offer was later disabled), or `unavailable` (disabled and not claimed). The claim action and `internalPaymentProxy` (always `all`) are unchanged. After the session/company check, a malformed value (empty, repeated, wrong-case key or value, anything but 1–16 lowercase ASCII letters) returns the HTML 400 "Invalid Payment page request."; a well-formed unknown filter, or a `detail` package not listed under the selected filter, returns HTML 404 without echoing input. The page shows counted tabs (`#paymentFilters`, `data-filter`, current tab `aria-current="page"`, counts over the whole catalog), lists only matching cards, sets `main[data-filter]` and the title `Payment ({filter}) - DarkOrbit`, and shows `#paymentEmpty` "No {filter} packages." when none match. The claimed summary and "Still available" totals stay unfiltered. Card, Previous/Next and Close links keep a non-`all` filter, and the detail position ("N of M") follows the filtered list. Covered by `LocalPaymentFilterTests` and `LocalMenuDetailRoutingTests`.

### Menu-147: local Pilot Sheet skill filter (2026-10-24)

`GET /indexInternal.es?action=internalPilotSheet` accepts an optional exact-case `filter`. The values are emulator-defined because no Live Pilot Sheet was captured: `all` (default), `trainable` (a point can be allocated now), `trained` (rank ≥ 1), `maxed` (maximum rank), and `locked` (prerequisite not met). A skill can match more than one filter. The page checks the query before the session. An empty or repeated filter returns HTML 400 "Invalid Pilot Sheet page request." The same applies to a wrong-case key such as `Filter`/`FILTER`, or any value other than 1–16 lowercase ASCII letters. After the 401 session check and the company redirect, a well-formed unknown filter returns HTML 404. A `detail` skill outside the selected filter, judged by the saved ranks, also returns 404. The 404 pages do not echo input. The page shows counted tabs (`#pilot-skill-filters`, `data-filter`, `data-count`, current tab `aria-current="page"`; counts cover all six skills), lists only matching rows, and replaces the table with `#pilotSkillEmpty` "No skills match the {label} filter." when none match. The point totals, Reset form, and active bonuses stay unfiltered. Row links, Previous/Next skill (`#detailPreviousSkill`/`#detailNextSkill`, new and within the filtered list) and Close keep a non-`all` filter. Requires/Unlocks links drop it. The allocate/reset POST contracts and their success redirects are unchanged. Tests: `LocalPilotSheetFilterTests`.

### Menu-148: local Skylab collector filter (2026-10-24)

`GET /indexInternal.es?action=internalSkylab` accepts an optional exact-case `filter`. The values are emulator-defined because no Live Skylab page was captured: `all` (default), `upgradable` (stock covers the next upgrade, 100 × level), `saving` (more stock needed) and `maxed` (level 10). Every collector has exactly one non-`all` status, shown as `data-status` on its row. The page shows counted tabs (`#skylabFilters`), only matching rows, and `#skylabEmpty` when none match; the status line, summary, notices and Collect form are not filtered. Detail, Previous/Next and Close links keep the filter, and Previous/Next move within the filtered list. Order of checks: malformed filter (empty, repeated, wrong-case key or value, not 1–16 lowercase letters) → HTML 400 before the session; no session → 401; no company → 302 to company choice; unknown filter → 404; unknown collector → 404; collector outside the filter (saved state) → 404. Error pages are `no-store` and never echo the input. Upgrade and Collect POSTs are unchanged. Tests: `ThDarkOrbitEmulator.Tests/ClientTesting/LocalSkylabFilterTests.cs`.

### Menu-161: Shop item detail from saved catalog data (2026-10-24)

No endpoint or query rule changed. `GET /indexInternal.es?action=internalShop&item={listingId}` now shows `#shopItemDetails` with listing and item IDs, type, rarity, enabled state, current stock, remaining per-account purchase allowance, optional restock interval and amount, and scalar item stats from the admin-edited JSON object (`#shopItemStats`). Nested stats are omitted; an empty object shows "No item stats recorded." The back link retains category and search. Purchase forms still use the same signed currency-specific keys and transactional POST. Values are HTML-encoded, and the list remains compact. These detail fields are local emulator data; no post-company Live Shop response or Redirect verification exists. Tests: `LocalShopItemDetailsTests`.

### Menu-181: Support rejects an empty section (2026-10-24)

No endpoint was added or removed. `GET /indexInternal.es?action=internalSupport&section=` (the `section` key present with an empty value) now returns the no-store HTML 400 page "The section must not be empty" with a Back to Support link, instead of silently opening the overview. Omitting `section` still opens the overview. Order of checks is unchanged: no session → 401, unknown company → 403, then unknown key, repeated key, over-long value or control character → 400, then empty section → 400, then malformed section → 400 and unknown section → 404. `internalForum` and `internalHelp` keep their existing contract, where an empty `topic=` shows the index. `internalSupport` still rejects `q` and `category`. This matches the empty-value rule already used by Launcher `section` (Menu-45) and Rankings `category`/`page` (Menu-179). Tests: `LocalSupportEmptySectionTests`.

### Menu-182: Launcher cookie lookup bound (2026-10-24)

No endpoint or query contract changed. `GET /index.es?action=externalDownload` validates the query before reading the session. It now treats a `dosid` cookie longer than 128 characters as signed out without querying the session store; a value of exactly 128 characters may be queried. It returns the same no-store page without setting a cookie. These are emulator-defined bounds, and Redirect client use is unverified. Tests: `LocalLauncherDownloadSessionTests`.
# World-7 jump bridge (2026-10-24)

`POST /spacemap/world/jump` accepts JSON `{ "portalId": int }` with the `dosid` cookie. A nearby valid gate returns the normal world state with destination map, position and a `jump` object containing `portalId`, `targetNodeId`, and `targetTcpHostPort`; a distant or unknown gate returns 409, malformed JSON 400, and non-JSON 415. `GET /spacemap/world/state` exposes persisted hull and map after travel. These are local emulator contracts, not captured official endpoints.

# World-9 local NPC endpoints (2026-10-24)

`GET /spacemap/world/state` adds `npcs` with `id,kind,shipId,x,y,hp,maxHp,shield,maxShield,credits,uridium,experience,honor`. `POST /spacemap/world/target` accepts `{ "npcId": int }` and returns that snapshot or 404. `POST /spacemap/world/fire` accepts the same JSON and returns `{status,target,damage,destroyed}`; out-of-range, missing and already destroyed NPCs return 409. Target lock and fire use authenticated pilot position and the shared NPC vitality store. These browser endpoints are emulator-only and were not observed on official hosts.

# Admin-var-1 runtime registry (2026-10-24)

The runtime variable registry is an internal emulator service backed by PostgreSQL and `sessions-redis`. It adds no Unity HTTP endpoint or TCP packet. The admin command surfaces and their direct client verification are pending.

The backoffice API now provides `GET /api/v1/backoffice/runtime-variables`, `GET /runtime-variables/{key}`, `GET /runtime-variables/history`, `PUT /runtime-variables/{key}`, `POST /runtime-variables/{key}/reset`, and `POST /runtime-variables/history/{changeId}/rollback` under the same prefix. These are emulator-defined routes requiring `OpsRead` or `OpsWrite`, not captured Unity routes. Angular CMS use remains unverified.

## Admin-var-2 management commands (2026-10-24)

The emulator adds authenticated `POST /api/v1/backoffice/admin/command` for text or structured admin commands and `GET /api/v1/backoffice/admin/command/history`. The existing runtime-variable routes also accept the shorter `/api/v1/backoffice/runtime-vars/*` alias. Command mutations require `OpsWrite`; history and variable reads require `OpsRead`. `scripts/admin/Invoke-ServerAdminCommand.ps1` sends `ServerManagement` attribution; the CMS uses `AdminCms`. These are emulator-defined backoffice contracts, not observed Unity endpoints. World state now includes a shared announcement and Redis-backed manual spawns; the browser Spacemap renders them. Native Unity handling of local announcement packet 31 is unverified.

# Admin-var-3 in-game admin bridge (2026-10-24)

Cookie-authenticated, admin-only emulator routes: `POST /spacemap/world/admin/command` accepts `{ "text": "/var set <key> <value>" }`; `GET /spacemap/world/admin/vars` lists effective variables; `GET /spacemap/world/admin/vars/history?key=&limit=&beforeChangeId=` pages through changes; `POST /spacemap/world/admin/vars/rollback` accepts `{ "changeId": number }`. Mutations call the shared admin dispatcher with `Surface = InGameAdmin`. Non-admin pilots receive 403. Browser world state includes `pilot.isAdmin` and current speed/laser damage. No official Unity endpoint is claimed.

# Admin-var-4 CMS runtime controls (2026-10-24)

Angular `/backoffice/runtime-vars` calls `GET /api/v1/backoffice/runtime-vars`, `PUT /runtime-vars/{key}` with `{value,reason}`, `POST /runtime-vars/{key}/reset` with `{reason}`, `GET /runtime-vars/history?limit=100&beforeChangeId=`, and `POST /runtime-vars/history/{changeId}/rollback` with `{reason}`. Its console posts `{text,surface:"AdminCms"}` to `/api/v1/backoffice/admin/command`. These are emulator backoffice contracts, not official Unity endpoints.

# World-22 cross-replica move notification (2026-10-24)

No HTTP endpoint changed. A provisional TCP socket now notices a saved travel trajectory written by a browser move on another API replica and sends one local `MoveCommand` to its map observers. A two-store loopback client decoded the packet; native Unity and live Redis delivery remain unverified.


# World-23 cross-node move delivery (2026-10-24)

No HTTP endpoint changed. The provisional TCP socket now also emits `MoveCommand` for pilots on its map that are connected to other nodes, detected through shared presence and travel. A loopback client decoded the packet; native Unity and live Redis delivery remain unverified.
# World-24 remote pilot presence (2026-10-24)

No HTTP endpoint changed. The emulator's provisional TCP listener adds `PilotCreateCommand` (34) and `PilotRemoveCommand` (35) for pilots observed through shared presence on another node, ordered around the existing move packet. A connected loopback client decoded the sequence. These are local emulator packets, not captured Unity bytes.


# World-25 map-level remote observer fanout (2026-10-24)

No HTTP endpoint or packet layout changed. Remote pilot packets on the provisional TCP listener now come from a shared 500 ms per-map snapshot instead of per-observer cache reads.

## World-27 (2026-10-24)

No endpoint or response shape changed. The emulator-only `GET /spacemap/world/state` `pilots` entries (`id`, `name`, `x`, `y`) now come from the node-local per-map snapshot, which can be up to 500 ms stale for arrivals and departures. Positions are still interpolated at response time. This is not a captured official Unity endpoint.

## World-31 (2026-10-24)

No endpoint or response shape changed. The emulator-only `POST /api/v1/backoffice/admin/command` is now driven by the shipped PowerShell CLI in a non-integration test. The response `data` for `var set`/`var rollback` is a `RuntimeVariableChangeEntry` (camelCase: `changeId`, `newValue`, `surface`, `changedBy`, `nodeId`, ...).
# World-48 emulator rollback conflict in CMS and Spacemap (2026-10-24)

Emulator-only `POST /api/v1/backoffice/runtime-vars/history/{changeId}/rollback` now returns HTTP 409 `{ "error": "Rollback conflict: …" }` when a later change supersedes the requested change, instead of an unhandled 500. The existing `POST /spacemap/world/admin/vars/rollback` returns the same status and body. Both were exercised from their shipped client components over live local HTTP; neither route is a captured official Unity endpoint.


# World-49 (2026-10-24)

No HTTP endpoint or response shape changed. Only the server-side ordering of the TCP login sequence changed: baselines are now taken before the entry packets (see world-protocol.md World-49).

# World-53 (2026-10-24)

No HTTP endpoint or response shape changed. The TCP world socket now also sends `ShipCreateCommand`/`ShipDestroyCommand` from its idle poll when the shared NPC set changes (see world-protocol.md World-53).

## World-54 emulator management-to-TCP verification (2026-10-24)

No endpoint or packet shape changed. The shipped CLI called emulator-only `POST /api/v1/backoffice/admin/command` with spawn-count set and rollback commands. An idle loopback TCP client decoded matching provisional `ShipCreateCommand` and `ShipDestroyCommand` packets. This is not an official Unity endpoint or captured native packet.


## World-55 emulator TCP NPC vitals (2026-10-24)

No HTTP endpoint changed. The emulator TCP world socket adds provisional `NpcHealthCommand=37` (int32 id, hp, maxHp, shield, maxShield), sent by the idle roster sync when shared NPC vitals differ from what that socket was last told. This is not an official Unity endpoint or a captured native packet.
## World-56 emulator NPC observer snapshot (2026-10-24)

No HTTP endpoint or packet schema changed. The emulator TCP listener's NPC create/destroy/health updates now share one node-local read-only NPC snapshot per map for 500 ms. Native Unity behavior remains unverified.

## World-59 emulator TCP NPC reward (2026-10-24)

No HTTP endpoint changed. Provisional TCP `NpcRewardCommand=30` now reports the amounts credited after runtime reward multipliers in its existing NPC-ID-plus-four-int32 payload. A connected loopback client decoded the packet. This is not a captured official Unity command.

## World-62 emulator NPC reward retry after spawn removal (2026-10-24)

The emulator-only `GET /spacemap/world/state` omits a killed NPC after its spawn count is set to zero. The killer can still retry `POST /spacemap/world/fire` with `{ "npcId": number }` and receive 409 `{ "status": "destroyed" }`; the saved reward is used for idempotent credit. The shipped Spacemap page observed both requests against live Kestrel. No official Unity endpoint was captured.

## World-69 emulator concurrent pickup (2026-10-24)

No endpoint shape changed. A second authenticated client claimed a selected box through emulator-only `POST /spacemap/world/collect`. The first client's next `GET /spacemap/world/state` omitted that box, and the shipped page displayed its unavailable outcome without issuing another collect request. No official Unity endpoint was captured.




## World-94 stale state poll after configuration switch (2026-10-24)

The local Spacemap page now counts completed configuration requests. A `/state` poll that started before a configuration request finished is discarded, and the page waits for any in-flight poll before its follow-up refresh. Previously the in-flight poll could repaint the old configuration and the follow-up refresh was skipped while that poll was running. `verify-spacemap-config-stale-poll-live.mjs` held a real `/state` response with configuration 1, clicked configuration 2 (`POST /config` 200), then released the held response. The DOM kept configuration 2 pressed after one fresh `/state`. No HTTP or TCP contract changed; native Unity/Vuplex remains unverified under the no-CA rule.

## World-96 jump conflict mesh owner header (2026-10-24)

`POST /spacemap/world/jump` now re-stamps `X-Mesh-Node-Id` / `X-Mesh-Map-Owner-Node` after reloading the shared pilot state, like move, config, collect, target and fire already did. Before, a replica holding a stale local pilot (map 1) answered `409 map_changed` with map 1's lease owner even though another replica had already jumped the pilot to map 2, so a router following the header would be sent to the old map's owner. No body or TCP contract changed.
