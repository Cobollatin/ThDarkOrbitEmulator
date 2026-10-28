# Repository index

- `ThDarkOrbitEmulator.ApiService/Features/`: HTTP endpoints and world TCP listener; Unity login and resources are implemented here.
- `ThDarkOrbitEmulator.Identity.*`: account and cockpit catalog persistence; SQL migrations in `Identity.Infrastructure/Migrations/Sql/`.
- `ThDarkOrbitEmulator.Tests/ClientTesting/`: focused Unity contract and world protocol tests; `Integration/` holds database and HTTP integration tests.
- `docs/cockpit-catalog.md`: cockpit evidence, unresolved native contracts, and public catalog routes.
- `docs/client-endpoint-map.md` and `docs/world-protocol.md`: observed client endpoints and provisional local world protocol.
- `_reversing/`: local analysis artifacts and captured sessions; `scripts/client-testing/` contains capture helpers.
- `_reversing/Analysis/trace_il2cpp.py`: bounded native disassembly using the ignored `il2cpp-dump/` method map; findings are recorded in `docs/cockpit-catalog.md`.
- `ThDarkOrbitEmulator.AppHost/`: Aspire wiring, including HTTPS and world listener ports.

Read the relevant feature directory and documentation before broader searches. Update this index when adding or moving modules.
