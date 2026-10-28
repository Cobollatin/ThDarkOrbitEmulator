# Unity world entry and movement protocol inventory (world-1, 2026-10-24)

## World-97 map conflict owner routing (2026-10-24)

Emulator browser commands refresh the shared pilot map before sending `409 map_changed`, then set `X-Mesh-Map-Owner-Node` from the current map lease. This also covers a map transition between command admission and its under-mutex check. The configuration and jump clients are checked against two live Kestrel replicas with distinct map owners. No native Unity packet shape is established.

## World-95 stale configuration after a map transition (2026-10-24)

The emulator Spacemap includes its displayed `mapId` in `POST /spacemap/world/config`. The HTTP route rejects a stale map before switching, and `WorldSessionStore.SwitchAsync` compares the map again after restoring shared state under the mesh mutex. The page fetches `/state` on `409 map_changed` and renders the new map and authoritative configuration. A queued configuration from an earlier map is dropped. A live Kestrel/jsdom check held the page request before delivery, jumped the pilot from a second authenticated client, then observed the 409 and map 1-2 with configuration 1. This is an emulator browser contract, not a confirmed native Unity packet.

## World-93 ordered browser configuration input (2026-10-24)

The emulator Spacemap serializes `POST /spacemap/world/config` from one page instance. It keeps the latest configuration click while a request is pending, then refreshes authoritative ship stats and the pressed button after each result. The server's existing five-second switch cooldown still applies: a rapid reverse switch returns HTTP 409 with `cooldownRemainingMs`, leaving the accepted configuration active and displaying the cooldown. A live Kestrel/jsdom client held the first request before delivery, confirmed that the second click did not overtake it, and observed configuration 2 in the DOM and `/state` after the reverse click was rejected. This is emulator browser behavior, not confirmed native Unity behavior.

## World-92 stale browser collectible pickup (2026-10-24)

The emulator Spacemap now sends its displayed `mapId` with `POST /spacemap/world/collect`. The HTTP route checks the optional source map after loading the pilot. `WorldCollectibles.CollectAsync` checks it again after restoring shared pilot state under the mesh mutex, before claiming a box or crediting its reward. A stale request receives `409 {"status":"map_changed"}`; the page fetches `/state`, clears its old box selection and renders the current map. A live Kestrel/jsdom check held the pickup before delivery, jumped through another authenticated client, then observed map 1-2 and no wallet credit. This is an emulator browser contract, not a confirmed native Unity packet.

## World-91 stale browser laser fire (2026-10-24)

The emulator Spacemap sends its displayed `mapId` with `POST /spacemap/world/fire`. The HTTP route rejects a stale source map with `409 {"status":"map_changed"}` before checking selection; `WorldNpcs.AttackAsync` checks again after restoring shared pilot state under the mesh mutex, before damage or reward. The page fetches `/state` on this conflict. A live Kestrel/jsdom check held Fire before delivery, jumped through a second authenticated client, and observed map 1-2 with no target. The field remains optional for older emulator callers. This does not establish native Unity packet behavior.

## World-90 stale browser NPC target (2026-10-24)

The emulator Spacemap now sends its displayed `mapId` with `POST /spacemap/world/target`. The HTTP route rejects an obsolete map with `409 {"status":"map_changed"}`; `WorldNpcs.SelectAsync` repeats the comparison after restoring shared pilot state under the mesh mutex. The page immediately loads `/state` and clears its old target when another client has jumped. A live Kestrel/jsdom check delayed the target before delivery, jumped through a second authenticated client, then observed map 1-2 and `None` in the rendered target. This does not establish native Unity target behavior.

## World-89 ordered browser target selection (2026-10-24)

The shipped emulator Spacemap page now sends one `POST /spacemap/world/target` at a time, retaining only the latest NPC click while a selection is pending. This prevents two requests from reaching the server in reverse order and leaving its selected NPC different from the rendered target. A live Kestrel/jsdom client held the first request before delivery, clicked a second Streuner, observed only one request until release, then received the second lock and a `/fire` response other than `not_selected`. The HTTP and provisional TCP payloads did not change. This does not establish native Unity target behavior.

## World-88 stale browser jump (2026-10-24)

The emulator-only `POST /spacemap/world/jump` accepts optional integer `mapId`. When the pilot has already changed maps, it returns HTTP 409 `{ "status": "map_changed" }` without another jump. The shipped Spacemap page sends its source map ID and immediately fetches `/state` after this conflict. A live Kestrel/jsdom check held the page's jump while a second authenticated client jumped; the page then rendered map 1-2, cleared its target, and reenabled Jump without a timer poll. The check does not establish the native Unity protocol.

## World-87 same-map target switch during laser response (2026-10-24)

The local Spacemap page discards a delayed `/spacemap/world/fire` result if a later map click changed its selection, even when both targets are on the same map. It still settles the pending Fire button. Live Kestrel/jsdom checks held a real nonlethal or lethal result after server processing, selected a second NPC through `POST /target`, then confirmed the second target and lock message survived. The server attack and reward still occur. No HTTP or provisional TCP contract changed; this is emulator client behavior, not confirmed native Unity behavior.

## World-86 single in-flight laser request (2026-10-24)

The shipped emulator Spacemap page permits one `POST /spacemap/world/fire` at a time. It disables Fire while the request and state refresh are pending, then enables it after success or rejection. Repeated clicks cannot send unintended extra shots. The TCP `LaserFireRequest=27` and HTTP payloads are unchanged. A live Kestrel/jsdom client held the first fire request and observed only one request after another click, then continued the NPC combat flow. Native Unity/Vuplex handling remains unverified under the no-CA rule.

## World-85 external jump during a pending move (2026-10-24)

When emulator `POST /spacemap/world/move` returns `409 map_changed`, the shipped page immediately requests `GET /spacemap/world/state` and renders the authoritative destination map. The same conflict refresh handles `move_changed`. It checks the local map epoch again after the fetch, so a late refresh cannot repaint a newer jump. A live Kestrel/jsdom check held a map-1 move before delivery, jumped the pilot through a second HTTP client, released the move, and observed map 2 in the page without a timer poll. No HTTP or provisional TCP payload changed; native Unity/Vuplex remains unverified.

## World-84 external map transition (2026-10-24)

The shipped emulator Spacemap page now treats a map change learned through `GET /spacemap/world/state` as a new map epoch. It clears its selected NPC, pending box and queued move, and discards old-map combat responses. A live Kestrel/jsdom client held real `/target` and `/fire` responses while another HTTP client jumped the same pilot; after the page polled map 2, releasing either response left the map-2 DOM, status and empty target intact. No HTTP or provisional TCP payload changed. Native Unity/Vuplex was not exercised under the no-CA rule.

## World-83 movement revision across live Redis replicas (2026-10-24)

The World-82 emulator `moveId` contract was exercised with two live Kestrel hosts, separate Redis cache connections and `RedisServerMeshMutex` instances. The shipped Spacemap page on replica B entered with the old revision. An authenticated client moved the same pilot through replica A; B's stale `POST /move` returned `409 move_changed`, refreshed `/state`, rendered A's destination, then retried with the new revision. Redis retained B's final destination. No packet or JSON shape changed. This is emulator browser and HTTP evidence, not a native Unity/Vuplex packet capture.

## World-82 stale same-map browser moves (2026-10-24)

Emulator `GET /spacemap/world/state` and `POST /spacemap/world/enter` expose `pilot.moveId`, a GUID for the currently saved movement command (empty GUID before travel). The shipped page sends it as `expectedMoveId` with `POST /spacemap/world/move`. Under the shared mesh mutex, the store reloads saved travel and returns `409 {"status":"move_changed"}` when another client has changed the destination. The rejected request leaves the saved target unchanged, including when the request includes a configuration switch. The page refreshes state and shows the other client's target; a further click uses the new revision. Existing callers may omit `expectedMoveId`. Speed and configuration retiming keep the command's revision. This is emulator protocol, not a captured Unity packet.

## World-81 ordered local Spacemap moves (2026-10-24)

The shipped local Spacemap page sends only one `POST /spacemap/world/move` at a time. Map clicks during a pending move replace a single queued destination; after the pending request settles, the page sends the latest destination. A target click cancels the queued move, and a successful gate jump clears it. The per-click response sequence still prevents older results from repainting the DOM. A live Kestrel/jsdom check held the first request before server delivery, clicked a second destination, observed no second request until release, and then observed the second destination in both the page and server state. HTTP and provisional TCP payloads are unchanged. Native Unity/Vuplex remains unverified under the no-CA rule.

## World-80 source-map movement guard (2026-10-24)

The shipped local Spacemap page sends `mapId` with `POST /spacemap/world/move`. The server checks it against the pilot's refreshed map while holding the shared mesh mutex and movement gate, before saving a new trajectory. A stale request receives HTTP 409 `{ "status": "map_changed" }`; older callers may omit `mapId`. A live Kestrel/jsdom check held the map-1 request before server delivery, jumped to map 2, released the request, and confirmed the 409, the map-2 DOM, and the unchanged map-2 destination. The provisional TCP protocol did not change. Native Unity/Vuplex remains unverified under the no-CA rule.

## World-79 state polling during pending movement (2026-10-24)

The local Spacemap page now skips its periodic `/spacemap/world/state` poll while a map or minimap movement request is pending. It also discards a poll that returns while movement is pending. A live Kestrel/jsdom check held the movement request before server delivery, invoked the page's poll, observed no state request or redraw, then released the move and observed a new poll and rendered destination. This changes no emulator HTTP shape or provisional TCP packet. Reverse processing order of two moves across replicas remains unresolved; native Unity/Vuplex is unverified under the no-CA rule.

## World-78 delayed same-map state poll (2026-10-24)

The local Spacemap page now discards a `/spacemap/world/state` response if a map or minimap click occurred while that poll was pending. It also stops a pending pickup response from repainting the state after a later click. A live Kestrel/jsdom check held a completed pre-click state response, moved the ship over real HTTP, and confirmed the older response could not restore its destination or position. This changes no HTTP shape or provisional TCP packet. Server processing order for concurrent moves across replicas and native Unity behavior remain unverified.

## World-77 same-map movement response order (2026-10-24)

The local Spacemap page tags each map click with an increasing sequence. A delayed `/move` or `/target` result from an earlier click no longer replaces the newer rendered destination or target. The HUD now shows the current movement destination. A live Kestrel/jsdom check held the first real move response until a second move rendered, then confirmed the destination stayed on the second command. The server still accepts concurrent move requests; reverse server processing order and native Unity behavior remain unverified. No TCP packet or HTTP shape changed.

## World-76 single-flight gate jump (2026-10-24)

The shipped emulator Spacemap page allows one pending `POST /spacemap/world/jump` at a time and disables the Jump button until that request settles. This prevents repeated input from submitting two map transitions before the first response renders. A live Kestrel/jsdom check held the real first response, dispatched a second click, observed exactly one jump request, then released it and saw map 2 and the reenabled button. No endpoint or provisional TCP contract changed. Native Unity/Vuplex remains unverified under the no-CA rule.

## World-75 late old-map combat responses (2026-10-24)

The shipped local Spacemap page now discards target and laser responses, including errors, when a gate jump completes while either request is pending. Live Kestrel/jsdom checks held a real map-1 `POST /spacemap/world/target` HTTP 200 and a real map-1 `POST /spacemap/world/fire` HTTP 409 until map 2 rendered. Both left the map-2 heading, empty target, and jump status intact. No HTTP or provisional TCP contract changed. Native Unity/Vuplex behavior remains unverified under the no-CA rule.

## World-74 late old-map move response (2026-10-24)

The shipped local Spacemap page now discards a `/spacemap/world/move` response if a successful gate jump occurred while that request was pending. A live Kestrel/jsdom check held a real map-1 move response until after the map-2 jump; the page kept map 2, its jump status, and sent no old-map `/collect`. No HTTP or provisional TCP contract changed. Native Unity/Vuplex behavior remains unverified under the no-CA rule.

## World-70 selected box claimed across replicas (2026-10-24)

The shipped local Spacemap page runs against live Kestrel replica A while a second authenticated client claims its selected box through replica B. Both hosts share the world cache and mutex. The next A state poll omits the box, so the page clears its selection, renders “Bonus box is no longer available,” and sends no duplicate collect. The wallet records one credit. This verifies the emulator HTTP and DOM surfaces with two hosts; it does not establish native Unity protocol compatibility or separate Redis processes.

## World-68 pickup during in-game admin radius change (2026-10-24)

An in-game admin TCP client on replica A set `world.collectibles.pickup_radius` to 400. The shipped local Spacemap page on replica B clicked a box 300 units away, sent `/move` and `/collect` through live Kestrel HTTP, received HTTP 200, and rendered `Collected 200 credits`. The test wallet recorded one credit. The page then rendered the rollback to 180. The focused check used a shared distributed-cache test instance; native Unity protocol behavior remains unverified.

## World-67 visible pickup range across replicas (2026-10-24)

The local Spacemap HUD now displays the `collectionRadius` returned by each world state poll in world units. An in-game admin socket on replica A set `world.collectibles.pickup_radius` to 400 and rolled it back to 180. The shipped page on replica B rendered `180 units → 400 units → 180 units` over live HTTP, using separate Redis connections and the production PostgreSQL runtime-variable store. This adds no packet or endpoint field; native Unity/Vuplex rendering remains unverified.

## World-66 pickup radius change during collection (2026-10-24)

The local Spacemap keeps a clicked box pending when `POST /spacemap/world/collect` returns `409 { "status": "distant" }`. Its next `GET /spacemap/world/state` reads the effective `collectionRadius`, and the page retries while the ship continues moving. Other collection errors still end the attempt. A live Kestrel/jsdom run changed the server radius from 400 to 180 between the page's state poll and collect request. The browser observed 409 beyond 180 units, then collected once within 180. This is an emulator HTTP flow; no native Unity packet was verified.

## World-65 live collectible pickup radius (2026-10-24)

Emulator `POST /spacemap/world/enter` and `GET /spacemap/world/state` now include `collectionRadius` (integer world units) alongside the map, pilot and boxes. The shipped local Spacemap page uses that value when deciding when to send `POST /spacemap/world/collect`, matching the server's `world.collectibles.pickup_radius` runtime variable. A missing field falls back to the existing 180-unit behavior. The live Kestrel/jsdom client check observed a 400-unit radius and a successful collection while the pilot remained farther than 180 units from the box. This is an emulator browser contract; native Unity compatibility remains unverified.

## World-64 cross-replica NPC reward retry (2026-10-24)

No packet or JSON contract changed. The shipped local Spacemap script killed a Streuner through live Kestrel replica A, then sent its retry through replica B after the selection key was removed, the spawn count went to zero, the pilot moved away, and the reward multiplier changed. Separate Redis connections carried the saved kill and reward snapshot; B returned `409 destroyed` and retried the original 400-credit award. This confirms the emulator HTTP client path across Redis, not native Unity/Vuplex or PostgreSQL economy persistence.

## World-63 reward retry without a selected target (2026-10-24)

After a lethal hit, the provisional TCP client may send `LaserFireRequest` (27) again without selecting the destroyed NPC. The server checks the saved kill's account ID and returns `LaserFireResultCommand` (36) with `destroyed`, after idempotent reward credit. The local HTTP `/spacemap/world/fire` accepts the same retry when its selection key has expired or been removed, returning 409 `destroyed`. Other accounts remain unauthorized. This uses the existing killed-NPC cache record; no packet or JSON layout changed. Native Unity behavior remains unverified.

## World-58 browser laser reward (2026-10-24)

A successful lethal `POST /spacemap/world/fire` response includes `reward` with the actual credited NPC values (`credits`, `uridium`, `experience`, `honor`), after runtime multipliers and after the credit succeeds. Nonlethal hits return `reward: null`. The shipped Spacemap page displays these values on destruction. This is the emulator HTTP contract; native Unity compatibility remains unverified.


## World-57 live Spacemap NPC visibility (2026-10-24)

The local Spacemap now displays the count of visible NPCs from its `/spacemap/world/state` response. A live Kestrel test exercised the shipped page JavaScript in jsdom: it rendered `2 visible`, a second `WorldNpcs` instance sharing the cache killed one NPC, and the next page poll rendered `1 visible` after the 500 ms observer snapshot TTL. Six concurrent HTTP state polls shared two NPC cache reads for two NPCs. No wire packet or JSON shape changed. Native Unity rendering remains unverified.

## World-52 TCP laser-fire completion (2026-10-24)

The provisional TCP `LaserFireResultCommand=36` now also reports `hit` for an accepted shot, after its `LaserHitCommand` and, on a kill, `ShipDestroyCommand` and `NpcRewardCommand`. Thus each selected fire request has a completion status on the firing socket. A loopback client decoded both a nonlethal and a lethal shot, including event order and one reward. This is an emulator contract; native Unity compatibility remains unverified.

## World-51 TCP laser-fire result status (2026-10-24)

Provisional `LaserFireResultCommand=36` (int32 NPC ID; length-prefixed UTF-8 status) replaces the previous generic `LoginErrorCommand` Malformed reply when a `LaserFireRequest` (27) is rejected. Status values match the browser `/spacemap/world/fire` bridge: `unselected` (the ID is not this connection's `SelectShipRequest` target), `distant` (outside `world.combat.fire_range`), `missing` (NPC not in the live spawn cycle), `destroyed` (another pilot killed it) and `invalid`. `missing` and `destroyed` clear the connection's selection. The socket stays open and no damage or reward is applied. The emulator defines this command; it is not a captured native packet.


## World-50 live Spacemap combat client (2026-10-24)

The shipped local Spacemap page now translates the emulator's HTTP 409 out-of-range combat status into an actionable message. `SpacemapClient_TargetsFiresAndRendersNpcDamage_OverLiveHttp` runs the actual page script in jsdom against live Kestrel world endpoints. It clicked a visible Streuner, saw its health in `#target`, fired from outside the configured 1000-unit range and saw “Target out of laser range. Move closer.” It then clicked the canvas to move, fired until the displayed health changed and the target disappeared, and confirmed one reward credit. The ship was painted throughout. These are emulator HTTP and browser contracts; native Unity TCP serialization/rendering remains unverified.

## World-47 stale rollback conflict (2026-10-24)

`RuntimeVariableStore.RollbackAsync` now restores a change's `previous_value` only if that change's `new_value` is still the live value. The check runs under the advisory lock and `FOR UPDATE` row lock. If a later change superseded it, the store raises `Rollback conflict: <key> is now <v>, not <x> set by change <id>`. The admin route returns HTTP 409 and no value or history row is written; the failed command is still recorded in `admin_command_history`. Undo newest-first. `scripts/admin/Invoke-ServerAdminCommand.ps1` now prints the server's `error` text and exits 1, where before it showed only `(409) Conflict`.

Direct client check, run against real PostgreSQL and Redis: `ServerManagementCli_StaleRollback_RealPostgres_IsRejectedAndSocketKeepsLiveValue` (Explicit/Integration). The shipped CLI set the radius to 500 and then 900, and an idle TCP client decoded `SpaceStationCommand`/`SafeZoneCommand` for both. A stale `var rollback 1` exited 1 with the conflict message, the value stayed 900, and the socket received no packet. Rolling back change 2 and then change 1 decoded 500/0 and then 1000/1. Durable history holds exactly four rows. TCP frames are unchanged. Native Unity is unverified.

## World-46 in-game admin audit reasons (2026-10-24)

The emulator Spacemap admin variable form now requires a reason and includes it in its `AdminCommandRequest`-equivalent HTTP slash command. History renders change ID, actor, node, reason and local timestamp with the old/new values. The rollback button submits `{changeId,reason}`; the HTTP bridge rejects a missing/blank reason with 400 and passes the reason into the command dispatcher. A live Kestrel/jsdom client check observed `Protected → Outside (radius 500) → Protected` and both distinct reasons in history. The world TCP protocol did not change. Native Unity/Vuplex remains unverified.

## World-45 in-game admin socket over real Redis/PostgreSQL replicas (2026-10-24)

This repeats the World-43 packet exchange with production storage. Provisional `AdminCommandRequest` (32) `/var set world.station.peace_radius 500 ...` and `/var rollback 1 ...` on `mesh-node-a` returned `AdminCommandResponse` (33) with PostgreSQL changeIds 1 and 2. The rollback response's reason is `Rollback of 1: World-45 in-game live check`. Replica B's TCP client decoded `SpaceStationCommand` `(100,2000,1300,500)` + `SafeZoneCommand` `0`, then `(100,2000,1300,1000)` + `1`, through its own Redis connection. Test: `ServerManagementCliLiveSocketTests.InGameAdminSocket_OnReplicaA_RealRedisAndPostgres_UpdatesLiveSpacemapAndSocketOnReplicaB`. Packet IDs remain emulator-defined.


## World-44 in-game admin panel over live HTTP (2026-10-24)

The shipped `LocalSpacemapPage` HTML/JS ran in jsdom against a live Kestrel `WorldHttpBridge`. An admin session exposed `#adminPanel`; selecting `world.station.peace_radius`, entering `500`, and submitting its variable form called `POST /spacemap/world/admin/command`. The page rendered `Outside (radius 500)` at `(2800,1300)` and a `1000 → 500 (InGameAdmin)` history row. Clicking that row's Rollback button called `POST /spacemap/world/admin/vars/rollback`; the page rendered `Protected` and a `500 → 1000` row. The main ship canvas drew. This is the emulator browser client and emulator HTTP contract; native Unity/Vuplex and TCP packet compatibility remain unverified.

## World-43 in-game admin socket on replica A to TCP and Spacemap on replica B (2026-10-24)

An admin pilot's loopback WorldServer socket on `mesh-node-a` sent provisional `AdminCommandRequest` (32) slash commands `/var set world.station.peace_radius 500 ...` and `/var rollback 1 ...`. Both returned `AdminCommandResponse` (33) with success byte 1 and the change entry (surface `InGameAdmin`, actor `Admin pilot:840`, node `mesh-node-a`). A separate TCP client on `mesh-node-b` decoded `SpaceStationCommand` `(100,2000,1300,500)` + `SafeZoneCommand` `0`, then `(100,2000,1300,1000)` + `1`, without reconnecting. Test: `ThDarkOrbitEmulator.Tests/ClientTesting/ServerManagementCliLiveSocketTests.cs` `InGameAdminSocket_OnReplicaA_UpdatesLiveSpacemapAndSocketClientOnReplicaB` (Explicit). Packet IDs and framing remain emulator-defined.


## World-42 CMS change rendered by Spacemap and decoded by TCP (2026-10-24)

The World-41 live CMS test now runs the shipped Spacemap page against replica B while the Angular editor changes the peace radius on replica A. Live `/spacemap/world/state` polls rendered `Protected → Outside (radius 500) → Protected` in `#safeZone`, kept the pilot at `(2800,1300)`, and drew the ship on the canvas. The connected provisional TCP client on B decoded the matching station and safe-zone packets. No HTTP contract or packet layout changed; native Unity compatibility is still unverified.

## World-41 Admin CMS change observed on the TCP socket (2026-10-24)

An Admin CMS **Save / Apply Live** of `world.station.peace_radius` = 500 on replica A, then **Rollback**, reached a connected WorldServer client on replica B as `SpaceStationCommand` (100, 2000, 1300, 500) + `SafeZoneCommand` 0, then radius 1000 + flag 1, within the one-second idle poll. Packets remain the emulator's provisional protocol.

## World-40 cross-replica CLI, Spacemap, and TCP safe zone (2026-10-24)

The World-39 live two-replica check now keeps a loopback WorldServer client connected to replica B while the shipped Server Management CLI changes `world.station.peace_radius` on replica A. B's production `RuntimeVariableStore` reads the shared distributed cache. The shipped Spacemap page showed `Protected → Outside (radius 500) → Protected`; the connected client decoded `SpaceStationCommand` radii `500 → 1000` and `SafeZoneCommand` flags `0 → 1` without reconnecting. The browser also moved its ship on B. This is the emulator's provisional TCP contract, not a captured native Unity packet. The write side remains a test stand-in for PostgreSQL, and the cache is in memory rather than a live Redis server.

## World-39 cross-replica live Spacemap via Server Management CLI (2026-10-24)

`verify-spacemap-live.mjs <baseUrl> <dosid> [cliBaseUrl]` can now send the CLI to a different replica than the page. The `[Explicit]` test `ServerManagementCli_OnReplicaA_UpdatesLiveSpacemapSafeZoneDomServedByReplicaB` starts two Kestrel hosts.

- Replica A (`mesh-node-a`) serves only the admin command route.
- Replica B (`mesh-node-b`) serves only `/spacemap/world/*`, through its own production `RuntimeVariableStore`.

The only link between them is one shared `IDistributedCache`. The shipped page polled B and its `#safeZone` read `Protected`, then `Outside (radius 500)`, then `Protected`, after CLI `var set world.station.peace_radius 500` and `var rollback 1` against A. Canvas movement on B still advanced `(2800,1300)` to about `(3366,1658)`. The world packets are unchanged.

## World-38 live browser movement rendering (2026-10-24)

The World-37 live HTTP Spacemap check now clicks the shipped map canvas after the CLI peace-radius rollback. The page sent `POST /spacemap/world/move`, then a real `GET /spacemap/world/state` advanced the pilot from `(2800,1300)` toward the clicked destination. The test observed both the position text and the main canvas ship translation advance on both axes. This confirms the local browser client and world bridge together; native Unity rendering and packet compatibility remain unverified.

## World-37 live Spacemap DOM via Server Management CLI (2026-10-24)

- New client script `scripts/client-testing/verify-spacemap-live.mjs <baseUrl> <dosid>` runs the shipped `LocalSpacemapPage` HTML/JS in jsdom. All `/spacemap/world/*` fetches go over real HTTP to a live host, with no synthetic responses. Between state polls it runs the shipped `scripts/admin/Invoke-ServerAdminCommand.ps1` as a real PowerShell process.
- New `[Explicit]` test `ServerManagementCliLiveSocketTests.ServerManagementCli_PeaceRadiusSetAndRollback_UpdateLiveSpacemapSafeZoneDom` starts one Kestrel host. It serves both the backoffice admin command route and the real `WorldHttpBridge.Map` routes over one shared runtime-variable registry. The pilot is parked at (2800,1300), 800 units from home station 100.
- Observed in the client DOM: `#safeZone` = `Protected`, then `Outside (radius 500)` after CLI `var set world.station.peace_radius 500`, then `Protected` after CLI `var rollback 1`. Position stayed `2800 / 1300`. Requests: `POST /enter` followed by three `GET /state`. Both changes were attributed to `ServerManagement` (changeIds 1 and 2).
- This closes the World-36 gap where the DOM check and the endpoint check were separate.
- Remaining gaps: the registry and dispatcher are in-memory, so PostgreSQL/Redis cross-replica propagation is not covered. The page ran in jsdom, not the signed-in Unity Vuplex WebView. The Angular CMS was not exercised.

## World-36 browser safe-zone HUD (2026-10-24)

The local Spacemap now reports `Protected` or `Outside (radius N)` beside the pilot position. It compares the returned ship coordinates with the returned station center and effective `peaceRadius` on every state poll. The server's `/spacemap/world/state` response was checked as the registry radius changed 1000 → 500 → 1000; the shipped page's JavaScript was exercised in jsdom for the same transitions. The browser requests in jsdom used synthetic responses, and native Unity rendering is unverified.

## World-35 Server Management CLI peace-radius change on a live socket (2026-10-24)

No packet layout changed. The shipped Server Management CLI (`scripts/admin/Invoke-ServerAdminCommand.ps1`, run as a separate PowerShell process) sent `var set world.station.peace_radius 500` to a real Kestrel host serving `POST /api/v1/backoffice/admin/command`. An idle loopback TCP client parked 800 units from home station 100 then decoded `SpaceStationCommand` (7) `(100, 2000, 1300, 500)` followed by `SafeZoneCommand` (9) `0`. `var rollback <id>` from the CLI produced `SpaceStationCommand` with radius 1000 and `SafeZoneCommand` `1`. These packets remain emulator-defined; native Unity handling is unverified.

## World-34 live safe-zone state (2026-10-24)

An admitted TCP socket now checks its interpolated ship position and `world.station.peace_radius` during the idle poll. A radius change sends the provisional `SpaceStationCommand` (7) with the current radius; crossing the boundary sends `SafeZoneCommand` (9) with `0` or `1`. Entry, local jump and in-game admin world refresh establish the announced baseline. A loopback client decoded a radius reduction from 1000 to 500, restoration to 1000, and a movement-driven safe-zone exit on one open socket. These are emulator-defined packets; native Unity handling remains unverified.

## World-33 live portal jump radius (2026-10-24)

The TCP `JumpRequest` handler reads `world.portal.jump_radius` from the runtime-variable registry on each jump, so a connected socket uses a new value without reconnecting. On a loopback client 700 units from portal 101, a jump at the default radius of 500 got `LoginErrorCommand` (`Malformed`). After the registry value changed to 800, the same socket got `MapChangeCommand` for map 2, followed by `MapInitializationCommand`. The rejection packet is the emulator's existing failure reply, not a captured Unity packet.

## World-32 own same-map teleport (2026-10-24)

When a second API replica teleports an admitted pilot within the same map, the idle TCP handler now reads the saved stationary position and sends provisional `MoveCommand` (12) with zero duration to that pilot's socket. It refreshes the socket's pilot coordinates before later movement. A connected loopback client decoded the snap and then sent a new move from the teleported point. This is an emulator packet, not a captured Unity packet.

## World-30 remote same-map teleport (2026-10-24)

An idle provisional TCP observer on another node now receives `MoveCommand` (12) with zero duration when a visible remote pilot is teleported to new coordinates on the same map. The observer remembers the last announced destination and receives one snap per changed saved position; a subsequent poll does not repeat it. Remote map departure still sends `PilotRemoveCommand`. This is an emulator-defined packet, not a captured Unity packet.

## World-29 batched mesh-tick presence heartbeat (2026-10-24)

`WorldSessionStore.RefreshLocalPresenceAsync` now groups every local pilot whose 15-second heartbeat is due, plus every pilot whose saved map changed, by map. It then performs one roster read-modify-write per map instead of one per pilot. Entry, departure, teleport and map transition still write immediately through the same `WritePresenceBatchAsync` core. Heartbeat due-checks use an optional `TimeProvider`; roster `SeenAtUtc` stays on wall-clock UTC so other replicas judge liveness the same way. The wire and JSON shapes are unchanged.

## World-28 presence heartbeat and browser map transition (2026-10-24)

The emulator's browser state path and mesh tick now refresh each local pilot's shared map-presence entry at most once per 15 seconds. First entry, explicit departure, and a map change still write immediately. If a state request restores a pilot onto a different saved map, it removes the old map entry in the same mesh mutex section before publishing the new one. The response and provisional TCP wire formats are unchanged. The presence expiry remains two minutes; a node that stops heartbeating is eventually pruned. This is an emulator contract, not a captured Unity behavior.

## World-26 nearby pilots in browser Spacemap (2026-10-24)

The emulator Spacemap draws other pilots from the existing `pilots` array in `/spacemap/world/state` on both canvases, labels them on the main map, and shows a nearby count. It excludes the current pilot by ID and redraws on each state poll, so movement and departure update the display. This changes no TCP command or official Unity contract; the array comes from shared map presence and saved positions.

## World-21 live speed retiming (2026-10-24)

An in-flight pilot's saved trajectory is retimed from its interpolated current position when effective speed changes. The idle TCP poll broadcasts provisional `MoveCommand` with the revised remaining duration before its `ShipAttributesCommand`; a Spacemap enter/state poll also reconciles the trajectory under the shared mesh mutex and map lease fence. Revised travel is saved in the distributed cache for the next replica. A connected loopback TCP client observed movement packets and attribute changes for a speed increase and rollback. Native Unity handling and a signed-in browser poll remain unverified.

## World-20 idle runtime attributes (2026-10-24)

While an admitted TCP socket waits for packets, the emulator checks effective speed and laser damage once per second after checking its map route. A change sends provisional `ShipAttributesCommand` over the connection's serialized writer. A connected loopback client observed speed/damage change from 300/30 to 600/60 and rollback to 300/30 without sending another packet. Existing movement keeps its saved trajectory until the next movement or configuration command. This does not confirm native Unity packet compatibility.

## World-19 idle TCP handoff (2026-10-24)

After login, the emulator TCP listener checks the shared saved map and map lease once per second while awaiting a packet. A remote teleport or lease reassignment sends provisional command 19 and releases the old socket without requiring a movement request. The pending read is cancelled only when the connection is being handed off. Connected loopback clients observed both idle routes; native Unity handling of command 19 remains unverified.

## World-18 remote teleport TCP handoff (2026-10-24)

Before processing each packet on an admitted TCP socket, the local listener reads the pilot's saved map from shared cache. If a remote admin teleported the pilot to another map, it sends provisional command 19 `MapChangeCommand` for the destination map and current owner address, then releases the old socket without applying the packet. Movement, configuration, portal and station repair writes also check for a changed map under the mesh mutex. A connected loopback client confirmed the command and unchanged destination coordinates after sending movement on the old socket. Native Unity handling remains unverified.

## World-17 remote teleport presence (2026-10-24)

Before renewing a locally connected pilot's presence, the mesh heartbeat checks the saved map. If an admin command on another replica teleported the pilot, the old node removes its stale local and distributed presence instead of making the ship reappear on the source map. A browser state endpoint test confirms the destination map and position after that heartbeat. The shipped Spacemap script redraws the destination on a synthetic state poll; a live Unity WebView and native TCP handoff remain unverified.

## World-16 station repair lease fence (2026-10-24)

Browser `GET /spacemap/world/state` and `POST /spacemap/world/enter` can repair a stationary ship at its home station. This runtime write now reloads pilot state under the shared mesh mutex and checks the map lease epoch before repair. A missing or changed lease returns emulator JSON 409 `map_lease_changed` without changing saved hull. The existing movement path already holds the mutex when it invokes station repair. Direct endpoint tests cover lost and mid-request changed leases; the shipped browser script shows the retry status with synthetic responses. Native Unity behavior remains unverified.

## World-15 target selection lease fence (2026-10-24)

Local TCP `SelectShipRequest` and browser `POST /spacemap/world/target` now select through the same shared target store. Selection checks the map lease under the mesh mutex and reloads the saved pilot map before writing a five-minute target lock. A stale lease sends the local TCP map-change route or returns browser JSON 409 `map_lease_changed`. NPC damage and box collection already had mutation-time lease checks. These commands remain emulator-defined; native Unity packet compatibility is unverified.

## World-14 browser lease fencing (2026-10-24)

The emulator Spacemap browser bridge now binds movement, configuration switching, portal jumps, collectible claims and NPC laser hits to the current live map lease epoch. Each mutation rechecks that epoch and active owner under the shared mesh mutex. A missing or changed lease returns HTTP 409 `{ "status": "map_lease_changed" }` without applying that command; the Spacemap shows a retry message. This is an emulator browser contract, verified through direct endpoint calls and execution of the shipped Spacemap JavaScript with synthetic responses. It does not establish native Unity protocol compatibility. Read-time station repair, target selection, and admin world mutations still need the same ownership audit.

## Client-verify-1 surface check (2026-10-24)

The shipped Spacemap HTML/JS was executed in jsdom for all four companies. DOM values, canvas paint calls for hero/companions/station/gate/NPC/box, and a canvas movement request/updated coordinate were observed with synthetic HTTP responses. Existing live loopback socket journeys passed separately. The two transports were not connected in one end-to-end run, and the native Unity packet parser remains unverified. See [client verification report](client-verification-report.md).

## World-12 TCP movement lease fence (2026-10-24)

TCP movement, configuration selection and portal jumps now pass the socket's admitted map owner and lease epoch into the state mutation. The session store reads the current lease while holding the mesh mutex shared with lease reassignment; a stale owner or epoch raises a lease-loss result before writing trajectory, runtime or map presence. The handler sends the provisional `MapChangeCommand` (19) with the current route and closes that socket. A test reassigns a map from node A to B, rejects all three stale operations, checks saved position and configuration, then moves on B through a fresh store. This covers those three TCP mutations only; collection, combat and browser commands are not yet epoch fenced. Redis lock expiry during a stalled operation remains a separate risk.

## World-10 verification (2026-10-24)

`WorldMeshJourneyTests` exercises the emulator's provisional TCP version/login, map and hero packets, companion packet, and move broadcast for MMO, EIC, VRU, and TST through three distinct loopback listeners selected by the leased `/xml/maps.php` address. A second journey checks HTTP Spacemap entry, shared movement/configuration, and a portal to a different lease owner. Collection and NPC target/fire are checked across separate session-store replicas with a shared distributed-cache test double. These assertions prove the local contracts and server state transitions; they do not prove that the official Unity client renders the ship or speaks this wire format.

## World-8 collectibles (2026-10-24)

Each map has six deterministic collectible slots for each five-minute UTC cycle: three bonus boxes (Credits, Uridium, Galaxy Gate energy) and three ores (Prometium, Endurium, Terbium). The server computes coordinates from map, slot and cycle. `GET /spacemap/world/state` adds `boxes` (`id,kind,x,y,assetId,amount`); the browser canvas draws them, flies to a clicked box and collects within 180 map units. `POST /spacemap/world/collect` takes JSON `{ "boxId": int }`, returns `{ "status":"collected", "reward":{ "assetId":string,"amount":int }, "boxId":int }`, or HTTP 409 with `status` `missing`, `distant`, `claimed`, or `cargo_full`. Ore counts toward the equipped ship's cargo capacity. The persisted Economy ledger uses reason `world_collect` and source `spacemap`; the existing Logbook shows the ledger entry.

The local TCP extension adds command 20 `CollectBoxRequest` (`boxId:i32`), 21 `CollectBoxRemoveCommand` (`boxId:i32`), 22 `CollectRewardCommand` (`boxId:i32,assetId:string,amount:i32`), and 23 `CollectBoxCreateCommand` (`boxId:i32,kind:string,x:i32,y:i32`). Generated box IDs encode map, respawn cycle, and slot so an old request cannot collect a replacement. Create packets follow entry and local jumps. Rejected collections send command 22 with status text in place of asset ID and amount zero. These IDs and bytes are emulator-defined; Unity compatibility has not been verified.

## Mesh-3 browser bridge (2026-10-24)

`/spacemap/world/state` and successful move/enter replies now include `pilots` (`id,name,x,y`) from the shared per-map presence index. Position reads interpolate the Redis-stored trajectory. Every authenticated world reply carries `X-Mesh-Node-Id` (serving API instance) and `X-Mesh-Map-Owner-Node` (live lease owner, or local fallback). Each HTTP request reloads saved travel and configuration so a YARP request landing on another replica observes the preceding request. Companion positions derive from the refreshed configuration and pilot loadout. Native Unity TCP byte compatibility remains unverified.

## World-6 configuration and companions (2026-10-24)

The local world session now retains complete configuration 1 and 2 stats from `LocalHangarEquipmentStore`: speed, maximum shield, laser damage and equipment. ID 14 `ConfigurationRequest` still carries one byte (`1` or `2`); a successful change retimes any in-flight trajectory at its interpolated position and sends ID 12 `MoveCommand` plus new ID 15 `ShipAttributesCommand` (`userId:i64,configuration:u8,speed:i32,maxHitPoints:i64,currentShield:i64,maxShield:i64,laserDamage:i32`). Current shield keeps its ratio to maximum shield when switching. Switching back is blocked for five seconds; the unchanged state is reported with ID 16 `ConfigurationCooldownCommand` (`remainingMilliseconds:i32`). Selecting the already active configuration is harmless. Browser `POST /spacemap/world/config` and the optional configuration on `/spacemap/world/move` use the same rule, returning JSON 409 `cooldownRemainingMs` on an early switch. `/spacemap/world/state` includes the effective shield, speed, laser damage and companions.

Equipped Flax and Iris assignments in the active configuration spawn as drones. An equipped P.E.T. 10 spawns only when the local `LocalPet` state is enabled and has positive fuel. `LocalPetStore` persists that state per account; the default is disabled with zero fuel. Its formation name is carried with each companion. Drones and P.E.T. use server-derived offsets around the ship's interpolated coordinates. Local ID 17 `CompanionStateCommand` contains `userId:i64,count:i32` followed by `kind:string,itemId:string,index:i32,formation:string,x:i32,y:i32,targetX:i32,targetY:i32,timeToTargetMs:i32` for each companion. The target and duration let a local client interpolate each follower during ship movement. It is sent at TCP world entry when companions exist, after movement, and after configuration changes (including an empty state to despawn companions). The browser canvas draws companion markers from the same state.

The local companion formation value currently comes from `LocalPet.Formation` for both drones and P.E.T. `arrow` places alternating drones behind the ship, `line` places them in a row, and other values use a semicircle. These offsets are local visual choices, not recovered native formation rules.

The selected configuration, shield, switch timestamp and trajectory are also saved in the configured distributed cache. Re-entering through a new `WorldSessionStore` restores them, including the remaining cooldown. This is a reconnect guarantee; simultaneous cross-replica command ordering and observer delivery still require map ownership and distributed routing. See [server mesh scaling](server-mesh-scaling.md).

On TCP re-entry, `HeroInitCommand` uses the restored effective stats, current shield and interpolated position. A restored configuration 2 also gets `ShipAttributesCommand` after the hero snapshot so the selected configuration is explicit to the local client.

These IDs, geometry, balancing and JSON endpoints are emulator-defined. They have unit, loopback TCP and HTTP handler coverage, but no native Unity packet fixture or visible Unity client verification. P.E.T. activation and refueling currently have no browser control; stored state must already be enabled and fueled for it to appear.

## World-5 browser Spacemap bridge (2026-10-24)

The authenticated local menu now has a **LAUNCH** link to `GET /indexInternal.es?action=internalMapRevolution`; `internalStart` also opens that view for a signed-in pilot with a company. The page is a responsive 2D canvas with the pilot ship, name, company, hull/shield bars, home station, gates, coordinates, minimap, and configuration controls. Clicking the map or minimap posts a destination, and a short state poll renders authoritative interpolated movement. The canvas is an emulator view; it is not the native Unity renderer or recovered WebGL client.

`POST /spacemap/world/enter`, `GET /spacemap/world/state`, `POST /spacemap/world/move` (`{"x":int,"y":int,"configuration":1|2|null}`), and `GET|POST /spacemap/world/config` (`{"configuration":1|2}` for POST) require a current `dosid` cookie. The login resolver used by TCP establishes pilot identity, company, hangar and equipment. These HTTP handlers share the **same singleton `WorldSessionStore`**, `WorldMapInstance`, movement engine, trajectory cache and TCP move broadcast. State replies carry map geometry, pilot identity, current and target coordinates, ship stats, configuration and radiation warning. Movement and config POSTs require JSON. A browser state poll reuses an already-connected pilot so it does not fetch account balances and equipment on every frame. The JSON contract and canvas are emulator-defined; native Unity client compatibility remains unverified.

## World-4 local ship movement (2026-10-24)

The authenticated TCP session now accepts local ID 11 `MoveRequest` with four big-endian `i32` values in the IL2CPP constructor order `positionX,targetY,targetX,positionY`. Reported position is ignored for authority. ID 14 `ConfigurationRequest` carries one byte (`1` or `2`) and changes the active equipment configuration while retaining a current destination. Invalid payloads/configurations receive the existing malformed error and close the session. Integer coordinates cannot encode NaN or infinity; malformed lengths are rejected. Targets outside `0..21000` and `0..13100` are clamped. ID 13 `RadiationWarningCommand` carries `1` when a request targets outside the map and `0` when a subsequent in-bounds request clears that warning.

The server derives the current position by linear interpolation from the prior start coordinate, target, speed, and UTC start timestamp. Speed is the active hangar's base speed plus assigned speed generators in the selected configuration (saved catalog `speed` stat first, known generator defaults otherwise). Travel duration is `ceil(EuclideanDistance / speed × 1000)` milliseconds. Local ID 12 `MoveCommand` carries `userId:i64,x:i32,y:i32,timeToTargetMs:i32` and is sent to the pilot and connected observers on the same map. The movement trajectory is saved in the distributed cache on each request so reconnecting mid-flight restores its interpolated current position; completed travel restores its target. The client-reported position does not override this state.

These IDs, payload bytes, milliseconds and speed units are **emulator-defined**. The IL2CPP metadata supplies the four MoveRequest field names and MoveCommand constructor names, but no verified native serialization or live movement fixture. Socket tests exercise the local contract; Unity rendering and click movement remain unverified.

## World-3 local hero state (2026-10-24)

After a successful version and authenticated login, the loopback listener now sends the existing login-ok response followed by local command IDs 6 `MapInitializationCommand`, 7 `SpaceStationCommand`, two ID 8 `JumpgateCreateCommand` packets, ID 9 `SafeZoneCommand`, and ID 10 `HeroInitCommand`. The four starter maps use faction IDs 1–4, map IDs 1/5/9/13, dimensions 21000 × 13100, and home spawn `(2000,1300)`. The home station has a 1000-unit peace radius. Two provisional exit portals sit at `(19500,1300)` and `(19500,11800)` and lead to the next two map IDs. These station/portal placements and IDs are emulator choices. The hero is sent after the environment packets; the connection remains open, and the pilot is removed from the connected map when it ends.

`HeroInitCommand` is the **local** name for a ship snapshot, not an exact recovered Unity class. Its big-endian payload is `userId:i64, userName:string, shipId:string, factionId:i32, mapId:i32, x:i32, y:i32, speed:i32, hitPoints:i64, maxHitPoints:i64, shield:i64, maxShield:i64, cargoLoad:i32, cargoCapacity:i32, laserDamage:i32, experience:i64, honor:i64, level:i32, credits:i64, uridium:i64, hangarSlot:u8, equippedCount:i32`, then `itemId:string, quantity:i32` for each equipped item. Current HP and shield equal their maxima on entry, and cargo load starts at zero. Strings use the world-2 two-byte length. Map initialization has four `i32` values (`mapId,width,height,factionId`); station and portal each have four `i32` values; safe zone is one byte (0/1).

The resolver reads the account's selected company, active hangar, credits/uridium from EconomyStore, configuration 1 assignments and owned inventory through LocalHangarEquipmentStore, and pilot ranks through LocalPilotSkillStore. Numeric `damage`, `shield`, and `speed` stats on saved Shop listings take priority; otherwise local balancing defaults use Pusat Plus/Phoenix base specs from `LocalHangars.Spec`; LF-2/LF-3 damage 100/150; SG3N-A03/B02 shields 3000/5000; and known G3N speed generator bonuses 10/20/30/40. Hull, shield, and laser skill bonuses apply the Pilot Sheet's 2% per rank. The current economy schema has no XP/Honor/Level balances or speed-generator catalog entries, so this packet sends 0 XP, 0 Honor and level 1 and cannot obtain newly equipped speed generators from the shipped store. The constants and formulas are emulator-defined, not captured combat values.

`WorldSessionStore` tracks connected pilots per map in memory and stores their last map/coordinates through the configured distributed cache (Redis in ApiService). Reconnects restore in-bounds coordinates for the same map; disconnected pilots are removed from the in-memory registry. There is no movement command or position update yet, so world-3 persists the entry position only. Unit and loopback tests check packet order, map/faction mapping, saved loadout stats and position restoration. **No official Unity render or native wire compatibility was verified.** The world-2 framing, IDs and address grammar remain provisional; the installed client may not parse these packets. Visible ship and movement remain Phase 2 acceptance gaps.

## World-2 local transport implementation (2026-10-24)

`Features/World/` now contains a loopback TCP hosted service, `WorldPacketReader` / `WorldPacketWriter`, a command dispatcher, and an auth resolver. `WorldServer` configuration in ApiService defaults to enabled on `127.0.0.1:60954`, version `local-1`, with a 65536-byte packet limit; AppHost sets the same address for one replica. For multiple replicas, AppHost requests dynamic loopback ports, and each process advertises its bound port in `/xml/maps.php` and `/xml/maps.xml`. This is not pilot-affine routing; see [mesh scaling](server-mesh-scaling.md). The local catalogue now has four starter maps: MMO 1, EIC 5, VRU 9, TST 13. `/unityApi/login.php` still uses its HTTPS `host` for web content and supplies `sessionID`, `userID`, and `mapID`; the candidate TCP address is supplied by the map XML. The client address parser has not been verified to accept `host:port` in `GameServerIP`.

**The following bytes are an emulator diagnostic contract, not recovered Unity/libcom bytes.** The frame has a four-byte big-endian payload length (excluding the prefix), followed by a two-byte big-endian command ID. The provisional IDs are 1 `VersionRequest`, 2 `VersionCommand`, 3 `LoginRequest`, 4 `LoginResponse`, and 5 `LoginErrorCommand`. Strings are UTF-8 with a two-byte big-endian byte length; user ID, credits and uridium are eight-byte big-endian integers. `VersionRequest` contains a version string. `VersionCommand` contains the configured version and a one-byte Boolean. `LoginRequest` contains `userId`, `dosid`, version, and instance ID in that order. The login response contains success status, user ID, company, map ID, active hangar slot, ship ID, credits, and uridium. The error response carries a one-byte local reason (malformed, unsupported version, unknown session, expired session, company required, user mismatch, unknown command). No raw or logged session ID is emitted by the server.

The resolver checks the dosid via `IGameAuthSessionStore`, expiration, matching user ID, and a selected company before loading the active hangar from `LocalHangarStore` and balances from `EconomyStore`. It closes the connection after a login response. Loopback tests cover fragmented and adjacent frames, invalid lengths and truncation, version and login, error commands, and all four company maps. No WebSocket upgrade path was added because the saved evidence does not establish that the Unity client uses one. The listener has not been exercised with the Unity client, and the server does not yet emit native `ShipInitializationCommand`, `ShipCreateCommand`, or movement commands. A visible ship and movement are therefore still open Phase 2 acceptance work.

Before describing this as client compatible, recover the native frame and module ID values, request write order, actual port and address grammar, and a redacted version/login packet fixture. Replace the provisional command codecs against that evidence, then verify world entry and movement with the client.

## Evidence and scope

This is an **inventory and implementation plan**, not a verified wire specification. The installed Unity client's IL2CPP v31 `global-metadata.dat` has SHA-256 `a9df074bb27ea58dbd19f322a6c6b45438fe198c74cc2f78a1d33f29604e3055`; the paired `GameAssembly.dll` has SHA-256 `868729de4486860841b40b59d04fd469e510a2854be354b59d945a7eaa472107` (see [IL2CPP inventory](../_reversing/Analysis/il2cpp-network-types.md)). The table indices below are from `.venv\Scripts\python.exe _reversing\Analysis\inspect_il2cpp_metadata.py --match '<regex>'`. That script reads names, field ranges, and method parameter names. Its `type_index` values index the IL2CPP runtime type table; it does **not** decode types, method bodies, module IDs, field write order, endianness, or call sequences. Constructor parameter order is metadata order, not proven serialization order.

The saved Live login session `20261021-224636-login` has 92 HTTP entries ([summary](../_reversing/Requests/sessions/20261021-224636-login/summary.json)). Its allowlisted `/unityApi/login.php` projection is documented in [client endpoint map](client-endpoint-map.md); the credential HAR is intentionally not opened here. That session does not establish a successful world connection or a movement exchange. `_reversing/WireShark/login/capture.pcapng` exists, but REA `inspect_pcap_capture` could not start its configured `tshark` (`capability_unavailable`: Windows runner requires an absolute executable path). No packet bytes, TCP destination port, command IDs, or world traffic are claimed from it. The HTTP HAR cannot represent the raw game TCP stream.

## Host discovery and connection boundary

1. `GET /unityApi/login.php` returns outer JSON with `data` as a JSON-encoded string. Its nested fields include `userID`, `sessionID`, `host` (captured Live value was an HTTPS game origin), `mapID`, `serverInfo`, and `hangars.data.hangars`. The active hangar supplies `shipId`, `modelId`, and `mapId` for the selected ship. See `UnityLoginCapturedResponse.cs` and the allowlisted body contract in [client endpoint map](client-endpoint-map.md). **Do not equate the HTTPS `host` with the TCP world endpoint.**
2. The client metadata has `DarkOrbit.Settings.get_MapXmlUrl()` and `DarkOrbit.MapsPattern` (4944) with `ID` and `GameServerIP`, plus `Parse(root,patterns)`. Metadata literals include `xml/maps.php` and `xml/maps.xml`. This supports a map-ID to game-server-address lookup **candidate**. No Live XML body or native caller expression is recovered; the local `/xml/maps.php` and `/xml/maps.xml` catalogue is provisional and uses loopback addresses.
3. `APServiceStorm.Net.Endpoint2Server.Impl.InfinicastServerAddress` (382) has `Host`, `Port`, `Address` properties (`get_*/set_*`) and a default constructor. `Infinicast.Client.Utils.NetFactory.CreateServerAddress(address)` is another address-construction candidate. `TcpEndpoint2ServerNetLayer` (384) has `Open(settings)`, `sendLoginRequest()`, `ReadThread()`, `Write(message)`, `SendToServer(stringMessage)` and `SendToServer(messages)`, with fields `client`, `stream`, `decoder`, `encoder`, `socketIo`, `readThread`, and `UseWebsockets`. These names establish a socket transport path, but do not prove that `MapsPattern.GameServerIP` is fed into that particular `Open` call, how an address containing a port is parsed, or whether a given session uses TCP or WebSocket.
4. A **second codec family** exists: `APServiceStorm.Net.Codec.APlay.APlayProtocolDecoder` (405), `APlayProtocolEncoder` (406), `APlayMessageType` (403: payload, binary JSON, gzip, introduction, ping and pong), `CumulativeProtocolDecoder` (386), and `IoBuffer` (387). `BinaryExtensions` (385) names `ReadInt32BE`, `ReadInt64BE`, `WriteBE`, and prefixed-string helpers. This is evidence for a big-endian APlay codec implementation, **not evidence that the `net.bigpoint.darkorbit.com.module` world commands use it**. The world `TcpEndpoint2ServerNetLayer.decoder` and `.encoder` type indices (31299/36459) have not been resolved to a concrete codec. The frame length prefix width, whether it includes the prefix, byte order, module-ID width and command-body order remain unknown. Do not implement a guessed prefix as compatible.

## Command inventory

All commands below are `net.bigpoint.darkorbit.com.module.*`. Each declares `.ctor()`, the shown parameterized `.ctor`, `getLibcomModuleId()`, `getLibcomModuleEstimatedLength()`, `read(input)`, and `write(output)`. These are generated binary-module types, not PHP or JSON contracts. The metadata records return/parameter runtime type indices for `read` (23306), `write` (23314), module ID (31925), and constructor return (44399), without decoding their C# types. Common constructor indices strongly suggest 31941 = integer, 31975 = wider integer, 40171 = string, 23500 = Boolean, 39314 = floating point; these are **inferences only** until type registration or method bodies confirm them.

| Stage | Exact class (metadata index) | Fields and constructor parameters in metadata order | Evidence limit |
| --- | --- | --- | --- |
| Version request | `VersionRequest` (14863) | `version`; `.ctor(version)` | String-like index 40171. Actual version value and wire ID unknown. |
| Version reply | `VersionCommand` (14862) | `version`, `equal`; `.ctor(version,equal)` | `equal` uses Boolean-like 23500. Reply ordering relative to login unknown. |
| Login request | `LoginRequest` (14339) | fields `userID,factionID,sessionID,version,instanceId,isMiniClient`; `.ctor(userID,factionID,sessionID,version,instanceId,isMiniClient)` | Likely carries the authenticated session ID. Numeric conversion, faction source, version and instance ID unknown. Never log `sessionID`. |
| Login result | `LoginResponse` (14341) | `status`; `.ctor(status)` | Status is a module/enum-like index 49164. Values unknown. |
| Login failure | `LoginErrorCommand` (14338) | `errorCode,mapId`; `.ctor(errorCode,mapId)` | Error enum-like index 49160; meanings unknown. |
| Hero state | `ShipInitializationCommand` (14704) | `.ctor(userId,userName,typeId,speed,shield,shieldMax,hitPoints,hitMax,cargoSpace,cargoSpaceMax,nanoHull,maxNanoHull,x,y,mapId,factionId,clanId,expansionStage,premium,ep,honourPoints,level,credits,uridium,jackpot,dailyRank,clanTag,galaxyGatesDone,useSystemFont,cloaked,refreshMap,captainEnergy,maxCaptainEnergy,modifier)` | This is the closest metadata match for hero initialization; **no `HeroInitCommand` exact class was found**. `typeId` is string-like, perhaps a ship loot ID, but equivalence is unproved. `hitPoints`, `hitMax`, `ep`, `honourPoints`, `credits`, `uridium` use wider-integer-like 31975; `jackpot` uses float-like 39314. There is `dailyRank`, not a `rings` field; `galaxyGatesDone` may relate to rings but is not verified. |
| Ship spawn | `ShipCreateCommand` (14697) | `.ctor(userId,typeId,expansionStage,clanTag,userName,x,y,factionId,clanId,dailyRank,warnBox,clanDiplomacy,galaxyGatesDone,useSystemFont,npc,cloaked,motherShipId,positionIndex,designSet,modifier,minimapStrategyModule,specialNpcType)` | Has identity, visual type, position, faction and clan fields. It lacks HP/shield/cargo, which are in initialization or other updates. Whether hero also receives this command is unobserved. |
| Client movement | `MoveRequest` (14435) | fields and `.ctor(positionX,targetY,targetX,positionY)` | Four integer-like positions. **Constructor order is unusual**; use the observed order, never reorder from naming intuition. No user ID or speed field. |
| Server movement | `MoveCommand` (14434) | `.ctor(userId,x,y,timeToTarget)` | Four integer-like values. `timeToTarget` is likely a duration, but milliseconds are not proven by the name alone. |
| Hero position | `HeroMoveCommand` (14235) | `.ctor(x,y)` | Possible separate authoritative hero update; relation to `MoveCommand` unknown. |

`ShipInitializationCommand` fields include all constructor names above. Its field list is not identical in order to that constructor. Therefore neither field declaration order nor constructor order can serve as a confirmed byte layout. `modifier` is a nested module-like value (constructor type index 16839), and `clanDiplomacy` and `minimapStrategyModule` are also module-like; their encodings and null markers are unknown.

## Map and visible-world commands

These names identify likely rendering inputs, not a demonstrated startup sequence:

| Class (metadata index) | Constructor names / purpose candidate |
| --- | --- |
| `MapChangedCommand` (14360) | `newMapId`; map transition cue. |
| `JumpgateCreateCommand` (14287) | `gateId,factionId,designId,x,y,isVisibleOnMinimap,showBubble,decoratorAssetTypes`; portal placement. |
| `SpaceStationCommand` (14730) | `peaceRadius,posY,type,id,posX`; station placement. |
| `AssetCreateCommand` (13899) | `type,userName,factionId,clanTag,assetId,designId,expansionStage,posX,posY,clanId,invisible,visibleOnWarnRadar,detectedByWarnRadar,showBubble,clanRelation,modifier`; world asset placement. |
| `AssetInfoCommand` (13901) | `assetId,type,designId,expansionStage,hitpoints,maxHitpoints,shielded,shieldEnergy,maxShieldEnergy`; asset stats. |
| `AddMapEntityCommand` (13849) | `x,hash,y`; generic map entity candidate. |
| `MapAddPOICommand` (14353) | `poiId,poiType,poiTypeSpecification,design,shape,shapeCoordinates,inverted,active`; point-of-interest geometry. |
| `MapAddControlPOIZoneCommand` (14351) | extends the POI constructor with `progressOwner,captureProgress,captureThreshold,timeTillUnlock,captured`. |

For ship visibility, `ShipCreateCommand.typeId` must ultimately resolve to client visual assets, but neither an asset manifest mapping nor an observed render has been established. `ShipInitializationCommand.typeId` and the active HTTP hangar's `shipId`/`modelId` are different contract surfaces; their equality must be measured, not assumed. Initial map entities, portals, and stations may be optional for first ship rendering, but this has not been tested.

## C# implementation design for ApiService

1. Add a dedicated world TCP listener/service, separate from HTTP route DTOs. Bind the configured game-server address and port from a validated map catalogue. The current `LocalMapsXml.GameServerIP = 127.0.0.1` entries have no port and cannot establish a usable world endpoint. Add a port only after capture or native method analysis determines the client's address grammar and default.
2. Define `WorldFrameReader` over `PipeReader` and `WorldFrameWriter` over `IBufferWriter<byte>` with a configurable, **evidence-selected** prefix format. Handle partial reads, multiple frames per read, zero/oversize lengths, EOF, cancellation and per-connection limits. Keep framing independent from command serialization. Record the exact prefix bytes and endianness in a sanitized fixture before enabling the listener.
3. Define a `LibcomReader`/`LibcomWriter` for primitive values and nested modules, plus a registry keyed by verified numeric `getLibcomModuleId()` results. Each codec follows the command's actual `read`/`write` body order recovered from native code or paired packet fixtures. Unknown IDs reject safely; do not silently dispatch by class name. Keep `sessionID` redacted in diagnostics and validate it against `IGameAuthSessionStore` before sending hero state.
4. Model login/version, `ShipInitializationCommand`, `ShipCreateCommand`, `MoveRequest`, and `MoveCommand` as separate command contracts. Populate hero values from a consistent saved account, active hangar and starter map; avoid copying synthetic menu stats into the packet without verifying the game's expected value ranges. On a movement request, validate coordinates against map bounds, compute a server-authoritative destination and travel time from speed, then broadcast the verified move command to interested clients. Preserve `positionX,targetY,targetX,positionY` in the decoder.
5. Verify in order: a token-free frame fixture; a redacted version/login fixture; world entry that visibly renders the ship; a single click movement and resulting server update; then other map entities. Test codec round trips against captured bytes and malformed frames. No protocol helper or listener was added in world-1, so no speculative unit tests are introduced.

## Open evidence needed for world-2

- Resolve `TcpEndpoint2ServerNetLayer` decoder/encoder and `socketIo` runtime types and inspect their native `Open`, `ReadThread`, `Write`, `sendLoginRequest` bodies. Establish whether the APlay codec and the bigpoint module codec are separate paths.
- Recover numeric module IDs, exact read/write order, primitive encodings, nested module/list format, frame length prefix and endianness from native bodies or redacted binary samples.
- Observe the **actual** map XML response, chosen world host and TCP port, and post-login command sequence. The saved HTTP login session alone cannot do this. A new Live capture requiring a Windows mitmproxy CA installation is outside this run's rules; no such install or capture was attempted.
- Verify `ShipInitializationCommand` versus `ShipCreateCommand` responsibilities, visual `typeId`, optional map commands, coordinate ranges and whether `timeToTarget` is milliseconds. A visible ship and one movement are the acceptance evidence for Phase 2.
# Mesh-2 world ownership status (2026-10-24)

The local world listener publishes its bound TCP port through the Redis-backed mesh node heartbeat. The coordinator assigns maps 1–16 and transfers leases on scale-out, drain, or heartbeat expiry, with an incrementing epoch on ownership changes. World entry and movement do not yet check map ownership or lease epochs, so this is coordination groundwork rather than safe multi-replica world play. See [server mesh scaling](server-mesh-scaling.md).

# World-7 portal travel and boundaries (2026-10-24)

The local 21000 × 13100 map catalog now serves faction starter maps 1/5/9/13 and neighboring maps 2/6/10/14 (with remaining faction-sector IDs accepted). Starter gates at (19500,1300) lead to the next map and arrive at (2500,1300); a second gate at (19500,11800) leads two maps ahead. Neighbor maps return through a gate at (1500,1300), arriving at (18500,1300). Stations have a 1000-unit peace radius. These coordinates and IDs are emulator defaults pending native map evidence.

Local `JumpRequest` (18) carries an int32 portal ID. The server checks the interpolated position is within 500 units, persists destination map and arrival, and sends `MapChangeCommand` (19): int32 map ID, string destination node ID, string destination TCP host:port. A jump on the local owner follows with map initialization, station, portals, safe zone and `HeroInit`. A jump to another owner closes the source socket after the map change; reconnect resolves the shared arrival state on the destination. Invalid or distant gate requests receive the local malformed error. Native Unity command compatibility remains unverified.

Out-of-bounds movement is clamped and sends the existing radiation warning. A boundary attempt removes 1% of maximum hull, to a minimum of 1 HP, at most once per second. A stationary ship within its station peace radius repairs 2% of maximum hull per state poll. Hull and hazard time are stored in the distributed runtime snapshot.

# Mesh-4 admin world controls (2026-10-24)

`GET /api/v1/backoffice/world/overview` lists the 16 map owners and currently present pilots from distributed Redis presence. `POST /api/v1/backoffice/world/pilots/{userId}/teleport` accepts `mapId`, `x`, `y`, and `repair`. It atomically replaces the distributed map position, clears travel, moves presence, and optionally restores hull and shield. It operates on pilots present within the two-minute presence window. Native TCP clients are not transparently redirected to a new node; they must reconnect for a cross-node teleport.

## Mesh-5 world operations (2026-10-24)

The CMS world pilots table reads the distributed `/api/v1/backoffice/world/overview` response and shows user ID/name, ship, map and saved X/Y coordinates. Its teleport form sends `{mapId,x,y,repair}` to `/api/v1/backoffice/world/pilots/{userId}/teleport` and refreshes the overview. These operations use emulator admin APIs; no native Unity protocol bytes were changed or verified.

# World-9 ambient NPC and combat contract (2026-10-24)

## Task 5 provisional rocket and autonomous NPC extension (2026-10-28)

Native Unity packet IDs and payloads remain unverified. The emulator adds `RocketFireRequest=38` (`i32 npcId`), `RocketHitCommand=39` (same five fields as laser hit), and `NpcFireCommand=40` (`i32 npcId,i64 targetUserId,i32 damage,i64 hp,shield`). TCP rockets require the current target lock. HTTP `/spacemap/world/fire` accepts optional `weapon: "rocket"`; omission uses laser. Rocket range, fixed damage and cooldown are `WorldNpcs` options. Both weapons use the same durable NPC vitality and killer-only reward retry path.

Iteration 2 adds provisional `LaserStopRequest=41` (empty payload). A successful NPC `LaserFireRequest=27` starts shared, server-timed fire after its immediate hit. The map lease owner repeats hits at `WorldNpcs.LaserCooldownMilliseconds` (default 1,000 ms) through the existing damage and reward path. A stop request, new target selection, invalid target lock, map change, lost range, death, or destroyed target ends repeated fire. The Flash reference suggests the start/stop behavior, but these Unity packet IDs and timing are emulator-defined pending a native capture.

The map lease owner's NPC tick selects the nearest eligible pilot from shared map presence, pursues within aggro range, and applies shield-first damage on its cooldown. The NPC position and target live in the cycle-scoped shared vitality record; pilot hull and shield are saved in shared runtime. Station peace zones suppress targeting. These behavior and wire rules are **provisional emulator contracts**, not observed official client behavior.

`WorldNpcs` supplies deterministic patrol positions on maps 1–16. `WorldNpcs:Templates` configures default Streuner, Lordakia and Saimon count, model ID, speed, HP, shield and reward values; `WorldNpcs:MapTemplates:{mapId}` can replace that list per map. The current 300-second spawn cycle and combat vitality are stored in distributed cache, and damage is serialized with the mesh mutex. The map lease owner sends NPC movement each second to local TCP observers within 5,000 units. The browser Spacemap polls `/spacemap/world/state` and draws the same NPC positions.

The **local provisional** TCP commands are `ShipCreateCommand=24` (int32 ID; strings kind/model; int32 x,y,hp,maxHp,shield,maxShield), `SelectShipRequest=25` (int32 ID), `TargetLockCommand=26` (int32 ID,hp,shield,maxHp,maxShield), `LaserFireRequest=27` (int32 ID), `LaserHitCommand=28` (int64 attacker; int32 ID,damage,hp,shield), `ShipDestroyCommand=29` (int32 ID), and `NpcRewardCommand=30` (int32 ID,credits,uridium,experience,honor). NPC patrols use the existing `MoveCommand=12` layout with NPC ID as int64. Login and map change send currently visible NPC creation packets. The owner also emits creation when a spawn reappears. Selection is per TCP connection; fire requires that selection. Combat range is 1,000 units by default and uses the pilot's configured laser damage against shield before hull.

The exact native Unity commands and framing remain unverified. This is an emulator contract and should not be mistaken for a captured packet fixture.

## World-11 TCP lease handoff (2026-10-24)

The provisional TCP listener restores a pilot's saved map before admitting a login and checks the current map lease before every connected command. A wrong-node login or a socket whose owner/lease epoch changed receives the existing local `MapChangeCommand` (19: map ID, owner node ID, TCP host:port) and closes. The old socket no longer accepts movement, configuration, jump, collection, target, or fire requests. The new owner restores position and runtime from distributed state on reconnect. An unavailable owner yields an empty route in that packet. Simulated development nodes share their physical listener.

This is command-boundary fencing, not an atomic epoch check inside each distributed state write. Browser world requests can still reach any API replica and are not owner routed. The packet remains an emulator-defined diagnostic contract; native Unity handling of `MapChangeCommand` is unverified.

# Admin-var-1 runtime tuning (2026-10-24)

The emulator now reads shared runtime overrides during movement, configuration switching, portal jumping, base repair, radiation damage, NPC attack/reward handling, and collectible pickup. These values are emulator-defined balancing settings; no native Unity packet field for changing them has been confirmed. A changed movement multiplier applies on the next move or configuration switch. An already running travel trajectory retains its saved speed until then.

The default value `0` for `world.radiation.damage_per_attempt` and `world.station.repair_hp_per_tick` preserves the prior percent-of-hull calculation; a positive value supplies an absolute amount. Runtime ship base-speed changes affect the next movement command for an already connected pilot. Browser world state reports the current speed, damage, station radius, and portal radius.

The provisional TCP entry packet now includes the effective station peace radius and ship speed/damage on connection or map entry. Changes made during an open socket are read at the next relevant command; no new native push packet for an admin change has been identified.

The shop listing response and purchase debit read `economy.shop_discount_percent` from the same registry. Existing sessions must request a fresh listing to see a new displayed price.

# Admin-var-2 world controls (2026-10-24)

The local admin dispatcher can teleport or heal an online pilot, grant economy balances, create a Redis-backed manual box/ore/NPC spawn for one hour, and publish a shared announcement for ten minutes. Browser `/spacemap/world/state` includes `announcement` and manual entities in `boxes` and `npcs`. The local TCP listener sends manual entities on entry; the owner node simulation also emits create packets for new spawns. New provisional command 31 carries a length-prefixed announcement string. These are emulator-only packets and have not been verified against native Unity.

# Admin-var-3 in-game admin protocol (2026-10-24)

The provisional local TCP protocol accepts `AdminCommandRequest` (32) after login: a length-prefixed UTF-8 command string (maximum 1024 bytes), including slash forms. Only `WorldPilot.IsAdmin` connections may execute it. Others receive `LoginErrorCommand` with `Unauthorized` (8). The success `AdminCommandResponse` (33) contains byte `1` and a two-byte big-endian length followed by JSON `AdminCommandResult` (up to 60000 bytes); a command error contains byte `0` and a short error string. Successful commands are followed by refreshed `ShipAttributesCommand` and world entry packets. This is an emulator-defined protocol; native Unity packet handling is unverified.

# Admin-var-4 CMS controls (2026-10-24)

The CMS uses the existing backoffice HTTP registry and command dispatcher. No TCP frame or command ID changes. Direct world-socket observation after a CMS edit remains open.

# World-22 remote browser move to idle TCP socket (2026-10-24)

An admitted provisional TCP socket polls its own saved distributed travel while idle. If a browser move was handled by another API replica, it broadcasts one `MoveCommand` with the saved destination and remaining duration to local TCP observers on that map. The trajectory fingerprint suppresses repeat packets; local moves already broadcast directly. A two-store shared-cache loopback socket test decoded the packet. This does not establish native Unity packet compatibility or deliver moves to observers connected to other nodes.


# World-23 cross-node movement to TCP observers (2026-10-24)

During its one-second idle poll, each admitted provisional TCP socket now reads the shared presence index for its map. For every live pilot that is not connected on this node, it reads that pilot's saved distributed travel. Each new trajectory produces one `MoveCommand` sent only to that observer, using the existing payload `int64 userId, int32 targetX, int32 targetY, int32 remainingMs`. Fingerprints are tracked per observer and cleared on leave. Finished trajectories (remaining distance 0) are recorded without being sent. Two session stores sharing one cache simulated two nodes in a connected loopback test, and the TCP client decoded the packet. This does not establish native Unity packet compatibility, and pilots on other nodes still get no `ShipCreate`/spawn packet.
# World-24 remote pilot visibility (2026-10-24)

An admitted provisional TCP socket polls shared map presence while idle and sends `PilotCreateCommand` (34) for each newly visible remote pilot before its `MoveCommand`. The payload is big-endian `int64 userId`, length-prefixed UTF-8 `name` and `shipId`, then `int32 factionId,mapId,x,y`. The position is interpolated from shared travel when moving. `PilotRemoveCommand` (35) carries `int64 userId` when the remote presence disappears or its saved map changes. Per-observer visibility is local socket presentation state; presence and position remain distributed. A connected loopback client decoded create, move and remove in that order. Native Unity packet compatibility and rendering remain unverified.


# World-25 map-level remote observer fanout (2026-10-24)

No packet layout changed. Remote `PilotCreateCommand`, `MoveCommand` and `PilotRemoveCommand` for pilots on another node are now derived from a node-local per-map snapshot (presence index plus each remote pilot's saved position/travel) that is shared by every idle observer on that map and expires after `WorldSessionStore.RemoteSnapshotTtl` (500 ms, below the 1 s idle poll). Remote-move and remove latency gains at most that TTL. The existing connected loopback tests still decode create, move once, and remove on a real socket.

# World-27 browser state uses the map snapshot (2026-10-24)

No packet or JSON layout changed. The `pilots` array in emulator-only `GET /spacemap/world/state` (and the `enter`/`move`/`config` responses that share it) now comes from `WorldSessionStore.VisiblePilotsAsync(mapId)`. This uses the same per-node, per-map `RemoteSnapshotAsync` as World-25 TCP observers instead of `ConnectedAsync`. Positions come from shared travel, or saved position when the pilot is idle, interpolated at response time. Pilots with a local TCP socket use in-memory travel. A pilot whose saved map differs from the polled map is omitted. Each poll still refreshes the caller's own presence heartbeat. A newly entered or departed remote pilot can appear or disappear up to `RemoteSnapshotTtl` (500 ms) later. Admin and mesh overviews keep the uncached `ConnectedAsync`.

# World-31 CLI runtime variable to TCP ship attributes (2026-10-24)

No packet layout changed. After a Server Management CLI `var set world.movement.speed_multiplier 2`, an idle connected socket receives the provisional `ShipAttributesCommand` (`int64 pilot, byte, int32 speed, int64, int64, int64, int32 laser`) with speed 600 (base 300 x 2), and after `var rollback` it receives speed 300. The packet comes from the session's existing runtime-variable refresh poll; no reconnect or login is needed.
# World-48 rollback conflict at client surfaces (2026-10-24)

The Backoffice runtime-variable rollback endpoint now maps the store's stale-change `InvalidOperationException` to HTTP 409 with `{ "error": "Rollback conflict: …" }`, matching the admin command and in-game routes. The shipped CMS and local Spacemap admin panel reload current values and history after a 409 while retaining the server's explanation. Live Kestrel checks submitted an obsolete change #1 after change #2 restored the peace radius; both clients displayed the conflict, kept radius 1000, and recorded no third change. The test writer uses an in-memory cache and history; World-47 separately verified the production PostgreSQL/Redis conflict with the CLI and TCP client. Native Unity compatibility remains unverified.


# World-49 login baselines taken before the entry burst (2026-10-24)

No packet layout changed. `WorldSessionHandler` now records the idle-refresh baselines (advertised `ShipAttributesCommand` speed/laser and the observed saved trajectory from `PrimeObservedTravelAsync`) **before** writing the entry packets, and the entry `ShipAttributesCommand` reuses that same `LiveStatsAsync` result. Previously both baselines were read after the entry burst had been flushed. A runtime-variable change or another replica's move that landed in that window was absorbed into the baseline, so the connected client never received the `ShipAttributesCommand`/`MoveCommand`. The worst case is now a duplicate announcement, not a missed one. Verified on loopback TCP clients; native Unity remains unverified.

# World-53 idle NPC roster sync (2026-10-24)

No packet layout changed. Before this change an admitted provisional TCP socket received NPC `ShipCreateCommand` (24) only on login or map change. It never learned that the shared NPC set had changed: a respawn cycle, a `world.spawn.*_count` runtime variable set/rollback from the CLI, CMS or in-game admin, an admin-event spawn, or a kill handled by another replica. Each socket now keeps the NPC ids it has been shown (`WorldConnection.KnownNpcs`). The one-second idle poll compares that set against `WorldNpcs.VisibleAsync(mapId)` from the shared cache and sends `ShipCreateCommand` for new ids and `ShipDestroyCommand` (29, `int32 npcId`) for ids that disappeared. If the destroyed id is the socket's selected target, the target is cleared. A kill on the local node writes a tombstone to every local observer before its `ShipDestroyCommand` broadcast. This stops the idle poll from destroying the NPC again or recreating it from a stale read. The tombstone is removed when a poll sees the id absent, so a later respawn with the same id is sent as new. Verified with a connected loopback TCP client (`Loopback_IdleSocket_SyncsNpcRosterFromVariablesAndRemoteKills`): spawn count 1→2 decoded a second `ShipCreate`; a second `WorldNpcs`/`WorldSessionStore` pair on the same cache (a second node) killed that NPC, and the client decoded its `ShipDestroy`; spawn count 0 decoded `ShipDestroy` for the first NPC. Native Unity compatibility remains unverified.

## World-54 Server Management spawn control on an open TCP client (2026-10-24)

The shipped management CLI sent `var set world.spawn.streuner_count 2`, then `0`, then rolled back the zero change through a live Kestrel admin route. An already connected loopback TCP world client received the added Streuner `ShipCreateCommand`, two `ShipDestroyCommand` packets, and two restored `ShipCreateCommand` packets without reconnecting. NPC IDs matched across remove and restore. The in-memory test registry recorded all three changes as `ServerManagement`; production PostgreSQL/Redis and native Unity wire compatibility were not checked.


## World-55 idle NPC vitals sync (2026-10-24)

New provisional `NpcHealthCommand=37` (int32 NPC ID, HP, max HP, shield, max shield; big-endian). The once-per-second idle roster sync now remembers the HP and shield each socket was last sent for each NPC. If the shared value differs, the sync sends command 37. This covers damage applied on another replica, where the local `LaserHitCommand` broadcast never reaches this socket, and survivors reset by a new spawn cycle. Local hits record the broadcast vitals for every observer on the map, so they do not cause a repeat. `ShipCreateCommand`, login and map change reset the record. The emulator defines command 37; it is not a captured native packet.
## World-56 shared NPC observer snapshot (2026-10-24)

TCP login and idle roster/vitals sync now use one 500 ms read-only NPC snapshot per map per API node. Selection and attack still read the distributed cache directly; a local hit evicts the node snapshot. The provisional create/destroy/health packet layouts are unchanged. Eight concurrent observers caused two NPC cache reads total (two spawned NPCs), then two more after expiry; a remote hit's shield value appeared after expiry. Existing loopback TCP roster and vitals cases passed. This is emulator protocol behavior, not a captured Unity packet.

## World-59 TCP reward parity (2026-10-24)

On a lethal laser hit, provisional `NpcRewardCommand=30` now sends the same `credits`, `uridium`, `experience`, and `honor` values that were credited after runtime multipliers. Packet layout remains five big-endian int32 values: NPC ID followed by those four amounts. A connected loopback TCP client decoded `(NPC ID, 800, 3, 600, 8)` for a Streuner whose base reward was `(400, 1, 400, 2)`; the reward store received the same adjusted amounts. This is an emulator packet, with no native Unity compatibility claim.

## World-60 reward retry snapshot (2026-10-24)

A lethal hit now saves the multiplier-adjusted four-asset reward alongside NPC HP, shield, killer and destroyed flag in the shared NPC cache entry, before attempting economy credit. A retry by the same killer reuses those amounts even when an admin changes reward multipliers afterward. Existing cached kills without a reward field retain the old recalculation fallback. The Spacemap HTTP client observed a lethal reward response followed by a 409 `destroyed` retry; the live host changed the credits multiplier between requests and recorded the original 400-credit amount on both credit attempts. The economy ledger's per-asset idempotency keys remain unchanged. No TCP packet or HTTP payload layout changed.

## World-61 distant kill retry (2026-10-24)

The destroyed NPC and original killer are checked before laser range for a repeat fire request. The same killer can therefore retry economy credit after moving away; a new attack on a living NPC still requires laser range. The retry retains the saved reward snapshot and returns `destroyed`. The shipped Spacemap client observed this over live HTTP after the pilot was teleported beyond range. No packet or payload layout changed.

## World-62 killed NPC retry after spawn removal (2026-10-24)

The emulator caches the killed NPC snapshot with its reward and killer for the respawn cycle. A retry by that killer can now reattempt idempotent credit when `world.spawn.streuner_count` becomes zero and the NPC is absent from the visible roster. Live attacks still require a spawned NPC and range. The shipped Spacemap page observed the NPC absent from `/state`, then received `409 destroyed` from `/fire`; the original 400-credit amount was retried. This is an emulator HTTP contract, not a captured Unity packet.

## World-69 selected box claimed in another tab (2026-10-24)

The shipped emulator Spacemap page now ends a pending pickup with “Bonus box is no longer available” when a world state poll no longer lists the selected box, or `/collect` returns `claimed` or `missing`. It does not retry the unavailable box. A live Kestrel check had a second authenticated client claim the box before the first page's next poll; the page rendered the outcome and sent no collect request. No HTTP field or TCP packet changed. Native Unity/Vuplex behavior remains unverified.

## World-71 target selection cancels a pending pickup (2026-10-24)

The local Spacemap page clears its selected bonus box when a map click selects an NPC. The ship may finish its existing flight, but state polls no longer issue an automatic `/collect`. A live Kestrel check ran the shipped page in jsdom, sent `/move` toward a box and `/target` for a Streuner, then polled until the pilot entered pickup range. The box remained visible, the target appeared in the DOM, and no `/collect` was sent. This changes client behavior only; no packet or endpoint contract changed. Native Unity/Vuplex remains unverified.

## World-72 gate jump clears old-map pickup intent (2026-10-24)

After a successful `/spacemap/world/jump`, the local Spacemap page clears its pending box and NPC target before rendering the destination map. A live Kestrel/jsdom client selected a map-1 box, sent `/move`, jumped through a gate to map 2, then polled `/state`. The page retained its jump status and sent no `/collect`. A failed jump leaves the current selection intact. No HTTP or TCP contract changed; native Unity/Vuplex behavior remains unverified.

## World-73 late state response after gate jump (2026-10-24)

The local Spacemap page increments a map epoch after a successful jump. A state poll started on the previous map cannot then replace the destination map, trigger an old-map pickup, or overwrite the jump status when its response arrives late. The same check applies after an in-flight pickup response. A live Kestrel/jsdom client held a real map-1 `/state` response, jumped to map 2, released the old response, and observed map 2 and the jump status remain rendered with no `/collect`. No HTTP or TCP contract changed; native Unity/Vuplex handling remains unverified.




## World-94 stale state poll after configuration switch (2026-10-24)

The local Spacemap page now counts completed configuration requests. A `/state` poll that started before a configuration request finished is discarded, and the page waits for any in-flight poll before its follow-up refresh. Previously the in-flight poll could repaint the old configuration and the follow-up refresh was skipped while that poll was running. `verify-spacemap-config-stale-poll-live.mjs` held a real `/state` response with configuration 1, clicked configuration 2 (`POST /config` 200), then released the held response. The DOM kept configuration 2 pressed after one fresh `/state`. No HTTP or TCP contract changed; native Unity/Vuplex remains unverified under the no-CA rule.

## World-96 jump conflict mesh owner header (2026-10-24)

`POST /spacemap/world/jump` now re-stamps `X-Mesh-Node-Id` / `X-Mesh-Map-Owner-Node` after reloading the shared pilot state, like move, config, collect, target and fire already did. Before, a replica holding a stale local pilot (map 1) answered `409 map_changed` with map 1's lease owner even though another replica had already jumped the pilot to map 2, so a router following the header would be sent to the old map's owner. No body or TCP contract changed.
# Local pilot destruction and repair

NPC damage may reduce a pilot to zero hull. At zero hull, the local world rejects movement, configuration changes, jumps, NPC fire, and box collection; station repair does not revive the ship. The emulator Spacemap shows a **Repair at home base** button that calls authenticated `POST /spacemap/world/respawn` with an empty JSON object. A successful response is the ordinary world state at the faction's first map and home station, with ten percent hull and zero shield. The action is single use until another destruction. This HTTP flow and repair amount are provisional local rules; no native Unity death packet is claimed.

Iteration 15 adds `{"location":"death"}` to that HTTP action on standard maps. A lethal NPC hit freezes travel at the hit position, and this choice repairs at those persisted coordinates on the same map. Gate maps reject the death-location choice. Omitted `location` or `"base"` retains the home-base result; unknown values return HTTP 400. The local death-location choice is currently free, pending an atomic repair purchase rule. Both choices remain local HTTP flows, not Unity packet claims.
