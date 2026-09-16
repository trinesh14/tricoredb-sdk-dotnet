#!/usr/bin/env sh
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$root/TriCoreDb.sln" -c Release
