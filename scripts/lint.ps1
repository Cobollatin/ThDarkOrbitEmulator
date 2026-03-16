param(
    [string]$Solution = "ThDarkOrbitEmulator.sln"
)

$ErrorActionPreference = "Stop"

Write-Host "Running lint checks for $Solution ..."
dotnet format $Solution --verify-no-changes --verbosity minimal
