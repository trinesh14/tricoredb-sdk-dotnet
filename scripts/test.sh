#!/usr/bin/env sh
# Runs the xUnit suite. Live tests start a private tricore-server; set TRICORE_SERVER_BIN
# to its path, or pass --offline to run only the tests that need no server.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/tests/TriCoreDb.Tests/TriCoreDb.Tests.csproj"
if [ "${1:-}" = "--offline" ]; then
  dotnet test "$project" -c Release --filter 'Category!=Live'
else
  dotnet test "$project" -c Release
fi
