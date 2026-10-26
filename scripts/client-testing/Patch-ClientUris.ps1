<#
.SYNOPSIS
    Inspects, patches, or resets hardcoded URIs in the DarkOrbit Unity client
    (global-metadata.dat IL2CPP string literals, StreamingAssets\config.txt, and update.ini).

.DESCRIPTION
    Uses IL2CPP v31 stringLiteral descriptor table patching so replacement strings
    (such as https://localhost:60952/) update the descriptor length in-place while
    zero-padding unused bytes in stringLiteralData. File size and all offsets in
    global-metadata.dat remain 100% unchanged.

    Creates automatic .orig.bak backups before modifying any file and verifies
    the original global-metadata.dat SHA-256 hash on backup and reset:
    a9df074bb27ea58dbd19f322a6c6b45438fe198c74cc2f78a1d33f29604e3055

.EXAMPLE
    .\scripts\client-testing\Patch-ClientUris.ps1 -Action Status
    .\scripts\client-testing\Patch-ClientUris.ps1 -Action Apply -TargetBaseUrl "https://localhost:60952"
    .\scripts\client-testing\Patch-ClientUris.ps1 -Action Apply -TargetBaseUrl "https://localhost:60952" -PatchCdn
    .\scripts\client-testing\Patch-ClientUris.ps1 -Action Reset
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet("Status", "Apply", "Reset")]
    [string]$Action = "Status",

    [Parameter()]
    [string]$TargetBaseUrl = "https://localhost:60952",

    [Parameter()]
    [string]$ClientRoot = "D:\Games\DarkOrbit Unity",

    [Parameter()]
    [switch]$PatchCdn,

    [Parameter()]
    [switch]$AsJson,

    [Parameter()]
    [string]$FixtureOriginalSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
$venvPython = Join-Path $repoRoot ".venv\Scripts\python.exe"
$pythonHelper = Join-Path $scriptDir "patch_il2cpp_uris.py"

# Windows keeps the running player's metadata and executable open. Fail before
# touching any patch target so a live session is never left partly updated.
if ($Action -ne "Status" -and
    [System.IO.Path]::GetFullPath($ClientRoot).TrimEnd('\') -ieq 'D:\Games\DarkOrbit Unity' -and
    (Get-Process -Name DarkOrbit -ErrorAction SilentlyContinue)) {
    throw "DarkOrbit.exe is running. Close the game before $Action, then retry."
}

if (-not (Test-Path -LiteralPath $pythonHelper)) {
    throw "Python helper script not found at: $pythonHelper"
}

$pythonExe = if (Test-Path -LiteralPath $venvPython) { $venvPython } else { "python" }

$pyArgs = @(
    $pythonHelper,
    "--action", $Action.ToLowerInvariant(),
    "--client-root", $ClientRoot,
    "--target-base-url", $TargetBaseUrl
)

if ($PatchCdn.IsPresent) {
    $pyArgs += "--patch-cdn"
}

if ($AsJson.IsPresent) {
    $pyArgs += "--json"
}
if ($FixtureOriginalSha256) {
    $pyArgs += @("--fixture-original-sha256", $FixtureOriginalSha256)
}

& $pythonExe @pyArgs
if ($LASTEXITCODE -ne 0) {
    throw "patch_il2cpp_uris.py failed with exit code $LASTEXITCODE"
}
