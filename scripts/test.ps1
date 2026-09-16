# Runs the xUnit suite. Live tests start a private tricore-server; set TRICORE_SERVER_BIN
# to its path, or pass -Offline to run only the tests that need no server.
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/TriCoreDb.Tests/TriCoreDb.Tests.csproj'
if ($Offline) {
    dotnet test $project -c Release --filter 'Category!=Live'
} else {
    dotnet test $project -c Release
}
exit $LASTEXITCODE
