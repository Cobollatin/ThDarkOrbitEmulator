<#
.SYNOPSIS
  One-command DarkOrbit Unity traffic capture: mitmdump -> launch client -> wait -> stop -> HAR export -> JSON summary.

.DESCRIPTION
  Non-interactive and agent-friendly. Everything lands in _reversing\Requests\sessions\<yyyyMMdd-HHmmss>-<SessionName>\:
    flow (mitmproxy dump), flow.har, summary.json, capture.log, mitmdump.log, screenshots\ (AutoLogin).
  The JSON summary is the only thing written to stdout; logs go to stderr and capture.log.

  Proxying (-ProxyMethod):
    Local  (default) mitmproxy local-capture mode ("local:DarkOrbit.exe,Vuplex WebView..."): intercepts by process,
           no system proxy change. Same approach as docs/client-testing.md.
    System mitmdump as a regular proxy on -ProxyPort (default 8089; 8080 is usually taken) and the WinINet (per-user) system proxy pointed at it.
           Original values are restored in finally (also on Ctrl+C) and by Stop-CaptureSession.ps1.
    None   mitmdump regular proxy only, nothing pointed at it (smoke tests).

  -Mode Live      : pass traffic upstream to the real servers (observe the official protocol).
  -Mode Redirect  : redirect captured game/API hosts (170.33.13.202, www.darkorbit.com, sas.bpsecure.com,
                    numbered gblN.darkorbit.com game hosts) -> local Aspire 60952/60953 with ssl_insecure=true, via launch_redirect.py redirect=true
                    (default) or -RedirectAddon RedirectToLocal (_reversing\Mitmproxy\redirect_to_local.py).
  Local mode targets exactly "DarkOrbit.exe,Vuplex WebView.vuplex" (override with -ProcessNames, or -TargetPid for local:<pid>).
  -LaunchVia Addon / -TrackChildPids reproduce the manual flow: launch_redirect.py launch=true launch_exe=... track_child_pids=true
  pid_log_file=<session>\darkorbit_pids.log.

  Credential order for -AutoLogin: env DO_USERNAME/DO_PASSWORD -> Credential Manager 'ThDarkOrbit' -> the captured SAS login POST
  (sas.bpsecure.com/Sas/Authentication) in -CredentialHar (default _reversing\Requests\flow.har, gitignored). Values are never logged.

  -AutoLogin installs a temporary shim for the Vuplex CEF host that adds --remote-debugging-port (DarkOrbit never enables
  it itself), then runs automate-login.mjs over raw CDP (Node 22, no npm deps). Credentials come from env DO_USERNAME/DO_PASSWORD or the Windows
  Credential Manager generic credential 'ThDarkOrbit' and are only passed to the node child process environment.

.EXITCODES
  0 ok | 1 unexpected error | 2 prerequisites (CA/venv/client missing) | 3 mitmdump failed to start or write
  4 client launch failed | 5 HAR export failed/timed out | 6 auto-login failed (or timed out) | 7 another session is active
  Every wait has a TTL: -MaxSeconds (300), -IdleStopSeconds (30), -MenuTimeoutSeconds (5), -NodeTimeoutSeconds (240),
  -MitmStartTimeoutSeconds (45), -MitmStopTimeoutSeconds (20), -ExportTimeoutSeconds (180); child trees are killed on timeout.
#>
[CmdletBinding()]
param(
    [ValidateSet('Live', 'Redirect')] [string]$Mode = 'Live',
    [string]$SessionName = 'capture',
    [switch]$NoLaunch,
    [int]$DurationSeconds = 0,          # 0 = no limit (wait for client exit; with -NoLaunch defaults to 30)
    [switch]$WaitForExit,               # wait for the client to exit even if -DurationSeconds is set (whichever first otherwise)
    [ValidateSet('Local', 'System', 'None')] [string]$ProxyMethod = 'Local',
    [int]$ProxyPort = 8089,
    [string]$ClientDir = 'D:\Games\DarkOrbit Unity',
    [switch]$UseLauncher,               # start DarkorbitLauncher.exe (updates, then starts DarkOrbit.exe) instead of DarkOrbit.exe
    [switch]$AutoLogin,
    [switch]$ProvisionLocalAccount,     # Redirect AutoLogin: register/refresh the local account using the in-memory login credentials
    [string]$AutoLoginConfig = '',      # default: automate-login.config.json next to this script
    [switch]$EnableCdp,                 # install the CDP shim without running automation (manual inspection)
    [int]$CdpPort = 9222,
    [string]$CredentialTarget = 'ThDarkOrbit',
    [switch]$KeepClientRunning,         # don't close the client when the duration ends
    [ValidateSet('Script', 'Addon')] [string]$LaunchVia = 'Script',  # Addon = launch_redirect.py launch/launch_exe (the manual flow)
    [switch]$TrackChildPids,            # launch_redirect.py track_child_pids (pid log in <session>\darkorbit_pids.log); implies -LaunchVia Addon
    [ValidateSet('LaunchRedirect', 'RedirectToLocal')] [string]$RedirectAddon = 'LaunchRedirect',  # Redirect mode script
    [string[]]$ProcessNames = @('DarkOrbit.exe', 'Vuplex WebView.vuplex'),  # local-mode targets (exactly the manual flow)
    [int]$TargetPid = 0,                # local:<pid> instead of process names (attach to a running client)
    [string]$CredentialHar = '',        # HAR holding a captured SAS login POST; default _reversing\Requests\flow.har
    [string]$WindowBounds = '3840,0,1280,1440',  # x,y,width,height for DarkOrbit's window (\\.\DISPLAY6; kept restored, re-checked every ~2s); '' disables
    # ---- TTLs (every wait is bounded) ----
    [int]$MaxSeconds = 300,             # hard cap for the whole session (start -> stop, before export); 0 disables
    [int]$IdleStopSeconds = 30,         # with -AutoLogin/-NoLaunch: after the auto steps, stop when the flow file stops growing this long; 0 disables
    [int]$MenuTimeoutSeconds = 5,       # automate-login.mjs: max wait for each menu-step element
    [int]$NodeTimeoutSeconds = 240,     # automate-login.mjs hard timeout (tree killed on breach)
    [int]$MitmStartTimeoutSeconds = 45,
    [int]$MitmStopTimeoutSeconds = 20,
    [int]$ExportTimeoutSeconds = 180    # export_flow_har.py hard timeout
)

$ErrorActionPreference = 'Stop'
if ($TrackChildPids) { $LaunchVia = 'Addon' }
. (Join-Path $PSScriptRoot 'CaptureCommon.ps1')

$exitCode = 0
$scriptStart = Get-Date
function Get-Remaining { if ($MaxSeconds -le 0) { return [int]::MaxValue }; [int]($MaxSeconds - ((Get-Date) - $scriptStart).TotalSeconds) }
$summary = [ordered]@{ ok = $false; exitCode = $null; mode = $Mode; proxyMethod = $ProxyMethod }
$proxySnapshot = $null; $mitm = $null; $proxyBackup = $null; $shimInstalled = $false; $client = $null; $stopFile = $null
$autoLoginResult = $null

function Fail([int]$Code, [string]$Msg) { $script:exitCode = $Code; throw $Msg }

try {
    # ---------- prerequisites ----------
    $existing = Read-State
    if ($existing -and $existing.mitmPid -and (Get-Process -Id $existing.mitmPid -ErrorAction SilentlyContinue)) {
        Fail 7 "Another capture session is active ($($existing.sessionDir)). Run Stop-CaptureSession.ps1 first."
    }
    if ($existing) { Fail 7 "Stale session state found ($StateFile). Run Stop-CaptureSession.ps1 to restore settings first." }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $safeName = ($SessionName -replace '[^\w\-\.]', '_')
    $sessionDir = Join-Path $SessionsRoot "$stamp-$safeName"
    New-Item -ItemType Directory -Force -Path $sessionDir | Out-Null
    $script:LogFile = Join-Path $sessionDir 'capture.log'
    $flowPath = Join-Path $sessionDir 'flow'
    $harPath = Join-Path $sessionDir 'flow.har'
    $summaryPath = Join-Path $sessionDir 'summary.json'
    $mitmLog = Join-Path $sessionDir 'mitmdump.log'
    $stopFile = Join-Path $sessionDir '.stop'
    $summary.sessionDir = $sessionDir; $summary.flow = $flowPath; $summary.har = $harPath; $summary.log = $script:LogFile
    Write-Log "Session $sessionDir (Mode=$Mode ProxyMethod=$ProxyMethod NoLaunch=$NoLaunch AutoLogin=$AutoLogin)"

    if (-not (Test-Path $Mitmdump) -or -not (Test-Path $VenvPython)) { Fail 2 "mitmproxy venv missing: $Mitmdump" }
    if (-not (Test-MitmCaInstalled)) {
        Fail 2 "mitmproxy CA not trusted. Run mitmdump once, then: certutil -user -addstore Root `"$env:USERPROFILE\.mitmproxy\mitmproxy-ca-cert.cer`""
    }
    Write-Log 'mitmproxy CA is installed in Root store'
    $clientExe = Join-Path $ClientDir ($(if ($UseLauncher) { 'DarkorbitLauncher.exe' } else { 'DarkOrbit.exe' }))
    if (-not $NoLaunch -and -not (Test-Path $clientExe)) { Fail 2 "Client not found: $clientExe" }
    if ($AutoLogin -and $NoLaunch) { Fail 2 '-AutoLogin requires launching the client' }
    if ($ProvisionLocalAccount -and (-not $AutoLogin -or $Mode -ne 'Redirect')) { Fail 2 '-ProvisionLocalAccount requires -Mode Redirect -AutoLogin' }

    $useShim = (-not $NoLaunch) -and ($AutoLogin -or $EnableCdp)
    if ($useShim) {
        Save-State @{ sessionDir = $sessionDir; mitmPid = $null; stopFile = $stopFile; proxyMethod = $ProxyMethod; proxyBackup = $null; clientDir = $ClientDir; shim = $true }
        Install-VuplexCdpShim $ClientDir; $shimInstalled = $true
        $env:DO_CDP_PORT = "$CdpPort"
    }
    # ---------- mitmdump ----------
    $proxySnapshot = Get-SystemProxy
    if ($ProxyMethod -ne 'Local' -and (Get-NetTCPConnection -LocalPort $ProxyPort -State Listen -ErrorAction SilentlyContinue)) { Fail 3 "Port $ProxyPort is already in use; pass -ProxyPort" }
    $mitmArgs = New-Object System.Collections.Generic.List[string]
    $launchRedirect = Join-Path $RepoRoot '_reversing\Mitmproxy\launch_redirect.py'
    $redirectToLocal = Join-Path $RepoRoot '_reversing\Mitmproxy\redirect_to_local.py'
    switch ($ProxyMethod) {
        'Local' {
            if ($TargetPid -gt 0) { $spec = "local:$TargetPid" }
            else {
                $names = New-Object System.Collections.Generic.List[string]; $names.AddRange([string[]]$ProcessNames)
                # with the CDP shim the real CEF host runs under a different file name
                if ($useShim -and -not $names.Contains('Vuplex WebView.real.vuplex')) { $names.Add('Vuplex WebView.real.vuplex') }
                $spec = 'local:' + ($names -join ',')
            }
            $mitmArgs.Add('--mode'); $mitmArgs.Add("`"$spec`"")
        }
        default { $mitmArgs.Add('--mode'); $mitmArgs.Add('regular'); $mitmArgs.Add('--listen-port'); $mitmArgs.Add("$ProxyPort") }
    }
    $mitmArgs.AddRange([string[]]@('-w', "`"$flowPath`"", '-s', "`"$(Join-Path $PSScriptRoot 'capture_control.py')`"", '--set', "`"stop_file=$stopFile`"", '--set', 'connection_strategy=lazy'))
    $needLaunchRedirect = ($LaunchVia -eq 'Addon' -and -not $NoLaunch) -or ($Mode -eq 'Redirect' -and $RedirectAddon -eq 'LaunchRedirect')
    if ($needLaunchRedirect) { $mitmArgs.AddRange([string[]]@('-s', "`"$launchRedirect`"")) }
    if ($Mode -eq 'Redirect') {
        $mitmArgs.AddRange([string[]]@('--set', 'ssl_insecure=true'))   # local Aspire uses a dev certificate
        if ($RedirectAddon -eq 'RedirectToLocal') { $mitmArgs.AddRange([string[]]@('-s', "`"$redirectToLocal`"")) }
        else { $mitmArgs.AddRange([string[]]@('--set', 'redirect=true')) }
    }
    if ($LaunchVia -eq 'Addon' -and -not $NoLaunch) {
        $pidLog = Join-Path $sessionDir 'darkorbit_pids.log'
        $mitmArgs.AddRange([string[]]@('--set', 'launch=true', '--set', "`"launch_exe=$clientExe`"", '--set', "`"pid_log_file=$pidLog`""))
        if ($TrackChildPids) { $mitmArgs.AddRange([string[]]@('--set', 'track_child_pids=true')) }
        $summary.pidLog = $pidLog
    }
    Write-Log "mitmdump $($mitmArgs -join ' ')"
    $env:PYTHONUNBUFFERED = '1'   # so readiness lines reach mitmdump.log immediately
    $mitm = Start-Process -FilePath $Mitmdump -ArgumentList $mitmArgs -WorkingDirectory $RepoRoot -WindowStyle Hidden `
        -RedirectStandardOutput $mitmLog -RedirectStandardError "$mitmLog.err" -PassThru
    Save-State @{ sessionDir = $sessionDir; mitmPid = $mitm.Id; stopFile = $stopFile; proxyMethod = $ProxyMethod; proxyBackup = $null; clientDir = $ClientDir; shim = $shimInstalled }

    $deadline = (Get-Date).AddSeconds($MitmStartTimeoutSeconds); $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($mitm.HasExited) { break }
        $out = (Get-Content -LiteralPath $mitmLog -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath "$mitmLog.err" -Raw -ErrorAction SilentlyContinue)
        if ($out -match 'proxy listening|Local redirector started|listening at|Proxy server listening') { $ready = $true; break }
        if ($ProxyMethod -ne 'Local' -and (Get-NetTCPConnection -LocalPort $ProxyPort -State Listen -ErrorAction SilentlyContinue)) { $ready = $true; break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $ready) { Stop-ProcessTree $mitm.Id; Fail 3 "mitmdump did not become ready within ${MitmStartTimeoutSeconds}s (tree killed; see $mitmLog / $mitmLog.err)" }
    Write-Log "mitmdump running (pid $($mitm.Id))"

    # ---------- proxy config ----------
    if ($ProxyMethod -eq 'System') {
        $proxyBackup = Get-SystemProxy
        Save-State @{ sessionDir = $sessionDir; mitmPid = $mitm.Id; stopFile = $stopFile; proxyMethod = $ProxyMethod; proxyBackup = $proxyBackup; clientDir = $ClientDir; shim = $shimInstalled }
        Set-SystemProxy "127.0.0.1:$ProxyPort"
        Write-Log "System proxy set to 127.0.0.1:$ProxyPort (backup: $(($proxyBackup | ConvertTo-Json -Compress)))"
    }

    # ---------- client ----------
    if (-not $NoLaunch) {
        if ($LaunchVia -eq 'Addon') {
            $procName = [IO.Path]::GetFileNameWithoutExtension($clientExe)
            $t = (Get-Date).AddSeconds(30)
            while (-not ($client = Get-Process -Name $procName -ErrorAction SilentlyContinue | Select-Object -First 1) -and (Get-Date) -lt $t) { Start-Sleep -Milliseconds 500 }
            if (-not $client) { Fail 4 "launch_redirect.py did not start $clientExe (see $mitmLog)" }
            Write-Log "launch_redirect.py launched $clientExe (pid $($client.Id))"
        } else {
            try { $client = Start-Process -FilePath $clientExe -WorkingDirectory $ClientDir -PassThru }
            catch { Fail 4 "Client launch failed: $($_.Exception.Message)" }
            Write-Log "Launched $clientExe (pid $($client.Id))"
        }
        $summary.clientPid = $client.Id
        if ($WindowBounds) { $wt = (Get-Date).AddSeconds(30); while (-not (Set-ClientWindow $WindowBounds) -and (Get-Date) -lt $wt) { Start-Sleep -Milliseconds 500 } }

        if ($AutoLogin) {
            $cfg = if ($AutoLoginConfig) { $AutoLoginConfig } else { Join-Path $PSScriptRoot 'automate-login.config.json' }
            if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail 2 'node (>= 22, built-in WebSocket) is required for -AutoLogin' }
            $user = $env:DO_USERNAME; $pass = $env:DO_PASSWORD
            if (-not $user -or -not $pass) {
                $c = Get-StoredCredential $CredentialTarget
                if ($c) { $user = $c[0]; $pass = $c[1]; Write-Log "Using credentials from Credential Manager target '$CredentialTarget'" }
            }
            $credHar = if ($CredentialHar) { $CredentialHar } else { Join-Path $RepoRoot '_reversing\Requests\flow.har' }
            if ((-not $user -or -not $pass) -and -not (Test-Path -LiteralPath $credHar)) {
                Fail 6 "No credentials: set DO_USERNAME/DO_PASSWORD, add generic credential '$CredentialTarget', or provide -CredentialHar"
            }
            if (-not $user -or -not $pass) { Write-Log "Credentials will be read by automate-login.mjs from the captured SAS login in $credHar" }
            $shots = Join-Path $sessionDir 'screenshots'
            $prevU = $env:DO_USERNAME; $prevP = $env:DO_PASSWORD
            if ($user -and $pass) { $env:DO_USERNAME = $user; $env:DO_PASSWORD = $pass } else { Remove-Item Env:\DO_USERNAME, Env:\DO_PASSWORD -ErrorAction SilentlyContinue }
            $winJob = $null
            if ($WindowBounds) { $winJob = Start-Job -ArgumentList (Join-Path $PSScriptRoot 'CaptureCommon.ps1'), $WindowBounds -ScriptBlock { param($common, $wb) . $common; while ($true) { try { [void](Set-ClientWindow $wb) } catch {}; Start-Sleep 2 } } }
            try {
                $nodeTtl = [Math]::Max(10, [Math]::Min($NodeTimeoutSeconds, (Get-Remaining)))
                $nodeExe = (Get-Command node).Source
                $nodeArgs = @((Join-Path $PSScriptRoot 'automate-login.mjs'), '--cdp', "http://127.0.0.1:$CdpPort", '--config', $cfg, '--out', $shots, '--creds-har', $credHar,
                    '--menu-timeout', "$MenuTimeoutSeconds", '--max-seconds', "$([Math]::Max(5, $nodeTtl - 5))")
                if ($ProvisionLocalAccount) { $nodeArgs += '--provision-local-account' }
                $r = Invoke-WithTimeout $nodeExe $nodeArgs $nodeTtl (Join-Path $sessionDir 'autologin.out')
                $alOut = @($r.stdout) + @($r.stderr); $alCode = $r.exitCode
                if ($r.timedOut) { Write-Log "automate-login.mjs timed out after ${nodeTtl}s" 'WARN' }
            } finally { if ($winJob) { Stop-Job $winJob -ErrorAction SilentlyContinue; Remove-Job $winJob -Force -ErrorAction SilentlyContinue }; $env:DO_USERNAME = $prevU; $env:DO_PASSWORD = $prevP; $user = $null; $pass = $null }
            $alOut | ForEach-Object { Write-Log "autologin: $_" }
            $autoLoginResult = [ordered]@{ exitCode = $alCode; screenshots = $shots }
            if ($alCode -ne 0) { Fail 6 "automate-login.mjs failed with exit code $alCode" }
        }
    }

    # ---------- wait ----------
    # Stop conditions (first wins): client exit | -DurationSeconds | -MaxSeconds hard cap | idle (no flow growth for
    # -IdleStopSeconds; with -AutoLogin or -NoLaunch, i.e. once the automatic steps are done).
    # -AutoLogin never waits for the client to exit unless -WaitForExit is given explicitly.
    $limit = $DurationSeconds
    if ($NoLaunch -and $limit -le 0) { $limit = 30 }
    $end = if ($limit -gt 0) { (Get-Date).AddSeconds($limit) } else { [datetime]::MaxValue }
    $idleOn = ($IdleStopSeconds -gt 0) -and ($AutoLogin -or $NoLaunch) -and -not $WaitForExit
    $lastSize = -1; $lastGrowth = Get-Date; $lastWin = [datetime]::MinValue
    Write-Log "Capturing (duration: $(if ($limit -gt 0) { "${limit}s" } else { 'none' }); max: ${MaxSeconds}s; idle stop: $(if ($idleOn) { "${IdleStopSeconds}s" } else { 'off' }); wait for client exit: $($WaitForExit -or (-not $NoLaunch -and -not $AutoLogin)))"
    $summary.stopReason = $null
    while ($true) {
        if ($mitm.HasExited) { Fail 3 "mitmdump exited unexpectedly (code $($mitm.ExitCode)); see $mitmLog.err" }
        $clientAlive = $false
        if ($client) { $clientAlive = [bool](Get-Process -Name 'DarkOrbit', 'DarkorbitLauncher' -ErrorAction SilentlyContinue) }
        if ($client -and -not $clientAlive) { $summary.stopReason = 'client-exit'; Write-Log 'Client exited'; break }
        if ($client -and $WindowBounds -and ((Get-Date) - $lastWin).TotalSeconds -ge 2) { $lastWin = Get-Date; try { [void](Set-ClientWindow $WindowBounds) } catch {} }
        if ((Get-Remaining) -le 0) { $summary.stopReason = 'max-seconds'; Write-Log "MaxSeconds ($MaxSeconds) reached" 'WARN'; break }
        if ((Get-Date) -ge $end -and -not ($WaitForExit -and $clientAlive)) { $summary.stopReason = 'duration'; Write-Log 'Duration reached'; break }
        if ($idleOn) {
            $size = if (Test-Path -LiteralPath $flowPath) { (Get-Item -LiteralPath $flowPath).Length } else { 0 }
            if ($size -ne $lastSize) { $lastSize = $size; $lastGrowth = Get-Date }
            elseif (((Get-Date) - $lastGrowth).TotalSeconds -ge $IdleStopSeconds) { $summary.stopReason = 'idle'; Write-Log "No new flows for ${IdleStopSeconds}s; stopping"; break }
        }
        Start-Sleep -Seconds 1
    }
}
catch {
    if ($exitCode -eq 0) { $exitCode = 1 }
    $summary.error = $_.Exception.Message
    Write-Log $_.Exception.Message 'ERROR'
}
finally {
    # Runs on normal exit, errors and Ctrl+C.
    if ($client -and -not $KeepClientRunning) {
        Get-Process -Name 'DarkOrbit', 'DarkorbitLauncher' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    if ($shimInstalled) { try { Uninstall-VuplexCdpShim $ClientDir } catch { Write-Log "Shim restore failed: $_ (run Stop-CaptureSession.ps1)" 'ERROR' } }
    if ($proxyBackup) { try { Restore-SystemProxy $proxyBackup; Write-Log 'System proxy restored' } catch { Write-Log "Proxy restore failed: $_ (run Stop-CaptureSession.ps1)" 'ERROR' } }
    if ($mitm) { Stop-Mitmdump $mitm.Id $stopFile $MitmStopTimeoutSeconds }
    if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
    Remove-Item Env:\DO_CDP_PORT -ErrorAction SilentlyContinue
}

# ---------- export ----------
if ($summary.Contains('sessionDir') -and (Test-Path -LiteralPath $summary.sessionDir)) {
    if (Test-Path -LiteralPath $flowPath) {
        $summary.flowBytes = (Get-Item -LiteralPath $flowPath).Length
        $er = Invoke-WithTimeout $VenvPython @((Join-Path $PSScriptRoot 'export_flow_har.py'), $flowPath, $harPath, '--summary', "$summaryPath.export") $ExportTimeoutSeconds (Join-Path $summary.sessionDir 'export.out')
        @($er.stdout) + @($er.stderr) | ForEach-Object { Write-Log "export: $_" }
        if ($er.timedOut) { Write-Log "HAR export timed out after ${ExportTimeoutSeconds}s" 'ERROR' }
        if ($er.exitCode -eq 0 -and (Test-Path "$summaryPath.export")) {
            $e = Get-Content "$summaryPath.export" -Raw | ConvertFrom-Json
            Remove-Item "$summaryPath.export"
            $summary.flows = $e.flows; $summary.entries = $e.entries; $summary.hosts = $e.hosts
        } else { if ($exitCode -eq 0) { $exitCode = 5 }; $summary.error = 'HAR export failed' }
    } else {
        Write-Log 'No flow file was written' 'WARN'
        if ($exitCode -eq 0 -and -not $NoLaunch) { $exitCode = 3; $summary.error = 'No flow file was written' }
        $summary.flowBytes = 0; $summary.entries = 0
    }
    if ($autoLoginResult) { $summary.autoLogin = $autoLoginResult }
    if ($ProxyMethod -eq 'System') { if ($proxySnapshot) { $summary.proxyRestored = (($proxySnapshot | ConvertTo-Json -Compress) -eq ((Get-SystemProxy) | ConvertTo-Json -Compress)) } }
}
$summary.ok = ($exitCode -eq 0); $summary.exitCode = $exitCode
$json = $summary | ConvertTo-Json -Depth 5
if ($summary.Contains('sessionDir') -and (Test-Path -LiteralPath $summary.sessionDir)) { $json | Set-Content -LiteralPath $summaryPath -Encoding UTF8 }
Write-Output $json
exit $exitCode
