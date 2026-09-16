#!/usr/bin/env sh
# Produces TriCoreDb.<version>.nupkg and .snupkg in ./artifacts. Does not publish.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet pack "$root/src/TriCoreDb/TriCoreDb.csproj" -c Release -o "$root/artifacts" -p:ContinuousIntegrationBuild=true
