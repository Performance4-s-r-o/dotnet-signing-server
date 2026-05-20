#!/bin/sh
set -e

# Match the runtime Sentry release to whatever the build stage stamped at
# image build time. Sentry.AspNetCore reads Sentry:Release via the config
# section (Sentry__Release env var maps to it).
if [ -s /app/sentry-release ]; then
    Sentry__Release="$(cat /app/sentry-release)"
    export Sentry__Release
    echo "[entrypoint] Sentry release = ${Sentry__Release}"
fi

exec dotnet dotnet-signing-server.dll
