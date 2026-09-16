#!/usr/bin/env sh
# Runs the cross-SDK conformance matrix for this SDK. The spec repository defaults to a
# sibling checkout; override with TRICOREDB_SDK_SPEC.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
spec="${TRICOREDB_SDK_SPEC:-$root/../tricoredb-sdk-spec}"
node "$spec/conformance/matrix.js" dotnet
