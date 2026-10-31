# ThDarkOrbitEmulator

A local server emulator for the DarkOrbit **Unity** Windows client. It uses .NET 10 and Aspire 13. It provides portal authentication and the Unity login/cockpit handoff, out-of-game menus and economy, a world server and HTTP spacemap, a scalable server mesh, and an Angular CMS/Backoffice. Everything runs on your own machine. The UI uses placeholder branding.

## Solution layout

| Project | Role |
| --- | --- |
| `ThDarkOrbitEmulator.AppHost` | Aspire orchestration: `api-game`, `gateway-players`, `cms-players`, Postgres, Redis, Keycloak, OTel, Prometheus, Grafana; client-testing dashboard commands |
| `ThDarkOrbitEmulator.ApiService` | Vertical-slice API (FastEndpoints + minimal API): portal, `/unityApi/*`, `/indexInternal.es`, economy, world/mesh, backoffice |
| `ThDarkOrbitEmulator.Gateway` | YARP gateway that clients connect to (`https://localhost:60952`, `https://localhost:443`, `http://localhost:60953`) |
| `ThDarkOrbitEmulator.Cms` | Angular player CMS and Backoffice |
| `ThDarkOrbitEmulator.Identity.*` | Accounts, companies, Keycloak integration, SQL migrations (`Migrations/Sql/000`–`031`) |
| `ThDarkOrbitEmulator.Cluster.*` | Worker spawning for the mesh |
| `ThDarkOrbitEmulator.ServiceDefaults` | OpenTelemetry, health, RBAC claims transformation |
| `ThDarkOrbitEmulator.Tests` | NUnit specification, architecture and integration tests |

## Quick start: play with the Unity client (no proxy)

The supported setup patches your own copy of the DarkOrbit Unity client so it talks to `https://localhost:60952` directly. You don't need mitmproxy or a system proxy. The repo does **not** ship any game files (no patched `global-metadata.dat`). The patcher edits your local install in place and keeps `.orig.bak` backups, so it works for every supported client build.

1. **Prerequisites:** Windows, .NET 10 SDK, Docker Desktop (running), Node.js and Python 3 on `PATH` (the patcher uses only the standard library). Trust the ASP.NET dev certificate once:

   ```powershell
   dotnet dev-certs https --trust
   ```

2. **Check the client build.** Supported metadata builds are listed in `KNOWN_ORIG_METADATA_SHA256` in `scripts/client-testing/patch_il2cpp_uris.py` (1.1.92 and 1.1.93). `GameAssembly.dll` and `DarkOrbit_Data\il2cpp_data\Metadata\global-metadata.dat` must come from the **same** build. A mismatch shows up as a black screen and a `FormatException` in `Init.Awake`.

   ```powershell
   $client = "C:\Games\DarkOrbit Unity"     # folder that contains DarkOrbit.exe
   .\scripts\client-testing\Patch-ClientUris.ps1 -Action Status -ClientRoot $client
   ```

3. **Patch the client** (close the game first):

   ```powershell
   .\scripts\client-testing\Patch-ClientUris.ps1 -Action Apply -ClientRoot $client
   ```

   Undo it at any time with `-Action Reset -ClientRoot $client`.

4. **Start the server:**

   ```powershell
   dotnet run --project ThDarkOrbitEmulator.AppHost --launch-profile https
   ```

   The first run pulls and creates the containers (Postgres, Redis, Keycloak, observability), which takes a few minutes. Containers use `ContainerLifetime.Persistent`, so they keep running after the AppHost stops and later starts take about 30 seconds. Stop them with `docker stop` when you're done. The server is ready when the world port `127.0.0.1:60954` is listening and `https://localhost:60952/health` returns 200. The Aspire dashboard is at `https://localhost:17081`.

5. **Launch the game** and log in with the seeded account `owner` / `owner`, or register one in the CMS at `http://localhost:4300`:

   ```powershell
   .\scripts\client-testing\Start-ClientConsole.ps1 -ClientRoot $client    # live Unity log in the console
   ```

   You can also start `DarkOrbit.exe` directly. Your 2D/3D choice and launch-screen language are stored on the server per account.

**Troubleshooting**

| Symptom | Fix |
| --- | --- |
| `api-game`/gateway fails with `SocketException 10013` | Windows reserved the port range (`netsh interface ipv4 show excludedportrange protocol=tcp`). From an elevated shell run `net stop winnat; net start winnat`. |
| Gateway can't bind 443 | Free port 443 (IIS, Skype, another web server). The client needs it for a hard-coded `https://localhost/` URL. |
| White or blank WebView, TLS errors in the Unity log | Re-run `dotnet dev-certs https --trust`. |
| `Patch-ClientUris.ps1` refuses to patch | The metadata hash is unknown (unsupported build) or the game is still running. |
| Unknown request returns 401 | The gateway answers unmapped routes with 401; check `api-game` logs in the dashboard for `endpoint=unmatched`. |

Older notes on mitmproxy Redirect mode (`Start-CaptureSession.ps1 -Mode Redirect`) are in `AGENTS.md`. That mode isn't needed for local play.

## Run

Prerequisites: .NET 10 SDK, the Aspire CLI or Visual Studio 2026, Docker Desktop, and Node.js.

```powershell
aspire run                       # or: dotnet run --project ThDarkOrbitEmulator.AppHost --launch-profile https
dotnet test ThDarkOrbitEmulator.slnx
```

The AppHost seeds a local platform owner (`owner`, Aspire parameter `owner-password`) with realm role `emulator-admin`. Dashboard links: Keycloak `:8081`, Prometheus `:19090`, Grafana `:13000`, plus pgAdmin and RedisInsight. See `docs/operations-dashboard.md` and `docs/rbac.md`.

## Unity client: dual HTTPS endpoints

The native client and its Vuplex WebView reach the emulator on **two** HTTPS origins. `gateway-players` serves both with the same routes:

- `https://localhost:60952`: the patched portal/core URLs and the WebView login (`OpenWebLogin` → `/indexInternal.es?action=internalStart` → `internalWebGL` dosid).
- `https://localhost:443`: IL2CPP literal `https://localhost/` has an 18-byte slot that cannot hold `:60952`, so the AppHost binds endpoint `unity-https-default` on 443. Port 443 must be free and can need an elevated shell.

The cockpit handoff then calls `GetLinkRequest` (`/unityApi/links.php`), `GetPost` (`/unityApi/login.php`), `GetNewsRequest` (`/unityApi/news.php`, `/flashAPI/brNews.php`) and `GetHangerPost`. Requests that arrive without a port advertise the configured WebView origin (`ClientGateway__HttpsOrigin`, `:60952`). Cockpit pages therefore open on the listener that holds the login cookie. Details: `docs/specs/02-client-uri-patching-and-connectivity.md`, `docs/cockpit-catalog.md`.

## Unity client: URI and console patching

Close the game first, then run:

```powershell
.\scripts\client-testing\Patch-ClientUris.ps1 -Action Status    # read-only (-AsJson available)
.\scripts\client-testing\Patch-ClientUris.ps1 -Action Apply     # point the client at https://localhost:60952
.\scripts\client-testing\Patch-ClientUris.ps1 -Action Reset     # restore all .orig.bak backups
```

`Apply` patches `global-metadata.dat`, `config.txt` and `update.ini`. It also switches `DarkOrbit.exe` to the console subsystem, strips `nolog=` from `DarkOrbit_Data\boot.config`, and writes `Start-DarkOrbitConsole.cmd` into the client folder. Launch the game with a live Unity log:

```powershell
.\scripts\client-testing\Start-ClientConsole.ps1    # or double-click Start-DarkOrbitConsole.cmd
```

It tails `unity-console.log` until the game exits. `Ctrl+C` kills the game process tree and exits cleanly. Network capture (Live/Redirect, mitmproxy, CDP auto-login) is covered in `AGENTS.md` and `docs/client-testing.md`.

## Cockpit / in-game WebView support

| Control | Route | Status |
| --- | --- | --- |
| News | `internalCommandCenter` (styled Command Center) | Served |
| Profile | `internalPilotSheet` | Served; click not re-tested in client |
| Rank | `internalHallofFame` | Served; click not re-tested in client |
| Hangar | `internalDock`, `internalDockEquipment`, native hangar list/detail/Manage | Works in client (list, detail, Manage, equipment window) |
| Shop | `internalShop`, `internalPayment` | Served |
| Open World / Spacemap | `internalMapRevolution` + `/spacemap/world/*`, world TCP `127.0.0.1:60954` | Native world entry works: movement, HUD, portals, jump (`J`), home station on x-1/x-8, 2D/3D switch |

The full evidence and limits are in `docs/cockpit-catalog.md`. Menu coverage is in `docs/menu-coverage.md`.

## Backoffice (CMS) and admin API

Open the CMS (`cms-players` in the Aspire dashboard) and go to `/backoffice`. Server-side RBAC is enforced (`docs/rbac.md`, `docs/specs/09-cms-backoffice-and-placeholder-ui.md`).

| Area | CMS route | API |
| --- | --- | --- |
| Languages | `/backoffice/cockpit` | `GET/PUT /api/v1/backoffice/cockpit/languages[/{code}]`; public `/unityApi/languages.php` |
| Server lists / instances | `/backoffice/cockpit` | `GET/PUT /api/v1/backoffice/cockpit/servers[/{code}]`; public `/unityApi/servers.php`, portal `internalServers` |
| Hangar ships (catalog and per-account slots) | `/backoffice/cockpit` | `GET/PUT /api/v1/backoffice/cockpit/ships[/{shipId}]`, `.../accounts/{accountId}/hangars[/{slot}]` |
| Runtime variables and admin commands | `/backoffice/runtime-vars` | `/api/v1/backoffice/runtime-vars/*`, `POST /api/v1/backoffice/admin/command` |
| Mesh / world | `/backoffice/mesh` | `/api/v1/backoffice/mesh/*`, `/api/v1/backoffice/world/*` |
| Economy | `/backoffice/economy` | `/api/v1/economy/*` |
| Identity, Keycloak, operations | `/backoffice/identity`, `/keycloak`, `/operations`, `/overview` | `/api/v1/backoffice/{identity,keycloak,operations}/*` |

Cockpit catalog data lives in Postgres (migration `030_cockpit_catalog`) and requires `emulator-admin`. The first login creates two starter hangar ships per account (Pusat Plus, Phoenix), and admin edits persist after that.

### Server Admin CLI

```powershell
.\scripts\admin\Start-AdminCli.ps1                                       # interactive REPL against https://localhost:60952
.\scripts\admin\Start-AdminCli.ps1 -AiQuery "double everyone's speed"    # natural language -> proposed commands
.\scripts\admin\Invoke-ServerAdminCommand.ps1 "/setvar world.ship.pusat_plus_base_speed 400 tuning"
```

Commands include `/vars`, `/getvar`, `/setvar`, `/resetvar`, `/varhistory`, `/rollbackvar`, `/grant`, `/spawn`, `/tp`, `/heal`, `/announce`, `/status`, `/mesh`, `/ai`, `/help` and `/exit`, with Tab completion. `/ai` uses a local Ollama or LM Studio model if one is running and a built-in resolver otherwise. Mutating proposals ask for confirmation unless you pass `-AutoExecute`. Contract: `docs/specs/08-admin-commands-and-runtime-variables.md`.

## Documentation

- `docs/specs/README.md`: specification index (SPEC-01…09) and test traceability matrix
- `docs/client-testing.md`, `docs/client-endpoint-map.md`, `docs/client-hardcoded-hosts.md`, `docs/rea-for-client-reversing.md`: client patching, capture and reversing
- `docs/cockpit-catalog.md`, `docs/menu-coverage.md`, `docs/client-verification-report.md`: cockpit and menu status
- `docs/world-protocol.md`, `docs/server-mesh-scaling.md`, `docs/economy.md` (`economy.openapi.yaml`): game systems
- `docs/operations-dashboard.md`, `docs/aspire-dashboard-naming.md`, `docs/rbac.md`: operations and security

## License

See `LICENSE`.
