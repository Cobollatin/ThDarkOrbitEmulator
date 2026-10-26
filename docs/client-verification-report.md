# Client verification report

## World-97 map conflict owner routing (2026-10-24)

The shipped Spacemap page runs in jsdom against two live Kestrel replicas. A configuration click is held on replica A while replica B jumps the pilot to map 2. The released command receives `409 map_changed` with the current map owner header, and the page refreshes its DOM to Map 1-2. A delayed jump client checks the same header rule. Both focused cases passed. The solution build had zero errors and all 4,315 non-integration tests passed. Four direct regressions passed for Server Management CLI, Angular CMS, in-game admin panel and connected TCP admin client; those unchanged surfaces do not consume this response header. This verifies the emulator browser and HTTP surfaces, not native Unity/Vuplex. The test did not force a transition after request admission but before the under-mutex check.

## World-93 configuration controls (2026-10-24)

The shipped Spacemap page ran in jsdom against live Kestrel HTTP. Its first configuration request was held before delivery while the player clicked the other configuration. The page sent no concurrent request, then sent the queued click after the first succeeded. The server returned 409 for the five-second cooldown; the page rendered the accepted configuration 2, showed the cooldown, and a separate `/state` read agreed. The solution built with 0 errors, all 4,315 non-integration tests passed, and four direct regressions passed for the Server Management CLI, Angular CMS, in-game admin panel and connected TCP admin client. Those four cover unchanged admin paths. Native Unity/Vuplex was not exercised because CA installation is prohibited in this run.

## World-91 delayed Fire delivery after jump (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. Its map-1 Fire request was held before network delivery while a second authenticated client jumped the pilot. The delayed `POST /fire` returned `409 map_changed`; the page fetched `/state` and rendered `Map 1-2` with `None` target. The focused explicit live check and the other 11 Spacemap race cases passed. A unit check confirmed the under-mutex rejection before damage or reward. The solution built with zero errors, all 4,312 non-integration tests passed, and four existing live client regressions passed for the management CLI, Angular CMS, in-game admin panel, and connected TCP admin client. Those four cover unchanged admin flows. This confirms the emulator browser DOM and HTTP contract. Native Unity/Vuplex was not exercised because this run prohibits installing the capture CA. Separate Redis processes were not exercised for this race.

## World-90 delayed target delivery after jump (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom with live Kestrel HTTP. Its map-1 NPC selection was held before network delivery while a second authenticated client jumped the pilot. The delayed `POST /target` returned `409 map_changed`; the page fetched `/state` and rendered `Map 1-2`, `None` target, and the transition status without a timer poll. The focused explicit live check passed. The solution built with zero errors, all 4,311 non-integration tests passed, and five other explicit direct client checks passed: prior target ordering, Server Management CLI, Angular CMS, in-game admin panel, and connected TCP admin client. The latter four admin checks cover unchanged paths. This confirms the emulator browser DOM and HTTP contract, not native Unity/Vuplex. The server-side guard restores distributed session state under the mesh mutex; separate Redis processes were not exercised in this check.

## World-89 ordered NPC target selection (2026-10-24)

The shipped Spacemap JavaScript ran in jsdom against live Kestrel HTTP. Its first `/target` request was held before server delivery; a second NPC click sent no concurrent request. After release, the second request reached the server, the page displayed its `Locked Streuner` target, and `/fire` did not return `not_selected` or `unselected`. All three focused live target/fire checks passed. The solution built with 0 errors, all 4,311 non-integration tests passed, and four existing explicit checks passed for Server Management CLI, Angular CMS, in-game browser admin and in-game TCP admin surfaces. Those four are regressions of unchanged admin paths. Native Unity/Vuplex remains unverified because this run excludes the CA installation needed for capture.

## World-88 jump conflict reconciliation (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. It held `POST /jump` before delivery while a second authenticated client jumped from map 1 to map 2. Releasing the page request returned 409 `map_changed`; the page immediately fetched `/state` and rendered “Map 1-2”, an empty target, and an enabled Jump button. The scripted poll was disabled, so it did not cause the update. Two server checks cover admission and the under-mutex refresh. The solution built with 0 errors; all 4,311 non-integration tests passed. The new live jump check, three earlier jump/page regressions, and four direct admin client regressions passed separately. Native Unity/Vuplex remains unverified under the no-CA rule.

## World-87 target switch during pending laser result (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom with real Kestrel `/enter`, `/state`, `/target`, and `/fire` requests. The check held the first laser response after Kestrel returned HTTP 200, selected a second live NPC, and released the old result. Both nonlethal and lethal cases retained the second target, its `Locked Streuner` status, and a painted ship; the Fire button reenabled. The solution build had 0 errors, all 4,309 non-integration tests passed, and four existing live admin regressions passed across the management CLI, Angular CMS, in-game admin page and TCP client. This verifies the changed emulator browser behavior directly; those admin regressions cover unchanged paths. Native Unity/Vuplex was not exercised because the required capture CA installation is prohibited for this run.

## World-86 repeated Fire input (2026-10-24)

The shipped Spacemap JavaScript ran in jsdom against live Kestrel HTTP. Its first `/fire` request was held before delivery, the Fire button was observed disabled, and another dispatched click produced no second request. After release, the page displayed the range error, enabled Fire, moved toward a Streuner and completed live nonlethal and lethal shots with the credited reward. The solution built with 0 errors, all 4,309 non-integration tests passed, and four explicit live regressions passed for the management CLI, Angular CMS, in-game admin panel, and TCP socket client. Those four cover existing admin flows; the combat page check directly covers this change. Native Unity/Vuplex was not exercised because the permitted capture path requires the excluded CA installation.

## World-85 external jump while a move is pending (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. Its map-1 movement request was held before delivery while a second authenticated HTTP client jumped the pilot to map 2. Releasing the move produced `409 map_changed`; the page fetched `/state` and displayed map 2 with no scheduled poll. The check also confirmed no old-map collect request. This directly verifies the emulator browser DOM and HTTP surface, not native Unity/Vuplex or separate-process replicas. The nine explicit movement/jump client checks passed.

The solution built with 0 errors, all 4,309 non-integration tests passed, and four established direct admin checks passed for the Server Management CLI, Angular CMS, in-game browser panel, and connected TCP admin client. Those admin checks are regressions; they do not exercise the new movement reconciliation.

## World-84 external jump while combat response is pending (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. A second authenticated HTTP client jumped the same pilot while the page's real `/target` or `/fire` response was held. The page polled `/state`, rendered map 2, cleared the target, then discarded the released old-map response without changing its map-2 status. Both cases passed, as did the two existing local-jump cases. This verifies the emulator DOM and HTTP surface, not signed-in Unity/Vuplex or the provisional TCP protocol.

The solution build passed with 0 errors. All 4,309 non-integration tests passed. Four explicit live admin regressions passed across the Angular CMS editor, Server Management CLI, in-game admin page, and connected TCP admin client; those checks cover existing admin paths, not the new external-jump guard.


## World-83 cross-replica stale movement (2026-10-24)

`SpacemapClient_StaleMoveAcrossRealRedisReplicas_RefreshesAndRetries` ran the shipped Spacemap JavaScript in jsdom against replica B while replica A changed the same pilot's destination. Both Kestrel hosts had separate Redis connections and stores. The browser observed `200` for A's move, `409 move_changed` on B, then `200` on retry; `#destination` and the saved Redis travel both ended at `17850 / 11135`. The focused explicit integration check passed. The solution built with 0 errors; all 4,309 non-integration tests, eight explicit movement/jump page checks, and four direct admin-surface regressions (CLI, Angular CMS, in-game page and TCP) passed. This verifies the emulator page DOM and live HTTP path. Signed-in Unity/Vuplex remains unverified because CA installation is forbidden in this run.

## World-82 stale browser move (2026-10-24)

The shipped Spacemap page ran in jsdom against live Kestrel HTTP. After it entered, a second authenticated HTTP client changed the same pilot's destination. The page sent its older `expectedMoveId`; Kestrel returned `409 move_changed`. The page then requested fresh state and rendered the other client's destination, which a separate `/state` request confirmed. A further page click sent the refreshed revision, returned 200, and rendered the new saved destination. A two-store shared-cache test also checked that the stale request leaves saved travel unchanged. The solution build completed with zero errors; all 4,309 non-integration tests and eight explicit move/jump page checks passed. Four existing live regressions passed for the Angular CMS editor, Server Management CLI, in-game browser admin panel, and TCP admin client; those paths do not use the changed movement contract. Native Unity/Vuplex was not exercised under the no-CA rule; the new check did not use separate Redis processes.

## World-81 ordered map clicks (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. Its first `/move` was held before server delivery while a second map click occurred. The page issued no second request until the first settled, then sent the second move; rendered `#destination` and a separate live `/state` response both showed the second target. The seven explicit move/jump page checks passed. The solution build had zero errors; all 4,308 non-integration tests passed. Four separate explicit live regressions passed for the Angular CMS editor, Server Management CLI, in-game browser admin panel, and TCP admin client; those paths do not use the changed movement code. Native Unity/Vuplex was not exercised because this run forbids the CA installation required for signed-in capture. Separate-process Redis replicas were not used for the new ordering check.

## World-80 delayed move delivery after jump (2026-10-24)

`verify-spacemap-move-delivered-after-jump-live.mjs` ran the shipped Spacemap HTML/JS in jsdom against real Kestrel HTTP. It held a map-1 move before network delivery, sent a gate jump, then delivered the old request. The client request contained `mapId: 1`; the server returned `409 map_changed`. The page still showed map 2, and a fresh `/state` showed the map-2 arrival destination `(2500,1300)` with no new travel. This confirms the emulator browser DOM and socket-free HTTP path, not native Unity/Vuplex or separate-process Redis replicas.

The solution build completed with zero errors and 4,308 non-integration .NET tests passed. Four selected live regressions passed across Server Management CLI, Angular CMS editor, in-game admin page, and connected TCP admin client. Those regressions cover existing command and runtime-variable paths; the delayed movement check covers this change.

## World-79 state poll while movement is pending (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel `/enter`, `/state`, and `/move`. Its fetch adapter held a movement request before delivery to Kestrel. A scheduled state poll during that pending move issued no request and caused no canvas redraw. After the movement response rendered destination `15120 / 10262`, the next scheduled poll called Kestrel and retained that destination. The focused explicit check and five browser movement/jump regressions passed. Separate live checks for the Angular CMS editor, Server Management CLI, in-game admin browser panel, and connected TCP admin client also passed. The solution built with 0 errors and all 4,307 non-integration tests passed. This confirms the emulator browser DOM and HTTP surface, not signed-in Unity/Vuplex. The four admin paths are regressions; they do not use the changed polling code.

## World-78 delayed state poll after movement (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel `/enter`, `/state`, and `/move` routes. Its fetch adapter held a completed pre-click `GET /state` response until a map click's `POST /move` had rendered destination `15120 / 10262`. Releasing the old response left the destination and ship position unchanged; exactly one state poll and one move were sent. The focused explicit client check and four existing explicit browser move/jump checks passed. The solution build had zero errors and all 4,307 non-integration tests passed. Four separate explicit live checks passed for the Angular CMS editor, Server Management CLI, in-game admin browser panel, and connected TCP admin client; those paths do not use the changed poll code. This verifies the emulator browser DOM and HTTP surface, not the signed-in Unity/Vuplex client.

## World-77 delayed same-map move (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom with real Kestrel `/enter` and two `/move` requests. Its fetch adapter held the first completed response until the second destination rendered, then released it. The DOM retained the second destination, position, and map. The four explicit move/jump browser checks passed. The solution built with zero errors and all 4,307 non-integration tests passed. Four existing explicit live regressions passed for the Angular CMS editor, Server Management CLI, in-game admin browser panel, and connected TCP admin client; those paths did not change. Native Unity/Vuplex remains unverified under the no-CA rule. The browser sequence does not establish server ordering when requests reach different replicas in reverse order.

## World-76 repeated jump input (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. Its first successful `/jump` response was held at the browser fetch boundary while a second click event was dispatched. The browser sent exactly one `/jump`, showed the Jump button disabled during the wait, then rendered map 2 and reenabled the button when the response arrived. This directly confirms the emulator browser DOM and HTTP behavior. Native Unity/Vuplex remains unverified because CA installation is excluded. CMS, CLI, and TCP behavior did not change.

The solution build passed with 0 errors. The non-integration suite passed 4,307/4,307; all three explicit jump browser checks passed. Four existing explicit live checks also passed for the Angular CMS variable editor, Server Management CLI, in-game admin browser panel, and connected in-game admin TCP client. Those four checks cover existing admin flows, not the new jump guard.

## World-75 delayed combat responses after jump (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. The fetch adapter held completed map-1 `/target` (200) and `/fire` (409) responses in separate runs, allowed `/jump` to render map 2, then released each response. The page kept the map-2 heading, `None` target, and `Jumped to map 1-2` status. These checks directly verify the emulator browser DOM and HTTP surface. Native Unity/Vuplex remains unverified because the required CA installation is excluded; CMS, CLI, and TCP admin behavior did not change.

The solution build completed with 0 errors. The non-integration suite passed 4,307/4,307. Both explicit delayed-response browser checks passed, along with four separate existing live checks covering the Angular CMS runtime-variable editor, Server Management CLI, in-game admin browser panel, and connected in-game admin TCP client. The four admin checks are regressions of existing flows; they do not exercise the new response-ordering behavior.

## World-74 delayed move after jump (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom and fetched real `/enter`, `/move`, `/jump`, and `/state` responses from Kestrel. The fetch adapter held the completed map-1 move response until after the page rendered map 2. Releasing it left the map-2 heading and jump status intact; the page sent no `/collect` for the old box. The earlier delayed-state response check also passed. The solution build had 0 errors, the non-integration suite passed 4,307/4,307, and both explicit browser checks passed. This directly verifies the emulator browser DOM and HTTP surface, not the native Unity/Vuplex client. No CMS, management CLI, or TCP admin behavior changed in this run.

## World-70 cross-replica pickup outcome (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom with real HTTP to live Kestrel replica A. A second authenticated HTTP client claimed the selected box through replica B. The page observed the missing box on its next state poll, rendered “Bonus box is no longer available,” and made no collect request. B returned HTTP 200 and the shared wallet recorded one credit. Each host had its own world/session objects, with a shared distributed-memory cache and mutex. This confirms the emulator browser DOM and HTTP surfaces; signed-in Unity/Vuplex, real Redis, and separate process hosts were not exercised. The Angular CMS, management CLI, and admin TCP client were not changed by this verification-only run.

The solution build completed with 0 errors. All 4,307 non-integration tests passed. The new two-replica check, the original single-host pickup check, and three existing live checks for management CLI, Angular CMS, and in-game admin TCP all passed.

## World-68 in-game admin change to browser pickup (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel replica B. A connected provisional TCP admin client on replica A set the pickup radius to 400 and rolled it back. The page rendered `180 → 400 → 180 units`, clicked a manual bonus box 300 units from the ship, issued `/move` and `/collect`, received HTTP 200 at a distance above 180, and showed `Collected 200 credits`. The test wallet counted one credit. This directly verifies the emulator browser and socket surfaces with a shared cache, not signed-in Unity/Vuplex or production PostgreSQL/Redis for this run. CMS and Server Management CLI were not exercised for this pickup scenario.

## World-67 pickup range HUD and cross-replica admin change (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel replica B. It rendered the player's ship, position, safe-zone transitions and the new `#pickupRange` HUD at `180 units → 400 units → 180 units`. An in-game admin sent set and rollback over a connected provisional TCP WorldServer client on replica A. The production `RuntimeVariableStore` wrote PostgreSQL history and published to Redis; replica B used a distinct Redis connection. Both the in-memory and real PostgreSQL/Redis versions of the explicit test passed. This confirms the emulator browser and socket clients, not signed-in Unity/Vuplex. The existing Angular CMS and Server Management CLI paths were not used for this radius change.

The solution build passed with 0 errors. All 4,307 non-integration tests passed. Four further explicit live client checks passed for the CLI across replicas, Angular CMS editor, in-game admin browser panel and pickup page. No AppHost, ApiService, Gateway or dcp process remained after the run.

## World-66 Spacemap pickup after a radius change (2026-10-24)

`SpacemapClient_RetriesPickupAfterLiveRadiusShrinks` ran the shipped page HTML/JS in jsdom with actual fetches to live Kestrel world routes. The test changed the server radius from 400 to 180 immediately before the first `/collect`. The page received `409 distant` at a distance above 180, displayed “Pickup range changed. Continuing toward box.”, then polled state and collected at or inside 180. The reward wallet was called once. The radius change used a test-only HTTP control route and in-memory registry; the check does not establish CMS/CLI propagation, live Redis/PostgreSQL behavior, or native Unity/Vuplex behavior.

The solution build passed with 0 errors (26 existing package warnings). The non-integration suite passed 4,307/4,307. Four separately selected live regression checks passed for the shipped Server Management CLI, Angular CMS variable editor, in-game admin panel, and connected TCP world client. Those checks exercised their existing flows; only the Spacemap check above exercises the pickup retry change.


## World-65 Spacemap pickup radius (2026-10-24)

`SpacemapClient_UsesLivePickupRadius_BeforeReachingDefaultRange` ran the shipped Spacemap HTML/JS in jsdom against live Kestrel HTTP. The state payload carried `collectionRadius: 400`; the page clicked a bonus box, sent `/move`, then sent `/collect` and rendered its reward while the ship was still more than 180 units from the box. This directly checks the local browser client behavior and successful server pickup at the runtime radius. The test used a test registry, not a CMS or CLI edit; signed-in Unity/Vuplex and cross-replica PostgreSQL/Redis propagation remain unverified.

The solution build passed with 0 errors, and all 4,307 non-integration tests passed. Four named live checks passed for the pickup page, Server Management CLI across replicas, Angular CMS to Spacemap and socket, and the in-game admin panel. The three admin checks are regressions of existing flows; they do not set the pickup-radius variable in this run.

## World-64 Spacemap retry across live Redis replicas (2026-10-24)

`SpacemapClient_KillOnReplicaA_RetriesRewardOnReplicaB_OverRealRedis` passed with the shipped page HTML/JS in jsdom, real HTTP to two Kestrel hosts, and distinct Redis connections. The DOM rendered the kill reward and ship; after the host removed selection, disabled spawning, changed the multiplier, and teleported the pilot, the page observed the distant, empty state on B and sent `/fire` to B. B returned `409 destroyed`; the reward recorder received the original 400 credits. This is direct local Spacemap client evidence. Signed-in Unity/Vuplex and PostgreSQL economy credit were outside this check.

The solution build completed with 0 errors; all 4,307 non-integration tests passed. Five explicitly selected live client checks passed: this two-replica Spacemap retry, Server Management CLI NPC roster, Angular CMS variable editor, in-game admin panel, and in-game TCP admin over real Redis/PostgreSQL.

## World-63 reward retry after target selection disappears (2026-10-24)

The connected loopback TCP client selected and killed a Streuner, then fired again without reselecting. It decoded `LaserFireResultCommand (npcId,"destroyed")` and the reward recorder saw a second idempotent credit attempt. A different account failed the saved-kill authorization check. The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP; after the kill, the host deleted its target selection key, changed the reward multiplier, removed the spawn and moved the pilot away. The page sent `/fire` again and received HTTP 409 `destroyed`; the recorder retained the original 400-credit snapshot. This confirms the emulator client surfaces, not native Unity/Vuplex.

The solution build had 0 errors and all 4,307 non-integration tests passed. Four named live client checks for Server Management CLI, Angular Admin CMS, in-game admin panel and in-game TCP admin passed. The existing two-replica in-game admin check backed by real PostgreSQL and Redis also passed; that regression check does not cover the new reward retry path.

## World-58 live browser combat reward (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel world endpoints. It entered the world, targeted a Streuner, rendered a distant-shot warning, moved, dealt damage, then rendered `Destroyed Streuner; earned 400 credits, 1 uridium, 400 XP, 2 honor.` The live lethal response carried those same values; the reward store recorded one credit. This verifies the local browser client surface and HTTP response, not signed-in Unity/Vuplex or separate live Redis replicas.

The final solution build passed with 0 errors. The non-integration suite passed 4,306/4,306 tests, and the named live browser combat check passed separately.


## World-57 live Spacemap NPC count (2026-10-24)

The shipped `LocalSpacemapPage` HTML/JS ran in jsdom with real HTTP requests to Kestrel. Its `#npcCount` displayed `2 visible`, then `1 visible` after a second `WorldNpcs` instance sharing the cache killed an NPC and the observer snapshot expired. A focused test also confirmed six concurrent HTTP state polls produced two NPC cache reads for two NPCs, and remote nonlethal damage appeared in the returned JSON. The separate named live checks for the management CLI NPC roster set/rollback, Angular CMS variable editor across two local replicas, and in-game TCP admin to Spacemap/TCP each passed (1/1); the loopback remote NPC vitals check also passed. This directly verifies the local Spacemap DOM and HTTP client surface. It does not verify signed-in Unity/Vuplex, separate live Redis replicas, Angular CMS, or Server Management CLI for this specific change.

## World-52 TCP accepted-shot client check (2026-10-24)

`Loopback_NpcSpawnLockLaserAndRewardPackets` used a connected loopback TCP client. It selected a 150-HP Streuner, fired once and decoded `LaserHitCommand` followed by `LaserFireResultCommand (id,"hit")`; the second shot decoded hit, destroy, reward, then the same `hit` result. The reward store recorded one credit. The rejected-shot loopback check also passed. This confirms the emulator socket surface only. Native Unity/Vuplex cannot be checked under the no-CA rule; Spacemap, CMS and CLI behavior was unchanged by this edit.

`dotnet build ThDarkOrbitEmulator.slnx --no-restore` passed with 0 errors; the non-integration .NET suite passed 4,302/4,302 tests.

## World-51 TCP client laser-fire rejection (2026-10-24)

`Loopback_RejectedLaserFire_SendsResultStatusAndKeepsSocketOpen` passed with a real loopback TCP client against `WorldSessionHandler`. It logged in 2,000 units from a Streuner. It decoded `ShipCreateCommand`, then fired without selecting and decoded `LaserFireResultCommand (id,"unselected")`. It selected the target, decoded `TargetLockCommand`, and fired twice. Each shot decoded `(id,"distant")` with no `LoginErrorCommand`, so the socket stayed usable and no reward was credited. The existing hit/destroy/reward loopback test still passes. Native Unity/Vuplex was not exercised because that requires the mitmproxy CA. The Spacemap, CMS and CLI surfaces are unchanged in this run.


## World-50 local Spacemap combat over live HTTP (2026-10-24)

`SpacemapClient_TargetsFiresAndRendersNpcDamage_OverLiveHttp` passed with the shipped `LocalSpacemapPage` HTML/JS in jsdom and real Kestrel `/spacemap/world/{enter,state,target,move,fire}` requests. The client selected a visible Streuner, rendered its health, displayed the server's distant-shot 409 as “Target out of laser range. Move closer.”, moved on a canvas click, rendered damage after firing, then rendered “Destroyed Streuner; rewards credited.” and cleared its target. A ship canvas draw and one server reward credit were observed. No synthetic HTTP responses were used. Native Unity/Vuplex, the Angular CMS, the Server Management CLI and a live TCP client were outside this focused combat check; their earlier checks remain documented below.


## World-46 in-game admin reason and history (2026-10-24)

The shipped `LocalSpacemapPage` ran in jsdom against live Kestrel HTTP through `InGameAdminPanel_VariableFormAndHistoryRollback_UpdateLiveSpacemap` (Explicit). Submitting a variable without a reason and clicking rollback with an empty reason sent no mutation. A direct rollback request without `reason` returned HTTP 400. With reasons, the page showed `Protected → Outside (radius 500) → Protected`, rendered the ship, and displayed both reasoned history entries with actor `Live admin:837` and node `mesh-node-a`. The test registry confirmed `1000→500` and `500→1000` with distinct reasons. The four-company synthetic browser exercise also passed. Separate Explicit live-client checks for the Server Management CLI, Angular CMS editor and in-game TCP command path each passed (1/1), preserving their observable Spacemap/socket behavior. This checks the emulator client surfaces; it does not establish native Unity/Vuplex, real PostgreSQL/Redis for this form, or AppHost routing.

## World-45 cross-replica in-game admin on real Redis + PostgreSQL (2026-10-24)

- Surfaces exercised: the in-game admin WorldServer TCP socket on replica A; a TCP client and the shipped `LocalSpacemapPage` HTML/JS in jsdom (`verify-spacemap-cms-live.mjs <url> <dosid> InGameAdmin`) over live HTTP on replica B.
- Backing: Testcontainers `postgres:16-alpine` + `redis:8.6`. Each replica has its own Redis connection, mesh mutex and production `RuntimeVariableStore`; there are no in-memory stand-ins.
- Observed DOM `#safeZone`: `Protected` → `Outside (radius 500)` → `Protected`, position `2800 / 1300`, 38 ship canvas draws, 19 requests. The socket saw radius/safe `500/0` then `1000/1`. PostgreSQL `identity.runtime_variable_history` holds 2 rows for the key: `InGameAdmin`, `Admin pilot:840`, `mesh-node-a`.
- Result: `InGameAdminSocket_OnReplicaA_RealRedisAndPostgres_UpdatesLiveSpacemapAndSocketOnReplicaB` passed (~10 s), as did the refactored World-43 in-memory variant. Both now share `RunInGameAdminAcrossReplicasAsync`.
- Not verified: AppHost-orchestrated replicas and native Unity/Vuplex.


## World-44 in-game admin panel client check (2026-10-24)

`InGameAdminPanel_VariableFormAndHistoryRollback_UpdateLiveSpacemap` runs the shipped page in jsdom with real HTTP to a local Kestrel host. The admin panel became visible, its variable selector showed `world.station.peace_radius` at 1000, its form submitted 500, and the DOM showed `Outside (radius 500)` plus a `1000 → 500 (InGameAdmin)` history row. Clicking the rendered rollback button restored `Protected` and added a `500 → 1000` row. Position stayed `2800 / 1300`, and canvas ship draws occurred. The server history attributed both changes to `InGameAdmin`, `Live admin:837`, `mesh-node-a`. Focused test passed. This directly exercises the emulator page and HTTP endpoints; jsdom is not signed-in Unity Vuplex, and the test uses an in-memory cache writer rather than PostgreSQL/Redis.

Solution build: 0 errors. The first non-integration run passed 4,300/4,301 and hit the pre-existing idle-socket timeout; that test passed alone. A second single-worker non-integration run passed all 4,301 tests.

## World-43 in-game admin command on replica A visible on replica B clients (2026-10-24)

- Surface exercised: the WorldServer TCP admin socket (command 32/33) on `mesh-node-a`; observed on `mesh-node-b` by a TCP client and by the shipped `LocalSpacemapPage` HTML/JS in jsdom over live HTTP (`scripts/client-testing/verify-spacemap-cms-live.mjs <url> <dosid> InGameAdmin`; the third argument labels the steps).
- Observed DOM `#safeZone`: `Protected` (entered) → `Outside (radius 500)` (InGameAdmin set) → `Protected` (InGameAdmin rollback); position stayed `2800 / 1300`; 38 ship canvas draws and 19 HTTP requests.
- Observed TCP on B: station radius 500 / safe flag 0, then 1000 / 1.
- History: two rows `InGameAdmin`, `Admin pilot:840`, `mesh-node-a`, `1000→500` and `500→1000`, with raw commands slash-expanded (`var set ...`, `var rollback 1 ...`).
- Result: `InGameAdminSocket_OnReplicaA_UpdatesLiveSpacemapAndSocketClientOnReplicaB` passed (4 s). Not verified: native Unity/Vuplex (mitmproxy CA not installable in this run), real Redis/PostgreSQL.


## World-42 CMS edit visible in Spacemap DOM and TCP on replica B (2026-10-24)

The named Explicit test `AdminCms_RuntimeVarsEditorOnReplicaA_UpdatesLiveSocketClientOnReplicaB` now also runs `verify-spacemap-cms-live.mjs`. It evaluates the shipped `LocalSpacemapPage` HTML/JavaScript in jsdom and sends its real `/spacemap/world/enter` and `/state` requests to replica B. While the Angular CMS component edits and rolls back the radius through replica A, B's DOM displayed `Protected → Outside (radius 500) → Protected` and drew the pilot ship; the connected TCP client decoded station radii `500 → 1000` and safe-zone flags `0 → 1`. The focused test passed, and `dotnet build --no-restore` had zero errors. Two concurrent-worker non-integration runs each timed out in the pre-existing `Loopback_ExternalVariableChangeAndRollback_RefreshIdleSocketAttributes` case; it passed alone, and the single-worker non-integration run passed all 4,301 tests. This confirms the emulator browser, CMS, and provisional socket surfaces with live HTTP and two Kestrel hosts. The shared cache is in memory, A's PostgreSQL writer is a stand-in, CMS RBAC is stubbed, and native Unity/Vuplex remains unverified.

## World-41 Angular CMS editor to a live socket client on another replica (2026-10-24)

The named Explicit test `ServerManagementCliLiveSocketTests.AdminCms_RuntimeVarsEditorOnReplicaA_UpdatesLiveSocketClientOnReplicaB` runs the shipped Angular `RuntimeVarsComponent` under `ng test` (vitest/jsdom) with its real `BackofficeApiService`, `authInterceptor` and HttpClient over jsdom XMLHttpRequest (CORS enforced) against replica A ("mesh-node-a"), which serves the real runtime-variable list/history/set/rollback endpoints. The spec types a reason and `500` into the `world.station.peace_radius` row, clicks **Save / Apply Live**, waits for the socket idle poll, then clicks **Rollback** on the rendered `1000 → 500` history row. Observed in the CMS DOM: status `world.station.peace_radius applied live.`, history row `1000 → 500` with surface `AdminCms` and node `mesh-node-a`, then `Change #1 rolled back.`, a `500 → 1000` AdminCms row and the editor value back at `1000`. A connected TCP WorldServer client on replica B decoded station radius `500` with safe-zone flag `0`, then `1000` with flag `1`. The test passed. Not exercised: the full CMS shell/login route (RBAC was stubbed to allow writes), the native Unity Vuplex client, real PostgreSQL/Redis (the A writer is a cache-writing stand-in, the shared cache is in memory) and AppHost deployment.

## World-40 CLI to two live client surfaces on another replica (2026-10-24)

The named Explicit two-replica Kestrel test passed with the shipped Server Management CLI calling A, the shipped `LocalSpacemapPage` JavaScript in jsdom fetching live HTTP from B, and a connected loopback TCP WorldServer client on B. The DOM showed `Protected → Outside (radius 500) → Protected`. On the same set and rollback, the TCP client decoded station radii `500 → 1000` and safe-zone flags `0 → 1`. The browser clicked the canvas, sent a real move request, and rendered advanced ship coordinates. The solution build had zero errors; the final full non-integration run passed all 4,302 tests. An earlier run had one idle-socket timeout that passed alone. This confirms observable behavior at the local emulator client surfaces. The native Unity Vuplex client, Angular CMS, real PostgreSQL, Redis, and AppHost deployment were not exercised; the TCP protocol is provisional.

## World-39 cross-replica Spacemap DOM (2026-10-24)

Client surfaces exercised directly: the shipped `LocalSpacemapPage` HTML/JS in jsdom, over real HTTP to replica B, and the shipped `Invoke-ServerAdminCommand.ps1`, as a real PowerShell process against replica A. These are separate Kestrel hosts on different ports that share only a distributed cache.

The DOM showed:

| Step | `#safeZone` |
|---|---|
| Entered | `Protected` |
| After CLI set (500) | `Outside (radius 500)` |
| After CLI rollback | `Protected` |

The canvas click then sent `POST /spacemap/world/move`, and the next poll showed `3366 / 1658`, with the ship drawn farther along both axes. The script output records `cliTarget` = A and `transport` = B. The named Explicit test `ServerManagementCli_OnReplicaA_UpdatesLiveSpacemapSafeZoneDomServedByReplicaB` passed. The World-37/38 single-host test still passes with the default (same-host) CLI target.

Not exercised: the signed-in Unity Vuplex WebView, the native TCP client, the Angular CMS, and real PostgreSQL/Redis.

## World-38 live local Spacemap movement (2026-10-24)

The shipped `LocalSpacemapPage` HTML/JS ran in jsdom against real Kestrel world endpoints. A canvas click sent `POST /spacemap/world/move`; after 1.2 seconds a real state poll advanced the pilot position and the recorded main canvas ship translation in both axes. The named Explicit test passed. This is direct verification of the local browser client code and HTTP surface, with a simulated canvas context. It is not a signed-in Unity Vuplex session or native TCP client; Angular CMS and live PostgreSQL/Redis were not part of this check.

## World-37 live Spacemap DOM via Server Management CLI (2026-10-24)

- New client script `scripts/client-testing/verify-spacemap-live.mjs <baseUrl> <dosid>` runs the shipped `LocalSpacemapPage` HTML/JS in jsdom. All `/spacemap/world/*` fetches go over real HTTP to a live host, with no synthetic responses. Between state polls it runs the shipped `scripts/admin/Invoke-ServerAdminCommand.ps1` as a real PowerShell process.
- New `[Explicit]` test `ServerManagementCliLiveSocketTests.ServerManagementCli_PeaceRadiusSetAndRollback_UpdateLiveSpacemapSafeZoneDom` starts one Kestrel host. It serves both the backoffice admin command route and the real `WorldHttpBridge.Map` routes over one shared runtime-variable registry. The pilot is parked at (2800,1300), 800 units from home station 100.
- Observed in the client DOM: `#safeZone` = `Protected`, then `Outside (radius 500)` after CLI `var set world.station.peace_radius 500`, then `Protected` after CLI `var rollback 1`. Position stayed `2800 / 1300`. Requests: `POST /enter` followed by three `GET /state`. Both changes were attributed to `ServerManagement` (changeIds 1 and 2).
- This closes the World-36 gap where the DOM check and the endpoint check were separate.
- Remaining gaps: the registry and dispatcher are in-memory, so PostgreSQL/Redis cross-replica propagation is not covered. The page ran in jsdom, not the signed-in Unity Vuplex WebView. The Angular CMS was not exercised.

## World-36 Spacemap safe-zone display (2026-10-24)

The actual `LocalSpacemapPage` HTML/JavaScript ran in jsdom for MMO, EIC, VRU and TST. Its DOM showed `Protected` at the station, `Outside (radius 500)` after a radius reduction with the ship 800 units away, and `Protected` after restoration to 1000; moving far from the station showed `Outside (radius 1000)`. These client fetch responses were synthetic. Separately, the real world state handler returned station `peaceRadius` 1000, 500, then 1000 as a test registry changed. The two checks establish the client and endpoint behavior separately; a signed-in WebView, native Unity client and live distributed registry were not connected.

## World-35 Server Management CLI to safe-zone socket verification (2026-10-24)

New `[Explicit]` test `ServerManagementCliLiveSocketTests.ServerManagementCli_PeaceRadiusSetAndRollback_RefreshConnectedTcpClientSafeZone` launches the shipped `scripts/admin/Invoke-ServerAdminCommand.ps1` as a real `powershell` process against a real Kestrel host serving `AdminCommandEndpoint`. An idle loopback TCP world client is logged in as pilot 835 at `(2800,1300)`, 800 units from home station 100. `var set world.station.peace_radius 500 cli shrink peace zone` returned a `ServerManagement` change with previous value `1000`. The socket then decoded `SpaceStationCommand` `(100,2000,1300,500)` and `SafeZoneCommand` `0`. The CLI rollback returned `1000`, and the socket decoded radius 1000 and safe-zone `1`. The World-31 speed test now shares the same host/socket helper; both Explicit tests passed when run by name. The test uses an in-memory registry and a `var set`/`var rollback`-only dispatcher. It does not exercise native Unity, a running AppHost, live PostgreSQL/Redis, signed-in Spacemap or Angular CMS. No AppHost, ApiService, Gateway or dcp process was started.

## World-34 safe-zone socket verification (2026-10-24)

`Loopback_MovementAndRuntimeRadius_RefreshSafeZoneOnOpenSocket` used a connected loopback TCP client. It decoded the initial safe-zone flag, then a station radius of 500 and safe flag `0` after a runtime change, a radius of 1000 and safe flag `1` after restoring the value in the test registry, and safe flag `0` after flying beyond the boundary. The test uses an in-memory variable registry, so it does not exercise a persisted rollback command. Native Unity, signed-in Spacemap, Angular CMS, Server Management CLI, and live PostgreSQL/Redis were not connected for this change.

The existing same-map admin teleport socket test now also decodes the safe-zone exit caused by teleporting outside the station. The solution build finished with 0 errors; all 58 transport tests and all 4,300 non-integration tests passed.

## World-30 same-map remote teleport (2026-10-24)

`Loopback_RemoteSameMapTeleport_SnapsVisiblePilotOnce` connected a real loopback TCP client to node A. A second store sharing the cache entered pilot 851 on map 1, moved it, then teleported it within map 1 to `(3400,2100)`. The client decoded the remote spawn, the travel packet, then `MoveCommand` with pilot 851, the new coordinates and duration zero; no duplicate arrived during another poll. This directly verifies the emulator socket surface. The solution build completed with zero errors and all 4,297 non-integration tests passed. The native Unity TCP client, a signed-in Spacemap WebView, CMS, CLI and live Redis/PostgreSQL replicas were not connected for this change.

## World-29 batched presence heartbeat (2026-10-24)

Verified with `MeshTick_WithSeveralDueLocalPilots_RewritesEachMapRosterOnce`. The test runs a real `WorldSessionStore` on a counting shared cache with a manual clock and three local pilots on map 1, one on map 2, and a remote pilot from a second store. A tick inside the interval did zero roster writes. A due tick did exactly one write for map 1 and one for map 2. The second node's `ConnectedAsync` (the admin/mesh view) then saw 811, 901, 902 and 903 on map 1 and 904 on map 2. The next tick did zero writes. Not exercised: native Unity WebView/TCP, live AppHost/Redis replicas, CMS, Server Management CLI.

## World-28 presence cadence and map transition (2026-10-24)

The real browser state handler was called three times on one session store while another store shared its cache. Each reply contained the remote moving pilot, and a counting cache observed zero map-presence writes during those polls and an additional mesh tick inside the 15-second heartbeat interval. A separate browser state request after a saved map change returned map 5 and found no old map 1 presence. The focused world/socket/jsdom group passed 78 tests, including the shipped Spacemap script run and connected loopback TCP tests; the jsdom fetch responses are synthetic. `dotnet build --no-restore` had zero errors. One full non-integration test run had an idle-socket timeout; that test passed alone, and the final full run passed all 4,295 tests. This does not verify the native Unity WebView or socket, live Redis/PostgreSQL replicas, Angular CMS, or Server Management CLI. A CA-dependent capture and container-backed AppHost were not started for verification under this run's boundary rules.

## World-26 nearby pilot display (2026-10-24)

The shipped `LocalSpacemapPage` HTML/JavaScript ran in jsdom for MMO, EIC, VRU and TST. On successive state polls, the client painted a second pilot and name, updated its canvas position, then removed it and returned the nearby count to zero. Fetch responses in this client run were synthetic. Existing `WorldHttpBridgeTests.SeparateReplicas_SharePresenceMovementAndConfigurationAcrossRequests` exercises the real state handler with two stores sharing a cache and confirms a two-pilot response. `dotnet build --no-restore` completed with zero errors. The first non-integration .NET run reported 1 failure of 4,293 with quiet output; the diagnostic rerun passed all 4,293. Native Unity WebView rendering, live Redis replicas, CMS and management CLI were not exercised for this display change.

## World-21 speed change during travel (2026-10-24)

A connected loopback TCP client sent a movement request to `(5000,1300)`, then observed a second `MoveCommand` and `ShipAttributesCommand` when the speed multiplier doubled, and the same sequence after rollback. The revised movement durations were bounded by the new speed, and the distributed-cache test store contained the updated travel speed. A direct Spacemap state endpoint call also returned the doubled effective speed after retiming saved travel. The solution build had zero errors and all 4,290 non-integration .NET tests passed. A signed-in WebView, Angular CMS, Server Management CLI, native Unity TCP client, and live PostgreSQL/Redis replicas were not directly exercised for this change.

## World-20 idle runtime change (2026-10-24)

A connected loopback TCP client logged in and remained idle while effective speed and laser values changed externally. It decoded `ShipAttributesCommand` showing 600 speed and 60 laser damage, then 300 and 30 after rollback, without sending a request. This directly exercises the emulator TCP client surface with a test runtime-variable registry. It does not establish PostgreSQL/Redis propagation, an actual CMS or Server Management CLI edit, or native Unity rendering. The solution build had zero errors and 4,288 non-integration tests passed. The CA-dependent Unity capture and container-backed Aspire deployment were excluded by this run's boundary rules.

## World-19 idle socket route (2026-10-24)

Connected loopback TCP clients received provisional `MapChangeCommand` within five seconds of a remote map 5 teleport and a map 1 lease reassignment without sending another packet. The tests decoded the advertised destination and confirmed saved coordinates remained intact. The solution build passed with zero errors, and 4,287 non-integration tests passed. This directly exercises the emulator TCP client surface; a native Unity TCP client, signed-in Spacemap WebView, Angular CMS and Server Management CLI were not connected. The capture path requiring CA installation and the container-backed Aspire stack remain outside this run's permitted boundary.

## World-18 remote teleport socket route (2026-10-24)

A connected loopback TCP client logged into map 1, was teleported from a second session store to map 5, and sent a movement packet through the old socket. It received provisional `MapChangeCommand` naming map 5 and `127.0.0.1:61001`; the saved destination remained `(3400,2100)`. A direct stale movement call was also rejected under the shared mutex. The solution build passed with zero errors and 4,285 non-integration tests passed. This directly confirms the emulator TCP socket surface, not the native Unity client. The signed-in Spacemap WebView, Angular CMS and Server Management CLI were not connected for this TCP-only change. The Unity capture path requiring mitmproxy CA installation and the container-backed Aspire path remain excluded by this run's boundary rules.

## World-17 remote teleport presence (2026-10-24)

An in-process two-store test teleported a pilot from map 1 to map 5 on a second node, ran the first node's heartbeat, and observed no map 1 presence. The real browser state endpoint then returned map 5 at `(3400,2100)` with one pilot entry. The shipped Spacemap HTML/JavaScript in jsdom displayed `Map 5-1` and `3400 / 2100` on its next poll for all four companies; that poll used a synthetic response. The solution build passed with zero errors, and 4,284 non-integration tests passed. The native Unity WebView, TCP client handoff, signed-in CMS, Server Management CLI and live Redis/PostgreSQL stack were not connected for this change.

## World-16 station repair and browser poll (2026-10-24)

The direct `WorldHttpBridge` endpoint test seeded a wounded pilot in the distributed-cache test double. A missing lease and a lease change after request start both returned 409 with hull still 1000; a current lease returned 200 and repaired hull to 3560. The shipped Spacemap HTML/JavaScript ran in jsdom for all four companies: a synthetic state-poll 409 displayed the map ownership retry text, kept the rendered coordinates, and the next poll resumed. The solution build passed with zero errors and 4,283 non-integration tests passed. An initial broad test filter unexpectedly started Aspire helpers; that run was stopped, its child processes were terminated, and a namespace-based filter completed without dcp or game services. This is direct exercise of the emulator endpoint and browser script, but not a connected Unity WebView or production Redis replica. The local provisional TCP socket, Angular CMS, and Server Management CLI were not connected to this repair flow; native protocol compatibility remains unconfirmed.

## World-15 target selection (2026-10-24)

The target-selection lease test reassigned map 1 and confirmed that a stale node could not write the shared target lock; the new owner could. The connected loopback TCP client test confirmed the SelectShipRequest target-lock packet and subsequent laser packets. The browser target endpoint returned the JSON 409 lease response, and the shipped Spacemap script in jsdom showed the retry message, kept its target clear, then locked the NPC on retry for all four companies. The jsdom HTTP responses were synthetic, and the endpoint and socket tests use the emulator's provisional protocol and in-memory distributed cache. A native Unity socket, signed-in browser, CMS, and management CLI were not directly observed for this change. The solution build passed with zero errors, and all 4,282 non-integration .NET tests passed.

## World-14 browser lease change (2026-10-24)

Direct `WorldHttpBridge` endpoint calls confirmed that browser move, configuration and jump return HTTP 409 `map_lease_changed` when map 1 has no live lease, leave the saved destination and configuration intact, and accept a movement command after map 1 is reassigned to a new owner epoch. Another endpoint test confirmed that collect and fire return the same 409 without awarding currency or damaging an NPC. A separate endpoint test changed the lease after the browser request captured its epoch and confirmed the stale move was rejected before state changed. The shipped Spacemap HTML/JavaScript ran in jsdom for all four companies: a synthetic 409 displayed "Map ownership changed. Try again.", preserved the ship's shown position, and a repeated canvas click moved the ship. The earlier loopback TCP client tests still cover owner handoff and packet behavior. The jsdom fetch responses are synthetic and these direct endpoint calls run in-process; a signed-in Unity WebView, connected Aspire replicas, PostgreSQL-backed CMS, and management CLI were not exercised together. Native ship rendering and native TCP wire compatibility remain unconfirmed. The solution build passed with zero errors, and 4,281 non-integration .NET tests passed.

## Client-verify-1 direct surface run (2026-10-24)

`DirectClientAdminAndWorldVerificationTests` executes the shipped `LocalSpacemapPage` HTML and JavaScript in jsdom through `scripts/client-testing/verify-spacemap-client.mjs`. Across MMO, EIC, VRU and TST it observed the Pusat Plus/Phoenix ship name and company badge in the DOM; canvas paint calls for the pilot ship, drone, P.E.T., station, gate, Streuner and bonus box; a canvas click issuing `POST /spacemap/world/move` for `(10500,6550)` and updating the displayed position; and the in-game admin form issuing `/var set world.movement.speed_multiplier 2`, showing speed 600, then rollback showing speed 300 and a new history row. **The fetch responses in this harness are synthetic.** It verifies client code behavior and request formation, not server interpolation, persisted history, authorization or the native Unity client.

The existing `WorldMeshJourneyTests` and `WorldTransportTests` were run with this test (51 total passing tests). They exercise real loopback TCP sockets and provisional emulator packets across the four companies, including world entry and movement. They do not use the Unity client. Angular's actual component test runner passed 13 tests, including runtime controls; its API is mocked. `dotnet build --no-restore` passed with zero errors. The full non-integration .NET run passed 4,279 tests with zero failures.

Still unconfirmed directly across a connected production stack: Server Management CLI `var set` reaching a live world TCP socket and browser simultaneously; Admin CMS edits/teleport/mesh scale reflected in a signed-in Spacemap; exact laser damage and reward multipliers in combat; `/heal`, `/spawn`, `/tp` and TCP admin rollback through the connected client; PostgreSQL history and cross-replica cache propagation; accurate travel-time halving; native Unity WebView rendering and wire compatibility. The Unity capture route needs the mitmproxy CA, which this run forbids installing. The Aspire/PostgreSQL integration path provisions containers outside the project boundary. No result in this report claims those behaviors passed.

## Admin-var-1 (2026-10-24)

The runtime registry migration and PostgreSQL cross-replica audit/rollback test passed. The native Spacemap DOM and TCP client, Angular Admin CMS, and Server Management CLI/AppHost have not yet been exercised with a runtime variable change. This feature has no confirmed native client compatibility claim. Client-surface verification remains open.

# Admin-var-2 verification (2026-10-24)

PostgreSQL integration tests exercised every dispatcher verb and checked `ServerManagement`, `AdminCms`, and `InGameAdmin` attribution in command history. A real HTTP test exercised the Backoffice command and runtime-vars routes, and invoked the PowerShell management CLI against that HTTP test host. A loopback TCP client observed a manual NPC spawn packet and provisional announcement command 31. A browser HTTP bridge test observed the shared announcement and spawns through a second service instance; the page contains the announcement DOM element. The solution builds and 4,268 non-integration tests pass. Browser rendering in an actual client, native Unity TCP command 31, Angular CMS, an Aspire dashboard click, and a CLI request against a running AppHost have not yet been observed in this run. The emulator-specific contract must not be treated as a captured official client protocol.

# Admin-var-3 verification (2026-10-24)

The local loopback TCP test sent command 32 through an actual socket and observed command 33, `InGameAdmin` dispatch, refreshed attributes and map packets; a non-admin socket received unauthorized code 8. Separate TCP cases changed speed and laser multipliers and decoded the refreshed ship attributes, observing 600 speed and 60 laser damage respectively. The cookie bridge tests exercised an admin slash command and non-admin 403. The Spacemap JavaScript parsed with Node. The DOM has the admin panel, variable selector, paged history, rollback controls and live HUD refresh. Direct Unity WebView interaction, PostgreSQL change history through the in-game route, and visible speed/laser changes in a signed-in client were not performed. The Unity capture path requires the trusted mitmproxy CA, whose installation is forbidden for this run. Starting the available Aspire AppHost would also create container resources outside the required project boundary. These direct-client requirements remain open.

The solution build completed with zero errors. The final non-integration run passed 4,277 tests with zero failures. No AppHost, API, Gateway, dcp, or testhost process from this run remains.

## World-32 same-map teleport socket verification (2026-10-24)

A connected loopback TCP world client began travel, stayed idle while a second session store teleported its pilot within map 1, then decoded `MoveCommand` for `(3400,2100)` with zero duration. It received no duplicate and sent a new move, which was computed from the teleported point. The local pilot record also reflected the new position. The solution build had zero errors and all 4,298 non-integration tests passed. This directly exercises the emulator's provisional TCP client surface. Native Unity, signed-in Spacemap, Angular CMS, Server Management CLI, and live PostgreSQL/Redis were not connected for this change.

## World-33 portal jump radius socket verification (2026-10-24)

A connected loopback TCP world client 700 units from portal 101 sent `JumpRequest` and decoded `LoginErrorCommand` at radius 500. After the registry value changed to 800, it sent the same request on the same socket and decoded `MapChangeCommand` `(2, "", "")` and then `MapInitializationCommand`. The solution build had zero errors and all 4,299 non-integration tests passed. This exercises the emulator's provisional TCP client surface. Native Unity, signed-in Spacemap, Angular CMS, Server Management CLI, and live PostgreSQL/Redis were not connected.

# Admin-var-4 CMS verification (2026-10-24)

Angular `npm test -- --watch=false` passed 13 tests in 6 files, including CMS request contracts and rendered runtime-variable controls. `npm run build` and `dotnet build --no-restore` passed; 4,277 non-integration .NET tests passed. The PostgreSQL backoffice integration fixture now asserts a CMS live edit, `AdminCms` history attribution, effective value, and rollback, but it was not run: Testcontainers creates resources outside this run's permitted project boundary. A signed-in Angular session, live game world observation after a CMS edit, and cross-replica confirmation remain open. The CA-dependent Unity capture was skipped under the run rules.

# World-22 remote browser movement to TCP (2026-10-24)

A connected loopback TCP client logged in on one session store and remained idle. A second store sharing the distributed-cache test implementation issued a move. The client decoded a `MoveCommand` with its pilot ID, destination `(5000,1300)` and positive remaining duration, then received no duplicate during the next poll. The solution build completed with zero errors and all 4,292 non-integration .NET tests passed. This directly checks the emulator TCP socket surface, not native Unity, signed-in Spacemap WebView, Angular CMS, management CLI, PostgreSQL or live Redis.


# World-23 cross-node movement to TCP observer (2026-10-24)

A connected loopback TCP client logged in as pilot 806 on store A and stayed idle. Store B (same cache, simulating another node) entered pilot 807 and moved it to `(4000,2500)`. The client decoded a `MoveCommand` for 807 with that destination and a positive duration, then received no duplicate in the next 1.2 s. The test is `WorldTransportTests.Loopback_PilotMovingOnAnotherNode_ReachesLocalTcpObserverOnce`. This exercises the emulator TCP socket surface only; native Unity, Spacemap WebView, Angular CMS, management CLI, PostgreSQL and live Redis were not exercised.
# World-24 remote pilot visibility (2026-10-24)

A connected loopback TCP client logged in on store A while store B, sharing the distributed-cache test instance, entered and moved pilot 807. The client decoded provisional `PilotCreateCommand` with pilot ID, name, ship, faction, map and position, then `MoveCommand` with the remote destination. After store B removed the pilot, the client decoded `PilotRemoveCommand`. This directly verifies the emulator socket surface. Native Unity ship rendering, signed-in Spacemap, Angular CMS, Server Management CLI, PostgreSQL and live Redis were not exercised for this change.


# World-25 map-level remote observer fanout (2026-10-24)

New test `RemoteObservers_OnSameMap_ShareOnePresenceAndTravelReadPerSnapshot`: three observers on node A (counting cache wrapper) each receive and decode a `PilotCreateCommand` and `MoveCommand` from their real `WorldConnection` packet stream for a pilot moved on node B, while the shared cache saw exactly one presence read and one travel read; after the TTL a new poll re-reads presence. The loopback TCP socket tests (`Loopback_PilotMovingOnAnotherNode_ReachesLocalTcpObserverOnce`, `Loopback_RemoteReplicaBrowserMove_ReachesIdleTcpObserver`) still pass, confirming create/move-once/remove on the socket surface. Native Unity rendering, signed-in Spacemap, Angular CMS, Server Management CLI, live Redis/PostgreSQL were not exercised for this change.

# World-27 browser state snapshot (2026-10-24)

New test `WorldHttpBridgeTests.StatePolls_OnOneReplica_ShareOneRemotePresenceAndPositionRead` uses two stores sharing one cache. Store B entered pilot 811 and moved it. Three real `GET /spacemap/world/state` handler calls on store A each returned pilot 811 by name, with an interpolated X between 1000 and 4000, while the counting cache saw exactly one read of 811's saved position. After the TTL, one more poll re-read it. The existing replica, teleport and jsdom `DirectClientAdminAndWorldVerificationTests` checks of the shipped Spacemap page still pass across all four companies, 81 world tests in total. Those jsdom fetch responses are synthetic. Native Unity WebView, a running AppHost with live Redis, Angular CMS and the Server Management CLI were not exercised for this change. AppHost/Testcontainers provision containers outside the project boundary, and the Unity capture route needs the forbidden mitmproxy CA.

# World-31 Server Management CLI to live TCP client (2026-10-24)

New test `ServerManagementCliLiveSocketTests.ServerManagementCli_VarSetAndRollback_RefreshConnectedTcpClientShipSpeed` starts a real Kestrel host serving the shipped `AdminCommandEndpoint` (`POST /api/v1/backoffice/admin/command`) and launches the shipped `scripts/admin/Invoke-ServerAdminCommand.ps1` as a separate `powershell` process against it. A loopback TCP world client logged in as pilot 831 stays idle on a real `WorldSessionHandler` that shares the host's runtime-variable registry. `var set world.movement.speed_multiplier 2 cli live socket` returned a change attributed to `ServerManagement` / `cli-operator` / `cli-node`, and the idle socket then decoded a `ShipAttributesCommand` with speed 600. `var rollback 1 cli undo` from the CLI returned value `1`, and the socket decoded speed 300. Both raw CLI command texts were recorded in the change history. The test is `[Explicit]` (run it by name): `UseFastEndpoints` leaves a process-wide resolver bound to the disposed test host, which broke two later `Factory.Create` tests in the full run. It runs without containers, so the PostgreSQL dispatcher and history are replaced by a test-local in-memory registry and a `var set`/`var rollback`-only dispatcher. The PostgreSQL path stays covered by the integration-tagged `AdminCommandHttpTests`. Not exercised: native Unity, a running AppHost, live Redis/PostgreSQL, signed-in Spacemap and Angular CMS.

# World-47 stale rollback via CLI on real PostgreSQL/Redis (2026-10-24)

The shipped `Invoke-ServerAdminCommand.ps1` ran against Kestrel `AdminCommandEndpoint` with the production `RuntimeVariableStore` on Testcontainers `postgres:16-alpine` + `redis:8.6` and an idle loopback TCP world client (pilot 847, 800 units from the station). Steps: set 500, then 900 (decoded 500/0, then 900/1). A stale rollback of change 1 exited 1 with `Admin command failed (HTTP 409): Rollback conflict…`; the value stayed 900 and no packet arrived. Rolling back 2, then 1, decoded 500/0, then 1000/1. PostgreSQL history: `1000,500,900,500` (newest first). Before this run, the CLI printed only `(409) Conflict`. Not exercised: Angular CMS/Spacemap rendering of the 409 text, AppHost replicas, and native Unity.
# World-48 stale rollback in CMS and Spacemap (2026-10-24)

Two named Explicit live-client tests passed against Kestrel. The shipped Angular `RuntimeVarsComponent`, real `BackofficeApiService` and browser XMLHttpRequest received HTTP 409 after clicking Rollback on obsolete change #1. Its alert displayed `Rollback conflict:`, its refreshed editor remained at radius 1000, and history retained the two successful changes. The shipped `LocalSpacemapPage` in jsdom submitted the same obsolete rollback over real HTTP, displayed the conflict in `#adminResult`, reloaded variables/history/state, and kept `#safeZone` as `Protected`; no third history entry was written. The live test writer models the production conflict with an in-memory cache, so this verifies client presentation and endpoint status, while World-47 covers production PostgreSQL/Redis through the CLI and TCP client. The solution build had zero errors. The non-integration suite passed 4,300/4,301 tests; `Loopback_RemoteReplicaBrowserMove_ReachesIdleTcpObserver` timed out under parallel load and passed immediately in isolation. Signed-in native Unity/Vuplex remains unverified under the CA restriction.


# World-49 lost idle-socket updates at login (2026-10-24)

Root cause of the two intermittent socket failures from World-47/48:
1. `Loopback_ExternalVariableChangeAndRollback_RefreshIdleSocketAttributes` changed speed and laser with two separate writes. The idle poll legitimately emitted the half-applied `(600, 30)`. The test now changes one variable per step, as one admin `var set`/rollback each does, and asserts every intermediate `ShipAttributesCommand` decoded by the TCP client: `(600,30) → (600,60) → (300,60) → (300,30)`.
2. `Loopback_RemoteReplicaBrowserMove_ReachesIdleTcpObserver` exposed a real server race. `WorldSessionHandler` primed the observed-travel and advertised-stats baselines only after flushing the entry packets, so a move or variable change landing in that window was never announced. The baselines are now taken before the entry burst.

Verification: the loopback TCP world-client tests ran six times back-to-back after the server fix (26/26 each) and eight times before it with the test fix (26/26 each). The full non-integration suite passed 4,301/4,301, both before and after the server change. The solution build had zero errors. Not exercised: AppHost replicas with live Redis, Angular CMS/Spacemap (unchanged), and native Unity (CA restriction).

# World-53 idle NPC roster sync (2026-10-24)

Client surface: a connected loopback TCP world client (`WorldPacketReader`/`WorldPacketWriter` on a real socket against `WorldSessionHandler`). New test `Loopback_IdleSocket_SyncsNpcRosterFromVariablesAndRemoteKills`. No packet was sent after login; every packet below came from the idle poll:
1. After login the client decoded one Streuner `ShipCreateCommand`.
2. `world.spawn.streuner_count` changed to 2, as an admin `var set` would. The client decoded a `ShipCreateCommand` with a different NPC id.
3. A second-node `WorldNpcs` + `WorldSessionStore` on the same cache selected and destroyed that NPC. The client decoded `ShipDestroyCommand` with that id; the local broadcast could not have sent it because the killer was not on that node.
4. The count changed to 0, as a rollback would. The client decoded `ShipDestroyCommand` for the first NPC.

The focused NPC/laser-fire loopback tests passed 11/11. Not exercised in this run: the Spacemap page (its `/spacemap/world/state` poll already reads `VisibleAsync`), the Angular CMS, the Server Management CLI against a live AppHost, AppHost replicas with live Redis, and native Unity (CA restriction).

## World-54 CLI to live TCP NPC roster (2026-10-24)

`ServerManagementCli_SpawnCountSetAndRollback_RefreshIdleTcpNpcRoster` ran the shipped PowerShell CLI against Kestrel while a real loopback TCP world client remained connected. The client decoded one initial Streuner, a second after `var set ... 2`, destruction of both after `var set ... 0`, and recreation of both after rolling back the zero change. The test asserted IDs and three `ServerManagement` history values. This directly verifies the emulator CLI and socket client surfaces. It uses an in-memory registry/cache and a provisional packet format; native Unity, Angular CMS, Spacemap DOM, live PostgreSQL/Redis replicas, and AppHost were not exercised for this change.

The existing live Spacemap DOM check (`ServerManagementCli_PeaceRadiusSetAndRollback_UpdateLiveSpacemapSafeZoneDom`) and Angular CMS to replica-B Spacemap/TCP check (`AdminCms_RuntimeVarsEditorOnReplicaA_UpdatesLiveSocketClientOnReplicaB`) also passed in this run (2/2). They exercise peace-radius controls, not the new spawn-count scenario. The non-integration suite passed 4,303/4,303 and the solution build had zero errors.


## World-55 TCP NPC vitals from remote damage (2026-10-24)

`Loopback_IdleSocket_SyncsNpcVitalsFromRemoteDamage`: a real loopback TCP world client logged in, received the Streuner `ShipCreateCommand`, and then sent nothing. A simulated second node sharing the cache selected the NPC and landed one nonlethal shot. The idle client decoded `NpcHealthCommand=37` with the NPC id, the post-hit HP and shield returned by the remote attack, and the unchanged max values. All 61 `WorldTransportTests` passed. Native Unity, Spacemap DOM, Angular CMS and live AppHost/Redis were not exercised for this change.
## World-56 NPC snapshot and TCP clients (2026-10-24)

The per-map observer snapshot reduced eight concurrent observer reads to one cache read per NPC per 500 ms. After a simulated remote hit and expiry, all eight saw the new shield. Existing real loopback TCP client tests for idle NPC roster changes and remote damage passed, confirming that provisional create/destroy/health packets still reach the socket client. Three existing explicit live-client checks also passed: the shipped management CLI changed NPC spawn count and rolled it back while an open TCP client decoded roster packets; the Angular CMS editor changed runtime variables across two local replicas while a Spacemap client and TCP client observed them; the shipped Spacemap page targeted and fired on an NPC over live HTTP and rendered the damage. Those three paths do not directly exercise the new NPC snapshot. Native Unity and live Redis replicas remain unverified. Unity capture remains excluded by the CA rule; no signed-in Unity behavior is claimed. Solution build: zero errors. Non-integration suite: 4,305/4,305 passed.

## World-59 credited reward on TCP client (2026-10-24)

`Loopback_NpcSpawnLockLaserAndRewardPackets` connected to the emulator WorldServer socket, selected a Streuner, fired two shots, and decoded the lethal hit, destroy, reward, and accepted-result packets. With reward multipliers 2/3/1.5/4, the `NpcRewardCommand` payload carried `(id, 800 credits, 3 uridium, 600 XP, 8 honor)`, matching the values passed to the reward store. This confirms the actual provisional TCP client stream. Native Unity packet handling remains unverified under the no-CA rule.

The solution build passed with 0 errors; the non-integration suite passed 4,306/4,306. Four named live client checks also passed separately: the shipped Spacemap combat page against Kestrel, the Angular CMS editor on replica A reaching a TCP socket and Spacemap page on replica B, the in-game admin panel in jsdom against live HTTP, and the shipped Server Management CLI on replica A reaching the Spacemap DOM on replica B. These checks cover their existing flows; only the TCP socket check directly exercises the corrected reward payload. Native Unity/Vuplex and live PostgreSQL/Redis replicas remain unverified for this change.

## World-60 reward retry after runtime multiplier change (2026-10-24)

The shipped Spacemap script ran in jsdom with real fetches to live Kestrel. It selected and killed a Streuner, rendered `400 credits, 1 uridium, 400 XP, 2 honor`, then retried `POST /spacemap/world/fire` after the host changed the credits multiplier from 1 to 5. The response was 409 `destroyed`; the test reward recorder received 400 credits on both attempts. A separate two-`WorldNpcs` shared-cache test changed all four reward multipliers between kill and retry and observed the original `(800, 3, 600, 8)` values. This verifies the emulator browser HTTP surface and shared-cache model. Native Unity, an Angular CMS edit during combat, and live PostgreSQL/Redis replicas were not exercised for this change.

The solution build passed with zero errors and 4,307/4,307 non-integration .NET tests passed. Named live checks also passed for the Angular CMS runtime-variable editor across two local replicas, the in-game admin panel, the Server Management CLI to Spacemap DOM, and the connected emulator TCP reward packet. Those checks cover existing paths; only the Spacemap retry and two-instance cache test directly exercise the reward snapshot.

## World-61 distant reward retry (2026-10-24)

The shipped Spacemap page ran in jsdom against live Kestrel HTTP. It entered, selected and killed a Streuner, rendered the 400-credit reward and ship canvas, then observed `GET /spacemap/world/state` reporting pilot position `(0,0)` after the host teleported it beyond laser range. The host also changed the credits multiplier to 5. The page retried `POST /spacemap/world/fire` and received 409 `destroyed`; the reward recorder received the original 400 credits on both attempts. A separate two-instance shared-cache test teleported the killer away before retrying on the second `WorldNpcs` instance and verified the original four reward amounts. This directly verifies the emulator browser HTTP surface and shared-cache behavior; native Unity/Vuplex and live Redis/PostgreSQL replicas remain unverified. The no-CA rule prevented a Unity capture.

The solution build passed with zero errors; 4,307/4,307 non-integration tests passed after the test change. Separate named client checks passed for the connected TCP reward packet, Angular CMS editor reaching a second local replica's socket and Spacemap, in-game admin panel over live HTTP, and the shipped Server Management CLI reaching a second local replica's Spacemap DOM. These are regression checks of existing flows; the distant retry was observed by the Spacemap check above.

## World-62 spawn removal reward retry (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. It selected, moved toward and killed a Streuner, displaying 400 credits and painting the ship. The host changed the credits multiplier to 5, set the Streuner spawn count to zero and teleported the pilot to `(0,0)`. The client read `/state`, observed the killed NPC absent and the new position, then sent `/fire` and received `409 destroyed`. The reward recorder saw a second credit attempt for the original 400 credits. A two-instance shared-cache test covered the same spawn removal. Native Unity/Vuplex remains unverified under the no-CA rule.

## World-69 selected box claimed by another client (2026-10-24)

`SpacemapClient_ShowsUnavailableWhenAnotherTabClaimsSelectedBox` ran the shipped page JavaScript in jsdom against live Kestrel. The page clicked a box and moved toward it; a second authenticated HTTP client claimed it when in range. The first page's next state poll rendered “Bonus box is no longer available” and made no `/collect` request. The server wallet recorded one credit. This confirms the emulator browser surface; signed-in Unity/Vuplex and a separate-process mesh were not exercised.

The solution build passed with 0 errors. The non-integration suite passed 4,307/4,307. Four additional explicit live-client checks passed for the Server Management CLI, Angular CMS variable editor, in-game admin panel, and connected TCP admin socket; these are regressions of their existing variable flows and do not exercise the pickup race.

# World-71 pending pickup canceled by NPC target (2026-10-24)

`SpacemapClient_TargetSelectionCancelsPendingBoxPickup` ran the shipped `LocalSpacemapPage` JavaScript in jsdom against live Kestrel HTTP. It observed `/enter`, `/move` toward a bonus box, `/target` for a Streuner, a rendered target, and `/state` polls until the pilot entered pickup range. No `/collect` request occurred; the box remained visible and the wallet recorded zero credits. This directly checks the emulator browser client surface. The pickup change was not exercised in native Unity/Vuplex or through live Redis/PostgreSQL. The CA-dependent Unity capture was skipped under the run rules.

The solution build had zero errors; all 4,307 non-integration tests passed. Three separate existing live client checks passed for Angular CMS, in-game admin/TCP, and Server Management CLI to Spacemap across local replicas. Those are regression checks of existing runtime-variable flows, not tests of the pickup change.

# World-72 gate jump cancels old-map pickup (2026-10-24)

`SpacemapClient_JumpCancelsPendingBoxPickup` runs the shipped page JavaScript in jsdom against live Kestrel HTTP. It observed `/enter`, `/move` toward a map-1 bonus box, `/jump` to map 2, and `/state` on map 2. The DOM retained “Jumped to map 1-2”; no `/collect` was sent and the wallet recorded zero credits. This verifies the emulator browser client transition. The native Unity/Vuplex client and a separate-process Redis mesh were not exercised; the CA-dependent capture was skipped under this run's rules.

The solution build passed with zero errors and all 4,307 non-integration tests passed. Three existing explicit live client checks for the Angular CMS editor, in-game admin panel, and Server Management CLI also passed; they are runtime-variable regressions and do not exercise the jump change.

# World-73 late old-map poll after jump (2026-10-24)

`SpacemapClient_JumpCancelsPendingBoxPickup` now holds a real `/spacemap/world/state` response from map 1 inside the shipped page's jsdom fetch adapter, completes a real `/jump` to map 2, then releases that response. The rendered map and jump status stay on map 2; no old-map `/collect` is sent. This checks the real browser script and Kestrel endpoints with a forced response ordering. It does not verify native Unity/Vuplex, which requires CA-dependent capture excluded by this run's rules, or separate-process Redis replicas.

Solution build: 0 errors. Non-integration .NET suite: 4,307/4,307 passed. Four separately selected live regressions passed for the Server Management CLI, Angular CMS editor, in-game admin panel, and connected TCP admin client; those cover existing runtime-variable flows, while the delayed-response check covers this change.


# World-92 delayed pickup after external jump (2026-10-24)

The shipped Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. It selected a map-1 box, held its `/collect` request before delivery, and a second authenticated client jumped the pilot to map 2. Releasing the pickup returned `409 map_changed`; the page immediately fetched `/state` and rendered `Map 1-2` and `Map changed to 1-2`. The test wallet saw zero credits. Two server checks cover the HTTP rejection and the under-mutex map refresh. The solution built with zero errors and all 4,314 non-integration tests passed. Five direct admin regressions passed across the management CLI, Angular CMS, in-game admin panel and connected TCP socket. Normal and changing-radius live pickup checks also passed after their scripts were made to wait for the move response before polling. Native Unity/Vuplex and separate-process Redis replicas were not exercised under this run's no-CA rule.



## World-94 stale state poll after configuration switch (2026-10-24)

The local Spacemap page now counts completed configuration requests. A `/state` poll that started before a configuration request finished is discarded, and the page waits for any in-flight poll before its follow-up refresh. Previously the in-flight poll could repaint the old configuration and the follow-up refresh was skipped while that poll was running. `verify-spacemap-config-stale-poll-live.mjs` held a real `/state` response with configuration 1, clicked configuration 2 (`POST /config` 200), then released the held response. The DOM kept configuration 2 pressed after one fresh `/state`. No HTTP or TCP contract changed; native Unity/Vuplex remains unverified under the no-CA rule.
## World-95 stale configuration after jump (2026-10-24)

The shipped emulator Spacemap HTML/JS ran in jsdom against live Kestrel HTTP. The page's map-1 `POST /config` was held before network delivery while another authenticated client jumped the pilot. The held request then returned HTTP 409 `map_changed`; the page fetched `/state`, displayed map 1-2, and kept configuration 1 selected. This directly verifies the changed emulator browser DOM and HTTP surface. It does not confirm native Unity/Vuplex behavior. The check used one Kestrel host, so separate-process Redis behavior for this race remains unverified.

The full solution built with 0 errors. All 4,315 non-integration tests passed. Three explicit configuration page checks passed against the final build. Four separate direct regression checks passed for the Server Management CLI, Angular CMS editor, in-game admin panel and connected TCP admin client; those four cover unchanged admin paths.


## World-96 jump conflict mesh owner header (2026-10-24)

`SpacemapClient_JumpConflictAdvertisesCurrentMapOwner` runs the shipped Spacemap HTML/JS in jsdom against two live Kestrel replicas sharing world cache, mutex and a mesh registry (map 1 leased to node `a`, map 2 to node `b`). The page's jump click on replica A was held before delivery while a second authenticated client jumped the pilot through replica B. Before the fix the released request returned `409 map_changed` with `X-Mesh-Map-Owner-Node: a` (failing run observed: "jump conflict advertised a, current map owner is b"). After the fix it advertises `b`, the page refreshed `/state` and the DOM shows `Map 1-2`. Script: `scripts/client-testing/verify-spacemap-jump-owner-header-live.mjs`. The three configuration live checks (ordered, stale poll, delivered after jump) were re-run and pass. Native Unity/Vuplex is not verified (CA-dependent capture excluded by this run's rules); replicas were in-process Kestrel hosts, not separate Redis processes.
