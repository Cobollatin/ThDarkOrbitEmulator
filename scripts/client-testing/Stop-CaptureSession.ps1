<#
.SYNOPSIS
  Crash cleanup for Start-CaptureSession.ps1: stops mitmdump, restores the system proxy and the original Vuplex host.
  Safe to run any time (idempotent). Prints a JSON result; exit 0 = clean.
.PARAMETER KillClient  Also close DarkOrbit / launcher processes.
.PARAMETER ClientDir   Used to restore the Vuplex shim when no state file exists.
#>
[CmdletBinding()]
param([switch]$KillClient, [string]$ClientDir = 'D:\Games\DarkOrbit Unity')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CaptureCommon.ps1')
$result = [ordered]@{ stateFound = $false; mitmStopped = $false; proxyRestored = $false; shimRestored = $false; errors = @() }
$state = Read-State
try {
    if ($state) {
        $result.stateFound = $true; $result.sessionDir = $state.sessionDir
        $script:LogFile = if ($state.sessionDir -and (Test-Path $state.sessionDir)) { Join-Path $state.sessionDir 'capture.log' } else { $null }
        if ($state.clientDir) { $ClientDir = $state.clientDir }
        if ($state.proxyBackup) { Restore-SystemProxy $state.proxyBackup; $result.proxyRestored = $true; Write-Log 'Stop-CaptureSession: system proxy restored' }
        if ($state.mitmPid) { Stop-Mitmdump $state.mitmPid $state.stopFile 10; $result.mitmStopped = $true }
    } else {
        # No state: stop any mitmdump launched from this repo's venv.
        Get-CimInstance Win32_Process -Filter "Name='mitmdump.exe'" | Where-Object { $_.CommandLine -like '*capture_control.py*' } | ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; $result.mitmStopped = $true }
    }
    if ($KillClient) { Get-Process -Name 'DarkOrbit', 'DarkorbitLauncher' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    $v = Get-VuplexPaths $ClientDir
    if (Test-Path -LiteralPath $v.Real) {
        if (-not $KillClient -and (Get-Process -Name 'DarkOrbit' -ErrorAction SilentlyContinue)) { $result.errors += 'DarkOrbit is running; rerun with -KillClient to restore the Vuplex host' }
        else { Uninstall-VuplexCdpShim $ClientDir; $result.shimRestored = $true }
    }
    if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
} catch { $result.errors += $_.Exception.Message }
$result.currentSystemProxy = Get-SystemProxy
$result | ConvertTo-Json -Depth 4
exit $(if ($result.errors.Count) { 1 } else { 0 })
