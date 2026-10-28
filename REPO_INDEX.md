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
- `.ralph/memory-index.json`: focused world entry, transport, simulation, and test paths; records the currently missing requested spec files.
- `docs/specs/03-hangar-inventory-and-shop.md`, `07-economy-progression-and-quests.md`, `08-clans-diplomacy-and-chat.md`: topical Task 3 audits supplement the existing numbered master specs. Shop, Dock, Skylab, and Galaxy Gates live in `ApiService/Features/IndexInternal/InternalStart/` with durable mutations in `ApiService/Features/Economy/EconomyStore.cs`; Cockpit routes live in `ApiService/Features/UnityApi/Login/CockpitCatalogEndpoints.cs`.

Read the relevant feature directory and documentation before broader searches. Update this index when adding or moving modules.
