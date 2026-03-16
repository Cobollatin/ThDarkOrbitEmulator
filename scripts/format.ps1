param(
    [string]$Solution = "ThDarkOrbitEmulator.sln"
)

$ErrorActionPreference = "Stop"

Write-Host "Formatting $Solution ..."
dotnet format $Solution --verbosity minimal
