# Repository index

- `ThDarkOrbitEmulator.ApiService/Features/`: HTTP endpoints and world TCP listener; Unity login and resources are implemented here.
- `ThDarkOrbitEmulator.Identity.*`: account and cockpit catalog persistence; SQL migrations in `Identity.Infrastructure/Migrations/Sql/`.
- `ThDarkOrbitEmulator.Tests/ClientTesting/`: focused Unity contract and world protocol tests; `Integration/` holds database and HTTP integration tests.
- `docs/cockpit-catalog.md`: cockpit evidence, unresolved native contracts, and public catalog routes.
- `docs/client-endpoint-map.md` and `docs/world-protocol.md`: observed client endpoints and provisional local world protocol.
- `_reversing/`: local analysis artifacts and captured sessions; `scripts/client-testing/` contains capture helpers.
- `_reversing/Analysis/trace_il2cpp.py`: bounded native disassembly using the ignored `il2cpp-dump/` method map; findings are recorded in `docs/cockpit-catalog.md`.
- `ThDarkOrbitEmulator.AppHost/`: Aspire wiring, including HTTPS and world listener ports.
- `ThDarkOrbitEmulator.Tests/Integration/WorldAssignedHangarPostgresTests.cs`: PostgreSQL cockpit assignment to world stat entry regression.
- `ThDarkOrbitEmulator.Tests/Integration/WorldRocketAndNpcAggroTests.cs`: shared-cache rocket cooldown, lethal reward, NPC pursuit and pilot damage integration tests.
- `.ralph/memory-index.json`: focused world entry, transport, simulation, and test paths; records the currently missing requested spec files.
- `docs/specs/03-hangar-inventory-and-shop.md`, `07-economy-progression-and-quests.md`, `08-clans-diplomacy-and-chat.md`: topical Task 3 audits supplement the existing numbered master specs. Shop, Dock, Skylab, and Galaxy Gates live in `ApiService/Features/IndexInternal/InternalStart/` with durable mutations in `ApiService/Features/Economy/EconomyStore.cs`; Cockpit routes live in `ApiService/Features/UnityApi/Login/CockpitCatalogEndpoints.cs`.
- `ApiService/Features/Economy/EconomyProgression.cs`: Task 6 provisional timed collector upgrades, refinery batches, and gate wave transitions; `IndexInternal/InternalStart/LocalProgressionAction.cs` and `LocalProgressionPanel.cs` expose signed menu forms. Schema migration: `Identity.Infrastructure/Migrations/Sql/031_skylab_jobs_and_gate_waves.*.pgsql`; integration coverage lives in the Skylab and Galaxy Gate PostgreSQL fixtures.
- `ApiService/Features/Economy/EconomyClanOperations.cs` and `IndexInternal/InternalStart/LocalClanOperations.cs`: Task 7 leader-scoped diplomacy and station modules; migration `032_clan_diplomacy_stations.*.pgsql`; PostgreSQL coverage in `LocalClanPostgresTests.cs`.
- `IndexInternal/InternalStart/LocalHangarEquipmentSelection.cs` and `EconomyStore.cs`: Task 7 atomic Dock transfer between ships, covered by `EconomyPostgresTests.cs`.

Read the relevant feature directory and documentation before broader searches. Update this index when adding or moving modules.
