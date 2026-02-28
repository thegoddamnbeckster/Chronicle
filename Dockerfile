# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /source

# Copy project files first — these change less often than source, so Docker
# can cache the restore layer efficiently.
COPY src/Chronicle.Core/Chronicle.Core.csproj              src/Chronicle.Core/
COPY src/Chronicle.Data/Chronicle.Data.csproj              src/Chronicle.Data/
COPY src/Chronicle.Services/Chronicle.Services.csproj      src/Chronicle.Services/
COPY src/Chronicle.API/Chronicle.API.csproj                src/Chronicle.API/
COPY src/Chronicle.Plugins/Chronicle.Plugins.csproj        src/Chronicle.Plugins/

RUN dotnet restore src/Chronicle.API/Chronicle.API.csproj

# Copy full source and publish a self-contained-friendly, trimmed release binary
COPY src/ src/

RUN dotnet publish src/Chronicle.API/Chronicle.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# Create a non-root user; all Chronicle files will be owned by it.
RUN addgroup -S chronicle && adduser -S chronicle -G chronicle

# Persistent directories — override with Docker volumes in production.
RUN mkdir -p /app/plugins /app/logs \
    && chown -R chronicle:chronicle /app

COPY --from=build /app/publish .
RUN chown -R chronicle:chronicle /app

USER chronicle

# Kestrel configuration
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "Chronicle.API.dll"]
