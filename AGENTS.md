# AGENTS.md

## Client network capture (DarkOrbit Unity) — for Codex / Cursor / Copilot

Windows-only, local machine. Scripts live in `scripts/client-testing/`; output goes to the gitignored
`_reversing/Requests/sessions/<yyyyMMdd-HHmmss>-<name>/` (`flow`, `flow.har`, `summary.json`, `capture.log`, `mitmdump.log`, `screenshots/`).

Run a capture (one command, non-interactive; prints a JSON summary on stdout, logs on stderr):

```powershell
# Observe the official servers (Live). The human logs in inside the game window; the script returns when the client is closed.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Live -SessionName login-hangar -WaitForExit

# Same, but stop after 10 minutes even if the client is still open
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Live -SessionName login -DurationSeconds 600

# Point the client at the local Aspire emulator instead (launch_redirect.py: 170.33.13.202 -> localhost:60952/60953; start the AppHost first)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Redirect -SessionName local-login -WaitForExit

# Fully unattended login + menu clicks over CDP (credentials from env DO_USERNAME/DO_PASSWORD or Credential Manager target 'ThDarkOrbit')
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Live -SessionName auto -AutoLogin -DurationSeconds 300

# Smoke test without the game
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -NoLaunch -DurationSeconds 10
```

Rules for agents:
- Without `-AutoLogin` a **human performs the login and gameplay in the game window**. Tell the user the client is open and wait; do not try to type into the game window.
- Never put credentials in files, command lines, logs or chat. `-AutoLogin` reads them from env or Windows Credential Manager (`cmdkey /generic:ThDarkOrbit /user:<name> /pass` is run by the human).
- Exit codes: 0 ok, 1 unexpected, 2 prerequisites (mitmproxy CA / venv / client missing), 3 mitmdump failed or wrote nothing,
  4 client launch failed, 5 HAR export failed, 6 auto-login failed, 7 another session active.
- If a run was killed (crash, closed terminal) or exit code 7: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Stop-CaptureSession.ps1 -KillClient`
  (stops mitmdump, restores the system proxy and the original Vuplex WebView host). It is idempotent.
- Default proxying is mitmproxy **local capture mode** (per process: DarkOrbit.exe, launcher, Vuplex WebView) — no system proxy change.
  `-ProxyMethod System` points the per-user WinINet proxy at mitmdump and restores it afterwards, but it records ALL apps' traffic; avoid it.
- `-AutoLogin`/`-EnableCdp` temporarily swap `DarkOrbit_Data\Plugins\x86\VuplexWebViewChromium\Vuplex WebView.vuplex` for a shim that adds
  `--remote-debugging-port=9222`; the original is restored in `finally` / by Stop-CaptureSession. Login selectors: `scripts/client-testing/automate-login.config.json`.
  Inspect the embedded UI with `node scripts\client-testing\automate-login.mjs --list` or http://127.0.0.1:9222 while a `-EnableCdp` session runs.

- Unity pauses rendering while its window is minimized, which stalls the Vuplex UI (logins/menu clicks then fail). Start-CaptureSession.ps1 now keeps DarkOrbit's window restored at `-WindowBounds` (default `3840,0,1280,1440` = \\.\DISPLAY6; pass '' to disable). Capture/login/menu runs before 2026-10-21 23:32 (UTC-5) may have failed only because the window was minimized: re-check those results instead of trusting them.

After the capture, analyze with REA:
1. Read `summary.json` (entry count, hosts) and pick the game/API hosts (e.g. `*.bpsecure.com`, `*.darkorbit.com`; ignore ads/analytics).
2. Run REA `inspect-web-network-capture` on `<session>\flow.har` to list endpoints, methods, request/response payloads.
3. Map each payload into DTOs/contracts in the emulator (C# records in the matching service), noting field names, types, optionality and
   which endpoint/flow produced them. Cross-check strings in `GameAssembly.dll`/`DarkorbitLauncher.dll` per `docs/rea-for-client-reversing.md` and `_reversing/REA.md`.
4. Binary (non-HTTP) game traffic is not in the HAR; use the `flow` file with `_reversing/Mitmproxy/*.py` helpers for TCP/WebSocket flows.
