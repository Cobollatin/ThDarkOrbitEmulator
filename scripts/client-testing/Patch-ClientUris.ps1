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
    the original global-metadata.dat SHA-256 against the known unmodified builds
    (KNOWN_ORIG_METADATA_SHA256 in patch_il2cpp_uris.py). Target literals are
    located by their original value, so per-build index differences are handled.

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
if ($Action -ne "Status") {
    $clientExe = Join-Path ([System.IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')) 'DarkOrbit.exe'
    $running = Get-Process -Name DarkOrbit -ErrorAction SilentlyContinue |
        Where-Object { -not $_.Path -or $_.Path -ieq $clientExe }
    if ($running) {
        throw "DarkOrbit.exe is running. Close the game before $Action, then retry."
    }
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
