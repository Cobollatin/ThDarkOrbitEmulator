# Server mesh scaling status

## World-97 map conflict owner routing (2026-10-24)

All emulator `map_changed` responses reload the pilot from distributed session state before advertising a map owner. This prevents a delayed command on one replica from routing its retry to the source map's node after another replica completed a jump. The live client check uses two Kestrel replicas with a shared distributed-memory cache and mesh registry; separate OS processes and Redis connections were not part of this check. No new process-local authority was introduced.

## World-95 stale configuration command (2026-10-24)

An optional source `mapId` on the emulator HTTP configuration command is checked both at admission and after shared pilot state is restored under the mesh mutex. A delayed old-map click cannot change ship configuration following a jump. The live check used one Kestrel host and two authenticated HTTP clients; separate Redis processes were not exercised for this race. No authoritative process-local state was added.

## World-93 browser configuration ordering (2026-10-24)

One Spacemap page now serializes its configuration clicks before they reach the mesh and refreshes server state after each reply, including cooldown conflicts. This client ordering does not coordinate commands from separate tabs or replicas; the distributed cache remains the authority for the accepted configuration and cooldown.

## World-92 collectible pickup after map transition (2026-10-24)

The optional source `mapId` on browser pickup is checked after the pilot is restored from distributed session state while the shared mesh mutex is held. An old-map pickup cannot claim a box or credit a reward after a jump. The live page check used one Kestrel host and two authenticated HTTP clients; separate Redis processes were not exercised for this race. No authoritative process-local state was added.

## World-91 laser fire after map transition (2026-10-24)

An optional source `mapId` on emulator browser Fire is compared again under the shared NPC mutex after the pilot is restored from distributed session state. A delayed old-map shot cannot damage an NPC on the new map. No authoritative process-local state was added. The direct browser check used one live Kestrel host and two authenticated HTTP clients; separate Redis connections and separate processes remain unverified for this race.

## World-90 target selection after map transition (2026-10-24)

An optional source `mapId` on emulator browser target requests is checked again while the shared NPC mutex is held and the pilot is restored from distributed session state. A delayed old-map target cannot be accepted after a jump. The direct browser check used one live Kestrel host and a second authenticated HTTP client. Independent Redis connections and separate processes remain unverified for this specific race. No process-local authoritative state was added.

## World-89 browser target request ordering (2026-10-24)

One Spacemap page now serializes its NPC target requests and sends the newest queued selection after the active request finishes. The server's existing distributed NPC selection remains authoritative; no process-local world state or runtime-variable state was added. The live check used one Kestrel host, so target ordering across independent client instances and separate Redis replicas remains unverified for this change.

## World-88 stale jump across clients (2026-10-24)

The browser jump now carries `mapId`, and both the HTTP admission check and the session store's under-mutex refresh reject an obsolete source map. This prevents a delayed jump from applying to the pilot's new map, including when two API replicas share the session cache. The focused live client check used one Kestrel host and two authenticated clients; separate Redis connections and a deployed AppHost were not exercised for this change.

## World-87 delayed combat UI result (2026-10-24)

The laser response guard compares the page's map and input sequence plus selected NPC before rendering. It adds no process-local world authority or storage; NPC attacks still use the existing distributed state. The live check used one Kestrel host, so cross-process delivery and native Unity remain open for this case.

## World-86 laser input ordering (2026-10-24)

The Fire request guard is local to one browser page and affects only pending UI input. NPC health, kills and rewards remain in the existing distributed world state and economy ledger. The live client check used one Kestrel host; it does not establish cross-process behavior or native Unity compatibility.

## World-85 movement conflict reconciliation (2026-10-24)

The Spacemap page now reconciles a `map_changed` movement conflict from the shared world state immediately, without waiting for a periodic poll. A map-epoch guard prevents an older conflict refresh from replacing a newer map view. The direct check used one Kestrel host and two HTTP clients; separate Aspire/Redis processes were not exercised for this case. Server ownership, travel persistence, and runtime-variable storage are unchanged.

## World-84 externally observed map handoff (2026-10-24)

When a second client moves a pilot to another map, the Spacemap page's next state poll advances its local map epoch and invalidates pending old-map combat UI work. Live Kestrel checks used one shared world host and a second HTTP client, not separate Aspire or Redis processes. The server mesh and persisted variable contracts are unchanged.

## World-83 movement revision on real Redis (2026-10-24)

Two Kestrel replicas used separate `ConnectionMultiplexer` and `RedisCache` instances plus the production `RedisServerMeshMutex`, backed by one Redis container. A move on replica A changed the shared `world:position:73` revision. The shipped browser page on B sent its older revision, received `409 move_changed`, refreshed, and retried. The final browser destination and Redis travel target were `(17850,11135)`. This closes the World-82 real Redis verification gap; it does not exercise separate OS processes or native Unity.

## World-82 movement revision across replicas (2026-10-24)

`WorldTravel.MoveId` is saved in the shared distributed `world:position:<userId>` value. A browser move includes the revision it last saw. Each replica refreshes this value and compares revisions while holding `IServerMeshMutex`, then writes a new GUID with the new destination. This prevents a stale second client on another replica from overwriting a newer destination. The shared-cache two-store check uses an in-process mutex; separate-process Redis remains to be verified for this change.

## World-81 ordered page moves (2026-10-24)

The shipped page serializes movement requests from one page instance, so its second click cannot overtake its first request at different API replicas. Only the latest waiting click is sent. This is a client ordering guarantee for one page instance; other clients and direct HTTP callers can still submit concurrent moves in reverse server order. The live check used one Kestrel host with the shared-state path; separate-process Redis replica ordering remains open. No state was added to a single-process static store.

## World-80 stale map movement (2026-10-24)

The browser move contract now carries the displayed source `mapId`. `WorldSessionStore.MoveAsync` refreshes the pilot from shared state and rejects a mismatched map while holding its movement gate and mesh mutex. The same check protects an optional configuration switch in that request. The direct live client check used one Kestrel host with the production shared-state code path; separate-process Redis replicas and reverse ordering of two moves on the same map remain open.

## World-79 pending browser movement (2026-10-24)

The local browser suppresses state polls until outstanding movement requests settle, preventing a stale snapshot from repainting during a move that has not reached its server. This is client-local ordering. The live check used one Kestrel host; authoritative reverse movement processing across mesh replicas still needs a server-side solution and direct verification. Shared world state and runtime-variable stores are unchanged.

## World-78 same-map state response order (2026-10-24)

The shipped local Spacemap client discards state polls started before a newer map click, so delayed replies cannot repaint an older ship destination. The live check used one Kestrel host; it does not resolve reverse server processing order across mesh replicas or add lease-owner routing. Shared runtime variables remain backed by PostgreSQL and Redis as described below.

## World-77 browser movement response order (2026-10-24)

The Spacemap browser discards an older map-click response after a newer click has rendered. This is browser-local sequencing; movement and runtime-variable authority remain in the existing shared stores. The forced-order check used one Kestrel host. Concurrent requests processed in reverse order by different replicas remain an open server-side ordering gap.

## World-76 browser jump submission (2026-10-24)

The Spacemap browser now keeps a pending gate jump single-flight, so a repeated click cannot submit a competing transition from the same page. This is browser-local control; authoritative map and session state remain in the existing shared stores. The direct check used one Kestrel host. It does not establish behavior under separate-process AppHost replicas or native Unity.

## World-75 client combat response ordering (2026-10-24)

The browser map epoch now also guards pending NPC target and laser response handling. A late response from a previous map cannot restore its target or overwrite the destination status. This is browser-local state; server world state and runtime variables continue to use the shared stores. The forced-order check used one live Kestrel host and does not establish separate-process mesh behavior.

## World-74 client response ordering (2026-10-24)

The Spacemap page's map epoch now applies to pending move responses as well as state polls. A response from an earlier map cannot overwrite the destination map after a jump. This changes only browser-local rendering state; world movement and session state remain in the existing shared server stores. The forced-order check used one live Kestrel host, so it does not establish separate-process mesh behavior.

## World-68 cross-replica pickup verification (2026-10-24)

The live two-host Kestrel check now goes beyond displaying the propagated pickup radius: replica B's browser client collects a box beyond the default range after replica A's in-game admin TCP set, and one wallet credit is observed. The focused run used a shared distributed-cache test instance. The real PostgreSQL and separate Redis connections variant remains an explicit integration check and was not run in World-68 because creating its containers would cross this run's project boundary.

## World-67 pickup range through production storage (2026-10-24)

The World-45 two-replica client check now also changes `world.collectibles.pickup_radius` through the in-game admin TCP socket on replica A. PostgreSQL records the change and rollback, and replica B's independent Redis-backed runtime store supplies the updated radius to the live Spacemap state route. The shipped page renders `180 → 400 → 180` without reconnecting. This is a two-host Kestrel integration check; AppHost/YARP routing and native Unity remain unverified.

## World-66 pickup radius race (2026-10-24)

The server checks the current runtime `world.collectibles.pickup_radius` when claiming a box. A page can therefore receive `409 distant` after an earlier state response advertised a larger radius. The shipped page now keeps its selected box and reads the next state before retrying; it does not assume the earlier radius remains valid. A live Kestrel browser check confirmed the transition from 400 to 180 and one successful credit. This run used one test host and an in-memory variable registry; cross-replica Redis/PostgreSQL propagation of this specific race remains unverified.

## World-65 effective pickup radius in each browser state (2026-10-24)

Each Spacemap state response reads `world.collectibles.pickup_radius` from `IRuntimeVariableRegistry`, which production backs with PostgreSQL and `sessions-redis`. The page uses the returned radius for its local pickup trigger; the authoritative `WorldCollectibles.CollectAsync` still checks the current value at claim time. The direct client test used one Kestrel host and a test registry, so cross-replica variable propagation for this specific pickup flow remains unverified.

## World-64 real Redis reward retry (2026-10-24)

Two live Kestrel replicas with separate Redis clients served the shipped Spacemap page. Replica A stored a lethal NPC vital and reward snapshot; replica B read them after target selection and spawn removal, returning the authorized retry status and original reward amount. The focused test uses a recording reward sink and shared test variables, so it verifies the distributed NPC cache path rather than PostgreSQL credit idempotence or independently hosted Aspire processes.

## World-63 killer-authorized reward retry (2026-10-24)

TCP and HTTP retry admission now reads the killed NPC's account ID from the existing distributed vital key, rather than relying only on a socket-local target or the five-minute selection key. The subsequent attack rechecks the saved kill and credits with the existing idempotency key. The loopback TCP and live HTTP checks used an in-memory distributed cache; real Redis replica propagation was not retested in this run.

## World-58 reward display (2026-10-24)

The lethal attack calculates reward values through the runtime-variable registry, credits the account, then returns those values to the browser. Existing distributed NPC vital and reward idempotency keys remain authoritative across mesh instances. The focused browser check used one local Kestrel host and did not validate separate live Redis replicas.


## World-57 browser observer verification (2026-10-24)

The existing per-map NPC snapshot is also used by browser `/spacemap/world/state` responses. A live Kestrel test measured two NPC vital cache reads for six concurrent browser state polls with two NPCs. After a second `WorldNpcs` instance on the shared distributed-memory cache killed one NPC, the shipped Spacemap page's next poll showed one visible NPC after the 500 ms TTL. This confirms local HTTP client propagation and read sharing; live Redis and separate process replicas remain unmeasured.

## World-52 TCP laser-fire completion (2026-10-24)

The accepted-shot result is sent only to the requesting socket after the existing distributed NPC damage and reward path completes. NPC vitality and reward idempotence remain in their shared stores. The direct client check used one loopback TCP listener with an in-memory distributed cache; Aspire replicas and real Redis/PostgreSQL were not exercised in this run.

## World-51 TCP laser-fire result (2026-10-24)

No mesh state moved. The rejection status comes from the same distributed-cache NPC vitality and lease-fenced `WorldNpcs.AttackAsync` call that every replica uses. Per-connection target selection stays socket-local by design. This run did not start Aspire replicas or real Redis/PostgreSQL.


## World-50 live combat browser coverage (2026-10-24)

The direct Spacemap combat check uses live HTTP and the same distributed-cache-backed `WorldSessionStore`/`WorldNpcs` path as the server, but one local Kestrel node with an in-memory distributed cache. It establishes the shipped browser client behavior for target, move, fire, damage and kill. Cross-replica NPC damage persistence remains covered by `BrowserStateTargetAndFire_ShareNpcStateAcrossApiReplicas`; this run did not start Aspire replicas or real Redis/PostgreSQL.

> World-47: runtime-variable rollback is now conflict-checked inside the PostgreSQL transaction (advisory + row lock). A stale rollback on any replica returns 409 instead of overwriting a newer value, so all replicas still observe one linear history. See [world protocol](world-protocol.md).


## World-46 in-game admin history fidelity (2026-10-24)

The local Spacemap panel now sends an explicit audit reason for set and rollback, and displays the persisted actor, node, reason, change ID and timestamp. The live HTTP client check used one Kestrel host with a cache-writing in-memory registry. It confirms the browser and endpoint contract; cross-replica Redis/PostgreSQL propagation remains covered separately by World-45. AppHost/DCP replicas and YARP ingress remain open.

## World-45 real Redis + PostgreSQL behind the cross-replica admin check (2026-10-24)

Closes the World-39..44 stand-in gap for the runtime-variable write and read path. `InGameAdminSocket_OnReplicaA_RealRedisAndPostgres_UpdatesLiveSpacemapAndSocketOnReplicaB` (Explicit, Integration) starts `postgres:16-alpine` and `redis:8.6` containers via Testcontainers and applies the identity migrations. Replica A (`mesh-node-a`) and replica B (`mesh-node-b`) each get their own StackExchange.Redis `ConnectionMultiplexer`, `RedisCache` and `RedisServerMeshMutex`. Each also runs its own production `RuntimeVariableStore` on the PostgreSQL connection. No cache or registry instance is shared in process. The admin socket on A ran the real `ChangeAsync` path: Redis mesh mutex, `pg_advisory_xact_lock`, row lock, history insert and commit, then the `runtime-variable:v1:<key>` publish. Replica B's TCP client and live Spacemap page followed `1000 → 500 → 1000`. B's history read returned changeIds 1 and 2 from PostgreSQL. A fresh Npgsql connection counted 2 durable rows, and B's own Redis connection saw the published key.

Fidelity finding: the production rollback records reason `Rollback of <id>: <reason>`. The World-43 stand-in kept the bare reason, so that test now expects the prefix only for the real store.

Still open: AppHost/DCP-orchestrated replicas and YARP ingress (Aspire wiring of `sessions-redis`/`data-postgres` into `api-game` replicas) were not started in this run. Native Unity/Vuplex was not run either, because the mitmproxy CA is not installable.


## World-44 admin browser control (2026-10-24)

The shipped in-game admin panel now has a live HTTP client check for set and history rollback. This test uses one Kestrel host and an in-memory distributed-cache stand-in; it does not establish Redis/PostgreSQL propagation or AppHost replica behavior. World-43 continues to cover cross-replica provisional TCP and browser observation of an in-game socket command.

## World-43 all three admin surfaces cross replicas (2026-10-24)

The in-game admin surface now has the same two-replica live check as the CLI (World-39/40) and CMS (World-41/42). The admin socket's dispatcher runs on replica A and writes `runtime-variable:v1:world.station.peace_radius` in the shared cache. Replica B's own `RuntimeVariableStore` serves both its TCP client and its live Spacemap HTTP page. Both followed the set and the rollback. Still open: the shared cache is an in-memory `IDistributedCache` (not sessions-redis), and the A writer stands in for the PostgreSQL commit and advisory lock in `RuntimeVariableStore.ChangeAsync`.


## World-42 CMS to two client surfaces on replica B (2026-10-24)

The live CMS edit and rollback on replica A are now observed on replica B through both its WorldServer TCP client and its live HTTP Spacemap page. The shipped page rendered the safe-zone transitions in jsdom from B's production `RuntimeVariableStore`; the socket received the corresponding packets without reconnecting. The two hosts still share an in-memory `IDistributedCache`, and A's writer stands in for PostgreSQL, so real Redis and PostgreSQL propagation remain open.

## World-41 CMS writes on one replica, socket reads on another (2026-10-24)

The Angular CMS now joins the two-replica check: its real HTTP calls hit the runtime-variable endpoints on `mesh-node-a` only, and replica B's own `RuntimeVariableStore` picked up the change and rollback from the shared distributed cache and pushed them to its TCP client. History rows carry surface `AdminCms` and node `mesh-node-a`. The shared cache is still in memory and the writer a stand-in for PostgreSQL.

## World-40 second-replica socket observation (2026-10-24)

The two-replica live check now connects a real loopback TCP client to replica B's `WorldSessionHandler` and its own production `RuntimeVariableStore`. The CLI writes on replica A through the shared cache, while both the Spacemap page and socket on B observe the set and rollback. The socket's idle polling delivered station radii `500 → 1000` and safe-zone flags `0 → 1`. The replica link is still a shared in-memory `IDistributedCache`; A's persistence writer is a stand-in, so PostgreSQL commit and real Redis propagation remain unverified.

## World-39 two-replica runtime-variable propagation to a live client (2026-10-24)

This closes the World-38 single-node gap for the distributed-cache read path. Two separate Kestrel replicas share only one distributed cache, which stands in for sessions-redis.

- Replica A (`mesh-node-a`) receives the shipped Server Management CLI commands.
- Replica B (`mesh-node-b`) serves the Spacemap bridge and resolves variables through its own production `RuntimeVariableStore`. Every catalog default was preloaded into the shared cache under `runtime-variable:v1:<key>`, so B never queries PostgreSQL.

A's test-only write path writes the same cache key the production store writes after its PostgreSQL commit. B's live client DOM followed both the set and the rollback on its next poll, with no restart or local invalidation. Both history entries carry `ServerManagement`, `mesh-node-a` and the exact previous values: `1000→500`, then `500→1000`.

Still unverified:
- The real PostgreSQL commit, the advisory lock, and `RuntimeVariableStore.ChangeAsync`. A test registry stands in for the write side.
- A real Redis server and AppHost-orchestrated replicas.

## World-38 browser movement observation (2026-10-24)

The live Spacemap script now exercises canvas click movement against the Kestrel world bridge and checks a subsequent state poll and ship draw coordinates. It still uses one in-process node and an in-memory cache/variable registry, so it does not establish movement or variable propagation across live Redis replicas.

## World-37 live Spacemap DOM via Server Management CLI (2026-10-24)

- New client script `scripts/client-testing/verify-spacemap-live.mjs <baseUrl> <dosid>` runs the shipped `LocalSpacemapPage` HTML/JS in jsdom. All `/spacemap/world/*` fetches go over real HTTP to a live host, with no synthetic responses. Between state polls it runs the shipped `scripts/admin/Invoke-ServerAdminCommand.ps1` as a real PowerShell process.
- New `[Explicit]` test `ServerManagementCliLiveSocketTests.ServerManagementCli_PeaceRadiusSetAndRollback_UpdateLiveSpacemapSafeZoneDom` starts one Kestrel host. It serves both the backoffice admin command route and the real `WorldHttpBridge.Map` routes over one shared runtime-variable registry. The pilot is parked at (2800,1300), 800 units from home station 100.
- Observed in the client DOM: `#safeZone` = `Protected`, then `Outside (radius 500)` after CLI `var set world.station.peace_radius 500`, then `Protected` after CLI `var rollback 1`. Position stayed `2800 / 1300`. Requests: `POST /enter` followed by three `GET /state`. Both changes were attributed to `ServerManagement` (changeIds 1 and 2).
- This closes the World-36 gap where the DOM check and the endpoint check were separate.
- Remaining gaps: the registry and dispatcher are in-memory, so PostgreSQL/Redis cross-replica propagation is not covered. The page ran in jsdom, not the signed-in Unity Vuplex WebView. The Angular CMS was not exercised.

## World-36 browser safe-zone presentation (2026-10-24)

The Spacemap safe-zone label derives from each fresh `/spacemap/world/state` response, which reads `world.station.peace_radius` through the runtime-variable registry. No extra browser endpoint or process-local radius cache was added. Endpoint and shipped JavaScript checks passed with a test registry and synthetic client fetch responses; live Redis/PostgreSQL replica propagation remains unverified.

## World-35 CLI-driven safe-zone change on an open socket (2026-10-24)

A runtime-variable change made from the Server Management surface now has a direct socket check for the World-34 safe-zone push. The CLI process and the TCP handler share one registry instance, so this confirms the operator-to-socket path and `ServerManagement` attribution. It does not confirm PostgreSQL/Redis propagation across replicas: the test registry is in memory, and no AppHost or containers were started.

## World-34 safe-zone updates on connected sockets (2026-10-24)

Each admitted TCP socket reads the effective station peace radius from the runtime-variable registry on its idle poll and compares its interpolated position to the station center. A changed radius is announced once with a station packet; a changed safe-zone flag is announced once with a safe-zone packet. The registry's production backing is PostgreSQL plus distributed cache. Verification used a test registry and loopback socket, not live Redis/PostgreSQL replicas.

## World-33 runtime jump radius on connected sockets (2026-10-24)

Portal jump range is not cached per socket or per process: every TCP jump reads `world.portal.jump_radius` through `IRuntimeVariableRegistry`, which ApiService backs with PostgreSQL and the shared cache. A registry change therefore applies to the next jump on any replica's open socket. Verification used a test double for the registry, not live PostgreSQL/Redis replicas.

## World-32 same-map teleport to the pilot socket (2026-10-24)

An idle world socket on one node observes a same-map admin teleport written by another node through the shared saved-position cache. It announces a zero-duration move once and refreshes the socket and local presence pilot used by subsequent movement and heartbeats. The observation is polled once per second; the saved position remains shared state, while the last announced position is local presentation bookkeeping. Loopback verification used two session stores sharing an in-memory distributed cache, not live Redis replicas.

## World-30 stationary remote position fanout (2026-10-24)

Each local TCP observer keeps a small last-announced-position map for visible remote pilots. The saved position remains in the distributed cache; the observer map only prevents duplicate packets. A same-map teleport on another replica is read through the shared per-map snapshot and sent as one zero-duration move within the socket's poll interval plus snapshot TTL. Observer departure clears this presentation state. The existing polling cost and native Unity compatibility gap remain.

## World-29 per-map heartbeat batching (2026-10-24)

Mesh-tick write amplification is now O(maps with due pilots) per node per 15 s, not O(local pilots). With N local pilots on one map, one tick costs one `world:presence:map:{id}` read and one write, all under the mesh mutex. Remote pilots in the roster are preserved. The roster is still a single JSON blob per map. A Redis hash per map (HSET/HEXPIRE per pilot) would remove the whole-blob rewrite, but it needs a non-`IDistributedCache` client.

## World-28 presence write cadence (2026-10-24)

Browser state polls and the mesh maintenance tick share a node-local 15-second heartbeat cadence per pilot. Within that interval, they avoid the Redis read-modify-write of the whole `world:presence:map:{id}` roster. Entry, leave, teleport and detected saved-map transitions still update presence immediately under the existing mesh mutex. The cadence is presentation bookkeeping; the shared cache remains authoritative, and the node-local timestamp contains no world/session state. This reduces write frequency, but each heartbeat still rewrites the map roster, so the cost for a very large map is still proportional to occupancy. A per-pilot presence structure or map-level event fanout remains a scaling gap.

## World-26 browser pilot visibility (2026-10-24)

The Spacemap browser renders other pilots returned by `WorldSessionStore.ConnectedAsync` through `/spacemap/world/state`. Each API replica reads the shared presence index and saved positions, so a browser can see pilots entered on another replica. This browser path still performs one saved-position read per pilot on every state poll and needs scale work for large maps. No new single-process state was added.

## World-21 shared travel retiming (2026-10-24)

Idle TCP sockets and browser enter/state polls reconcile in-flight travel against the current shared speed variable. The write uses the per-pilot gate, mesh mutex, and map lease fence, then persists travel and runtime to the shared cache. A local TCP movement broadcast informs connected observers; remote observer fanout is still absent. A connected loopback client confirmed revised movement durations and rollback, but cross-replica PostgreSQL/Redis propagation remains unverified in a live deployment.

## World-20 idle runtime propagation (2026-10-24)

Each admitted TCP connection now polls the shared effective ship stats once per second and sends updated attributes when speed or laser damage changes. It checks saved map and lease first, so a stale socket routes away instead of advertising attributes on its former map. The connected socket test used a shared test registry and observed change plus rollback; PostgreSQL/Redis and live CMS or CLI writers were not connected in this test. Polling adds runtime-variable cache reads per connected pilot.

## World-19 idle socket routing (2026-10-24)

The TCP listener polls shared saved-map and lease state once per second while waiting for a client packet. An idle socket now receives the provisional destination route after a remote teleport or owner reassignment, then disconnects for reconnection. Loopback socket tests confirm both routes and preserved pilot coordinates. Polling adds cache reads per connected pilot; cross-node observer delivery, native Unity reconnect behavior and live Redis deployment remain unverified.

## World-18 stale TCP map check (2026-10-24)

An admitted TCP socket reads the pilot's saved map before dispatching each packet. Movement, configuration, portal travel and station repair also reject a changed saved map after reloading under the mesh mutex, closing the race between that first read and the write. A remote teleport to a different map routes and releases the old socket even when its original map lease is still valid. A loopback socket test observed the destination route and no stale movement write. Native reconnect handling remains unconfirmed.

## World-17 remote teleport heartbeat (2026-10-24)

Heartbeat presence renewal now reads the shared saved map while holding the mesh mutex. A node whose local pilot was teleported by another replica removes the old map presence and local pilot entry, rather than renewing a ghost ship. The focused cross-store test and browser state endpoint test pass. A connected TCP socket on the former node still needs an explicit route/close notification; live Redis replica behavior remains unverified.

## World-16 read path mutation fence (2026-10-24)

World entry and state polling can mutate hull through home-station repair. That repair now takes the per-pilot gate and shared mesh mutex, reloads the distributed runtime snapshot, and checks the captured map lease epoch before writing. A stale browser poll receives 409. This narrows a cross-replica race; the distributed mutex expiration and live Redis/PostgreSQL deployment behavior remain unverified.

## World-15 target selection (2026-10-24)

NPC target selection now checks the map owner and epoch while holding the shared mesh mutex before saving the target in Redis. The TCP and browser handlers use that path. Collection and NPC attack already checked the lease under the mutex; the earlier world-12 note below saying otherwise is superseded. Mutex expiry during a stalled command and cross-node observer delivery remain open.

## World-14 browser command epochs (2026-10-24)

Browser movement, configuration, jump, collection and fire requests capture the active lease epoch, then validate it while holding the shared mesh mutex for their state mutation. A missing or changed lease yields HTTP 409 and a client retry message. Requests can still execute on any API replica: this fences a reassignment race but is not map-owner routing. Station repair during state polling and admin mutations still require an ownership audit. Target selection was fenced in world-15. The Redis mutex expiry and cross-node TCP observer fanout remain scaling gaps.

## Client-verify-1 status (2026-10-24)

The Spacemap client script responds to a synthetic runtime-variable change and rollback in a headless DOM run. Existing loopback socket tests pass, but this run did not connect the CMS or Server Management CLI to PostgreSQL, Redis, a live API replica and a signed-in world client together. Immediate cross-replica propagation and history attribution remain end-to-end verification gaps. See [client verification report](client-verification-report.md).

## World-12 command fencing (2026-10-24)

The TCP listener carries its admitted map lease epoch into movement, configuration and portal commands. Each session-store mutation checks owner, epoch, lease expiry and active node status while holding the same Redis mesh mutex as the coordinator. A reassignment that completes first makes the old command fail before it updates distributed pilot state; the TCP handler returns the new map route and disconnects. If the command holds the mutex first, reassignment waits for the write. A shared-cache test covers stale rejection, unchanged saved state and recovery on the new node. Collection, NPC combat, and target selection gained this check in later runs. Browser commands are served on any API node, and the Redis mutex's 30-second expiry can permit an overlap if an operation stalls. Cross-node observer fanout and native client handoff are still open.

## World-8 collectible claims (2026-10-24)

All replicas derive the same map slots from the UTC five-minute cycle. A shared Redis claim marker and the mesh mutex coordinate collection; the marker is written before the Postgres Economy ledger credit, whose idempotency key includes map, slot and cycle. This prevents two replicas from paying the same spawn. If a process dies after reservation and before the ledger write, that spawn stays unavailable until the next cycle. Browser clients see removal on their next state poll. Local TCP observers receive removal immediately; remote TCP observers still need cross-node fanout. Cargo is checked using persisted ore balances while the mesh mutex is held. An infrastructure administrator must supply shared Redis and Postgres for multiple instances.

## Mesh-5 CMS control panel (2026-10-24)

The CMS Backoffice **Service Mesh & World** page polls topology and world overview every 10 seconds. It shows node count against desired replicas, pilot and map counts, the Aspire Dashboard link, node TCP addresses, CPU, memory, leases and pilots, plus live pilot positions. `backoffice.ops.read` grants the page; `backoffice.ops.write` enables scaling, rebalance, drain, activate and teleport controls. Admins retain both permissions in the CMS role mapping.

`POST /api/v1/backoffice/mesh/nodes/{nodeId}/activate` reactivates a live draining node and rebalances leases; an offline or already active node returns 409. The scale form only updates the Redis desired target and local simulated workers; it does not change AppHost's running replica count. The page states this limitation beside the controls. A real node with connected pilots cannot be drained through the API.

## Mesh-4 Backoffice control plane (2026-10-24)

`GET /api/v1/backoffice/mesh/topology` reports persisted desired replicas, the 16-replica bound, active nodes, distributed presence count, leases, node metrics and the local Aspire Dashboard link. `PUT /mesh/scale` persists desired replicas (1–16) and `MaxMapsPerNode` (1–256) in the Redis registry, then rebalances leases. `POST /mesh/rebalance` runs the same coordinator pass. These writes require `backoffice.ops.write`; reads require `backoffice.ops.read`.

`LocalWorkerSpawner` registers explicitly marked simulated nodes for single-process development. They share the real process's TCP listener and do not represent independently deployable workers. Changing the desired value does not reconfigure a running Aspire AppHost; start AppHost with `Cluster:DesiredWorkers` for physical replicas. `MaxMapsPerNode` remains a capacity hint when there are fewer real nodes than maps require.

`POST /mesh/nodes/{nodeId}/drain` moves leases to healthy nodes. It returns 409 when no healthy target exists or a real node reports connected TCP pilots; transparent live socket migration is not implemented. Redis retains saved pilot position, runtime and presence. A draining node stays draining across heartbeats. Lease epoch fencing and cross-node observer delivery remain open.

The mesh heartbeat refreshes local pilots' distributed presence, so quiet connected pilots remain visible in topology and world overview.

## Mesh-3 shared world bridge (2026-10-24)

YARP now forwards `/spacemap/world/*` to the API cluster. Requests may reach any replica: `WorldSessionStore` reloads distributed runtime and trajectory on each HTTP request, and a short-lived `world:presence:map:{id}` cache index supplies map occupants. Redis mutex coordination serializes presence edits and, in the hosted API, movement/configuration writes; connection handles and TCP broadcasts remain local to each process. The HTTP response reports the handling node and current lease owner. Map XML and login's emulator-only `gameServerIP` resolve the owner's advertised TCP port, falling back to the local configured listener without a live lease.

Remaining scaling constraints: the world TCP listener binds loopback and its advertised address works only on the same physical host; cross-host ingress needs a routable TCP endpoint. YARP serves shared state but does not forward commands to the lease owner or fence writes by lease epoch. TCP observer broadcasts are local, and the cache map index is time limited rather than a durable subscription feed. Do not treat the owner header as proof of authoritative command execution.

## Mesh-2 node registry and map coordinator (2026-10-24)

Every `api-game` process now heartbeats a `ServerMeshNode` into the shared `sessions-redis` distributed cache. The record includes node identity, HTTP and bound world TCP addresses, status, start/heartbeat timestamps, connected pilot count, leased maps, process CPU and working set. A Redis `SET NX` mutex serializes registry updates across processes. The 16 local faction/sector maps (IDs 1–16) are assigned to the least loaded active node; a joining node takes half of an overloaded owner's maps. Draining immediately transfers its maps; a heartbeat older than `LeaseTtlSeconds` marks the node offline and transfers its maps on the next heartbeat. Lease epochs rise on owner changes. The hosted service waits for the world TCP listener's actual bound port, then heartbeats at the configured interval and drains on orderly shutdown.

`GET /api/v1/backoffice/operations/mesh` exposes the registry snapshot under `OpsRead` authorization for CMS operations. `MaxMapsPerNode` is presently a capacity hint: one node must still be able to hold all maps when it is the only healthy node. Lease epochs and ownership are coordination data only; world command writes do not yet enforce those epochs. Redis mutex expiry during a stalled cache operation remains a fencing risk. The HTTP endpoint is the process's `ASPNETCORE_URLS` value (or logical `http://api-game`), which may need a routable per-replica address for cross-host routing. There is no map-owner gateway routing, cross-replica observer delivery or CMS visual mesh view yet. Keep one authoritative gameplay replica until those are complete.


## Mesh-1 configuration (2026-10-24)

Topology: `gateway-players` YARP ingress → Aspire service discovery → 1–16 `api-game` replicas → `sessions-redis` for distributed sessions and map coordination, with `data-postgres` for durable account and economy records. Each API process emits a distinct OpenTelemetry `service.instance.id` (machine and process ID by default, or `Cluster:NodeId`) to the Aspire Dashboard and OTLP collector; the collector feeds Prometheus and Grafana. The Dashboard's **Operations: Mesh topology** command still reports configured replicas rather than reading the live registry.

Set `Cluster:DesiredWorkers` in AppHost configuration to 1–16, or override it with `Emulator:Mesh:ApiReplicas` (for example `Emulator__Mesh__ApiReplicas=4`). AppHost passes the count to `.WithReplicas(...)` and ApiService. Default is one replica. Cluster options validate `DesiredWorkers` (1–16), `MaxMapsPerNode` (default 4), `HeartbeatIntervalSeconds` (10), and `LeaseTtlSeconds` (30; greater than two heartbeat intervals). `NodeId` defaults to a process-unique machine/process pair; an administrator can assign it explicitly. These are configuration bounds, not yet a lease coordinator.

The single API process still binds world TCP `127.0.0.1:60954`. With two or more replicas AppHost sets `WorldServer:DynamicTcpPort=true` and `Port=0`, so the OS allocates separate loopback ports. Outside AppHost, `WorldServer:DynamicTcpPortRange` can select a bounded `first-last` range (up to 1,024 ports), trying free ports in order. The bound port is reflected in that process's `/xml/maps.php` response. A YARP request may land on a different replica than the player's subsequent request; the response must not be treated as stable pilot routing. TCP remains loopback only, so this is a same-host development foundation, not a multi-host TCP ingress.

Redis now stores node registrations, heartbeats, and one epoch-bearing lease per map. The next step is to make pilot routing resolve the lease owner before world entry and forward commands to it. `data-postgres` holds durable game data; Redis holds short-lived route and session state. After node failure, another node receives a higher lease epoch, but pilot state handoff, fenced writes and observer resubscription are not implemented. Keep one replica for authoritative world play until those mechanisms and failover tests exist.

The Phase 2 target is several `api-game` world instances behind the Aspire and YARP service mesh. Redis already backs the login session, saved world trajectory, selected configuration, shield, cooldown timestamp and local P.E.T. state. World-6 verifies that a pilot's configuration and cooldown survive a reconnect through a fresh `WorldSessionStore` sharing that cache.

The current runtime is **not yet safe to scale horizontally for gameplay**. `WorldSessionStore` still keeps connected sockets, active pilots and observer lists inside one API process. Writes from different replicas have no distributed per-pilot serialization. Aspire can start multiple `api-game` replicas with distinct TCP ports and the coordinator assigns map owners, but YARP has no map-owner routing or WebSocket/TCP handoff. Increasing gameplay replicas today would split observers and could race movements or configuration switches.

Required mesh path:

1. Give each world node a stable instance ID and register its HTTP/TCP address, capacity, version and heartbeat in Redis with a short TTL; expose liveness, map ownership and player counts to Aspire telemetry and CMS.
2. Assign each map instance to one owner through a Redis lease with an epoch/fencing token. Only that owner may update authoritative pilot trajectories and configuration state; reassign after lease expiry.
3. Route world entry and subsequent commands to the current map owner. Use Aspire service discovery for API destinations and a YARP policy for browser world endpoints; advertise the chosen TCP node to the native client once its address contract is verified.
4. Move session snapshots and cross-node observer events behind a distributed state/event interface. Use atomic updates or fenced writes for movement, shield and cooldown, then publish spawn, move, attribute and companion events so observers on other nodes see them.
5. Exercise one-to-sixteen replica changes, owner failure and reconnect in integration tests. Verify one authoritative ship position, a five-second global cooldown, exactly one companion set per pilot and current owner visibility in the Dashboard/CMS.

The local `WorldServer` wire contract remains provisional; native Unity packet compatibility and visible client entry still need a safe fixture and client verification.
# World-7 map handoff (2026-10-24)

Portal travel changes the shared pilot map, arrival position, runtime snapshot and per-map presence under the Redis mesh mutex. It looks up the destination's unexpired map lease and active node and returns that node's advertised TCP endpoint. The HTTP bridge can answer through any API replica. A TCP socket on a different source owner sends the destination and releases its local connection while preserving destination presence for reconnect. The receiving node restores map and position from the distributed cache.

This is a provisional application handoff, not a transparent YARP TCP relay. The native Unity client has not been verified to honor the local map-change packet or reconnect to the advertised endpoint. Multi-host ingress needs a reachable `WorldTcpHost` on each node; current local deployments bind loopback. Lease epoch fencing and distributed observer broadcasts remain open.

# World-9 NPC lease simulation (2026-10-24)

NPC patrol coordinates derive from map, slot and UTC time, so replicas return the same position without per-process simulation state. HP, shield and destruction live in the distributed cache under map/NPC/spawn-cycle keys. A Redis mesh mutex serializes cross-replica hits. Only the current map lease owner runs the one-second TCP movement broadcast. Credits, Uridium, Experience and Honor rewards use idempotent Postgres economy ledger keys; the credit entry also drives `npc_kills` quest progress. Reward retries by the killer can finish after a partial ledger write.

The TCP connection table remains local to each API replica. Owner broadcasts do not yet fan out to socket observers connected to another node, and lease failover may require those clients to reconnect for NPC spawn packets. Combat HTTP state is shared immediately through Redis. The NPC movement task does not yet expose per-map NPC telemetry on the CMS.

# World-10 mesh verification (2026-10-24)

`WorldMeshJourneyTests` registers one, then three, then four real node records against one distributed-cache test store and mutex. The 16 map leases rebalance to 5–6 maps at three nodes and four maps at four nodes; `/xml/maps.php` resolves starter maps to the active owners. A cross-owner portal and a drain retain the pilot's destination map, coordinates, ship and configuration through a fresh replica store. `MeshBackofficeEndpointTests` also calls the Backoffice drain route and checks reassigned epochs and pilot state. This is an in-process contract suite, not a four-process Aspire or Redis outage test. Backoffice currently rejects draining a real node with active TCP pilots; existing sockets have no transparent migration, and remote TCP observer broadcasts still need fanout.

## World-11 TCP lease command checks (2026-10-24)

World TCP admission reads the pilot's persisted map before creating presence and compares its lease owner with the actual local node ID. Every subsequent TCP command checks both owner and admitted lease epoch. When the lease moves or its epoch changes, the socket receives a local map-change route and disconnects; reconnect on the new owner restores distributed pilot state. The hosted listener now derives the same machine/process node ID as the heartbeat when `Cluster:NodeId` is unset. A transport test covers wrong-node login, a drain during a connection, blocked stale movement, and movement after reconnect.

There is still a race between the command check and its distributed write, so this is not full fencing. A complete mesh needs an epoch condition within the state mutation or a single owner command queue, browser owner routing, and cross-node observer fanout. Simulated workers share the real TCP listener and are development-only.

# Admin-var-1 runtime variable registry (2026-10-24)

`IRuntimeVariableRegistry` uses `identity.runtime_variables` as the durable override store and `identity.runtime_variable_history` as an append-only audit log. `sessions-redis` holds shared read-through values and history entries so game replicas read a newly written override without a process restart. A PostgreSQL advisory lock and row lock serialize writes across replicas. The mesh coordinator reads the shared heartbeat TTL, desired replica count, and map limit on each snapshot/update; changing desired count alone does not start new OS processes. The local spawner and CMS/CLI/in-game command surfaces still need integration with this registry.

The `OpsRead` backoffice API exposes `GET /api/v1/backoffice/runtime-variables`, `GET /runtime-variables/{key}`, and `GET /runtime-variables/history?key=&limit=&beforeChangeId=` for descending history pages. `OpsWrite` allows `PUT /runtime-variables/{key}` with `{value,reason}`, `POST /runtime-variables/{key}/reset` with `{reason}`, and `POST /runtime-variables/history/{changeId}/rollback` with `{reason}`. These are emulator admin contracts. The API attributes them to `AdminCms`; a future CLI and in-game adapter must supply their own surface and authenticated actor.

# Admin-var-2 command routing (2026-10-24)

`IAdminCommandDispatcher` runs in `api-game` and is reached through the authenticated Backoffice API from the Server Management CLI and future admin clients. Variable overrides and immutable change history use PostgreSQL plus `sessions-redis`; mesh operations use the shared registry. Manual world spawns and the latest announcement use Redis so a different API replica reads the same state. The command audit records surface, actor, node, text, outcome and timestamp in PostgreSQL. Aspire's variable report reads stored live values and the history report reads the last 50 changes; the command help report provides CLI syntax. The CLI or Backoffice API shows the complete catalogue, including untouched defaults. Native multi-instance delivery still requires client confirmation.

For the management CLI, set `THDARKORBIT_ADMIN_TOKEN` in the current PowerShell process to an `OpsWrite` bearer token and run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/admin/Invoke-ServerAdminCommand.ps1 -CommandText 'help'`. The token is read from the process environment and is not accepted as a command-line argument. `-BaseUri` selects the local gateway or API origin when its default is different.

# Admin-var-3 shared in-game variables (2026-10-24)

Both the browser and local TCP admin paths call the shared dispatcher. Runtime variable overrides and change history remain in PostgreSQL and `sessions-redis`; world stat reads use that registry on each state poll or TCP response. In-world admin eligibility travels with the authenticated game session and `WorldPilot`. Replica and native Unity verification remain open.

# Admin-var-4 CMS mesh behavior (2026-10-24)

The CMS reads and mutates the same PostgreSQL backed runtime registry through `/api/v1/backoffice/runtime-vars` and the shared command dispatcher. It displays `ServerManagement`, `AdminCms`, and `InGameAdmin` audit entries with node attribution and pages older history by change ID. Cross-replica propagation after a live CMS action still needs direct client confirmation.

# World-22 remote browser movement observation (2026-10-24)

Each admitted TCP socket checks its pilot's saved travel during its one-second idle poll. A browser move served by another API replica now reaches observers attached to that socket's node through one provisional `MoveCommand`. Local broadcasts mark the trajectory as seen to avoid repeat delivery. This adds a distributed-cache read per idle connected pilot; it is not a general distributed event bus and does not deliver to TCP observers on a different node. A connected loopback test used two session stores sharing one cache; live Redis replicas remain unverified.


# World-23 cross-node movement delivery (2026-10-24)

A pilot on node B now becomes visible as movement to TCP observers on node A on the same map. Node A's idle poll reads the shared presence index `world:presence:map:{id}` and the shared `world:position:{userId}` travel. It sends each new remote trajectory once per observer. Cost: per idle observer per second, one presence read plus one travel read for each remote pilot on the map (O(observers x remote pilots)). That is fine for small maps; a Redis pub/sub fan-out per map is the scalable follow-up. A connected loopback test used two stores sharing one cache. Live Redis replicas and an actual two-process AppHost remain unverified.
# World-24 remote pilot spawn and removal (2026-10-24)

Each idle observer now compares the shared map presence index with its per-socket visible pilot IDs. A new remote pilot produces a provisional create packet before movement; a departed or teleported pilot produces a remove packet. Spawn position comes from the shared saved position or interpolated travel, and a saved map mismatch suppresses stale-map creation. The visible-ID set is ephemeral presentation state only. The current polling path reads presence twice and may read each remote pilot's position and travel separately per observer, so Redis pub/sub or map-level fanout remains needed for scale. A loopback socket confirms the sequence; live Redis replicas and native Unity rendering remain unverified.


# World-25 map-level remote observer fanout (2026-10-24)

Resolves the world-24 cost gap. `ObserveRemotePilotsAsync` and `ObserveRemoteMovesAsync` now share one `RemoteSnapshotAsync(mapId)`: a single `Lazy<Task>` per map coalesces concurrent observers, reads `world:presence:map:{id}` once and each non-local pilot's `world:position:{id}` once, and is reused for 500 ms. Shared-cache reads per idle tick drop from O(observers x remotes x 2) to O(maps x remotes) per node. Local sockets are filtered by each caller; the snapshot is a read-only memo, never written back, and a failed read is evicted rather than cached, so the distributed cache stays the source of truth. Redis pub/sub (push instead of poll) remains the next step for very large maps.

# World-27 browser state polls share the map snapshot (2026-10-24)

This closes the World-26 cost gap. Browser `/spacemap/world/state` polls previously read every occupant's `world:position:{id}` on every request, which cost O(browser polls x occupants). They now read the World-25 per-map snapshot, so a node reads each remote pilot's saved position at most once per 500 ms TTL, however many browser and TCP observers poll. The caller's presence heartbeat (read-modify-write of `world:presence:map:{id}`) remains per poll. The snapshot is a read-only memo and is never written back, so Redis remains the source of truth. Redis pub/sub and a heartbeat that does not rewrite the whole index are the next scale steps.

# World-31 CLI-driven runtime variable reaches a live socket (2026-10-24)

No mesh code changed. A new non-integration test confirms that a runtime-variable change made through the Server Management CLI over HTTP reaches an already-connected TCP world socket through the shared `IRuntimeVariableRegistry`, with no reconnect, and that a CLI rollback restores it. In production that registry is the PostgreSQL + `sessions-redis` backed `RuntimeVariableStore`. Propagation of a CLI change across replicas against live Redis remains unconfirmed.
# World-48 stale rollback display (2026-10-24)

Rollback conflicts now return 409 from the CMS endpoint. The CMS and Spacemap panels reload their value and history views after that response, so a rejected stale action does not leave an obsolete value displayed. Live local HTTP checks covered both clients and preserved the current peace radius; their writer used distributed-memory cache. The production PostgreSQL transaction and Redis path was verified separately in World-47, not repeated here. No AppHost replicas were started in this run.


# World-49 cross-replica move/variable race at login (2026-10-24)

A browser move written to `sessions-redis` by replica B, or a runtime-variable change from any surface, could be lost for a TCP client that had just entered on replica A. This happened when the change landed between A flushing the entry packets and A priming its observed-travel/advertised-stats baselines. The baselines are now captured before the entry burst. This was the root cause of the intermittent `Loopback_RemoteReplicaBrowserMove_ReachesIdleTcpObserver` timeout. The two-store shared-cache loopback test models two replicas over distributed-memory cache; AppHost replicas and live Redis were not started in this run.

# World-53 NPC roster follows shared state on every replica (2026-10-24)

NPC kills and spawn-count variables were already stored in shared state (`world:npc:{map}:{id}:{cycle}` in `sessions-redis`, plus runtime variables in PostgreSQL/Redis). However, a TCP socket on replica A only saw an NPC kill broadcast by its own replica. Every idle socket now re-reads the visible NPC set from the shared cache once per second and sends create/destroy differences, so a kill on replica B or a variable change from any surface reaches replica A's clients within about 1 s. The only node-local state is which NPCs each socket has been shown. The cost is one `VisibleAsync` read (one cache GET per spawned NPC) per idle socket per second; a per-map snapshot like `RemoteSnapshotAsync` is the next optimisation if Redis load matters. The model is two stores over one distributed-memory cache. AppHost replicas with live Redis were not started.

## World-54 management command to idle NPC roster (2026-10-24)

A live local admin HTTP host and open TCP world socket now verify the World-53 NPC roster propagation from the shipped Server Management CLI, including change history and rollback. The test uses one node with distributed-memory cache; it does not measure Redis load or prove cross-replica propagation. The per-socket NPC read cost from World-53 remains open.


## World-55 cross-replica NPC vitals (2026-10-24)

NPC HP and shield now reach idle sockets on every replica through the World-53 shared-cache poll. No extra cache reads were added; the comparison reuses the existing per-NPC read. Per-socket vitals live on the connection (they are only "what this socket was told"), and the authoritative state stays in the distributed cache. The test simulates the second node with a separate `WorldNpcs`/`WorldSessionStore` that shares one in-memory cache. Live Redis replicas were not run.
## World-56 NPC observer read sharing (2026-10-24)

`WorldNpcs.VisibleForObserversAsync` coalesces concurrent TCP observer reads per map on each node and reuses the result for 500 ms. With N connected observers and M spawned NPCs, the shared NPC vital cache reads are now O(M) per map per snapshot instead of O(N × M) per idle poll. The memo is only presentation state; the distributed cache remains authoritative, attacks and selections bypass it, and a local attack evicts it. A remote attack becomes visible after the TTL. A concurrent eight-observer test measured two reads for two NPCs, then two after expiry. Live Redis replicas and high-load behavior remain unmeasured.

## World-59 reward packet consistency (2026-10-24)

The lethal attack passes its already credited, multiplier-adjusted reward to the local TCP broadcast. No new process-local reward state is introduced; the shared NPC vitality and idempotent economy ledger remain authoritative. The direct socket check uses one in-memory distributed-cache host, so separate live Redis/PostgreSQL replicas remain unverified for this change.

## World-60 reward retry consistency (2026-10-24)

The NPC kill's four credited amounts are part of the same distributed-cache vital record as its destroyed marker and killer. A retry on another API instance reads that snapshot rather than current runtime multipliers. A two-`WorldNpcs` shared-cache test changed all four multipliers between kill and retry and observed the original amounts. The live Spacemap retry used one Kestrel host; live Redis/PostgreSQL replicas remain unverified.

## World-61 reward retry after movement (2026-10-24)

Retry eligibility now reads the shared killed-NPC record before testing current laser range. This permits the recorded killer to complete an idempotent credit retry after moving across the map, including on another API replica using the same cache. The live Spacemap check used one Kestrel host and an in-memory cache; live Redis/PostgreSQL replicas remain unverified.

## World-62 saved NPC kill across spawn changes (2026-10-24)

The killed NPC snapshot, killer and reward live in the existing distributed-cache vital record under `world:npc:<map>:<id>:<cycle>`. Two `WorldNpcs` instances sharing the cache confirmed that a spawn count change to zero does not prevent the killer's reward retry on the other instance. The live Spacemap HTTP check used an in-memory distributed cache; real Redis/PostgreSQL replicas remain unverified for this change.

## World-69 concurrent pickup client outcome (2026-10-24)

A box claim stored in the shared world cache removes that box from the next browser state response. The shipped page now clears its pending selection and explains the disappearance instead of silently ending the attempt. A live Kestrel check used two authenticated clients and confirmed one wallet credit and no duplicate collect request. This check used one host with distributed-memory cache; cross-replica Redis propagation was not rerun.

## World-70 two-host pickup observation (2026-10-24)

An explicit live check starts two Kestrel hosts with separate `WorldSessionStore` and `WorldCollectibles` instances over one shared distributed-memory cache and mesh mutex. A page on A selects and moves toward a box; an authenticated client on B claims it. A's subsequent state poll observes the removal, renders the unavailable message, and makes no collect call. One wallet credit is recorded. Real Redis and distinct processes remain unverified in this check.

## World-71 local pickup intent (2026-10-24)

The pending box ID is client presentation state. Selecting an NPC clears it without changing distributed world, box, or runtime-variable state. The live page check used one Kestrel host and its distributed-memory cache; real Redis and separate-process replicas were not exercised for this interaction.

## World-72 jump pickup intent (2026-10-24)

A successful map jump clears the old-map box and target IDs in the Spacemap page. These are client-local selections; the server's map travel, distributed presence, and collectible keys are unchanged. The live browser check used one Kestrel host with distributed-memory cache. Real Redis and separate-process replicas were not exercised for this interaction.

## World-73 late browser poll (2026-10-24)

The map epoch is local to one Spacemap page and only orders its HTTP responses. It does not hold authoritative map, session, collectible, or runtime-variable state in process memory. The live delayed-response check used one Kestrel host with distributed-memory cache; separate-process Redis replicas were not exercised.





## World-94 stale state poll after configuration switch (2026-10-24)

The local Spacemap page now counts completed configuration requests. A `/state` poll that started before a configuration request finished is discarded, and the page waits for any in-flight poll before its follow-up refresh. Previously the in-flight poll could repaint the old configuration and the follow-up refresh was skipped while that poll was running. `verify-spacemap-config-stale-poll-live.mjs` held a real `/state` response with configuration 1, clicked configuration 2 (`POST /config` 200), then released the held response. The DOM kept configuration 2 pressed after one fresh `/state`. No HTTP or TCP contract changed; native Unity/Vuplex remains unverified under the no-CA rule.

## World-96 jump conflict mesh owner header (2026-10-24)

`POST /spacemap/world/jump` now re-stamps `X-Mesh-Node-Id` / `X-Mesh-Map-Owner-Node` after reloading the shared pilot state, like move, config, collect, target and fire already did. Before, a replica holding a stale local pilot (map 1) answered `409 map_changed` with map 1's lease owner even though another replica had already jumped the pilot to map 2, so a router following the header would be sent to the old map's owner. No body or TCP contract changed.
