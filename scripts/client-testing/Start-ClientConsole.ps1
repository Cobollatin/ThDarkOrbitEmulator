[CmdletBinding()]
param([string]$ClientRoot = 'D:\Games\DarkOrbit Unity')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exe = Join-Path $ClientRoot 'DarkOrbit.exe'
$log = Join-Path $ClientRoot 'unity-console.log'
if (-not (Test-Path -LiteralPath $exe)) { throw "Client missing: $exe" }

# Unity's child processes inherit stdout handles, so a console pipeline can wait
# for them after DarkOrbit.exe exits. Tail Unity's file log instead.
[System.IO.File]::WriteAllText($log, '')
$proc = $null
$stream = $null
$reader = $null
$pending = ''

function Write-NewLogLines {
    $script:pending += $script:reader.ReadToEnd()
    $lines = [regex]::Split($script:pending, "`r`n|`n|`r")
    for ($i = 0; $i -lt $lines.Length - 1; $i++) {
        Write-Host $lines[$i]
    }
    $script:pending = $lines[-1]
}

try {
    $stream = [System.IO.FileStream]::new($log, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
    $proc = Start-Process -FilePath $exe -ArgumentList @('-logFile', ('"{0}"' -f $log)) `
        -WorkingDirectory $ClientRoot -PassThru

    while (-not $proc.HasExited) {
        Write-NewLogLines
        Start-Sleep -Milliseconds 100
    }
}
finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        try { $proc.Kill($true) }
        catch [System.InvalidOperationException] { } # Exited between HasExited and Kill.
    }
    if ($null -ne $reader) {
        Write-NewLogLines
        if ($pending.Length -gt 0) { Write-Host $pending }
        $reader.Dispose()
    }
    elseif ($null -ne $stream) {
        $stream.Dispose()
    }
}

if ($proc.ExitCode -ne 0) { throw "DarkOrbit.exe exited with code $($proc.ExitCode)" }
