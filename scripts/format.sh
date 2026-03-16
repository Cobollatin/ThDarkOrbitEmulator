#!/usr/bin/env bash
set -euo pipefail

solution="${1:-ThDarkOrbitEmulator.sln}"

echo "Formatting ${solution} ..."
dotnet format "${solution}" --verbosity minimal
