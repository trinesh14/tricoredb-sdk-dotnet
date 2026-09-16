# Produces TriCoreDb.<version>.nupkg and .snupkg in ./artifacts. Does not publish.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
dotnet pack (Join-Path $root 'src/TriCoreDb/TriCoreDb.csproj') -c Release -o $out -p:ContinuousIntegrationBuild=true
exit $LASTEXITCODE
