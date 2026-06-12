# Chronicle Plugin Authoring Guide

Chronicle's functionality is extended through plugins. This guide covers everything
you need to know to build, package, and publish a plugin.

---

## Plugin Types

| Interface | Purpose |
|---|---|
| `IMetadataProvider` | Fetch metadata (title, poster, ratings) from an external service |
| `IImportProvider` | Import watch/read history, ratings, and watchlist from an external account |
| `IFileScannerPlugin` | Scan local directories for media files |
| `IWidgetPlugin` | Provide a dashboard widget |
| `IPluginTask` | Provide a custom background task (beyond the built-in well-known types) |

A single plugin assembly can implement multiple interfaces. For example, the Trakt plugin
implements both `IMetadataProvider` (to support Fix Match and enrichment) and `IImportProvider`
(to sync watch history from the user's account).

---

## Prerequisites

- **.NET 9 SDK** — [dotnet.microsoft.com](https://dotnet.microsoft.com/download)
- **Chronicle** cloned as a sibling directory — plugins reference `Chronicle.Plugins` as a local project reference

```
<base>\
  Chronicle\                   ← main app
  Chronicle.Plugin.MyPlugin\   ← your plugin
```

---

## Project Setup

1. Create a .NET 9 class library:
   ```
   dotnet new classlib -n Chronicle.Plugin.MyPlugin -f net9.0
   ```

2. Add a reference to `Chronicle.Plugins`. You must **not** copy it to your output —
   it is provided by the Chronicle host at runtime:
   ```xml
   <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                     Private="false" ExcludeAssets="runtime" />
   ```
   Setting `<Private>false</Private>` (and `ExcludeAssets="runtime"`) is critical. If
   `Chronicle.Plugins.dll` ends up in your plugin directory, it will be loaded twice into
   the process, causing type identity mismatches that silently break the plugin.

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

Every plugin must ship a `manifest.json` alongside its DLL. Chronicle reads this file at
load time to register the plugin.

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
  "fixMatchHint":          "Enter a URL or ID from example.com to override the automatic match.",
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
| `fixMatchHint` | No | Short hint shown to users in the Fix Match panel. If omitted, the panel shows a generic prompt. |
| `background_tasks` | No | Array of background tasks to register on install. Omit if your plugin has no scheduled work. |

### background_tasks fields

| Field | Required | Description |
|---|---|---|
| `task_id` | Yes | Either a well-known ID (see below) or a custom ID matching your `IPluginTask.TaskId`. |
| `display_name` | Yes | Shown as the task heading in the Background Tasks UI. |
| `description` | No | Shown as the task subtitle. |
| `default_cron` | Yes | 5-field cron expression in UTC. Example: `"0 4 * * *"` = every day at 4 am UTC. |
| `default_enabled` | Yes | `true` or `false`. Users can override this after install. |
| `schedulable` | No | `false` to hide the cron editor for one-time tasks. Defaults to `true`. |
| `run_confirmation_title` / `run_confirmation_message` | No | Shown in the confirmation dialog before a manual run. |

### Well-known task IDs

Declare one of these `task_id` values to get Chronicle's built-in task execution — no extra code needed.

| `task_id` | What Chronicle does |
|---|---|
| `fetch-missing-metadata` | Processes your plugin's enrichment queue: fetches metadata for newly imported items that don't have it yet. Requires `IMetadataProvider`. |
| `resync-all-metadata` | Re-downloads metadata for all library items already matched to your plugin. Requires `IMetadataProvider`. |
| `import-all` | Triggers a full import of the user's external account history. Requires `IImportProvider`. |
| `delta-sync` | Triggers an incremental sync since the last run. Requires `IImportProvider`. |

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
            new SettingDefinition("api_key", "API Key", SettingType.Password,
                required: true, description: "Your API key from example.com")
        ]);

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _apiKey = settings.GetValueOrDefault("api_key") ?? string.Empty;
    }

    public Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context,
        CancellationToken ct = default) { ... }

    public Task<MediaMetadata?> GetByIdAsync(string externalId,
        CancellationToken ct = default) { ... }

    public Task<byte[]> GetImageAsync(string url,
        CancellationToken ct = default) { ... }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) { ... }
}
```

`HealthCheckAsync()` is called by Chronicle to show the **HEALTHY / UNHEALTHY** badge on the
Plugins page. Fetch a known small resource from your service and return `true` if successful.

### Fix Match input handling

Users can override the automatic match via the **Fix Match** button on the media detail page.
Chronicle calls `POST /api/v1/media/{id}/refresh/{pluginId}` with an `input` body.

Your plugin receives the user's input as the `query` in `MediaSearchContext`. In `GetByIdAsync`,
check if the incoming `externalId` looks like a URL from your service and normalise it to your
internal ID format before calling the API. Chronicle will also call `GetByIdAsync` directly if
the user pastes something that resolves to a known ID.

The `fixMatchHint` you declare in `manifest.json` is shown to the user in the Fix Match panel
so they know what format to enter (e.g. `"Paste a TMDB URL or a typed ID like movie:550"`).

### How metadata is stored and displayed

When enrichment completes, the `MediaMetadata` you return is serialised and stored under your
plugin's full ID in the item's `metadata_json` column:

```json
{
  "chronicle.plugin.tmdb":        { "title": "...", "posterUrl": "...", ... },
  "chronicle.plugin.musicbrainz": { "title": "...", "externalId": "...", ... }
}
```

This also applies when a user adds an item via **Add Media** using your plugin's search results —
the initial metadata blob is stored under your plugin's ID from the moment the item is created,
not under a generic key.

The Chronicle frontend automatically renders a **PluginMetadataBox** for every key present —
no frontend code changes needed when a new plugin is installed. Your plugin's `iconUrl` and
`name` from the manifest are used as the box header.

The `ExternalId` you return is stored in the enrichment row and used on subsequent runs to
call `GetByIdAsync` directly rather than re-searching.

The `PosterUrl` from your result is promoted to `media_items.poster_url` if the item has no
poster yet.

### Cross-reference seeding via ExtendedData

If your plugin knows the IDs that other plugins use for the same item, put them in `ExtendedData`
under an `ids` key. Chronicle reads this when an item is first added via Add Media and
**pre-seeds enrichment rows** for those other plugins with the correct external ID, so they
skip the text-search step entirely and call `GetByIdAsync` directly with the known ID.

This prevents mis-matches. For example, Trakt knows a show's TMDB ID exactly. Without
cross-reference seeding, TMDB would text-search the show title and might match the wrong
item. With it, TMDB goes straight to the right ID.

The expected `ids` structure (mirrors Trakt's format, also used by SIMKL):

```csharp
ExtendedData = JsonSerializer.SerializeToElement(new
{
    ids = new
    {
        tmdb = 87533,           // TMDB numeric ID (Chronicle formats as "tv:87533" or "movie:87533")
        imdb = "tt8009690",     // IMDB ID string
        tvdb = 355534,          // TVDB numeric ID (reserved; not yet consumed by a built-in plugin)
    }
})
```

Chronicle extracts `ids.tmdb` and `ids.imdb` and looks up whether a plugin is registered for
each source. If found, it creates a pre-seeded `MediaItemEnrichment` row with `Status = Pending`
and the known `ExternalId`, so the next enrichment run calls `GetByIdAsync` directly.

**You only need this if your plugin is an authoritative cross-reference source** (like Trakt or
SIMKL, which hold verified mappings to TMDB/IMDB). Pure metadata providers like TMDB itself
have no need to seed other plugins.

Fields not consumed by Chronicle as cross-references (e.g. `tvdb`, `trakt`, custom IDs) are
still preserved in `ExtendedData` and stored in `metadata_json` for display and future use.

---

## Implementing IImportProvider

`IImportProvider` is for plugins that sync watch/read history from an external account.
Implement it alongside (or instead of) `IMetadataProvider`.

```csharp
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

public class MyImportProvider : IImportProvider
{
    public string PluginId => "com.example.myplugin";

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _accessToken = settings.GetValueOrDefault("access_token") ?? string.Empty;
    }

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        // Return true if the stored access token is valid.
        return Task.FromResult(!string.IsNullOrEmpty(_accessToken));
    }

    // Full import — retrieve everything
    public IAsyncEnumerable<ImportedItem> ImportAllAsync(CancellationToken ct = default)
    { ... }

    // Delta import — retrieve only activity since `since`
    public IAsyncEnumerable<ImportedItem> ImportSinceAsync(DateTimeOffset since,
        CancellationToken ct = default)
    { ... }
}
```

Each `ImportedItem` you yield represents one watch event, rating, or library status change.
Chronicle's `SyncOrchestrationService` does the heavy lifting: it matches each item to an
existing `MediaItem` (4-stage: ExternalId → cross-ref AdditionalIds → title+year → create
stub) and deduplicates watch events by `(MediaItemId, Timestamp)`.

### Optional enrichment hooks

`IImportProvider` has two optional default-interface methods. Override them if your service
returns richer metadata than the basic sync response:

```csharp
// Return full MediaMetadata for a specific item (avoids a separate metadata fetch)
public Task<MediaMetadata?> GetItemMetadataAsync(string externalId, string mediaType,
    CancellationToken ct = default) { ... }

// Return cast/crew credits for a specific item
public Task<IReadOnlyList<MediaCredit>?> GetCreditsAsync(string externalId, string mediaType,
    CancellationToken ct = default) { ... }
```

---

## Implementing IPluginTask (custom background tasks)

Only needed if you declare a `task_id` that is **not** one of the well-known IDs listed above.

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

Chronicle discovers `IPluginTask` implementations by scanning your plugin assembly at load
time. The `TaskId` property is matched against the `task_id` declared in `manifest.json`.
One class per declared custom task.

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
| Trakt | `#ed2224` | `#c01f21` |
| SIMKL | `#00b4d8` | `#0096b4` |
| FanEdit (IFDB) | `#8B1A1A` | `#C0392B` |
| Hardcover | `#8b5cf6` | `#7c3aed` |

---

## Settings Schema

If your plugin needs user-supplied configuration (API keys, usernames, etc.), return a
`PluginSettingsSchema` from `GetSettingsSchema()`. Chronicle renders the settings form
automatically in the Plugins → Configure panel.

```csharp
public PluginSettingsSchema GetSettingsSchema() => new(
    Settings:
    [
        new SettingDefinition(
            key:         "api_key",
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

Settings values are encrypted in the database (using ASP.NET Core Data Protection).
Retrieve them via the `Configure(settings)` method called by Chronicle after decryption.

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

**To add your plugin to Chronicle's built-in catalog**, open a pull request to the Chronicle
repository and add an entry to the `PluginCatalog` array in
`src/Chronicle.API/Controllers/PluginsController.cs`:

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

The `Sha256` field is a security measure — Chronicle verifies the downloaded ZIP matches this
hash before installing. **Update it with every new release and update the catalog entry to match.**

---

## Plugin Lifecycle

1. **Install** — Chronicle downloads the ZIP, verifies SHA-256, extracts to
   `plugins/{plugin_id}/`, loads the assembly with an isolated `PluginLoadContext`, then seeds
   any `background_tasks` declared in the manifest into the `background_tasks` table.
2. **Load** — On startup, Chronicle loads all installed plugins, discovers their
   `IMetadataProvider` / `IImportProvider` / `IPluginTask` implementations, and registers them.
3. **Unload/Reload** — Plugins can be hot-reloaded without restarting Chronicle via
   `POST /api/v1/plugins/{pluginId}/unload` and `/reload`.
4. **Uninstall** — Chronicle stops and unloads the plugin assembly, removes the plugin
   directory, and cascades-deletes its background task rows.

Background tasks created from the manifest are owned by the plugin row via a foreign key
(`plugin_id`). When the plugin is uninstalled, its tasks are automatically removed.

---

## Coding Conventions

Follow the same conventions used in Chronicle itself:

- **Async everywhere** — all I/O operations use `async`/`await`; method names end in `Async`
- **CancellationToken** — every async method accepts `CancellationToken ct = default` and passes it through
- **Host validation in Fix Match** — when your `GetByIdAsync` normalises URLs, always validate the host before making HTTP calls (prevents open redirect / SSRF)
- **Lossless ingestion** — store everything your API returns; unmapped fields go into `ExtendedData`
- **Rate limiting** — always enforce rate limits; serialize outbound HTTP calls with a `SemaphoreSlim`
- **Null safety** — enable `<Nullable>enable</Nullable>` in your `.csproj`

---

## Reference Implementations

| Plugin | Type | Demonstrates |
|--------|------|-------------|
| **TMDB** — [`thegoddamnbeckster/Chronicle.Plugin.TMDB`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TMDB) | `IMetadataProvider` | API key settings, movie + TV hierarchy, Fix Match URL normalisation |
| **MusicBrainz** — [`thegoddamnbeckster/Chronicle.Plugin.MusicBrainz`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.MusicBrainz) | `IMetadataProvider` | Multi-level hierarchy, no-API-key HTTP client, cover art, audiobook search cascade |
| **Trakt** — [`thegoddamnbeckster/Chronicle.Plugin.Trakt`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Trakt) | `IMetadataProvider` + `IImportProvider` | OAuth device flow, watch history sync, rate limit handling, credits import |
| **SIMKL** — [`thegoddamnbeckster/Chronicle.Plugin.Simkl`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Simkl) | `IMetadataProvider` + `IImportProvider` | PIN auth, full + delta sync, Fix Match URL normalisation |
| **FanEdit (IFDB)** — [`thegoddamnbeckster/Chronicle.Plugin.FanEdit`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FanEdit) | `IMetadataProvider` | HTML scraping, session cookie auth, strict rate limiting |
| **Hardcover** — [`thegoddamnbeckster/Chronicle.Plugin.Hardcover`](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover) | `IMetadataProvider` + `IImportProvider` | GraphQL API client, reading history import, series hierarchy |
