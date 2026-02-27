# Phase 2: Core Features Implementation Plan

**Version:** 1.0
**Phase:** v0.6 - v1.0
**Timeline:** 4-5 months
**Target Completion:** Q2 2026

---

## Phase Objective

Transform the Phase 1 single-media tracker into a fully extensible, plugin-driven platform
supporting TV, Movies, and Music. Introduce the plugin system, structured logging, Windows
service support, and API key authentication for scrobblers.

**Target User:** Slightly above average computer user — comfortable installing software,
editing a config file if guided, but not a developer. Advanced options exist but are hidden
behind an explicit toggle.

**Success Criteria:**
- Chronicle runs as a Windows service with a configurable service account
- User can browse, install, and configure curated plugins from within the UI
- TMDB plugin provides metadata for TV shows and Movies
- MusicBrainz plugin provides metadata for Music
- External scrobblers can authenticate via API key
- Every plugin logs to its own folder under Chronicle's central log directory
- All settings pages have a "Show Advanced Settings" toggle for power-user options

---

## GitHub Repositories Required

Each plugin and the registry are separate repositories. Create these before Phase 2 begins:

| Repository | Purpose |
|---|---|
| `thegoddamnbeckster/Chronicle` | Main application (existing) |
| `thegoddamnbeckster/Chronicle-Registry` | Curated plugin registry |
| `thegoddamnbeckster/Chronicle.Plugin.TMDB` | TMDB metadata plugin |
| `thegoddamnbeckster/Chronicle.Plugin.MusicBrainz` | MusicBrainz metadata plugin |

---

## Implementation Sequence

---

### Step 1: Structured Logging (Serilog)

This step comes first because every subsequent step depends on reliable logging.

**1.1 Install Serilog**

In `Chronicle.API`:
```xml
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="Serilog.Sinks.File" Version="5.*" />
<PackageReference Include="Serilog.Sinks.Console" Version="4.*" />
<PackageReference Include="Serilog.Formatting.Compact" Version="2.*" />
```

**1.2 Log Directory Structure**

```
logs/
├── chronicle-20260601.log          ← Main rolling log (all components)
└── plugins/
    ├── chronicle.plugin.tmdb/
    │   └── chronicle.plugin.tmdb-20260601.log
    └── chronicle.plugin.musicbrainz/
        └── chronicle.plugin.musicbrainz-20260601.log
```

**1.3 Serilog Configuration**

Configure in `Program.cs` before `builder.Build()`:

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/chronicle-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();
```

**1.4 Per-Plugin Logger Factory**

When a plugin is loaded, create a dedicated logger that writes to both the main log and its
own folder. The plugin host creates this logger and injects it into the plugin:

```csharp
public ILogger CreatePluginLogger(string pluginId)
{
    return new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Logger(Log.Logger)   // Forward to main log
        .WriteTo.File(
            path: $"logs/plugins/{pluginId}/{pluginId}-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30)
        .CreateLogger()
        .ForContext("PluginId", pluginId);
}
```

**1.5 Request Logging Middleware**

Add Serilog request logging to capture method, path, status code, and duration for all
API calls:

```csharp
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
});
```

**1.6 Log Settings in appsettings.json**

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "RetainedLogDays": 30
  }
}
```

**Tasks:**
- [ ] Install Serilog NuGet packages
- [ ] Configure main rolling file sink
- [ ] Configure per-plugin logger factory
- [ ] Add request logging middleware
- [ ] Expose log level and retention as settings in appsettings.json
- [ ] Verify logs folder is gitignored (already is)
- [ ] Write unit tests for log path generation

**Deliverable:** Structured rolling logs in `logs/` with per-plugin sub-folders

---

### Step 2: Windows Service Support

Chronicle must run as a Windows service, exactly like Sonarr and Radarr. The service account
must be easy for a non-developer to configure.

**2.1 Add Windows Service Package**

In `Chronicle.API`:
```xml
<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="9.*" />
```

**2.2 Enable Service Hosting**

In `Program.cs`, add before `builder.Build()`:

```csharp
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "Chronicle";
});
```

The app now behaves as a normal console app when run directly and as a Windows service
when started by the Service Control Manager.

**2.3 Service Installer Script**

`scripts/install-service.ps1` — run once to register Chronicle as a Windows service:

```powershell
param(
    [string]$InstallPath = "C:\Chronicle",
    [string]$ServiceUser = "LocalService",
    [string]$ServicePassword = ""
)

$ServiceName    = "Chronicle"
$DisplayName    = "Chronicle Media Tracker"
$Description    = "Self-hosted universal media tracking platform."
$ExePath        = Join-Path $InstallPath "Chronicle.API.exe"

# Validate install path
if (-not (Test-Path $ExePath)) {
    Write-Error "Chronicle.API.exe not found at $ExePath. Run publish first."
    exit 1
}

# Remove existing service if present
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing Chronicle service..."
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create the service
$params = @{
    Name           = $ServiceName
    DisplayName    = $DisplayName
    Description    = $Description
    BinaryPathName = $ExePath
    StartupType    = "Automatic"
}

if ($ServiceUser -notin @("LocalSystem", "LocalService", "NetworkService")) {
    $params.Credential = New-Object System.Management.Automation.PSCredential(
        $ServiceUser,
        (ConvertTo-SecureString $ServicePassword -AsPlainText -Force)
    )
} else {
    sc.exe create $ServiceName binPath= $ExePath start= auto obj= "NT AUTHORITY\$ServiceUser"
}

New-Service @params -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Chronicle service installed successfully." -ForegroundColor Green
Write-Host "Service user: $ServiceUser"
Write-Host ""
Write-Host "To start: Start-Service Chronicle"
Write-Host "To open:  http://localhost:8080"
```

**2.4 Service Uninstaller Script**

`scripts/uninstall-service.ps1`:

```powershell
$ServiceName = "Chronicle"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName
    Write-Host "Chronicle service removed." -ForegroundColor Green
} else {
    Write-Host "Chronicle service not found."
}
```

**2.5 Service User Options**

Service user is configured during installation and can be changed from the UI.
Present three simple options (plus an Advanced toggle for custom accounts):

| Option | Who should use it |
|---|---|
| **Local Service** (default) | Most users. Limited network access. |
| **Network Service** | Needed if Chronicle accesses network shares. |
| **Local System** | Full local access. Use only if other options fail. |
| **Custom account** *(advanced)* | Power users running Chronicle as a domain user. |

**2.6 Changing Service User from the UI**

Settings → Service → "Change Service Account":
1. User selects new account type from the simple radio buttons
2. Chronicle generates the correct PowerShell command
3. A dialog shows the command with a "Copy to Clipboard" button and instructions to run
   it in an administrator PowerShell, then restart the service
4. After the service restarts, the UI reflects the new account

> **Note:** Chronicle cannot change its own service account at runtime (Windows restriction).
> The UI guides the user through doing it themselves with a one-line command.

**2.7 Service Status in UI**

Settings → Service shows:
- Service status badge (Running / Stopped / Not installed)
- Current service account
- Service start type (Automatic / Manual / Disabled)
- "Restart Service" button (available when running)
- Start/Stop buttons
- Uptime

**Tasks:**
- [ ] Add `Microsoft.Extensions.Hosting.WindowsServices` package
- [ ] Add `UseWindowsService()` to Program.cs
- [ ] Write `scripts/install-service.ps1`
- [ ] Write `scripts/uninstall-service.ps1`
- [ ] Update publish script to include installer scripts
- [ ] Add Service settings page to frontend
- [ ] Test service install/uninstall on clean Windows 10/11
- [ ] Test service starts on reboot
- [ ] Test log files are written when running as a service (path must be absolute)

**Deliverable:** Chronicle installable and runnable as a Windows service

---

### Step 3: API Key Authentication for Scrobblers

Scrobbler tools (Kodi addons, SIMKL integration) authenticate with Chronicle using API
keys rather than JWT. This is because scrobblers run unattended and cannot do interactive
login flows.

**3.1 API Key Format**

```
chr_live_[32 random alphanumeric characters]
```

Example: `chr_live_a8f3k2p9x1m5n7q4r6t0w2y8z3b5c1d`

**3.2 API Token Model (already exists in Core)**

The `ApiToken` model is already in `Chronicle.Core`. Implement the service and controller:

```csharp
// Chronicle.Services/IApiTokenService.cs
public interface IApiTokenService
{
    Task<ApiToken> CreateTokenAsync(int userId, string name, string[] scopes);
    Task<ApiToken?> ValidateTokenAsync(string token);
    Task<IEnumerable<ApiToken>> GetUserTokensAsync(int userId);
    Task RevokeTokenAsync(int tokenId, int userId);
}
```

**3.3 Scopes**

| Scope | Description |
|---|---|
| `scrobble` | Submit scrobble events |
| `read` | Read library and history |
| `admin` | Full access (for trusted tools) |

**3.4 Authentication Middleware**

Chronicle checks `X-API-Key` header before falling back to JWT. Both auth methods work
on all endpoints, so a scrobbler can use either:

```csharp
// In AuthenticationHandler
var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
if (apiKey != null)
{
    var token = await _apiTokenService.ValidateTokenAsync(apiKey);
    if (token != null)
    {
        // Build ClaimsPrincipal from token scopes
    }
}
```

**3.5 API Endpoints**

```
POST   /api/v1/auth/tokens              Create new API key
GET    /api/v1/auth/tokens              List user's API keys
DELETE /api/v1/auth/tokens/{id}         Revoke API key
```

**3.6 UI — API Keys Page**

Settings → API Keys:
- Table of existing keys (name, scopes, created, last used)
- "Create New Key" button → modal with name + scope checkboxes
- Key is shown **once** in full on creation, then only last 8 chars are shown
- Revoke button per key

**Tasks:**
- [ ] Implement `ApiTokenService`
- [ ] Add API key middleware to authentication pipeline
- [ ] Add `AuthController` endpoints for token CRUD
- [ ] Add API Keys settings page to frontend
- [ ] Integration tests for API key auth
- [ ] Document API key usage for scrobbler developers

**Deliverable:** Scrobblers can authenticate with `X-API-Key` header

---

### Step 4: Plugin System — Core Infrastructure

The plugin system is the foundation of Phase 2. All metadata providers, media types, and
future extensibility run through it.

**4.1 Plugin Types**

```csharp
// Chronicle.Plugins/IPlugin.cs
public interface IPlugin
{
    string Id { get; }            // e.g. "chronicle.plugin.tmdb"
    string Name { get; }
    string Version { get; }
    string MinChronicleVersion { get; }
    ILogger Logger { set; }       // Injected by host on load
    Task InitializeAsync(IPluginSettings settings);
    Task<bool> HealthCheckAsync();
}

// Plugin type interfaces (Chronicle.Plugins/)
public interface IMetadataPlugin : IPlugin
{
    string[] SupportedMediaTypes { get; }
    Task<SearchResult[]> SearchAsync(string query, string mediaType);
    Task<MediaMetadata> GetByIdAsync(string externalId);
    Task<byte[]> GetImageAsync(string url);
}

public interface IMediaTypePlugin : IPlugin
{
    MediaTypeDefinition GetMediaTypeDefinition();
}

public interface IScrobblePlugin : IPlugin
{
    // Scrobble plugins receive events — they are the source, not the receiver.
    // Chronicle is always the receiver. This interface is for future outbound
    // notification use (e.g. notify another service when Chronicle logs a watch).
}
```

**4.2 Plugin Manifest Format**

Every plugin ZIP must contain a `manifest.json` at its root:

```json
{
  "id": "chronicle.plugin.tmdb",
  "name": "The Movie Database (TMDB)",
  "version": "1.2.0",
  "minChronicleVersion": "0.6.0",
  "type": "metadata",
  "author": "thegoddamnbeckster",
  "repository": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB",
  "description": "Metadata for TV shows and movies via the TMDB API.",
  "icon": "icon.png",
  "supportedMediaTypes": ["tv", "movies"],
  "entryAssembly": "Chronicle.Plugin.TMDB.dll",
  "files": [
    { "path": "manifest.json",          "sha256": "<hash>" },
    { "path": "Chronicle.Plugin.TMDB.dll", "sha256": "<hash>" },
    { "path": "icon.png",               "sha256": "<hash>" }
  ],
  "settings": [
    {
      "key": "ApiKey",
      "displayName": "TMDB API Key",
      "description": "Your personal API key from themoviedb.org/settings/api",
      "type": "string",
      "required": true,
      "secret": true,
      "helpUrl": "https://www.themoviedb.org/settings/api"
    },
    {
      "key": "Language",
      "displayName": "Preferred Language",
      "description": "Language code for metadata (e.g. en-US)",
      "type": "string",
      "required": false,
      "default": "en-US",
      "advanced": true
    }
  ]
}
```

**4.3 Plugin ZIP Structure**

```
Chronicle.Plugin.TMDB-1.2.0.zip
├── manifest.json           ← Required. Must be at root.
├── Chronicle.Plugin.TMDB.dll
├── icon.png                ← Displayed in plugin browser
└── assets/                 ← Optional additional assets
    └── placeholder.png
```

**4.4 Prohibited File Types (Security)**

The plugin installer rejects any ZIP containing files with these extensions:

```
.exe .com .bat .cmd .ps1 .sh .bash .zsh .vbs .js .mjs .ts
.msi .msp .pif .scr .hta .jar .py .rb .pl .php .lua
```

Only `.dll` files are treated as code assemblies. All other file types are assets only.

**4.5 Plugin Verification (Hash Check)**

The ZIP hash is **never** bundled with the plugin. Chronicle fetches it separately from
the plugin's GitHub release:

```
Verification flow:
1. Chronicle downloads plugin ZIP to temp directory
2. Chronicle fetches hash from registry entry's hashUrl
   (e.g. https://github.com/thegoddamnbeckster/.../releases/download/v1.2.0/plugin.zip.sha256)
3. Chronicle computes SHA-256 of the downloaded ZIP
4. Hashes must match exactly — if not, ZIP is deleted and user is notified
5. After hash passes, extract ZIP to plugins/{plugin-id}/
6. Verify each file listed in manifest.json against its sha256
7. If all files pass, mark plugin as installed
```

**4.6 Assembly Isolation**

Each plugin assembly is loaded in its own `AssemblyLoadContext` so plugins cannot
interfere with each other or with Chronicle's own assemblies:

```csharp
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }
}
```

**4.7 Plugin Host Service**

`Chronicle.Services/Plugins/PluginHostService.cs` manages the lifecycle of all loaded plugins:

```csharp
public interface IPluginHostService
{
    IReadOnlyList<LoadedPlugin> LoadedPlugins { get; }
    Task LoadAllAsync();
    Task<LoadedPlugin> LoadAsync(string pluginId);
    Task UnloadAsync(string pluginId);
    IMetadataPlugin? GetMetadataPlugin(string pluginId);
    IEnumerable<IMetadataPlugin> GetMetadataPluginsForMediaType(string mediaType);
}
```

**Tasks:**
- [ ] Define all plugin interfaces in `Chronicle.Plugins`
- [ ] Implement `PluginLoadContext` (AssemblyLoadContext isolation)
- [ ] Implement `PluginHostService`
- [ ] Implement manifest parser and validator
- [ ] Implement prohibited file type scanner
- [ ] Implement SHA-256 verification pipeline
- [ ] Implement plugin settings store (encrypt secrets at rest using DPAPI or AES-256)
- [ ] Implement per-plugin logger injection
- [ ] Register `IPluginHostService` in DI container
- [ ] Load all installed plugins on startup
- [ ] Unit tests: manifest validation, file scanner, hash verification
- [ ] Integration tests: full install/load cycle with a test plugin

**Deliverable:** Plugin infrastructure capable of loading, verifying, and isolating plugins

---

### Step 5: Plugin Registry & Distribution

**5.1 Chronicle-Registry Repository Structure**

`thegoddamnbeckster/Chronicle-Registry` contains:

```
Chronicle-Registry/
├── registry.json          ← Master plugin catalogue
├── README.md
└── icons/                 ← Cached plugin icons (optional)
```

**5.2 Registry Format**

`registry.json`:

```json
{
  "registryVersion": "1",
  "registryUrl": "https://raw.githubusercontent.com/thegoddamnbeckster/Chronicle-Registry/main/registry.json",
  "updated": "2026-06-01T00:00:00Z",
  "plugins": [
    {
      "id": "chronicle.plugin.tmdb",
      "name": "The Movie Database (TMDB)",
      "description": "Metadata for TV shows and movies via the TMDB API.",
      "type": "metadata",
      "author": "thegoddamnbeckster",
      "repository": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB",
      "icon": "https://raw.githubusercontent.com/thegoddamnbeckster/Chronicle.Plugin.TMDB/main/icon.png",
      "tags": ["metadata", "tv", "movies"],
      "latestVersion": "1.2.0",
      "versions": [
        {
          "version": "1.2.0",
          "releaseDate": "2026-06-01",
          "minChronicleVersion": "0.6.0",
          "downloadUrl": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB/releases/download/v1.2.0/Chronicle.Plugin.TMDB-1.2.0.zip",
          "hashUrl": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB/releases/download/v1.2.0/Chronicle.Plugin.TMDB-1.2.0.zip.sha256",
          "releaseNotes": "Added movie collection support.",
          "changelog": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB/releases/tag/v1.2.0"
        }
      ]
    }
  ]
}
```

**5.3 Registry URL in Chronicle Config**

```json
{
  "Plugins": {
    "RegistryUrl": "https://raw.githubusercontent.com/thegoddamnbeckster/Chronicle-Registry/main/registry.json",
    "RegistryCacheDurationMinutes": 60,
    "InstallPath": "plugins"
  }
}
```

**5.4 Plugin Service API Endpoints**

```
GET    /api/v1/plugins                  List installed plugins
GET    /api/v1/plugins/available        Fetch catalogue from registry
POST   /api/v1/plugins/{id}/install     Download, verify, and install
POST   /api/v1/plugins/{id}/update      Update to latest version
DELETE /api/v1/plugins/{id}             Uninstall
GET    /api/v1/plugins/{id}/settings    Get plugin settings (secrets masked)
PUT    /api/v1/plugins/{id}/settings    Save plugin settings
POST   /api/v1/plugins/{id}/test        Run plugin health check
```

**5.5 Version Enforcement**

Chronicle refuses to install a plugin whose `minChronicleVersion` is higher than the
running Chronicle version. The UI shows a clear message:

> "This plugin requires Chronicle 0.8.0 or later. You are running 0.6.0."

**Tasks:**
- [ ] Create `thegoddamnbeckster/Chronicle-Registry` GitHub repository
- [ ] Write initial `registry.json` with TMDB and MusicBrainz entries
- [ ] Implement `PluginRegistryService` (fetch, cache, parse registry)
- [ ] Implement `PluginInstallerService` (download, verify, extract, install)
- [ ] Implement `PluginUpdaterService` (check for updates, apply)
- [ ] Add plugin settings encryption (secrets stored encrypted in database)
- [ ] Write all plugin API endpoints
- [ ] Background task: check for plugin updates on startup (configurable interval)
- [ ] Unit tests for registry parsing and version comparison
- [ ] Integration tests for install/update/uninstall flow

**Deliverable:** Chronicle can browse, install, update, and configure plugins from the UI

---

### Step 6: Plugin Management UI

The plugin experience must be seamless. Installing a plugin should take under a minute.

**6.1 Plugins Page Layout**

The Plugins page (`/settings/plugins`) has two tabs:

- **Installed** — Cards for each installed plugin showing name, version, status, settings button
- **Available** — Catalogue from the registry; searchable and filterable by type

**6.2 Plugin Card (Available)**

```
┌─────────────────────────────────────────┐
│ [icon]  TMDB                  [Install] │
│         Metadata · TV, Movies           │
│         Provides TV show and movie      │
│         metadata via the TMDB API.      │
│         v1.2.0  ·  by thegoddamnbeckster│
└─────────────────────────────────────────┘
```

**6.3 Plugin Card (Installed)**

```
┌─────────────────────────────────────────┐
│ [icon]  TMDB             ● Working      │
│         v1.2.0                          │
│         [Settings]  [Test]  [Remove]    │
│         Update available: v1.3.0        │
└─────────────────────────────────────────┘
```

**6.4 Plugin Settings Modal**

When the user clicks "Settings" on an installed plugin:
- Form fields generated from the plugin's `settings` schema in manifest.json
- Secret fields show masked values with a "Reveal" toggle
- Each field shows its description and an optional help link
- Advanced fields are hidden behind "Show Advanced Settings" toggle
- "Test Connection" button runs the plugin's health check and shows result

**6.5 Install Flow (User Experience)**

1. User clicks "Install" on a plugin card
2. Progress bar: Downloading → Verifying → Installing
3. If verification fails: red alert with explanation, no files kept
4. If successful: card moves to Installed tab, user prompted to configure settings
5. Settings modal opens automatically with required fields highlighted

**Tasks:**
- [ ] Plugins page with Installed / Available tabs
- [ ] Plugin card components
- [ ] Install/update progress flow with status feedback
- [ ] Plugin settings modal (schema-driven form generation)
- [ ] Advanced settings toggle in plugin settings
- [ ] Health check test button with result display
- [ ] Update notification badge

**Deliverable:** Full plugin management UI — browse, install, configure, update, remove

---

### Step 7: Advanced Settings Toggle (UI Pattern)

This is a global UI pattern applied to every settings page throughout the application.

**7.1 Principle**

- Default view shows only the settings that 90% of users will ever need
- A "Show Advanced Settings" link/toggle at the bottom of each section reveals the rest
- The toggle state is remembered per-page in localStorage
- Advanced settings are visually distinguished (slightly dimmer label, optional "Advanced" badge)

**7.2 Implementation**

```tsx
// components/AdvancedSettingsToggle.tsx
export function AdvancedSettingsToggle({ children }: { children: React.ReactNode }) {
  const [show, setShow] = useLocalStorage('advancedSettings', false)
  return (
    <>
      <button onClick={() => setShow(!show)} className={styles.toggle}>
        {show ? '▲ Hide Advanced Settings' : '▼ Show Advanced Settings'}
      </button>
      {show && <div className={styles.advanced}>{children}</div>}
    </>
  )
}
```

**7.3 Applied To**

| Settings Page | Standard Settings | Advanced Settings |
|---|---|---|
| General | Port, data directory, log level | Log retention days, registry URL, update check interval |
| Service | Status, start/stop, service user (simple) | Custom account name/password, service start type |
| API Keys | Create/revoke keys | Scoped permissions per key |
| Plugin settings | Required fields | Optional fields marked `"advanced": true` in manifest |
| Database | (none) | Connection string, WAL mode, vacuum schedule |
| Security | JWT expiry | Clock skew, token signing algorithm |

**Tasks:**
- [ ] Implement `AdvancedSettingsToggle` component
- [ ] Apply to all existing settings pages
- [ ] Persist toggle state in localStorage per page
- [ ] Audit all settings — decide what is standard vs advanced

**Deliverable:** Consistent advanced settings pattern across all settings pages

---

### Step 8: Movie Media Type + TMDB Plugin

**8.1 Movie Media Type (Seed Data)**

Add a Movies media type to the database seed in `ChronicleDbContext`:

```csharp
new MediaType
{
    Id = 2,
    Name = "movies",
    DisplayName = "Movies",
    Icon = "film",
    HierarchyLevels = 1,            // A movie is a single item (no episodes)
    PrimaryInteractionVerb = "watch",
    ProgressUnit = "minutes",
    SupportedStatuses = "plan_to_watch,watching,completed,dropped"
}
```

**8.2 TMDB Plugin Project**

Create `thegoddamnbeckster/Chronicle.Plugin.TMDB` as a separate GitHub repository:

```
Chronicle.Plugin.TMDB/
├── Chronicle.Plugin.TMDB.sln
├── src/
│   └── Chronicle.Plugin.TMDB/
│       ├── Chronicle.Plugin.TMDB.csproj
│       ├── TmdbPlugin.cs             ← Implements IMetadataPlugin
│       ├── TmdbClient.cs             ← HTTP client wrapper for TMDB API
│       ├── Models/                   ← TMDB API response models
│       └── Mappers/                  ← TMDB models → Chronicle MediaMetadata
├── tests/
│   └── Chronicle.Plugin.TMDB.Tests/
├── icon.png
├── build/
│   └── build.ps1                     ← Builds ZIP + SHA-256 hash file
└── README.md
```

**8.3 TMDB Plugin Build Script**

`build/build.ps1` — produces the distributable ZIP and its hash:

```powershell
param([string]$Version = "1.0.0")

dotnet publish src/Chronicle.Plugin.TMDB -c Release -o dist/
Compress-Archive -Path dist/* -DestinationPath "Chronicle.Plugin.TMDB-$Version.zip" -Force
$hash = (Get-FileHash "Chronicle.Plugin.TMDB-$Version.zip" -Algorithm SHA256).Hash
$hash | Out-File "Chronicle.Plugin.TMDB-$Version.zip.sha256" -NoNewline
Write-Host "Built Chronicle.Plugin.TMDB-$Version.zip"
Write-Host "SHA-256: $hash"
```

Both files are uploaded as GitHub Release assets. The registry's `hashUrl` points to the
`.sha256` file.

**8.4 TMDB API Key Requirement**

The TMDB plugin requires a free TMDB API key. The settings form provides:
- Field: "TMDB API Key" (required, secret)
- Help text: "Get your free API key at themoviedb.org/settings/api"
- "Test Connection" button verifies the key before saving

**Tasks:**
- [ ] Create `thegoddamnbeckster/Chronicle.Plugin.TMDB` repository
- [ ] Implement TMDB API client (search, get by ID, get image)
- [ ] Implement `TmdbPlugin` (IMetadataPlugin)
- [ ] Implement TV + Movie result mapping to Chronicle `MediaMetadata`
- [ ] Write `build.ps1` for ZIP + hash generation
- [ ] Add Movies seed data migration in Chronicle.Data
- [ ] Update MediaController to use metadata plugins for search
- [ ] Unit tests for TMDB client and mapper
- [ ] Release v1.0.0 to GitHub with ZIP and hash assets
- [ ] Add to Chronicle-Registry

**Deliverable:** Movies searchable and metadata-enriched via TMDB

---

### Step 9: Music Media Type + MusicBrainz Plugin

**9.1 Music Media Type (Seed Data)**

```csharp
new MediaType
{
    Id = 3,
    Name = "music",
    DisplayName = "Music",
    Icon = "music",
    HierarchyLevels = 3,            // Artist → Album → Track
    PrimaryInteractionVerb = "listen",
    ProgressUnit = "tracks",
    SupportedStatuses = "plan_to_listen,listening,completed,dropped"
}
```

**9.2 MusicBrainz Plugin Project**

Create `thegoddamnbeckster/Chronicle.Plugin.MusicBrainz` as a separate GitHub repository.
MusicBrainz is free and requires no API key (rate limiting only — max 1 req/sec).

```
Chronicle.Plugin.MusicBrainz/
├── Chronicle.Plugin.MusicBrainz.sln
├── src/
│   └── Chronicle.Plugin.MusicBrainz/
│       ├── Chronicle.Plugin.MusicBrainz.csproj
│       ├── MusicBrainzPlugin.cs
│       ├── MusicBrainzClient.cs       ← Respects 1 req/sec rate limit
│       ├── Models/
│       └── Mappers/
├── tests/
├── icon.png
├── build/
│   └── build.ps1
└── README.md
```

**9.3 MusicBrainz Rate Limiting**

MusicBrainz requires a maximum of 1 request per second and a meaningful User-Agent string.
The plugin implements a simple request queue:

```csharp
private readonly SemaphoreSlim _rateLimiter = new(1, 1);
private DateTime _lastRequest = DateTime.MinValue;

private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
{
    await _rateLimiter.WaitAsync();
    try
    {
        var elapsed = DateTime.UtcNow - _lastRequest;
        if (elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(TimeSpan.FromSeconds(1) - elapsed);
        _lastRequest = DateTime.UtcNow;
        return await _httpClient.SendAsync(request);
    }
    finally { _rateLimiter.Release(); }
}
```

**Tasks:**
- [ ] Create `thegoddamnbeckster/Chronicle.Plugin.MusicBrainz` repository
- [ ] Implement MusicBrainz API client with rate limiting
- [ ] Implement `MusicBrainzPlugin` (IMetadataPlugin)
- [ ] Implement Artist/Album/Track hierarchy mapping
- [ ] Write `build.ps1`
- [ ] Add Music seed data migration
- [ ] Unit tests
- [ ] Release v1.0.0 to GitHub
- [ ] Add to Chronicle-Registry

**Deliverable:** Music artists, albums, and tracks searchable via MusicBrainz

---

### Step 10: Docker & PostgreSQL

**10.1 Dockerfile**

`Dockerfile` in repo root:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/Chronicle.API/Chronicle.API.csproj", "src/Chronicle.API/"]
COPY ["src/Chronicle.Core/Chronicle.Core.csproj", "src/Chronicle.Core/"]
COPY ["src/Chronicle.Data/Chronicle.Data.csproj", "src/Chronicle.Data/"]
COPY ["src/Chronicle.Services/Chronicle.Services.csproj", "src/Chronicle.Services/"]
RUN dotnet restore "src/Chronicle.API/Chronicle.API.csproj"
COPY . .
RUN dotnet publish "src/Chronicle.API/Chronicle.API.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
VOLUME ["/app/data", "/app/logs", "/app/plugins"]
ENTRYPOINT ["dotnet", "Chronicle.API.dll"]
```

**10.2 docker-compose.yml**

```yaml
version: '3.8'

services:
  chronicle:
    image: ghcr.io/thegoddamnbeckster/chronicle:latest
    container_name: chronicle
    restart: unless-stopped
    ports:
      - "8080:8080"
    volumes:
      - ./data:/app/data
      - ./logs:/app/logs
      - ./plugins:/app/plugins
    environment:
      - ConnectionStrings__DefaultConnection=Data Source=/app/data/chronicle.db
      - Security__JwtSecret=CHANGE_THIS_TO_A_LONG_RANDOM_STRING
    # Uncomment to use PostgreSQL instead of SQLite:
    # depends_on:
    #   - db
    # environment:
    #   - ConnectionStrings__DefaultConnection=Host=db;Database=chronicle;Username=chronicle;Password=changeme
    #   - Database__Provider=PostgreSQL

  # db:
  #   image: postgres:16
  #   container_name: chronicle-db
  #   restart: unless-stopped
  #   environment:
  #     POSTGRES_DB: chronicle
  #     POSTGRES_USER: chronicle
  #     POSTGRES_PASSWORD: changeme
  #   volumes:
  #     - ./pgdata:/var/lib/postgresql/data
```

**10.3 PostgreSQL Support**

Chronicle detects the database provider from a config value and registers the appropriate
EF Core provider:

```json
{
  "Database": {
    "Provider": "SQLite"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=chronicle.db"
  }
}
```

```csharp
// Program.cs
var provider = builder.Configuration["Database:Provider"] ?? "SQLite";
builder.Services.AddDbContext<ChronicleDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")!;
    _ = provider switch
    {
        "PostgreSQL" => options.UseNpgsql(connStr),
        _            => options.UseSqlite(connStr)
    };
});
```

NuGet packages to add to `Chronicle.Data`:
```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.*" />
```

**Tasks:**
- [ ] Write Dockerfile
- [ ] Write docker-compose.yml (SQLite default, PostgreSQL commented out)
- [ ] Add Npgsql EF Core package
- [ ] Implement provider switching in Program.cs
- [ ] Test migrations against PostgreSQL
- [ ] Add `.dockerignore`
- [ ] Write GitHub Actions workflow to build and push to GHCR on release tag
- [ ] Document Docker deployment in DEPLOYMENT.md

**Deliverable:** Chronicle deployable as a Docker container with SQLite or PostgreSQL

---

### Step 11: End-to-End Testing & Polish

**11.1 Integration Test Coverage Gaps (from Phase 1)**

These endpoints have no integration tests yet:
- `MediaController` — CRUD, search
- `LibraryController` — add/update/remove entries
- `UsersController` — get/update profile
- `StatsController` — stats aggregation

**11.2 New Integration Tests**

- Plugin install/verify/load with a test plugin ZIP
- API key creation and authentication
- TMDB plugin search (against TMDB sandbox or recorded response)
- Service startup as Windows service

**11.3 Manual Smoke Test Checklist**

- [ ] Fresh install on Windows 10/11 (clean machine)
- [ ] Register first user (auto-admin)
- [ ] Install TMDB plugin from plugin browser
- [ ] Search for "Breaking Bad" — results appear with artwork
- [ ] Add to library
- [ ] Scrobble an episode via API with API key
- [ ] View dashboard — stats update
- [ ] View history
- [ ] Restart Chronicle service — library and settings persist
- [ ] Install as Windows service, reboot — auto-starts

---

## New File Summary

### In `Chronicle` (main repo)

```
src/
├── Chronicle.API/
│   ├── PortManager.cs                    ← Phase 1 (exists)
│   └── Program.cs                        ← Updated: Serilog, service, PostgreSQL
├── Chronicle.Plugins/
│   ├── IPlugin.cs                        ← Base plugin interface
│   ├── IMetadataPlugin.cs
│   ├── IMediaTypePlugin.cs
│   └── PluginManifest.cs                 ← Manifest deserialization model
└── Chronicle.Services/
    ├── Plugins/
    │   ├── IPluginHostService.cs
    │   ├── PluginHostService.cs
    │   ├── IPluginInstallerService.cs
    │   ├── PluginInstallerService.cs
    │   ├── IPluginRegistryService.cs
    │   ├── PluginRegistryService.cs
    │   ├── PluginLoadContext.cs
    │   └── PluginSettings.cs
    └── IApiTokenService.cs / ApiTokenService.cs

scripts/
├── install-service.ps1                   ← New
├── uninstall-service.ps1                 ← New
└── publish-windows.ps1                   ← Updated to bundle service scripts

Dockerfile                                ← New
docker-compose.yml                        ← New
.dockerignore                             ← New
```

### Separate Repositories (new)

```
thegoddamnbeckster/Chronicle-Registry
thegoddamnbeckster/Chronicle.Plugin.TMDB
thegoddamnbeckster/Chronicle.Plugin.MusicBrainz
```

---

## Dependencies Added in Phase 2

**Chronicle.API:**
```xml
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="Serilog.Sinks.File" Version="5.*" />
<PackageReference Include="Serilog.Sinks.Console" Version="4.*" />
<PackageReference Include="Serilog.Formatting.Compact" Version="2.*" />
<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="9.*" />
```

**Chronicle.Data:**
```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.*" />
```

**Chronicle.Plugin.TMDB (separate project):**
```xml
<PackageReference Include="TMDbLib" Version="2.*" />
```

---

## Success Checklist

- [ ] Serilog writing to rolling file; plugin logs in sub-folders
- [ ] Chronicle installs and runs as a Windows service
- [ ] Service account changeable via guided UI workflow
- [ ] API keys authenticate scrobbler requests
- [ ] Plugin ZIP install, verification, and loading fully working
- [ ] Plugin settings form generated from manifest schema
- [ ] Advanced settings hidden by default with working toggle
- [ ] TMDB plugin installable, configurable, and returning metadata
- [ ] MusicBrainz plugin installable and returning metadata
- [ ] Movies and Music media types available
- [ ] Docker image builds and runs
- [ ] PostgreSQL supported as alternative to SQLite
- [ ] All new code has unit tests
- [ ] Integration test coverage includes media, library, and plugin endpoints
- [ ] Manual smoke test passes on clean Windows machine
