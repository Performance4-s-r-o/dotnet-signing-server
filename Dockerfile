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
# COPY .env /app/.env
WORKDIR /app

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Define the entry point for the container
ENTRYPOINT ["dotnet", "dotnet-signing-server.dll"]
