#!/usr/bin/env bash
# Regenerates all NuGet lock files (Debug, Release, tests) and checks them in locked mode.
set -euo pipefail

cd "$(dirname "$0")/.."

# Debug lock files: the solution (main project and tests).
dotnet restore dotnet-signing-server.sln --force-evaluate

# Release lock file: what the production image ships (no Testcontainers).
dotnet restore dotnet-signing-server.csproj -p:Configuration=Release --force-evaluate

# Both must now pass exactly as CI and the Dockerfile run them.
dotnet restore dotnet-signing-server.sln --locked-mode
dotnet restore dotnet-signing-server.csproj -p:Configuration=Release --locked-mode

git status --short -- '*.lock.json'
