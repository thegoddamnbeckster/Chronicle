# Chronicle Plugin Authoring Guide

Chronicle's functionality is extended through plugins. This guide covers everything
you need to know to build, package, and publish a plugin.

---

## Plugin Types

| Interface | Purpose |
|---|---|
| `IMetadataProvider` | Fetch metadata (title, poster, ratings) from an external service |
| `IFileScannerPlugin` | Scan local directories for media files |
| `IWidgetPlugin` | Provide a dashboard widget |
| `IPluginTask` | Provide a custom background task (beyond the built-in well-known types) |

A single plugin assembly can implement multiple interfaces.

---

## Project Setup

1. Create a .NET 9 class library:
   ```
   dotnet new classlib -n Chronicle.Plugin.MyPlugin -f net9.0
   ```

2. Add a reference to `Chronicle.Plugins.dll`. You must **not** copy it to your output — it is provided by the Chronicle host at runtime:
   ```xml
   <ItemGroup>
     <Reference Include="Chronicle.Plugins">
       <HintPath>path\to\Chronicle.Plugins.dll</HintPath>
       <Private>false</Private>
     </Reference>
   </ItemGroup>
   ```
   Setting `<Private>false</Private>` is critical. If `Chronicle.Plugins.dll` ends up in your ZIP, it will be loaded twice into the process, causing type identity mismatches that silently break the plugin.

3. Add a `manifest.json` to your project root and configure it to copy on build:
   ```xml
   <ItemGroup>
     <None Update="manifest.json">
       <CopyToOutputDirectory>Always</CopyToOutputDirectory>
     </None>
   </ItemGroup>
   ```

---

## manifest.json Reference

Every plugin must ship a `manifest.json` alongside its DLL. Chronicle reads this file at load time to register the plugin.

```json
{
  "plugin_id":             "com.example.myplugin",
  "name":                  "My Plugin",
  "version":               "1.0.0",
  "author":                "Your Name",
  "description":           "What this plugin does.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "MyNamespace.MyPluginClass",
  "iconUrl":               "https://example.com/favicon.ico",
  "brandColorLight":       "#3A86FF",
  "brandColorDark":        "#5E9BFF",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up metadata for newly imported items.",
      "default_cron":    "0 4 * * *",
      "default_enabled": true
    }
  ]
}
```

### Field reference

| Field | Required | Description |
|---|---|---|
| `plugin_id` | Yes | Unique reverse-domain identifier. Use your domain or GitHub username as the prefix, e.g. `com.example.myplugin` or `io.github.yourname.myplugin`. Must be globally unique. |
| `name` | Yes | Human-readable display name shown in the UI. |
| `version` | Yes | Semantic version string (`MAJOR.MINOR.PATCH`). Bump on every release. |
| `author` | Yes | Author name or organisation. |
| `description` | No | One or two sentences shown in the catalog and installed-plugins list. |
| `min_chronicle_version` | Yes | Minimum Chronicle version your plugin requires. Use `"0.1.0"` if unsure. |
| `entry_type` | Yes | Fully-qualified class name of your plugin's main class, e.g. `"MyNamespace.MyPlugin"`. |
| `iconUrl` | No | URL of an icon (the service's favicon works well). Shown on the Plugins page and Background Tasks cards. HTTPS recommended. |
| `brandColorLight` | No | Hex colour (`#RRGGBB`) used for task card accents in light mode. |
| `brandColorDark` | No | Hex colour used in dark mode. Should be visible against a dark background. |
| `background_tasks` | No | Array of background tasks to register on install. Omit if your plugin has no scheduled work. |

### background_tasks fields

| Field | Required | Description |
|---|---|---|
| `task_id` | Yes | Either a well-known ID (see below) or a custom ID matching your `IPluginTask.TaskId`. |
| `display_name` | Yes | Shown as the task heading in the Background Tasks UI. |
| `description` | No | Shown as the task subtitle. |
| `default_cron` | Yes | 5-field cron expression in UTC. Example: `"0 4 * * *"` = every day at 4 am UTC. |
| `default_enabled` | Yes | `true` or `false`. Users can override this after install. |

### Well-known task IDs

Declare one of these `task_id` values to get Chronicle's built-in task execution — no extra code needed.

| `task_id` | What Chronicle does |
|---|---|
| `fetch-missing-metadata` | Processes your plugin's enrichment queue: fetches metadata from your service for newly imported items that don't have it yet. Requires your plugin to implement `IMetadataProvider`. |
| `resync-all-metadata` | Re-downloads metadata from your service for all library items already matched to your plugin. Useful for picking up corrections and updated artwork. Requires `IMetadataProvider`. |

---

## Implementing IMetadataProvider

```csharp
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

public class MyMetadataProvider : IMetadataProvider
{
    public string Name    => "My Plugin";
    public string Version => "1.0.0";

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
        [new MediaTypeSupport("movies"), new MediaTypeSupport("tv")];

    public PluginSettingsSchema GetSettingsSchema() => new(
        Settings:
        [
            new SettingDefinition("apiKey", "API Key", SettingType.Password,
                required: true, description: "Your API key from example.com")
        ]);

    public Task<MediaMetadata> SearchAsync(string query)  { ... }
    public Task<MediaMetadata> GetByIdAsync(string id)    { ... }
    public Task<byte[]>        GetImageAsync(string url)  { ... }
    public Task<bool>          HealthCheckAsync()         { ... }
}
```

`HealthCheckAsync()` is called by Chronicle to show the **HEALTHY / UNHEALTHY** badge on the Plugins page. Fetch a known small resource from your service and return `true` if successful.

---

## Implementing IPluginTask (custom background tasks)

Only needed if you declare a `task_id` that is **not** one of the well-known IDs above.

```csharp
using Chronicle.Plugins;

public class MyCustomTask : IPluginTask
{
    // Must match the task_id declared in manifest.json exactly
    public string TaskId => "my-custom-sync";

    public async Task RunAsync(CancellationToken ct)
    {
        // Your scheduled work here
    }
}
```

Chronicle discovers `IPluginTask` implementations by scanning your plugin assembly at load time. The `TaskId` property is matched against the `task_id` declared in `manifest.json`. One class per declared custom task.

---

## Branding Guidelines

- Use your service's official brand colour if one exists.
- `brandColorLight` should have good contrast on a white or light-grey background.
- `brandColorDark` should be visible on a dark background (Chronicle's default theme is near-black). Lighter shades of your brand colour typically work well.
- Both fields are optional. Chronicle falls back to its own accent colour if either is absent.
- Format: 6-digit hex with leading `#`, e.g. `"#BA478F"`. CSS variables and `rgba()` are not supported.

**Reference values used by the built-in plugins:**

| Plugin | `brandColorLight` | `brandColorDark` |
|---|---|---|
| TMDB | `#01B4E4` | `#0d9ec9` |
| MusicBrainz | `#BA478F` | `#CF6BAA` |

---

## Settings Schema

If your plugin needs user-supplied configuration (API keys, usernames, etc.), return a `PluginSettingsSchema` from `GetSettingsSchema()`. Chronicle renders the settings form automatically in the Plugins → Configure panel.

```csharp
public PluginSettingsSchema GetSettingsSchema() => new(
    Settings:
    [
        new SettingDefinition(
            key:         "apiKey",
            label:       "API Key",
            type:        SettingType.Password,
            required:    true,
            description: "Your free API key from https://example.com/settings"),

        new SettingDefinition(
            key:          "language",
            label:        "Language",
            type:         SettingType.Text,
            required:     false,
            defaultValue: "en",
            description:  "ISO 639-1 language code for metadata (e.g. en, de, fr)"),
    ]);
```

Chronicle calls `GetSettingsSchema()` to build the form and encrypts the saved values in the database. Retrieve them at runtime via constructor-injected `IPluginSettingsProvider`.

---

## Build and Packaging

Chronicle installs plugins from a ZIP archive. The ZIP must contain:
- Your plugin DLL (`Chronicle.Plugin.MyPlugin.dll`)
- Any NuGet dependency DLLs your code uses at runtime
- `manifest.json`
- **Do NOT include** `Chronicle.Core.dll` or `Chronicle.Plugins.dll` — these are provided by the host

Build script (PowerShell):

```powershell
dotnet publish . -c Release -o publish --no-self-contained

# Exclude Chronicle host assemblies and debug symbols
$files = Get-ChildItem publish | Where-Object {
    $_.Extension -ne '.pdb' -and
    $_.Name -notmatch '^Chronicle\.(Core|Plugins)\.'
}
Compress-Archive -Path ($files.FullName) -DestinationPath 'Chronicle.Plugin.MyPlugin.zip' -Force

# Print the SHA-256 (needed for catalog entry)
(Get-FileHash 'Chronicle.Plugin.MyPlugin.zip' -Algorithm SHA256).Hash.ToLower()
```

---

## Publishing a GitHub Release

Chronicle's plugin catalog installs plugins directly from GitHub releases.

1. Create a public GitHub repository for your plugin.
2. Build the ZIP as shown above and note the SHA-256 hash.
3. Create a GitHub release (tag: `v1.0.0`, title: `v1.0.0 — Initial Release`).
4. Upload the ZIP as a release asset named `Chronicle.Plugin.MyPlugin.zip`.
5. The asset filename must match the `AssetName` field in your catalog entry.

**To add your plugin to Chronicle's built-in catalog**, open a pull request to the Chronicle repository and add an entry to the `PluginCatalog` array in `src/Chronicle.API/Controllers/PluginsController.cs`:

```csharp
new PluginCatalogEntry(
    PluginId:    "com.example.myplugin",
    Name:        "My Plugin",
    Description: "What it does.",
    Author:      "Your Name",
    IconUrl:     "https://example.com/favicon.ico",
    GithubRepo:  "yourname/Chronicle.Plugin.MyPlugin",
    AssetName:   "Chronicle.Plugin.MyPlugin.zip",
    DllName:     "Chronicle.Plugin.MyPlugin.dll",
    Tags:        ["movies", "metadata"],
    Sha256:      "the-lowercase-sha256-of-your-zip"
),
```

The `Sha256` field is a security measure — Chronicle verifies the downloaded ZIP matches this hash before installing. **Update it with every new release and update the catalog entry to match.**

---

## Plugin Lifecycle

1. **Install** — Chronicle downloads the ZIP, verifies SHA-256, extracts to `plugins/{plugin_id}/`, loads the assembly with an isolated `PluginLoadContext`, then seeds any `background_tasks` declared in the manifest into the `background_tasks` table.
2. **Load** — On startup, Chronicle loads all installed plugins, discovers their `IMetadataProvider` / `IPluginTask` implementations, and registers them.
3. **Uninstall** — Chronicle stops and unloads the plugin assembly, removes the plugin directory, and cascades-deletes its background task rows.

Background tasks created from the manifest are owned by the plugin row via a foreign key (`plugin_id`). When the plugin is uninstalled, its tasks are automatically removed.

---

## Reference Implementation

The TMDB and MusicBrainz plugins are the canonical references:

- **TMDB** — [`thegoddamnbeckster/Chronicle.Plugin.TMDB`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB)
  Demonstrates `IMetadataProvider` for movies and TV, API key settings schema, and TMDB-flavoured branding.

- **MusicBrainz** — [`thegoddamnbeckster/Chronicle.Plugin.MusicBrainz`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz)
  Demonstrates `IMetadataProvider` across a multi-level hierarchy (artist → album → track), no-API-key HTTP client with rate-limit handling, and cover art fetching via the Cover Art Archive.
