# Runs the cross-SDK conformance matrix for this SDK. The spec repository defaults to a
# sibling checkout; override with TRICOREDB_SDK_SPEC.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$spec = $env:TRICOREDB_SDK_SPEC
if (-not $spec) { $spec = Join-Path (Split-Path -Parent $root) 'tricoredb-sdk-spec' }
node (Join-Path $spec 'conformance/matrix.js') dotnet
exit $LASTEXITCODE
