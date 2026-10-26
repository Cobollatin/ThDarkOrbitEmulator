# SPEC-07: Scalable Server Mesh & .NET Aspire Orchestration

## 1. Scope & Purpose
Specifies multi-replica `api-game` orchestration in `ThDarkOrbitEmulator.AppHost`, distributed map ownership and leasing in `Features/World/Mesh/ServerMeshCoordinator`, cross-node portal jump handoffs in `WorldSessionHandler`, and the Redis-backed shared registry. These are local emulator contracts, not official server behaviour.

Executable contract: `ThDarkOrbitEmulator.Tests/ClientTesting/ServerMeshSpecificationTests.cs` and `ThDarkOrbitEmulator.Tests/ClientTesting/MeshBackofficeHttpSpecificationTests.cs` (plus `Operations/ServerMeshCoordinatorTests`, `Operations/MeshScalingTests`, `Integration/WorldMeshJourneyTests`, `Integration/MeshBackofficeEndpointTests`).

## 2. Data Model
| Type | Fields | Notes |
| --- | --- | --- |
| `ServerMeshNode` | `NodeId, InstanceName, HttpEndpoint, WorldTcpHost, WorldTcpPort, Status, StartedAtUtc, LastHeartbeatUtc, ActivePilots, LeasedMapIds[], ProcessCpuPercent, WorkingSetBytes, Simulated` | `LeasedMapIds` is always recomputed by the coordinator; values sent by a node are ignored. |
| `ServerMeshNodeStatus` | `Active`, `Draining`, `Offline` | Serialised as names (`"Draining"`); legacy numeric values are still read. |
| `ServerMapLease` | `MapId, NodeId, Epoch, ExpiresAtUtc` | `Epoch` is a per-map fencing counter. |
| `ServerMeshSnapshot` | `Nodes[], Leases[], DesiredReplicas, MaxMapsPerNode` | Persisted as JSON under cache key `mesh:v1:registry` (1 day absolute expiry). |

The partitioned map catalogue is `ServerMeshCoordinator.GameMapIds = 1..16` (four faction blocks of four maps).

## 3. Concurrency
Every mutation (`HeartbeatAsync`, `SetDrainingAsync`, `SetActiveAsync`, `RebalanceAsync`, `ScaleAsync`) runs inside `IServerMeshMutex`. In production `RedisServerMeshMutex` uses `SET mesh:v1:coordinator-lock <token> NX EX 30`, polls every 25 ms, and releases with a compare-and-delete Lua script so a replica never deletes another replica's lock. `GetSnapshotAsync` is lock-free and read-only.

## 4. Node Registration & Heartbeats
- `ServerMeshHostedService` validates `ClusterOptions` before any work. The defaults are one desired worker, four advisory maps per node, a 10-second heartbeat interval, a 30-second lease TTL, and node id `{MachineName}-{ProcessId}`. Desired workers must be `1..16`, map capacity `1..256`, heartbeat interval `1..300` seconds, and TTL must be strictly greater than twice the heartbeat interval and at most 3,600 seconds. `NodeId` may be null, empty or whitespace; a nonempty value must be at most 128 UTF-16 characters and contain no control character. Invalid options throw `InvalidOperationException` before registering the node.
- The hosted service resolves a blank node id to `{MachineName}-{ProcessId}` once and records a single startup timestamp. If the world listener is enabled, it waits in 20 ms steps until `ActualPort` is nonzero, or cancellation arrives. A disabled listener does not wait. The first heartbeat occurs immediately after listener readiness, with that startup timestamp, the current UTC heartbeat timestamp, current active-pilot count, advertised world host and actual port, process CPU percentage and working-set bytes. Environment variables supply the instance name (`OTEL_SERVICE_NAME`, default `api-game`) and HTTP endpoint (`ASPNETCORE_URLS`, default `http://api-game`). The first CPU percentage is zero when the elapsed time is not positive; subsequent samples are clamped to `0..100`.
- After each successful `HeartbeatAsync`, the service refreshes local pilot presence, logs the leased map ids from that heartbeat snapshot, and waits `Cluster:HeartbeatIntervalSeconds` before repeating. It does not acquire, rebalance or scale leases itself. Cancellation ends the loop. On every exit after entering the guarded loop, including a heartbeat or presence failure, `finally` attempts `SetDrainingAsync` for this node with its own three-second cancellation token; drain failure is logged and swallowed. An exception other than stopping-token cancellation still escapes the service after this drain attempt.
- A heartbeat upserts the node, stamps `LastHeartbeatUtc = now`, and sets `Status = Active` **unless** the stored node is `Draining`, which is sticky until `SetActiveAsync`.
- TTL = runtime variable `mesh.heartbeat_ttl_seconds` when the registry is wired, otherwise `Cluster:LeaseTtlSeconds` (default 30 s). `ClusterOptions.Validate` requires `TTL > 2 × heartbeat interval` and `TTL ≤ 3600`.
- `SetDrainingAsync`/`SetActiveAsync` on an unknown node id are no-ops; `SetActiveAsync` only affects `Draining` nodes.

`ServerMeshSpecificationTests` executes the configuration boundary matrix, including inclusive map capacity and heartbeat bounds, strict TTL separation and ceiling, default values, and null/blank/control-character node ids. `ApiServiceVerticalSliceTests.MeshHeartbeatHost_WaitsForListenerThenPublishesPresenceAndDrainsOnExit` pins the hosted-service registration, ordering, cancellation and mutation boundary. The hosted loop is not driven with a live world listener by these tests.

## 5. Stale Node Eviction
A node whose `now − LastHeartbeatUtc > TTL` is stale.
- The boundary is inclusive: at exactly `LastHeartbeatUtc + TTL`, the node remains active; one clock tick later it is stale. A snapshot read projects stale nodes as offline and clears only their reported `LeasedMapIds`. It preserves the stored leases, owners and epochs until a mutation runs.
- **On read** (`GetSnapshotAsync`): reported as `Offline` with `LeasedMapIds = []`; stored state is not changed.
- **On the next write**: marked `Offline` in the stored registry and excluded from the active set, so its maps fail over (section 6). Offline nodes remain listed for operators; they rejoin by heartbeating again.
- A heartbeat from an offline node can restore it to `Active`. Rebalancing then moves some maps back without resetting their per-map epochs: every ownership change increments the prior epoch once, while maps retained by their current owner keep their epoch. All resulting lease expiries renew to `now + TTL`.
- If no active node remains, every existing lease is kept but expired (`ExpiresAtUtc = now`) and no node lists any map.

## 6. Map Ownership Leasing & Deterministic Partitioning
On every write, active nodes (status `Active` and heartbeat within TTL) are ordered by `NodeId` (ordinal). For each map in `1..16` in ascending order:
1. The target is the active node with the fewest leases, ties broken by ordinal `NodeId`.
2. A lease **moves** to the target if its current owner is not active, or the owner holds more than `target + 1` leases. A moved lease gets `Epoch = oldEpoch + 1` (first assignment: `1`) and `ExpiresAtUtc = now + TTL`.
3. Otherwise the owner keeps the lease, with the same epoch and renewed expiry.

Guarantees (tested for 1, 2, 3, 4, 5, 8, 16 nodes): every map has exactly one lease; per-node loads differ by at most 1; healthy owners are sticky (renewal keeps owner and epoch); load distribution is independent of registration order. `MaxMapsPerNode` is advisory metadata reported in the snapshot; it does not cap assignment.

## 7. Cross-Node Portal Jump Handoff
The handoff "token" is the route triple `(OwnerNodeId, Epoch, "host:port")` derived by `WorldSessionHandler.CurrentRouteAsync` from the unexpired lease for the destination map and its `Active` owner.
- Wire form: `MapChangeCommand` (19) = `i32 mapId, str targetNodeId, str targetTcpHostPort` (see SPEC-06). An empty node id/endpoint means "no current owner; reconnect via the default listener".
- On `JumpRequest`, if the destination owner differs from the local node, the source sends `MapChangeCommand`, marks the session `handedOff`, and releases the TCP connection with `DetachForHandoff` (not `LeaveAsync`), so shared pilot state already at the arrival point is not removed. A local owner instead sends the map change followed by a new entry burst and records the admitted epoch.
- Epoch fencing: when an idle poll observes a different owner or epoch than the admitted one, or `WorldLeaseLostException` is raised, the server sends `MapChangeCommand` for the current owner and hands the pilot off. Draining or failing a node therefore always raises the epoch of each moved map.
- Simulated owners on the same physical listener resolve to the local node id so development replicas don't redirect to themselves.

## 8. Scaling & Aspire `MeshScalingOptions`
- `MeshScalingOptions.DesiredReplicas(configuration)` reads `Emulator:Mesh:ApiReplicas`, else `Cluster:DesiredWorkers`, else `1`. Values that don't parse as an integer or fall outside **1..16** throw `InvalidOperationException("Api replicas must be between 1 and 16.")` and stop the AppHost.
- The AppHost applies `.WithReplicas(n)` to `api-game` and passes `Cluster__DesiredWorkers = n`. With `n = 1` the world listener uses the fixed port `60954`; with `n > 1` it uses `WorldServer__Port=0` and `WorldServer__DynamicTcpPort=true`, so each replica advertises its own TCP endpoint in its heartbeat.
- `ClusterOptions.Validate` enforces the same bounds: `DesiredWorkers 1..16`, `MaxMapsPerNode 1..256`, `HeartbeatIntervalSeconds 1..300`, `NodeId` ≤ 128 chars with no control characters.
- Runtime scaling (`ScaleAsync(desired, maxMaps)`, admin `mesh scale`): throws `ArgumentOutOfRangeException` for `desired ∉ 1..16` or `maxMaps ∉ 1..256`. It removes earlier simulated nodes, then adds `local-sim-1..k` (`k = desired − activeRealNodes`, `InstanceName = "api-game (simulated)"`, `Simulated = true`) that share the first active real node's TCP endpoint, then rebalances. Simulations are visible, not separate processes or failure domains. Scaling down to 1 returns every map to the real node(s). With no active real node, no simulations are created.
- Runtime variables (`ServerMesh` category): `mesh.desired_replicas`, `mesh.max_maps_per_node`, `mesh.heartbeat_ttl_seconds`. When wired they override the stored snapshot values in `GetSnapshotAsync`.

## 9. Surfaces
- Admin commands: `mesh status`, `mesh scale <n> [maxMaps]`, `mesh drain|activate <node>`, `mesh rebalance` (SPEC-08).
- Backoffice/CMS: `BackofficeMeshEndpoints` and `ServerMeshStatusEndpoint` expose the snapshot (SPEC-09).

### 9.1 Backoffice Mesh & World HTTP Contract
All routes are version 1, tagged `Backoffice`, and authorised by policy only (the route table and permissions are pinned in SPEC-09). Reads require `ops.read`; mutations require `ops.write`. Every **successful** reply sets `Cache-Control: no-store`; a 400, 404 or 409 reply does not set it. Validation and conflict errors are `{ "error": "<message>" }`, not the shared `{code, message}` catalog shape.

| Route | Check order | Outcome |
| :--- | :--- | :--- |
| `GET backoffice/operations/mesh` | none | 200 with the unmodified `GetSnapshotAsync` snapshot; node status is serialised by name. |
| `PUT backoffice/mesh/scale` | `DesiredReplicas` in `1..16` **and** `MaxMapsPerNode` in `1..256` (both inclusive) | Either out of range → 400 `DesiredReplicas must be 1..16 and MaxMapsPerNode 1..256.`, and the registry is not called. Otherwise `LocalWorkerSpawner` forwards both values unchanged to `ScaleAsync` → 200 with its snapshot. |
| `POST backoffice/mesh/nodes/{nodeId}/drain` | one snapshot read; ordinal (case-sensitive) node id match | Unknown → 404. The node is not `Active`, **or** fewer than two `Active` nodes exist (counting the target), **or** it is a real (non-`Simulated`) node with `ActivePilots > 0` → 409 `Node cannot drain while pilots are connected or no healthy target exists.` Simulated nodes may drain with pilots. Otherwise `SetDrainingAsync(nodeId)` → 200. |
| `POST backoffice/mesh/nodes/{nodeId}/activate` | one snapshot read; ordinal node id match | Unknown → 404. Status other than `Draining` (including `Offline`) → 409 `Only a live draining node can be activated.` Otherwise `SetActiveAsync(nodeId)` → 200. The pilot count is ignored. |
| `POST backoffice/mesh/rebalance` | none | Always `RebalanceAsync` → 200. |
| `GET backoffice/mesh/topology` | none | 200 `MeshTopologyResponse`. `DesiredWorkers` and `DesiredReplicas` both echo the snapshot's `DesiredReplicas`. `MaxReplicas` is fixed at 16. `ActiveNodeCount` counts only `Active` nodes. `TotalOnlinePilots` counts distinct user ids across all game maps. |
| `GET backoffice/world/overview` | none | 200 with one entry per `ServerMeshCoordinator.GameMapIds` map: owner from the first lease for that map (or `null`) and the online pilot count; plus the deduplicated pilot list. |
| `POST backoffice/world/pilots/{userId}/teleport` | `userId > 0`, `MapId` in `1..16`, `X` in `0..21000`, `Y` in `0..13100` (all inclusive) | Any violation → 400 `Invalid pilot, map or coordinates.`, without touching sessions. A pilot that is not connected → 404. Otherwise 200 with the teleported `WorldPilot`. |

Both read endpoints take one registry snapshot, then query shared presence for every game map. Presence entries at least two minutes old are excluded. They deduplicate by numeric `UserId`, keeping the first entry in ascending map order; a duplicate on a later map does not add to the total or that map's count. The topology echoes snapshot nodes and leases without filtering status or expiry, counts only `Active` nodes, and reports the distinct pilot count. It echoes `DesiredReplicas` as both `DesiredWorkers` and `DesiredReplicas`, reports `MaxReplicas = 16`, and returns the fixed dashboard link `https://localhost:17081`. World overview emits all 16 maps in catalog order, counts the deduplicated pilots by their retained `MapId`, and takes the first matching lease's `NodeId` as owner. It does not check that lease's expiry or node status; missing leases yield `null` owner. Both respond 200 with `no-store`.

Preconditions are checked against a single snapshot read and are not re-checked under the mesh mutex, so a concurrent heartbeat can still change the node between the check and the mutation (§3 governs only the mutation itself).

## 10. Provisional Map XML Resource

`GET /xml/maps.php` and `GET /xml/maps.xml` are anonymous aliases tagged for `gbl3.darkorbit.com`. Both delegate to `LocalMapsXml.WriteResponseAsync` with the configured world listener and a read-only mesh snapshot; neither uses a game-auth session. Their shape is provisional because no official response body has been captured.

- The XML starts with `<?xml version="1.0" encoding="utf-8"?>`, then a `maps` root with one empty `map` per distinct starter map in `CompanyCatalog`, ordered by numeric ID. Each map has exactly `ID` and `GameServerIP` attributes.
- With the world listener enabled, the fallback address is `WorldServerOptions.Host:Port`. With it disabled, the fallback is bare `127.0.0.1`. Options are validated before body construction.
- For each starter map independently, an unexpired lease (`ExpiresAtUtc > current UTC`) overrides the fallback only if its owner node exists, is `Active`, and has a TCP port in `1..65535`. The advertised address is that node's `WorldTcpHost:WorldTcpPort`. An expired lease, missing owner, draining or offline node, or invalid port uses the fallback. The writer only reads the snapshot; it does not renew leases.
- The response is HTTP 200, `application/xml; charset=utf-8`, `Cache-Control: no-cache`, `X-Content-Type-Options: nosniff`, and an exact UTF-8 byte length. It sets no cookie. Query strings and request cookies do not affect the body. The UTF-8 bytes have no BOM.

`ClientTesting/MapsResourceSpecificationTests.cs` covers listener selection, per-map override, unavailable-owner boundaries and configured response bytes. `ApiServiceVerticalSliceTests.MapResourceAliases_UseOneMeshAwareWriterWithoutSessionState` pins route symmetry and snapshot-only ownership selection. `LocalMapsXmlResponseTests` covers catalogue and baseline XML shape. No official map XML response has been captured.

## 11. Known Gaps
- Development replicas are simulations sharing one listener; real multi-process failover is covered only by Docker-backed integration tests.
- `MaxMapsPerNode` is not enforced during partitioning.
- The world TCP handoff packet layout is provisional (SPEC-06).
- Backoffice mesh drain and activate preconditions are checked outside the mesh mutex (§9.1). Topology hard-codes `MaxReplicas = 16` and the Aspire dashboard link `https://localhost:17081`. Mesh errors use `{error}` rather than the shared error catalog.
