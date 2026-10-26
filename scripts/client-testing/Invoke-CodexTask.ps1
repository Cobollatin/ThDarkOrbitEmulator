<#
.SYNOPSIS
  Run ONE `codex exec` task with hard TTLs and a no-output watchdog; kill the whole process tree on breach.

.DESCRIPTION
  Launches: Get-Content -Raw <PromptFile> | codex exec --dangerously-bypass-approvals-and-sandbox -C <Repo> --color never -o <LastMessage> -
  (stdout+stderr -> <Log>). The prompt MUST start with the "# HARD RULES" block (refused otherwise).
  Watchdogs:
    -TtlMinutes  (25) hard wall-clock cap.
    -IdleMinutes (5)  no growth of the codex log AND of the newest capture session's files for this long.
  On breach the codex tree is killed and Stop-CaptureSession.ps1 -KillClient runs (restores the Vuplex shim, stops mitmdump/client).
  Usage/rate-limit messages in the log tail (codex error lines) produce a distinct exit code.
  A JSON result is written to <Log>.result.json and stdout.

.EXITCODES
  0 codex exited 0 | 1 codex exited non-zero | 2 bad arguments / prompt missing HARD RULES | 3 codex not found
  10 TTL exceeded (killed) | 11 no-output watchdog (killed) | 20 usage / rate limit reported
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PromptFile,
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$Repo = '',                # default: the repo containing this script
    [int]$TtlMinutes = 25,
    [int]$IdleMinutes = 5,
    [string]$Log = '',
    [string]$LastMessage = '',
    [switch]$NoLaunch      # validate and print the command, launch nothing
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CaptureCommon.ps1')
if (-not $Repo) { $Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
$sessions = Join-Path $Repo '_reversing\Requests\sessions'
if (-not $Log) { $Log = Join-Path $sessions "codex-run-$RunId.log" }
if (-not $LastMessage) { $LastMessage = Join-Path $sessions "codex-last-message-$RunId.md" }
$script:LogFile = "$Log.wrapper.log"
$LimitPattern = "(?i)(usage limit|rate[ -]?limit(ed)?|hit your .{0,40}limit|too many requests|quota (exceeded|reached)|insufficient_quota|limit will reset|try again (at|in) )"

function Finish([int]$Code, [string]$Reason, $Extra = @{}) {
    $r = [ordered]@{ runId = $RunId; exitCode = $Code; reason = $Reason; log = $Log; lastMessage = $LastMessage
        started = $script:start; ended = (Get-Date -Format o); minutes = if ($script:t0) { [math]::Round(((Get-Date) - $script:t0).TotalMinutes, 1) } else { 0 } }
    foreach ($k in $Extra.Keys) { $r[$k] = $Extra[$k] }
    $json = $r | ConvertTo-Json -Depth 4
    if (-not $NoLaunch) { $json | Set-Content -LiteralPath "$Log.result.json" -Encoding UTF8 }
    Write-Output $json; exit $Code
}
function Get-LimitMessage {
    $tail = @(Get-Content -LiteralPath $Log -Tail 60 -ErrorAction SilentlyContinue)
    # only codex's own error/status lines, not model prose about rate limits
    $hit = $tail | Where-Object { $_ -match '^\s*(\[[^\]]*\]\s*)?(ERROR|Error|error|stream error|WARN)\b' -and $_ -match $LimitPattern } | Select-Object -Last 1
    if (-not $hit) { $hit = $tail | Where-Object { $_ -match '(?i)(you.ve hit your usage limit|usage limit reached|rate limit reached|429 Too Many Requests)' } | Select-Object -Last 1 }
    $hit
}
function Get-ActivityStamp {
    $t = @()
    if (Test-Path -LiteralPath $Log) { $t += (Get-Item -LiteralPath $Log).LastWriteTime }
    if (Test-Path -LiteralPath $LastMessage) { $t += (Get-Item -LiteralPath $LastMessage).LastWriteTime }
    $s = Get-ChildItem -LiteralPath $sessions -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($s) { $t += (Get-ChildItem -LiteralPath $s.FullName -File -Recurse -ErrorAction SilentlyContinue | Measure-Object LastWriteTime -Maximum).Maximum }
    ($t | Where-Object { $_ } | Measure-Object -Maximum).Maximum
}

$script:start = Get-Date -Format o; $script:t0 = $null
if (-not (Test-Path -LiteralPath $PromptFile)) { Finish 2 "prompt not found: $PromptFile" }
$first = (Get-Content -LiteralPath $PromptFile -TotalCount 5 | Where-Object { $_.Trim() } | Select-Object -First 1)
if ($first -notmatch '^\s*#\s*HARD RULES') { Finish 2 'prompt does not start with the # HARD RULES block' }
$codex = Get-Command codex -ErrorAction SilentlyContinue
if (-not $codex) { Finish 3 'codex not on PATH' }
$pf = (Resolve-Path -LiteralPath $PromptFile).Path
$cmd = "Get-Content -Raw '$pf' | codex exec --dangerously-bypass-approvals-and-sandbox -C '$Repo' --color never -o '$LastMessage' - *> '$Log'; exit `$LASTEXITCODE"
if ($NoLaunch) { Finish 0 'NoLaunch: validated' @{ command = $cmd; ttlMinutes = $TtlMinutes; idleMinutes = $IdleMinutes; codex = $codex.Source } }

$script:t0 = Get-Date
Write-Log "Launching codex run $RunId (TTL ${TtlMinutes}m, idle ${IdleMinutes}m): $cmd"
$p = Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd -WorkingDirectory $Repo -WindowStyle Hidden -PassThru
$null = $p.Handle
$deadline = $script:t0.AddMinutes($TtlMinutes); $lastAct = Get-Date; $lastStamp = $null; $breach = $null
while (-not $p.WaitForExit(5000)) {
    $stamp = Get-ActivityStamp
    if ($stamp -ne $lastStamp) { $lastStamp = $stamp; $lastAct = Get-Date }
    $lim = Get-LimitMessage
    if ($lim) { $breach = @{ code = 20; reason = 'usage/rate limit'; message = $lim.Trim() }; break }
    if ((Get-Date) -ge $deadline) { $breach = @{ code = 10; reason = "TTL ${TtlMinutes}m exceeded" }; break }
    if (((Get-Date) - $lastAct).TotalMinutes -ge $IdleMinutes) { $breach = @{ code = 11; reason = "no output for ${IdleMinutes}m" }; break }
}
if ($breach) {
    Write-Log "$($breach.reason); killing process tree $($p.Id)" 'WARN'
    Stop-ProcessTree $p.Id
    try { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Stop-CaptureSession.ps1') -KillClient *> "$Log.stopcapture.log" } catch {}
    $ex = @{ pid = $p.Id }; if ($breach.message) { $ex.limitMessage = $breach.message; $ex.limitTime = (Get-Date -Format o) }
    Finish $breach.code $breach.reason $ex
}
$code = $p.ExitCode
$lim = Get-LimitMessage
if ($lim) { Finish 20 'usage/rate limit' @{ codexExit = $code; limitMessage = $lim.Trim(); limitTime = (Get-Date -Format o) } }
if ($code -eq 0) { Finish 0 'ok' @{ codexExit = 0 } } else { Finish 1 "codex exited $code" @{ codexExit = $code } }