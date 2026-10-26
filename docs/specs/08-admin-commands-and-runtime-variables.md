# SPEC-08: Admin commands and runtime variables

## Scope and surfaces

`AdminCommandDispatcher` interprets commands. `RuntimeVariableStore` persists values and immutable history in PostgreSQL and caches current values. These are emulator controls, not captured official client contracts. The only accepted attribution strings are `ServerManagement`, `AdminCms`, and `InGameAdmin`; commands require a nonblank actor and node ID, and changes require a reason.

| Surface | Entry and access |
| --- | --- |
| ServerManagement | `scripts/admin/Invoke-ServerAdminCommand.ps1` sends `surface: ServerManagement` to `POST /api/v1/backoffice/admin/command`. The backoffice variable routes use `X-Admin-Surface: ServerManagement` for this attribution. `OpsRead` and `OpsWrite` policies apply. |
| AdminCms | CMS `/backoffice/runtime-vars` calls `/api/v1/backoffice/runtime-vars` and posts commands with `surface: AdminCms`. This is the default for backoffice variable routes. Same policies apply. |
| InGameAdmin | An admin pilot calls `/spacemap/world/admin/command`, `/vars`, `/vars/history`, `/vars/rollback`, or sends provisional TCP `AdminCommandRequest` (32). The HTTP bridge checks the authenticated session's admin flag (or the local `owner` username); TCP checks the connected pilot's admin flag. Both force this surface. |

The common backoffice command endpoint accepts any of the three client supplied surface strings, so the string is attribution rather than an authorization boundary. Both `runtime-variables` and `runtime-vars` backoffice route spellings exist. In-game get/set/reset use the command endpoint. The backoffice routes also include GET list/get/history, PUT set, POST reset, and POST history rollback.

| HTTP route | Request | Response and errors |
| --- | --- | --- |
| `POST /api/v1/backoffice/admin/command` | `{text?,verb?,arguments?,surface?}`; surface defaults to `AdminCms` | `AdminCommandResult {command,data,auditId}`; invalid surface/argument 400, missing target 404, conflict 409 |
| `GET /api/v1/backoffice/runtime-vars` | none | Array of effective `RuntimeVariable` records |
| `GET /api/v1/backoffice/runtime-vars/{key}` | dotted key | `RuntimeVariable`; unknown key 404 |
| `PUT /api/v1/backoffice/runtime-vars/{key}` | `{value,reason}` | `RuntimeVariableChangeEntry`; invalid input 400 |
| `POST /api/v1/backoffice/runtime-vars/{key}/reset` | `{reason}` | New change entry; unknown key 404 |
| `GET /api/v1/backoffice/runtime-vars/history` | optional `key`, `limit`, `beforeChangeId` | Descending change entries; default limit 100 |
| `POST /api/v1/backoffice/runtime-vars/history/{changeId}/rollback` | `{reason}` | New change entry; unknown ID 404, stale value 409 |
| `GET /spacemap/world/admin/vars` | authenticated admin session | Effective variable array; non-admin 403 |
| `GET /spacemap/world/admin/vars/history` | optional `key`, `limit`, `beforeChangeId` | Descending changes; default limit 20, maximum 100; non-admin 403 |
| `POST /spacemap/world/admin/command` | `{text}` | Command result; invalid JSON/input 400, non-JSON media 415, non-admin 403, missing target 404, conflict 409 |
| `POST /spacemap/world/admin/vars/rollback` | `{changeId,reason}` with reason 1..256 characters | Command result; invalid input 400, non-admin 403, missing ID 404, conflict 409 |

`RuntimeVariable` contains `key`, `category`, `valueType`, `defaultValue`, `currentValue` in JSON. `AdminCommandResult.data` depends on the verb. Backoffice command history is separately available at `GET /api/v1/backoffice/admin/command/history?limit=`; it returns descending command IDs with outcome/error and defaults to 100, clamped to 1..1,000. The provisional TCP request carries one encoded command string and responds with success byte plus JSON result, or failure byte plus error string.

For in-game HTTP commands, the authenticated pilot's admin check runs before JSON body parsing or command dispatch. The body must be a JSON object with a nonblank string `text`; missing, null, numeric, whitespace-only and malformed values return 400 without dispatch. An unauthorized pilot receives 403 even when the body is malformed. Successful responses use `Cache-Control: no-store`. HTTP and TCP both expand shortcuts and construct `InGameAdmin` attribution from the authenticated or connected pilot, rather than accepting an actor or surface from the command body.

## Built-in variable catalog

Defaults are invariant-culture strings. The three mesh defaults are read from `Cluster` configuration with the listed fallbacks. A persisted row supersedes a catalog default. `CurrentValue` initially equals `DefaultValue`.

| Key | Category | Type | Default | Live consumer or meaning |
| --- | --- | --- | --- | --- |
| `world.tick_rate_ms` | World | int | 1000 | NPC simulation delay |
| `world.radiation.damage_per_attempt` | World | int | 0 | boundary move damage |
| `world.config.switch_cooldown_ms` | World | int | 5000 | configuration switch cooldown |
| `world.portal.jump_radius` | World | int | 500 | portal proximity |
| `world.station.peace_radius` | World | int | 1000 | station safe area |
| `world.station.repair_hp_per_tick` | World | int | 0 | station repair |
| `world.movement.speed_multiplier` | Movement | double | 1 | live speed |
| `world.ship.pusat_plus_base_speed` | Movement | int | 370 | slot 1 base speed |
| `world.ship.phoenix_base_speed` | Movement | int | 320 | slot 2 base speed |
| `world.combat.laser_damage_multiplier` | Combat | double | 1 | live laser damage and NPC hits |
| `world.combat.fire_range` | Combat | int | 1000 | NPC laser range |
| `world.combat.shield_absorption_ratio` | Combat | double | 1 | NPC shield absorption |
| `world.spawn.streuner_count` | Spawns | int | 3 | Streuner roster count |
| `world.spawn.lordakia_count` | Spawns | int | 2 | Lordakia roster count |
| `world.spawn.saimon_count` | Spawns | int | 1 | Saimon roster count |
| `world.spawn.bonus_box_count` | Spawns | int | 6 | generated collectible slots |
| `world.collectibles.respawn_seconds` | Collectibles | int | 300 | cycle and claim lifetime |
| `world.collectibles.pickup_radius` | Collectibles | int | 180 | pickup proximity and HTTP state |
| `economy.credits_reward_multiplier` | Economy | double | 1 | NPC and box credits |
| `economy.uridium_reward_multiplier` | Economy | double | 1 | NPC and box uridium |
| `economy.xp_reward_multiplier` | Economy | double | 1 | NPC experience |
| `economy.honor_reward_multiplier` | Economy | double | 1 | NPC honor |
| `economy.shop_discount_percent` | Economy | double | 0 | shop discount control |
| `mesh.desired_replicas` | ServerMesh | int | `Cluster:DesiredWorkers`, fallback 1 | desired mesh size |
| `mesh.max_maps_per_node` | ServerMesh | int | `Cluster:MaxMapsPerNode`, fallback 4 | stored limit; assignment does not enforce it |
| `mesh.heartbeat_ttl_seconds` | ServerMesh | int | `Cluster:LeaseTtlSeconds`, fallback 30 | lease lifetime |

`SetAsync` also accepts an unknown valid dotted key. It creates a `Dynamic` variable, infers `int`, `double` or `string`, and uses the normalized first value as its default. This does not create a gameplay consumer. Numeric input is finite and within ±1,000,000; strings are at most 4,096 characters. Known keys reject negatives. Additional bounds: tick 20..60,000 ms; respawn 1..86,400 s; NPC counts 0..30; box count 0..1,000; shield ratio 0..1; discount 0..100; speed multiplier >0..100; replicas 1..16; maps/node 1..256; heartbeat TTL greater than twice `Cluster:HeartbeatIntervalSeconds` (fallback 10) and at most 3,600 s.

## Write-path validation contract

Every write (`SetAsync`, `ResetToDefaultAsync`, `RollbackAsync`) goes through one write path in `RuntimeVariableStore`. The gates run in this order, and the first failure aborts the write with nothing persisted:

1. **Key grammar** (checked before any connection is opened). The key must match `^[a-z][a-z0-9]*(\.[a-z][a-z0-9_]*)+$` and be at most 128 characters. So the first segment has lowercase letters and digits only, there is at least one more segment, every segment starts with a lowercase letter, and only later segments may contain `_`. Hyphens, uppercase, empty segments and surrounding whitespace are rejected. Failure: `ArgumentException` with `ParamName = "key"`.
2. **Attribution** (also checked before any connection). `Surface` must be exactly `ServerManagement`, `AdminCms` or `InGameAdmin` (case-sensitive). `ChangedBy`, `NodeId` and `Reason` must not be null or whitespace. Failure: `ArgumentException` with `ParamName = "actor"`.
3. **Connection.** If no `identity-write` connection string is configured, the write fails with `InvalidOperationException`. A request that reaches this point has passed gates 1 and 2.
4. **Rollback staleness** (rollback only). See the audit section below.
5. **Normalization** against the stored `value_type`, using invariant culture. The result is the stored canonical text. Failure: `ArgumentException` with `ParamName = "value"`.
   - `int`: `NumberStyles.Integer` (leading/trailing whitespace and a sign are allowed), range ±1,000,000. `"007"` is stored as `7`, `"+5"` as `5`. Decimals and exponents are rejected.
   - `double`: `NumberStyles.Float`, finite, range ±1,000,000, stored round-trip (`"R"`). `"1e3"` is stored as `1000` and `"1000000.0"` as `1000000`. `NaN`, `Infinity` and comma decimals are rejected.
   - `string`: at most 4,096 characters, stored verbatim.
   - Any other type: always rejected.
6. **Range.** Values that are not numeric skip this check. Numeric values for catalog keys must be ≥ 0, and keys with explicit bounds must also fall inside them (all bounds inclusive unless noted): `world.tick_rate_ms` 20..60,000; `world.collectibles.respawn_seconds` 1..86,400; the three NPC spawn counts 0..30; `world.spawn.bonus_box_count` 0..1,000; `world.combat.shield_absorption_ratio` 0..1; `economy.shop_discount_percent` 0..100; `world.movement.speed_multiplier` greater than 0 and at most 100; `mesh.desired_replicas` 1..16; `mesh.max_maps_per_node` 1..256; `mesh.heartbeat_ttl_seconds` greater than 2 × `Cluster:HeartbeatIntervalSeconds` (fallback 10) and at most 3,600. Dynamic (non-catalog) keys accept negative values. Failure: `ArgumentOutOfRangeException`.

Reads: `GetAsync` returns a cached `runtime-variable:v1:{key}` entry without querying PostgreSQL. `NumberAsync` parses `CurrentValue` with invariant culture, so the process culture (for example `es-PE`, which uses a decimal comma) cannot change the result. `ResetToDefaultAsync` resolves the default through `GetAsync` and then applies gates 1–6, so a reset with invalid attribution fails even when the value is served from cache.

## Command grammar and effects

Root and subverb are case insensitive. Text is whitespace tokenized, single/double quotes group text, and backslash escapes inside quotes. Text over 2,048 characters, unterminated quotes, missing arguments, bad numbers and unknown verbs fail. `AdminCommand` may instead contain `Verb` and `Arguments`. Each successful or failed execution writes `identity.admin_command_history` with surface, actor, node, text, success and error when PostgreSQL is reachable. Success returns an audit ID.

`Parse` counts input characters before tokenization: 2,048 characters are accepted, 2,049 are rejected even if the text is all whitespace. Any Unicode whitespace separates tokens outside quotes. Single and double quotes can start in the middle of a token, and adjacent quoted and unquoted pieces form one token. Empty quoted pieces do not create an argument. Outside quotes, a backslash is literal. Inside either quote style, a backslash consumes the next character and appends it literally, including a quote or another backslash; a final backslash leaves the quote unterminated. Blank text returns no tokens, and dispatch then rejects the empty command. These are emulator parser rules, not native client evidence.

| Command | Effect |
| --- | --- |
| `help` | Lists accepted grammar. |
| `var list [category\|search]` | Lists effective values; optional case insensitive category/key search. |
| `var get <key>` | Gets one effective value. |
| `var set <key> <value> [reason]` | Validates, persists, caches and audits a change. Default reason is `Admin command`. |
| `var reset <key> [reason]` | Writes the stored default as a new change. |
| `var history [key] [limit]` | Newest change ID first; default 100, clamp 1..1,000. A numeric third token is a limit. |
| `var rollback <changeId> [reason]` | Restores the previous value and appends a change; conflicts when current value differs from that change's new value. |
| `mesh status` | Returns nodes, leases and scale settings. |
| `mesh scale <desiredReplicas> [maxMapsPerNode]` | Sets both mesh variables and scales; limits 1..16 and 1..256. |
| `mesh drain <nodeId>` | Drains an active node when another active target exists and a real node has no pilots. |
| `mesh activate <nodeId>` | Reactivates a draining node. |
| `mesh rebalance` | Recomputes map leases. |
| `world status [mapId]` | Returns map owners and online pilots. |
| `world tp <userIdOrName> <mapId> <x> <y>` | Teleports an online pilot; map 1..16, x 0..21,000, y 0..13,100. |
| `world heal <userIdOrName>` | Restores online pilot HP and shield. |
| `world spawn <box\|ore\|streuner\|lordakia\|saimon> <mapId> <x> <y>` | Creates a manual world event at valid coordinates. |
| `world grant <userIdOrName> <credits\|uridium\|xp\|honor> <amount>` | Credits a positive amount through the economy ledger; `xp` maps to `experience`. |
| `world announce <message>` | Publishes 1..500 characters. |

`economy grant` and `mesh assign-map` are requested names but **not implemented verbs**. The shipped grant verb is `world grant`; map ownership is computed by rebalance. Tests must reject the missing spellings.

For an in-game command beginning with `/`, `InGameAdmin.Expand` maps `/tp <map> <x> <y>`, `/heal`, and `/grant <asset> <amount>` to commands targeting the issuing pilot, and maps `/spawn ...` to `world spawn ...`. Other leading slashes are removed and the remaining command is dispatched. This shortcut is available on both in-game HTTP and TCP entry points.

Only a slash at character zero triggers expansion; leading whitespace leaves text unchanged. A lone slash or slash followed by whitespace expands to `help`. Shortcut names are case insensitive. `/heal` ignores additional tokens. Unrecognized slash commands have exactly the first slash removed, preserving the remainder's case and spacing.

Shortcut rewriting has three output shapes:

- `/tp` and `/grant` emit `world tp <userId> ` or `world grant <userId> ` followed by the **parsed** remaining tokens joined by single spaces. Quotes are therefore removed and repeated whitespace collapses. With no further tokens the output keeps a trailing space, and dispatch then fails with a missing argument.
- `/spawn` emits `world ` followed by the **raw** text after the slash, so case, quotes and spacing are kept (`/SPAWN Box 1 2 3` becomes `world SPAWN Box 1 2 3`). The dispatcher then lowercases the spawn kind.
- `/heal` always emits `world heal <userId>`, so a different target named after it is ignored.

The text after the slash is parsed before matching, so an unterminated quote after a slash throws `ArgumentException`. Text without a leading slash is never parsed. `//heal` removes one slash and returns `/heal`. `InGameAdmin.Actor` uses surface `InGameAdmin` and actor `<userName>:<userId>`. A null, empty or whitespace node ID falls back to `<MachineName>-<ProcessId>`.

## Manual world events contract

`WorldAdminEvents` stores manual spawns and the current announcement in the shared distributed cache, so every replica sees the same state.

- Spawns are stored per map under `world:admin:spawns:<mapId>` with a one-hour absolute lifetime, and each write holds the mesh mutex. Maps never share spawns. Boxes and NPCs keep their insertion order.
- Each spawn gets a random negative ID, so it cannot clash with the positive IDs of generated world objects.
- `box` creates collectible kind `bonus` with 200 `credits`. `ore` creates collectible kind `ore` with 10 `prometium`.
- NPC spawns use the capitalized kind as the name and `npc_<kind>` as the ship ID, start at full HP and shield, and reward 400 credits, 1 uridium, 400 experience and 2 honor:

| Kind | HP | Shield |
| --- | ---: | ---: |
| `streuner` | 800 | 200 |
| `lordakia` | 2,000 | 800 |
| `saimon` | 4,000 | 1,600 |

- One map holds at most 200 manual spawns, counting boxes and NPCs together. The 201st spawn throws `InvalidOperationException` (HTTP 409 through the admin endpoint) and leaves the list unchanged. Other maps are not affected.
- `WorldAdminEvents` does not validate the spawn kind, map or coordinates; `world spawn` in the dispatcher does that first.
- `LatestAsync` returns null until an announcement exists. Each announcement replaces the previous one, keeps the actor, surface and UTC timestamp, and expires 10 minutes after it was written.

## Interactive Server Admin CLI

`scripts/admin/Start-AdminCli.ps1` starts `scripts/admin/admin-cli.mjs` (Node.js) as an interactive REPL on the ServerManagement surface. `Invoke-ServerAdminCommand.ps1` without `CommandText`/`AiQuery`, or with `-Interactive`, opens the same REPL.

```powershell
.\scripts\admin\Start-AdminCli.ps1                                   # https://localhost:60952, local seed account
.\scripts\admin\Start-AdminCli.ps1 -BaseUri https://localhost:60952 -Username owner
.\scripts\admin\Start-AdminCli.ps1 -AiQuery "make everyone twice as fast" -AutoExecute
```

- Target: `-BaseUri` (aliases `-Url`, `-GatewayUrl`), else `THDARKORBIT_GATEWAY_URL`, else `https://localhost:60952`. The wrapper sets `NODE_TLS_REJECT_UNAUTHORIZED=0` for the local development certificate only, so do not point it at a non-local host.
- Sign-in: `/api/v1/identity/login`, falling back to the local Keycloak realm token endpoint. The defaults are the AppHost platform-owner seed (`owner`, parameter `owner-password`). Do not pass real credentials on the command line.
- Calls: `GET /api/v1/backoffice/runtime-vars`, `GET /api/v1/backoffice/runtime-vars/history`, and `POST /api/v1/backoffice/admin/command`. The same policies and attribution rules apply as for every other ServerManagement call.
- Slash commands: `/vars [category]`, `/getvar`, `/setvar <key> <value> [reason]`, `/resetvar`, `/varhistory [key] [limit]`, `/rollbackvar <changeId>`, `/grant`, `/spawn`, `/tp`, `/heal`, `/announce`, `/status [map]`, `/mesh [status|scale|rebalance|drain|activate]`, `/ai <request>`, `/help`, `/exit`. Tab completion covers command names and variable keys.
- `/ai` and `-AiQuery` turn plain English into proposed slash commands. They use a local Ollama (`127.0.0.1:11434`) or LM Studio (`127.0.0.1:1234`) model when one answers, and a built-in rule catalog otherwise. Read-only proposals run directly. Mutating proposals are shown for confirmation unless `-AutoExecute` (`--auto-execute` / `-y`) is given.

## Audit, ordering, rollback and live effects

Each variable entry has `ChangeId`, `VariableKey`, `Category`, `PreviousValue`, `NewValue`, `DefaultValue`, `ValueType`, `Surface`, `ChangedBy`, `NodeId`, `Reason`, optional `RawCommand`, and `ChangedAtUtc`. A PostgreSQL advisory transaction lock and row lock serialize writes. History is ordered by descending change ID; `beforeChangeId` selects older rows. Set, reset and rollback append entries even for equal values. Rollback checks **value equality**, not latest change ID: a later write restoring the same text can allow an older rollback. Current values are cached for 30 days after reads and writes.

Movement reads current base speed and multiplier when a move begins; `LiveStatsAsync` reports current speed and laser damage. NPC attacks read current damage multiplier, range and shield ratio. Collectible visibility/pickup read current count, cycle, radius and reward multipliers. Already running travel, credited rewards and killed NPC reward snapshots are not retroactively recalculated. Any surface writes the shared registry, so subsequent operations on other surfaces/replicas use the updated value subject to cache and observer snapshot delay.

## Verification boundary

`ClientTesting/AdminAndRuntimeVariablesSpecificationTests` pins the catalog, parser boundaries, in-game shortcut rewriting and actor fallback, manual spawn shapes, per-map isolation, the 200-spawn cap, announcement replacement, surface attribution, in-memory history/rollback semantics, and live movement/combat/collectible consumers without Docker. `ClientTesting/WorldHttpBridgeTests` pins in-game HTTP command admission, malformed input precedence and dispatch attribution. `Architecture/ApiServiceVerticalSliceTests` checks backoffice admin policies and the shared HTTP/TCP in-game admission and attribution boundary. `ClientTesting/RuntimeVariableValidationSpecificationTests` pins the write-path validation contract: key grammar, attribution, normalization and every range boundary, plus cached reads, without Docker. `Integration/AdminCommandDispatcherTests`, `Integration/RuntimeVariablePostgresTests`, and `Integration/AdminCommandHttpTests` exercise PostgreSQL audit and HTTP paths when Docker is available. The non-Docker suite cannot establish database durability or a real three-surface end-to-end transaction.
