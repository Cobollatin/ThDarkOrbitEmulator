# Client testing (local on-demand)

Aspire commands on **`gateway-players`** for pointing the **DarkOrbit Unity** Windows client at your local emulator. **Local machine only** — requires game files, PowerShell, and mitmproxy.

Enable in `ThDarkOrbitEmulator.AppHost/appsettings.Development.json` (`ClientTesting:Enabled`). Disabled in CI automatically.

## mitmproxy addon

Uses the existing reversing addon (not a duplicate):

**`_reversing/Mitmproxy/launch_redirect.py`**

- `redirect=true` — rewrites the observed game/API hosts (`170.33.13.202`, `www.darkorbit.com`, `sas.bpsecure.com`, `gbl3.darkorbit.com`, `gbl6.darkorbit.com`) → `localhost:60952` (HTTPS) / `127.0.0.1:60953` (HTTP)
- `redirect_all=true` — optional via `ClientTesting:MitmRedirectAll` (all HTTP/S to gateway)
- `launch` / `launch_exe` / `track_child_pids` / `pid_log_file` — same as your manual workflow
- `flow_log_file` — optional JSONL for **`client-test-analyze-traffic`**

## Patch the installed client (one time per install)

Close `DarkOrbit.exe` first; Apply and Reset refuse to run while it is open.

```powershell
.\scripts\client-testing\Patch-ClientUris.ps1 -Action Status            # read-only; add -AsJson for machine output
.\scripts\client-testing\Patch-ClientUris.ps1 -Action Apply             # default target https://localhost:60952
.\scripts\client-testing\Patch-ClientUris.ps1 -Action Reset             # restore every .orig.bak
```

Apply rewrites the portal/download literals in `global-metadata.dat`, `config.txt` `localHost` and `update.ini` domains. It flips `DarkOrbit.exe` to the console subsystem, strips `nolog=` from `DarkOrbit_Data\boot.config` so Unity writes its log, and drops `Start-DarkOrbitConsole.cmd` in the client folder. The same actions are Aspire commands on `gateway-players` (`client-test-patch-uris-status|apply|reset`). Full contract: `docs/specs/02-client-uri-patching-and-connectivity.md`.

## Run the client with a live Unity console

```powershell
.\scripts\client-testing\Start-ClientConsole.ps1                        # -ClientRoot defaults to D:\Games\DarkOrbit Unity
# or double-click Start-DarkOrbitConsole.cmd in the client folder
```

The script starts `DarkOrbit.exe -logFile unity-console.log` and tails that log until the game exits. `Ctrl+C` kills the game process tree and returns to the prompt cleanly.

The emulator must be listening on **both** `https://localhost:60952` (WebView login, patched portal URLs) and `https://localhost:443` (the unpatchable `https://localhost/` IL2CPP literal). The AppHost binds both on `gateway-players`. Port 443 can need an elevated shell and must be free.

## Workflow

For an automated local login after the AppHost is healthy, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Redirect -SessionName local-provisioned-login -AutoLogin -ProvisionLocalAccount
```

`-ProvisionLocalAccount` uses the existing AutoLogin credential source to call the loopback gateway's registration endpoint. It is valid only in Redirect mode with AutoLogin. The credential values are passed in memory; the helper logs only the registration HTTP status. An existing account's local password is refreshed.

1. Start Aspire; align gateway ports with `HTTPS_REDIRECT_PORT` / `HTTP_REDIRECT_PORT` in the addon (default `60952` / `60953`).
2. **`client-test-endpoints`** — catalog of emulator routes for hardcoded client URLs.
3. **`client-test-scan-urls`** — `scripts/Find-HardcodedUrls.ps1`.
4. **`client-testing/url-replacements.json`** — binary-safe URL patches.
5. **`client-test-replace-preview`** / **`client-test-replace-apply`**.
6. **`client-test-start-mitm`** — starts mitmproxy with the addon above.
7. **`client-test-analyze-traffic`** after playing.
8. **`client-test-replace-rollback`** when done.

## mitmproxy (manual)

```powershell
mitmproxy --mode "local:DarkOrbit.exe,Vuplex WebView.vuplex" -s _reversing/Mitmproxy/launch_redirect.py `
  --set launch=true `
  --set launch_exe="D:\Games\DarkOrbit Unity\DarkOrbit.exe" `
  --set track_child_pids=true `
  --set pid_log_file="client-testing/darkorbit_pids.log" `
  --set flow_log_file="client-testing/client-traffic.jsonl" `
  --set redirect=true
```

## CLI

```bash
aspire resource gateway-players client-test-start-mitm
aspire resource gateway-players client-test-analyze-traffic
```

## Output (gitignored)

Under `client-testing/` — scan JSONL, traffic log, replacement manifests, PID log.
