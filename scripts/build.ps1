param(
    [string]$Solution = "ThDarkOrbitEmulator.sln"
)

$ErrorActionPreference = "Stop"

Write-Host "Building $Solution ..."
dotnet build $Solution --configuration Debug --nologo
