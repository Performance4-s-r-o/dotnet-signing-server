# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers to leverage Docker cache
COPY ["dotnet-signing-server.csproj", "."]
RUN dotnet restore "./dotnet-signing-server.csproj"

# Copy the rest of the application source code
COPY . .

# Sentry release = YYYY-MM-DD-<short-sha>. Computed in the build stage,
# persisted to a file that the runtime stage copies and the entrypoint
# exports as Sentry__Release at startup. Empty SHA (local builds) leaves
# the file empty and Sentry falls back to its default release detection.
ARG SENTRY_RELEASE_SHA=
RUN if [ -n "$SENTRY_RELEASE_SHA" ]; then \
        printf '%s-%s' "$(date -u +%Y-%m-%d)" "$SENTRY_RELEASE_SHA" > /sentry-release; \
        echo "[sentry] release = $(cat /sentry-release)"; \
    else \
        : > /sentry-release; \
    fi

# Build and publish the application
RUN dotnet build "dotnet-signing-server.csproj" -c Release -o /app/build
RUN dotnet publish "dotnet-signing-server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Create the final, smaller runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV DOTNET_USE_POLLING_FILE_WATCHER=1
ENV DATA_PROTECTION_KEYS_PATH=/app/data-protection-keys

RUN apt-get update && \
    apt-get install -y --no-install-recommends ghostscript curl && \
    rm -rf /var/lib/apt/lists/*

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
