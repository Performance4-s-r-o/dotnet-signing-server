# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:f7e28a0a3f6f9235929ec19274fd0ddf8534e9159f99fac602ac73304f1ce99f AS build
WORKDIR /src

# Copy csproj and restore as distinct layers to leverage Docker cache
# The Release lock file pins every package (#84): a restore that would change one fails the build.
COPY ["dotnet-signing-server.csproj", "nuget.config", "packages.release.lock.json", "./"]
RUN dotnet restore "./dotnet-signing-server.csproj" -p:Configuration=Release --locked-mode

# Copy the rest of the application source code
COPY . .

# Sentry release = YYYY-MM-DD-<short-sha>. Computed in the build stage,
# persisted to a file that the runtime stage copies and the entrypoint
# exports as Sentry__Release at startup. Empty SHA (local builds) leaves
# the file empty and Sentry falls back to its default release detection.
ARG SENTRY_RELEASE_SHA=
RUN if [ -n "$SENTRY_RELEASE_SHA" ]; then \
        printf '%s-%s' "$(date -u +%Y-%m-%d)" "$(printf '%.7s' "$SENTRY_RELEASE_SHA")" > /sentry-release; \
        echo "[sentry] release = $(cat /sentry-release)"; \
    else \
        : > /sentry-release; \
    fi

# Build and publish the application
RUN dotnet build "dotnet-signing-server.csproj" -c Release --no-restore -o /app/build
RUN dotnet publish "dotnet-signing-server.csproj" -c Release --no-restore -o /app/publish /p:UseAppHost=false

# Stage 2: Create the final, smaller runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:ea721098d6ea2894ff9bd8387821a1659d4765d6c014b8d648bb1da16f016f5a AS final
WORKDIR /app

ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV DATA_PROTECTION_KEYS_PATH=/app/data-protection-keys

# Ghostscript is pinned (#84): it renders attacker-supplied PDF/PS and has a history of critical CVEs,
# so which version runs must be a decision, not whatever apt has today. When Debian ships a newer
# build the old one disappears from the archive and this build fails on purpose: bump the version
# (check https://security-tracker.debian.org/tracker/source-package/ghostscript) in the same change.
ARG GS_VERSION=10.0.0~dfsg-11+deb12u8
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        "ghostscript=${GS_VERSION}" "libgs10=${GS_VERSION}" "libgs10-common=${GS_VERSION}" "libgs-common=${GS_VERSION}" curl \
    || { echo "GS_VERSION ${GS_VERSION} is no longer in the Debian archive: bump it (security-tracker.debian.org/tracker/source-package/ghostscript), see #84" >&2; exit 1; } && \
    rm -rf /var/lib/apt/lists/* && \
    gs --version

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser -d /app appuser

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Carry the build-stage release file and shell entrypoint that exports it.
COPY --from=build /sentry-release /app/sentry-release
COPY --from=build /src/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

# Create directories and set ownership
RUN mkdir -p /app/data-protection-keys && \
    chown -R appuser:appuser /app

USER appuser

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8085/health || exit 1

# Shell entrypoint reads /app/sentry-release and exports Sentry__Release
# before launching the app, so the runtime SDK reports under the same
# release name the image was built with.
ENTRYPOINT ["/app/docker-entrypoint.sh"]
