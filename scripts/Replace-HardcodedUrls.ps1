<#
.SYNOPSIS
Replaces hardcoded URLs in binary or text files, with rollback support.

.DESCRIPTION
The script patches ASCII and UTF-16LE URL byte sequences in place. It writes
backups and a manifest before changing files so changes can be rolled back.
Longer replacements are refused because they would shift binary data.

.PARAMETER Mode
Apply replaces mapped URLs. Rollback restores files from a manifest.

.PARAMETER DryRun
Preview work without mutating files. In Apply mode this writes a
.dry-run.manifest.json. In Rollback mode it validates restore operations.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Replace-HardcodedUrls.ps1 -Help

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Replace-HardcodedUrls.ps1 -Mode Apply -TargetPath 'D:\Games\DarkOrbit Unity' -MappingPath .\url-replacements.json -ScanReportPath .\hardcoded-url-scan.jsonl -DryRun

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Replace-HardcodedUrls.ps1 -Mode Rollback -ManifestPath .\url-replacement-YYYYMMDD-HHMMSS.manifest.json
#>
param(
    [Parameter()]
    [ValidateSet("Apply", "Rollback")]
    [string]$Mode = "Apply",

    [Parameter()]
    [string]$TargetPath = "D:\Games\DarkOrbit Unity",

    [Parameter()]
    [string]$MappingPath,

    [Parameter()]
    [string]$ScanReportPath,

    [Parameter()]
    [string]$OutputDirectory = (Get-Location).Path,

    [Parameter()]
    [string]$ManifestPath,

    [Parameter()]
    [switch]$AllowShorterBinaryReplacement,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [switch]$WhatIfOnly
    ,

    [Parameter()]
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Show-Help {
    $scriptName = Split-Path -Leaf $PSCommandPath
    @"
Replace hardcoded URLs in binary or text files, with rollback support.

USAGE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\$scriptName -Help

  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\$scriptName `
    -Mode Apply `
    -TargetPath 'D:\Games\DarkOrbit Unity' `
    -MappingPath .\url-replacements.json `
    -ScanReportPath .\hardcoded-url-scan.jsonl `
    -OutputDirectory .

  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\$scriptName `
    -Mode Rollback `
    -ManifestPath .\url-replacement-YYYYMMDD-HHMMSS.manifest.json

MODES
  Apply
    Replaces mapped URLs and writes a rollback manifest.

  Rollback
    Restores files from backups recorded in a manifest.

OPTIONS
  -Help
    Prints this help text.

  -DryRun
    Preview work without mutating files.
    In Apply mode, writes a .dry-run.manifest.json and does not patch files.
    In Rollback mode, validates restore operations and does not copy backups.

  -WhatIfOnly
    Deprecated alias for -DryRun. Kept so older commands still work.

  -AllowShorterBinaryReplacement
    Allows shorter replacement strings by padding trailing bytes with nulls.
    Longer replacements are always refused because they would shift binary data.

MAPPING FILE
  JSON array:
  [
    {
      "from": "https://darkorbit-22.bpsecure.com",
      "to":   "https://localhost.example.test"
    }
  ]

  old/new are also accepted instead of from/to.

ROLLBACK MEMORY
  Apply mode writes:
    .url-replacement-backups\RUN_ID\...
    url-replacement-RUN_ID.manifest.json

  The manifest records files, offsets, original bytes, patched bytes, hashes,
  backup paths, and skipped replacements. Rollback uses this manifest and
  refuses to restore if a patched file changed after Apply mode.
"@
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Convert-Hex {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes
    )

    return [Convert]::ToHexString($Bytes).ToLowerInvariant()
}

function ConvertFrom-Hex {
    param(
        [Parameter(Mandatory)]
        [string]$Hex
    )

    if (($Hex.Length % 2) -ne 0) {
        throw "Invalid hex string length."
    }

    $bytes = [byte[]]::new($Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($Hex.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Get-Bytes {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [ValidateSet("ascii", "utf-16le")]
        [string]$EncodingName
    )

    if ($EncodingName -eq "utf-16le") {
        return [Text.Encoding]::Unicode.GetBytes($Value)
    }

    return [Text.Encoding]::ASCII.GetBytes($Value)
}

function Find-BytePattern {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [byte[]]$Pattern
    )

    if ($Pattern.Length -eq 0 -or $Bytes.Length -lt $Pattern.Length) {
        return
    }

    for ($i = 0; $i -le ($Bytes.Length - $Pattern.Length); $i++) {
        $matched = $true
        for ($j = 0; $j -lt $Pattern.Length; $j++) {
            if ($Bytes[$i + $j] -ne $Pattern[$j]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            $i
            $i += $Pattern.Length - 1
        }
    }
}

function Read-Mappings {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $json = $raw | ConvertFrom-Json
    $rows = if ($json -is [array]) { $json } elseif ($json.PSObject.Properties.Name -contains "mappings") { $json.mappings } else { $json }
    $mappings = [Collections.Generic.List[object]]::new()

    foreach ($row in $rows) {
        $from = if ($row.PSObject.Properties.Name -contains "from") { [string]$row.from } else { [string]$row.old }
        $to = if ($row.PSObject.Properties.Name -contains "to") { [string]$row.to } else { [string]$row.new }

        if ([string]::IsNullOrWhiteSpace($from) -or [string]::IsNullOrWhiteSpace($to)) {
            throw "Each mapping must include from/to or old/new properties."
        }

        $mappings.Add([pscustomobject]@{
            from = $from
            to   = $to
        }) | Out-Null
    }

    return $mappings
}

function Read-ScanReport {
    param(
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return @()
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Scan report not found: $Path"
    }

    $hits = [Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $hit = $line | ConvertFrom-Json
        if (($hit.PSObject.Properties.Name -contains "kind") -and $hit.kind -like "base64-*") {
            continue
        }

        if ($hit.PSObject.Properties.Name -contains "file" -and $hit.PSObject.Properties.Name -contains "url") {
            $hits.Add($hit) | Out-Null
        }
    }

    return $hits
}

function Get-CandidateFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [object[]]$ScanHits,

        [Parameter(Mandatory)]
        [object[]]$Mappings
    )

    $mappedUrls = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($mapping in $Mappings) {
        $mappedUrls.Add($mapping.from) | Out-Null
    }

    $fromScan = @(
        $ScanHits |
            Where-Object { $mappedUrls.Contains([string]$_.url) } |
            Select-Object -ExpandProperty file -Unique
    )

    if ($fromScan.Count -gt 0) {
        return @($fromScan | Where-Object { Test-Path -LiteralPath $_ })
    }

    return @(Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Select-Object -ExpandProperty FullName)
}

function New-ManifestSkeleton {
    param(
        [Parameter(Mandatory)]
        [string]$TargetPath,

        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [object[]]$Mappings,

        [Parameter(Mandatory)]
        [bool]$AllowShorterBinaryReplacement
    )

    $runId = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupRoot = Join-Path $OutputDirectory ".url-replacement-backups\$runId"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

    return [pscustomobject]@{
        version                       = 1
        run_id                        = $runId
        created_utc                   = (Get-Date).ToUniversalTime().ToString("o")
        target_path                   = $TargetPath
        backup_root                   = $backupRoot
        allow_shorter_binary_replace  = $AllowShorterBinaryReplacement
        mappings                      = @($Mappings)
        files                         = @()
        skipped                       = @()
    }
}

function Save-Manifest {
    param(
        [Parameter(Mandatory)]
        [object]$Manifest,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Apply-Replacements {
    param(
        [Parameter(Mandatory)]
        [string]$TargetPath,

        [Parameter(Mandatory)]
        [object[]]$Mappings,

        [object[]]$ScanHits,

        [Parameter(Mandatory)]
        [string]$OutputDirectory,

        [Parameter(Mandatory)]
        [bool]$AllowShorterBinaryReplacement,

        [Parameter(Mandatory)]
        [bool]$WhatIfOnly
    )

    $resolvedTarget = (Resolve-Path -LiteralPath $TargetPath).Path
    $resolvedOutput = if (Test-Path -LiteralPath $OutputDirectory) {
        (Resolve-Path -LiteralPath $OutputDirectory).Path
    }
    else {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
        (Resolve-Path -LiteralPath $OutputDirectory).Path
    }

    $manifest = New-ManifestSkeleton `
        -TargetPath $resolvedTarget `
        -OutputDirectory $resolvedOutput `
        -Mappings $Mappings `
        -AllowShorterBinaryReplacement $AllowShorterBinaryReplacement

    $manifestPath = Join-Path $resolvedOutput "url-replacement-$($manifest.run_id).manifest.json"
    $candidateFiles = Get-CandidateFiles -Root $resolvedTarget -ScanHits $ScanHits -Mappings $Mappings

    foreach ($filePath in $candidateFiles) {
        $bytes = [IO.File]::ReadAllBytes($filePath)
        $fileChanges = [Collections.Generic.List[object]]::new()

        foreach ($mapping in $Mappings) {
            foreach ($encodingName in @("ascii", "utf-16le")) {
                $oldBytes = Get-Bytes -Value $mapping.from -EncodingName $encodingName
                $newBytesRaw = Get-Bytes -Value $mapping.to -EncodingName $encodingName

                if ($newBytesRaw.Length -gt $oldBytes.Length) {
                    $manifest.skipped += [pscustomobject]@{
                        file     = $filePath
                        from     = $mapping.from
                        to       = $mapping.to
                        encoding = $encodingName
                        reason   = "Replacement is longer than original byte sequence. Binary patching would shift file contents."
                    }
                    continue
                }

                if ($newBytesRaw.Length -lt $oldBytes.Length -and -not $AllowShorterBinaryReplacement) {
                    $manifest.skipped += [pscustomobject]@{
                        file     = $filePath
                        from     = $mapping.from
                        to       = $mapping.to
                        encoding = $encodingName
                        reason   = "Replacement is shorter. Re-run with -AllowShorterBinaryReplacement to pad trailing bytes with nulls."
                    }
                    continue
                }

                $newBytes = [byte[]]::new($oldBytes.Length)
                [Array]::Copy($newBytesRaw, 0, $newBytes, 0, $newBytesRaw.Length)

                foreach ($offset in Find-BytePattern -Bytes $bytes -Pattern $oldBytes) {
                    $before = [byte[]]::new($oldBytes.Length)
                    [Array]::Copy($bytes, $offset, $before, 0, $oldBytes.Length)
                    [Array]::Copy($newBytes, 0, $bytes, $offset, $newBytes.Length)

                    $fileChanges.Add([pscustomobject]@{
                        offset       = $offset
                        encoding     = $encodingName
                        from         = $mapping.from
                        to           = $mapping.to
                        original_hex = Convert-Hex -Bytes $before
                        patched_hex  = Convert-Hex -Bytes $newBytes
                    }) | Out-Null
                }
            }
        }

        if ($fileChanges.Count -eq 0) {
            continue
        }

        $relativeName = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($filePath)).TrimEnd("=") -replace "\+", "-" -replace "/", "_"
        $backupPath = Join-Path $manifest.backup_root "$relativeName.bin"

        $fileEntry = [pscustomobject]@{
            file               = $filePath
            backup             = $backupPath
            original_sha256    = Get-Sha256 -Path $filePath
            patched_sha256     = ""
            changes            = @($fileChanges)
        }

        if (-not $WhatIfOnly) {
            Copy-Item -LiteralPath $filePath -Destination $backupPath -Force
            [IO.File]::WriteAllBytes($filePath, $bytes)
            $fileEntry.patched_sha256 = Get-Sha256 -Path $filePath
        }

        $manifest.files += $fileEntry
    }

    if ($WhatIfOnly) {
        $manifestPath = Join-Path $resolvedOutput "url-replacement-$($manifest.run_id).dry-run.manifest.json"
    }

    Save-Manifest -Manifest $manifest -Path $manifestPath
    return $manifestPath
}

function Restore-FromManifest {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [bool]$WhatIfOnly
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $restored = [Collections.Generic.List[object]]::new()

    foreach ($fileEntry in $manifest.files) {
        if (-not (Test-Path -LiteralPath $fileEntry.backup)) {
            throw "Backup missing for '$($fileEntry.file)': $($fileEntry.backup)"
        }

        if ((Test-Path -LiteralPath $fileEntry.file) -and $fileEntry.patched_sha256) {
            $currentHash = Get-Sha256 -Path $fileEntry.file
            if ($currentHash -ne $fileEntry.patched_sha256) {
                throw "Refusing rollback because file changed after patch: $($fileEntry.file)"
            }
        }

        if (-not $WhatIfOnly) {
            Copy-Item -LiteralPath $fileEntry.backup -Destination $fileEntry.file -Force
        }

        $restored.Add([pscustomobject]@{
            file            = $fileEntry.file
            backup          = $fileEntry.backup
            original_sha256 = $fileEntry.original_sha256
        }) | Out-Null
    }

    return $restored
}

$isDryRun = [bool]$DryRun -or [bool]$WhatIfOnly

if ($Help) {
    Show-Help
    return
}

if ($Mode -eq "Apply") {
    if ([string]::IsNullOrWhiteSpace($MappingPath)) {
        throw "MappingPath is required for Apply mode. Provide JSON like: [{`"from`":`"https://old/`",`"to`":`"https://new/`"}]"
    }

    $mappings = Read-Mappings -Path $MappingPath
    $scanHits = Read-ScanReport -Path $ScanReportPath
    $manifest = Apply-Replacements `
        -TargetPath $TargetPath `
        -Mappings $mappings `
        -ScanHits $scanHits `
        -OutputDirectory $OutputDirectory `
        -AllowShorterBinaryReplacement ([bool]$AllowShorterBinaryReplacement) `
        -WhatIfOnly $isDryRun

    Write-Host "Manifest: $manifest"
    Write-Host "Rollback command:"
    Write-Host "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Replace-HardcodedUrls.ps1 -Mode Rollback -ManifestPath '$manifest'"
}
else {
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        throw "ManifestPath is required for Rollback mode."
    }

    $restored = Restore-FromManifest -ManifestPath $ManifestPath -WhatIfOnly $isDryRun
    Write-Host "Rollback entries: $($restored.Count)"
    foreach ($entry in $restored) {
        Write-Host "Restored: $($entry.file)"
    }
}
