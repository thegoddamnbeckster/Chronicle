# ── Stage 1: Build the API ──────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build-api
WORKDIR /source

# Copy project files first — these change less often than source, so Docker
# can cache the restore layer efficiently.
COPY src/Chronicle.Core/Chronicle.Core.csproj              src/Chronicle.Core/
COPY src/Chronicle.Data/Chronicle.Data.csproj              src/Chronicle.Data/
COPY src/Chronicle.Services/Chronicle.Services.csproj      src/Chronicle.Services/
COPY src/Chronicle.API/Chronicle.API.csproj                src/Chronicle.API/
COPY src/Chronicle.Plugins/Chronicle.Plugins.csproj         src/Chronicle.Plugins/

RUN dotnet restore src/Chronicle.API/Chronicle.API.csproj

# Copy full source and publish a self-contained-friendly, trimmed release binary
COPY src/ src/

RUN dotnet publish src/Chronicle.API/Chronicle.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ── Stage 2: Build the web frontend ─────────────────────────────────────────────
# Built separately from the API (own base image, own cache layer) and only its static
# `dist/` output is carried into the runtime image -- Program.cs serves it directly from
# wwwroot (see its own "Serve the built React frontend" comment), so production is a single
# container on a single port with no separate nginx/reverse-proxy container needed.
FROM node:22-alpine AS build-web
WORKDIR /source
COPY src/Chronicle.Web/package.json src/Chronicle.Web/package-lock.json ./
RUN npm ci
COPY src/Chronicle.Web/ ./
# ports.json is deliberately NOT copied in here -- vite.config.ts only reads it for the DEV
# server's own listen port and proxy target, neither of which affects `vite build`'s static
# output. The built app calls the API via relative /api/... URLs, correct on whatever port the
# single production container ends up listening on.
RUN npm run build

# ── Stage 3: Runtime ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# Create a non-root user; all Chronicle files will be owned by it.
RUN addgroup -S chronicle && adduser -S chronicle -G chronicle

# Everything this container needs to survive being recreated (an image update, not just a
# restart) lives under bind-mounted /app subdirectories -- see docker-compose.yml's volumes.
# Without a volume, /app/keys (Data Protection, which encrypts every plugin's stored
# credentials) and /app/data (the SQLite database, when not overridden to point at PostgreSQL)
# would silently reset on every `docker compose up` after a rebuild -- breaking every
# configured plugin's saved settings and losing the entire library, all at once. (Browser
# sessions are deliberately NOT persisted: a container restart signs everyone out.)
RUN mkdir -p /app/plugins /app/logs /app/keys /app/data \
    && chown -R chronicle:chronicle /app

COPY --from=build-api /app/publish .
COPY --from=build-web /source/dist ./wwwroot
RUN chown -R chronicle:chronicle /app

USER chronicle

ENV ASPNETCORE_ENVIRONMENT=Production
# The actual listen port is resolved by PortManager (env var, then ports.json, then Chronicle's
# own 7979 default) -- see Program.cs. Setting it here too keeps EXPOSE accurate and makes the
# port this image listens on self-documenting without needing to read the source to find out.
ENV CHRONICLE_API_PORT=7979

EXPOSE 7979

ENTRYPOINT ["dotnet", "Chronicle.API.dll"]
