# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers to leverage Docker cache
COPY ["dotnet-signing-server.csproj", "."]
RUN dotnet restore "./dotnet-signing-server.csproj"

# Copy the rest of the application source code
COPY . .

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

# Create directories and set ownership
RUN mkdir -p /app/data-protection-keys && \
    chown -R appuser:appuser /app

USER appuser

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8085/health || exit 1

# Define the entry point for the container
ENTRYPOINT ["dotnet", "dotnet-signing-server.dll"]
