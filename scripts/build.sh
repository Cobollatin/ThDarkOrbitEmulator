#!/usr/bin/env bash
set -euo pipefail

solution="${1:-ThDarkOrbitEmulator.sln}"

echo "Building ${solution} ..."
dotnet build "${solution}" --configuration Debug --nologo
