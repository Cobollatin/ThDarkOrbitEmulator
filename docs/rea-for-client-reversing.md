# REA for ThDarkOrbitEmulator

REA ([morluto/rea](https://github.com/morluto/rea), npm `rea-agents@6.7.0`) is wired for **Codex**, **Cursor**, and **GitHub Copilot CLI** on this machine. It does **not** replace mitmproxy live redirect; it helps find hardcoded hosts in client binaries and turn captured traffic into typed shapes for the emulator.

## Agent registration (already done)

| Client | Config written |
| --- | --- |
| Codex | `C:\Users\Cobollatin\.codex\config.toml` → `[mcp_servers.rea]` |
| Cursor | `C:\Users\Cobollatin\.cursor\mcp.json` → `mcpServers.rea` |
| Copilot CLI | `C:\Users\Cobollatin\.copilot\mcp-config.json` → `mcpServers.rea` |
| Skill | `C:\Users\Cobollatin\.agents\skills\reverse-engineer-anything` |

Restart Codex / Cursor / Copilot CLI after this change so they load the MCP server.

Command used by every client:

```text
cmd.exe /d /c npx -y rea-agents@6.7.0 mcp
```

Refresh later:

```powershell
npx -y rea-agents@latest setup --client codex --client cursor --client copilot_cli --yes
```

## Keep mitmproxy for live redirect

Until every host the Unity client dials is patched (or DNS/hosts redirected), keep:

- `_reversing/Mitmproxy/launch_redirect.py`
- Aspire `client-test-start-mitm` / docs in `docs/client-testing.md`
- `scripts/Find-HardcodedUrls.ps1` and `scripts/Replace-HardcodedUrls.ps1`

Known hosts already in `scripts/client-testing/url-replacements.template.json`:

- `https://gbl3.darkorbit.com`
- `https://www.darkorbit.com`
- `https://darkorbit-22.bpsecure.com`
- `https://sas.bpsecure.com`

## Primary targets on disk

| Path | Role |
| --- | --- |
| `D:\Games\DarkOrbit Unity\DarkOrbit.exe` | Unity player stub |
| `D:\Games\DarkOrbit Unity\GameAssembly.dll` | IL2CPP native game code (URL/string hunting + Ghidra) |
| `D:\Games\DarkOrbit Unity\DarkOrbit_Data\il2cpp_data\Metadata\global-metadata.dat` | IL2CPP type/method names |
| `D:\Games\DarkOrbit Unity\DarkorbitLauncher.dll` | Managed .NET launcher (REA `inspect-managed-*` works without Ghidra) |
| `_reversing/Requests/flow` | mitmproxy dump (REA `inspect-web-network-capture … mitmproxy`) |
| `_reversing/Requests/*.jsonl` | Custom extracted flows for your Python scripts |
| `_reversing/Ghidra/DarkOrbit.*` | Existing Ghidra project |

## Goal A — find where the URL lives (so proxy can shrink)

1. Run the binary string scan (already in-repo):

```powershell
pwsh -File scripts/Find-HardcodedUrls.ps1 -TargetPath "D:\Games\DarkOrbit Unity"
```

2. In **Codex**, open `D:\Local_Source\Projects\ThDarkOrbitEmulator` and ask REA to:

   - Inspect `GameAssembly.dll` / `DarkOrbit.exe` for string/xref evidence of the hosts above (and any new hosts from mitm logs).
   - Correlate hits with `global-metadata.dat` type/method names when available.
   - Propose exact byte-safe replacements for `Replace-HardcodedUrls.ps1` (same ASCII/UTF-16 length).

3. Optional native depth: point REA at Ghidra by setting `GHIDRA_INSTALL_DIR` to a **Ghidra 12.1.x** extract (this machine currently has only `%LOCALAPPDATA%\ghidra` caches, not a verified install path). Then re-run:

```powershell
npx -y rea-agents@latest setup --client codex --client cursor --yes
npx -y rea-agents@latest doctor --provider ghidra --json
```

Windows Ghidra support in REA is still experimental (see REA `docs/windows-ghidra-p0.md`).

## Goal B — map request/response bodies to emulator types

1. Capture with mitmproxy (`_reversing/Requests/flow` or a fresh dump under `client-testing/`).

2. Ask Codex + REA:

```text
Inspect _reversing/Requests/flow as mitmproxy. List DarkOrbit / bpsecure / gbl3 API calls with method, path, content-type, and request/response shape. Propose C# record/DTO names under ThDarkOrbitEmulator.Gateway or ApiService that match the payloads (evidence from the capture only).
```

CLI equivalent:

```powershell
npx -y rea-agents@latest inspect-web-network-capture "D:\Local_Source\Projects\ThDarkOrbitEmulator\_reversing\Requests\flow" mitmproxy --json
```

3. Keep using your `*.jsonl` + `classify_flows.py` for bulk classification; use REA when you want agent-driven typing into the .NET solution.

## Verified smoke tests (2026-10-21, America/Lima)

- Codex / Cursor / Copilot CLI registrations: **aligned** (`rea doctor --client codex`).
- `inspect-managed-artifact` on `DarkorbitLauncher.dll`: **works** (modern .NET PE/CLI).
- `_reversing/Requests/flow`: valid mitmproxy dump (**77** HTTP flows).
- `global-metadata.dat` via `inspect-artifact`: not treated as a generic binary target on this REA version; use Codex REA tools / Ghidra / your existing scripts for IL2CPP metadata.
- Ghidra provider: **not configured** (`GHIDRA_INSTALL_DIR` unset).

## Safety

Analyze only client builds and captures you are authorized to reverse for this emulator. Do not commit `_reversing/Data/account.txt` or live session cookies into git.

## Windows note for captures

REA's native `mitmproxy` capture reader is primarily verified on Linux. If `inspect-web-network-capture … mitmproxy` fails on Windows, export HAR from mitmweb/mitmdump and use:

```powershell
npx -y rea-agents@latest inspect-web-network-capture "D:\path\to\capture.har" har --json
```

Your `_reversing/Requests/*.jsonl` extractors remain the reliable Windows path for bulk flow work.

### Export HAR on Windows

```powershell
.\.venv\Scripts\python.exe scripts\client-testing\export_flow_har.py
npx -y rea-agents@latest inspect-web-network-capture "_reversing\Requests\flow.har" har --json
```


## Automated capture

`scripts/client-testing/Start-CaptureSession.ps1` runs the whole interception in one command: checks the mitmproxy CA, starts
`mitmdump` (local capture mode for `DarkOrbit.exe` / launcher / `Vuplex WebView.vuplex`, no system proxy change), launches the client,
waits for client exit or `-DurationSeconds`, stops mitmdump cleanly, exports `flow.har` and prints a JSON summary (paths, entry count, hosts).
Output: `_reversing/Requests/sessions/<timestamp>-<name>/`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Live -SessionName login -WaitForExit
```

- `-Mode Redirect` loads `_reversing/Mitmproxy/launch_redirect.py` (redirect to local Aspire); `-NoLaunch` for smoke tests.
- `-AutoLogin` logs in over CDP (`automate-login.mjs`, selectors in `automate-login.config.json`, credentials from env/Credential Manager).
  The in-client UI is Vuplex 3D WebView (CEF, Chrome 100); remote debugging is enabled by a temporary host shim.
- After a crash: `Stop-CaptureSession.ps1 -KillClient`.
- Then feed `flow.har` to REA `inspect-web-network-capture` and map payloads into DTOs. See `AGENTS.md`.
