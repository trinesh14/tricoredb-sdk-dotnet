$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'TriCoreDb.sln') -c Release
exit $LASTEXITCODE
