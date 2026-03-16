#!/usr/bin/env bash
set -euo pipefail

solution="${1:-ThDarkOrbitEmulator.sln}"

echo "Running lint checks for ${solution} ..."
dotnet format "${solution}" --verify-no-changes --verbosity minimal
