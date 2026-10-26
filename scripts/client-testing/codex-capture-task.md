# HARD RULES (read first; these override everything below)

This run has the sandbox disabled. Stay strictly inside the project boundary:
1. Only create or modify files under `D:\Local_Source\Projects\ThDarkOrbitEmulator`.
2. The only allowed exception: `Start-CaptureSession.ps1` temporarily swaps `Vuplex WebView.vuplex` in `D:\Games\DarkOrbit Unity` and restores it itself (or via `Stop-CaptureSession.ps1 -KillClient`). Never edit, move or delete anything in `D:\Games\DarkOrbit Unity` by hand.
3. Do not touch other directories, the Windows system proxy, registry, global/tool config (`~/.codex`, git config, npm/dotnet global settings) or the user profile.
4. No installs outside the repo `.venv` / `node_modules`. No `git commit`, `push`, `reset`, `clean`, `stash`, `checkout` or `restore`.
5. Never print, log, echo or write credentials (do not open the credential HAR to read them).
6. Only run: `scripts\client-testing\Start-CaptureSession.ps1` / `Stop-CaptureSession.ps1` / `automate-login.mjs`, the REA MCP tools, `dotnet build`, and read-only commands (Get-Content, Get-ChildItem, rg, git status/diff/log).
If a step would require breaking a rule, stop and explain instead.
# Codex task: automated DarkOrbit capture -> REA -> DTOs

You are working in `D:\Local_Source\Projects\ThDarkOrbitEmulator` (Windows, PowerShell 5.1; `python` is not on PATH — use `.venv\Scripts\python.exe`).
Read `AGENTS.md` (section "Client network capture") and `docs/rea-for-client-reversing.md` first.

Hard rules:
- Never print, log, echo, commit or write game credentials anywhere. `-AutoLogin` resolves them itself (env -> Credential Manager
  `ThDarkOrbit` -> captured SAS login in `_reversing\Requests\flow.har`). Do not open or grep that HAR for the password.
- Do not revert, commit, stash or delete existing user changes. Do not touch `.git`.
- Always leave the machine clean: if anything goes wrong run
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Stop-CaptureSession.ps1 -KillClient`.

## 1. Capture (max 3 attempts)
Run from the repo root:
```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\client-testing\Start-CaptureSession.ps1 -Mode Live -SessionName login -AutoLogin -DurationSeconds 180
```
It prints a JSON summary (stdout) with `sessionDir`, `har`, `entries`, `hosts`, `exitCode`, `autoLogin`. Then:
- Look at the PNGs in `<sessionDir>\screenshots\` and `<sessionDir>\capture.log` (lines `autologin:`).
- Login succeeded if the post-submit screenshots show the logged-in DarkOrbit UI (hangar / account / start page) and
  `hosts` include game hosts beyond the landing page (e.g. `sas.bpsecure.com` POST followed by `*.darkorbit.com` / `darkorbit-*.bpsecure.com` game pages).
- If it failed (exit code 6, still on the login form, error banner, captcha, cookie banner blocking): fix
  `scripts\client-testing\automate-login.config.json` (selectors, `preSteps`, `successSelector`/`successUrlPattern`) or
  `scripts\client-testing\automate-login.mjs`. To inspect the live DOM, run the capture with `-EnableCdp` instead of `-AutoLogin`
  and use `node scripts\client-testing\automate-login.mjs --list` / CDP at http://127.0.0.1:9222. Then retry.
- Stop after 3 failed attempts and report why (e.g. hCaptcha). Do NOT attempt to bypass a captcha.

## 2. Analyze with REA (MCP server `rea`)
On the successful session's `flow.har` (and `flow` for non-HTTP), use REA `inspect-web-network-capture` to enumerate endpoints:
method, host, path, query, request body shape, response body shape, content types, status codes, auth/cookie/session tokens (names only).
Ignore ads/analytics/CDN hosts (google*, doubleclick, bing, inmobi, quantserve, hcaptcha, cloudflare-dns, etc.).

## 3. Produce code + docs
- Inspect the solution layout first (`*.sln*`, `ThDarkOrbitEmulator.ApiService\Features\{Sas,UnityApi,FlashApi,GameAuth,IndexEs,...}`)
  and put DTOs next to the feature that serves that endpoint, following existing naming/namespace conventions
  (e.g. `Features\Sas\Contracts\`, `Features\UnityApi\<Area>\Contracts\`). Create one C# `sealed record` per request/response body
  with typed properties (`JsonPropertyName` matching the wire names, nullable where optional, enums only when values are evident).
  Form-encoded bodies become records too. Do not change endpoint behavior; DTOs only. Run `dotnet build` and fix errors.
- Write `docs/client-endpoint-map.md`: table of endpoints (host, method, path, purpose, request DTO, response DTO, observed status,
  emulator feature folder, implemented yes/no), the login sequence in order, and open questions. Redact any secret values
  (tokens, session ids, credentials) as `<redacted>`.
- Finish with a short summary: session folder used, attempts made, files created, build result.
