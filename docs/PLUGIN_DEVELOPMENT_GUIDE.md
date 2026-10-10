# Writing a Chronicle plugin, start to finish

This guide takes you from an empty folder to a plugin that is built, running in your own Chronicle, and (if you want)
published so other people can install it. It is deliberately practical. The reference material (every interface and
field) is in [`PLUGIN_SYSTEM.md`](PLUGIN_SYSTEM.md); the existing plugins (`W:\Scripts\Chronicle.Plugin.*`) are the best
worked examples. `Chronicle.Plugin.TVMaze` is the smallest complete metadata provider.

## 1. What a plugin is

A plugin is a .NET 9 class library plus a `manifest.json`, in its own folder. Chronicle loads it into an isolated load
context at start. One plugin implements one (occasionally more) of these interfaces from `Chronicle.Plugins`:

| Interface | Use it to |
|---|---|
| `IMetadataProvider` | look up titles, people, artwork and episode lists from a service (TMDB, TVMaze, MusicBrainz...) |
| `IFileScannerPlugin` | read files on disk and report what they are (the FileScanner plugin) |
| `IImportProvider` | import a user's history or lists from a service (Trakt, Simkl, LastFM...) |
| `IWidgetPlugin` | add a dashboard widget |
| `IReportPlugin` | add a report |
| `IThemePlugin` | add colour themes |
| `IPluginTask` | run scheduled work (declared in the manifest's `background_tasks`) |

Plugins are **stateless**: keep nothing between calls except what `Configure` gave you. Anything that must persist goes in
Chronicle's database (through the settings the plugin declares) rather than in files you write yourself.

## 2. What Chronicle will and will not load

Read this first; it explains most "why won't it install" problems.

* **A plugin must have a `manifest.json` next to its DLL**, and the `plugin_id` in it must match the id the code reports.
* **Only plugins in Chronicle's catalog install by default.** The catalog is the hosted file `plugins.json` in Chronicle's
  repository (each entry is a GitHub repo; Chronicle reads it from the address in the app setting `plugins.catalog_url`, and
  keeps the last copy it could read, then a built-in list, if that is unreachable). A plugin id that is not on it is
  refused at install and ignored when its folder is found at start, until an administrator sets the app setting
  `plugins.allow_unlisted` to `true` (`PUT /api/v1/settings/app/plugins.allow_unlisted` with `{ "value": "true" }`; there is no page for it on purpose). Use that
  switch for your own private plugins.
* **A plugin must live inside Chronicle's `plugins/` folder** (`<install folder>/plugins/<plugin id>/`). The install route
  accepts no path anywhere else.
* **Chronicle remembers a hash of the plugin's files** (every DLL in its folder plus `manifest.json`), recorded when the
  plugin is installed or updated through Chronicle. If the files on disk later differ, the plugin is **not loaded**,
  administrators get a notification, and the Plugins page shows "Blocked: files changed" with an **Approve changed files**
  button. Approve only changes you made yourself. Updating from the catalog records the new hash by itself.

When you are developing, you rebuild constantly, so you must tell Chronicle the new files are yours. Two ways:

```powershell
# Re-record every installed plugin's files as trusted, then exit (does not start the server):
cd src\Chronicle.API
dotnet run -- --accept-plugin-changes
```

`scripts\RunTestEnvironment.ps1` rebuilds all the plugins and runs exactly this for you before starting the API.
Or use **Approve changed files** on the Plugins page.

## 3. Prerequisites

* .NET 9 SDK.
* The Chronicle repository checked out next to your plugin (plugins reference `Chronicle.Plugins`):

```
W:\Scripts\
    Chronicle\                    <- the app
    Chronicle.Plugin.Example\     <- your plugin (this guide)
```

## 4. Create the project

```powershell
cd W:\Scripts
mkdir Chronicle.Plugin.Example
cd Chronicle.Plugin.Example
dotnet new classlib -n Chronicle.Plugin.Example -o . --framework net9.0
```

Replace the generated `.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Chronicle.Plugin.Example</AssemblyName>
    <RootNamespace>Chronicle.Plugin.Example</RootNamespace>
    <Version>1.0.0</Version>
    <Authors>You</Authors>
  </PropertyGroup>

  <ItemGroup>
    <!-- Chronicle.Plugins.dll is provided by the host at runtime: do NOT copy it to the output. -->
    <ProjectReference Include="../Chronicle/src/Chronicle.Plugins/Chronicle.Plugins.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

Two rules that bite people: **never** copy `Chronicle.Plugins.dll`, `Chronicle.Core.dll` or the Microsoft/System
assemblies into your plugin folder (the host supplies them; a second copy breaks type identity), and put the DLL's
**assembly name** equal to the namespace of your entry class minus the class name (the catalog derives the DLL name from it).

## 5. The manifest

`manifest.json` (next to the `.csproj`):

```json
{
  "plugin_id":   "chronicle.plugin.example",
  "name":        "Example",
  "version":     "1.0.0",
  "author":      "You",
  "description": "A tiny example metadata provider.",
  "min_chronicle_version": "0.7.0",
  "entry_type":  "Chronicle.Plugin.Example.ExampleMetadataProvider",
  "iconUrl":     "https://example.com/favicon.ico",
  "brandColorLight": "#3366CC",
  "brandColorDark":  "#6699FF",
  "fixMatchHint": "Enter an Example ID, for example 1234.",
  "supported_media_types": ["movies"],
  "background_tasks": [],
  "settings": []
}
```

* `plugin_id` is permanent; it keys the plugin's settings, enrichment rows and external ids. Use reverse-domain style.
* `entry_type` is the fully-qualified class name Chronicle instantiates.
* `version` should be bumped for every release; Chronicle shows it and the update check compares it.
* `supported_media_types` lists the media types the plugin handles; the Plugins page uses it to filter the catalog to
  "plugins that handle this type". Optional, but include it.
* `background_tasks` entries have `task_id`, `display_name`, `description`, `default_cron`, `default_enabled` (see TVMaze).

## 6. A minimal metadata provider

`ExampleMetadataProvider.cs`:

```csharp
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Example;

public sealed class ExampleMetadataProvider : IMetadataProvider
{
    private string _apiKey = "";

    // ── identity (must match manifest.json) ──────────────────────────────
    public string PluginId => "chronicle.plugin.example";
    public string Name     => "Example";
    public string Version  => "1.0.0";
    public string Author   => "You";

    // ── what this plugin can do ──────────────────────────────────────────
    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        // MediaTypeName must match a Chronicle media type ("movies", "tv", "music"...). If it is new, DisplayName and
        // the hierarchy fields describe it and Chronicle creates it for you.
        new MediaTypeSupport { MediaTypeName = "movies", DisplayName = "Movies", HierarchyLevels = 1 },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition { Key = "api_key", Label = "API key", Type = SettingType.Password, Required = true },
        ],
    };

    // Called with the saved settings at load and whenever the user saves them. Do not do slow work here.
    public void Configure(IReadOnlyDictionary<string, string> settings) =>
        _apiKey = settings.TryGetValue("api_key", out var key) ? key : "";

    // ── search: return scored candidates for what Chronicle already knows ─
    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        // context.Name, context.Year, context.KnownExternalIds ("imdb" -> "tt0133093") ... tell you what to look for.
        await Task.CompletedTask;
        var hit = new MediaMetadata { ExternalId = "1234", Source = PluginId, Title = context.Name, Year = context.Year };
        return [new ScoredCandidate(hit, Score: 90, ScoreReason: "exact title")];
    }

    // ── fetch the full record for one id ─────────────────────────────────
    public Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default) =>
        Task.FromResult(new MediaMetadata
        {
            ExternalId = externalId, Source = PluginId, Title = "The Example", Year = 2020,
            Overview = "A film about examples.", Genres = ["Documentary"],
            PosterUrl = "https://example.com/poster.jpg",
        });

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(_apiKey.Length > 0);
}
```

Things worth knowing:

* **Scores** are 0-100. Chronicle only accepts a match above its threshold, and prefers `IdentifierMatch = true` (you
  matched on a known external id) over a name match.
* **Return everything you have.** Chronicle is lossless: fields that do not map to a first-class column (`ExtendedData`,
  `AdditionalImages`, `AlternateNames`, `Cast`, `Crew`, `Tags`) are stored, not dropped. Do not trim data to be tidy.
* **Throw `PluginAuthException`** when credentials are wrong or expired; Chronicle then shows the plugin as needing
  attention instead of retrying forever.
* **Respect `CancellationToken`**, and use one long-lived `HttpClient` (not one per call). Be polite to the service:
  honour its rate limits; Chronicle may call you thousands of times on a first import.
* **Log through `Microsoft.Extensions.Logging`.** Each plugin gets its own log file. Never log secrets.
* **Never hard-code anything about one person's setup** (paths, addresses, names). Take it from the settings schema.
* Episode lists and person credits are optional overrides (`GetEpisodeListAsync`, `GetPersonCreditsAsync`).

Settings types: `Text`, `Password`, `Number`, `Boolean`, `Dropdown`, `MultiSelect`, `Url`, `FilePath`, `TextArea`, and
`Notice` (a read-only callout, saved as nothing). Passwords are stored encrypted.

## 7. Build and run it in your Chronicle

```powershell
cd W:\Scripts\Chronicle.Plugin.Example
dotnet build -c Release
```

Copy the output into a folder named after the plugin id inside Chronicle's plugins folder:

```powershell
$dest = "W:\Scripts\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.example"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.Example.dll" $dest
Copy-Item "manifest.json" $dest
```

Only your own DLL and `manifest.json` (plus any third-party NuGet DLLs your plugin needs) go in the folder.

Then:

1. Because your plugin id is not in the catalog, switch on the app setting **`plugins.allow_unlisted`** = `true` (admin).
2. Start (or restart) Chronicle. It finds the folder, registers the plugin and loads it. Or call
   `POST /api/v1/plugins` with `{ "dllPath": "<full path to the DLL inside plugins/...>" }`.
3. Open **Plugins**, find *Example*, click **Configure**, enter the key, click **Health check**.
4. Add a movie and run **Fix match** or Refresh metadata; your `SearchAsync`/`GetByIdAsync` are called.
5. After every rebuild: copy the new files in, then run `dotnet run -- --accept-plugin-changes` from
   `src\Chronicle.API` (or press **Approve changed files**), then **Reload**.

For a plugin you will keep working on, put it in `RunTestEnvironment.ps1`'s list so it is rebuilt and deployed with the
others.

## 8. Test it

* Unit-test your parsing and scoring without the network: feed canned JSON to your client class.
* Add a test that `GetSettingsSchema()` has the keys `Configure` reads, and that `Configure` with an empty dictionary
  does not throw.
* Chronicle's `Chronicle.Tests.Unit` shows how providers are exercised (`MetadataEnrichmentServiceTests`).
* Check the plugin's log file (`src/Chronicle.API/logs`) after a real lookup. Warnings there are usually a missing
  setting or a rate limit.

## 9. Publish it so others can install it

Installing from the catalog works from **GitHub releases**:

1. Put the plugin in its own public repo (for example `you/Chronicle.Plugin.Example`) with `manifest.json` at the root.
2. Build, then zip the contents of the output folder (the DLL, `manifest.json`, and any dependencies, no `Chronicle.*`
   host assemblies) into **one `.zip`**.
3. Create a GitHub release whose tag is the version (`v1.0.0`) and attach exactly that one zip.
4. Add the plugin to `plugins.json` at the root of Chronicle's repository and open a pull request:
   `{ "plugin_id": "chronicle.plugin.example", "github_repo": "you/Chronicle.Plugin.Example", "tags": ["movies", "metadata"] }`.
   No Chronicle release is needed: once the file is merged, every Chronicle picks the plugin up within about fifteen minutes
   (or at once with **Refresh** in the catalog panel). The catalog entry is resolved live from your latest release and the
   `manifest.json` at that tag, so you do not re-submit for each release: publish a new release and Chronicle's update check
   offers it.
5. Keep the manifest `version` equal to the release tag; the update check compares them.

The catalog file is the allowlist that keeps unknown plugins from being installed, which is why it is reviewed through a pull request.
If you only want the plugin for yourself, skip steps 3-5 and use `plugins.allow_unlisted`.

## 10. Checklist before you call it done

- [ ] `plugin_id` identical in `manifest.json` and in code; `entry_type` correct; version bumped
- [ ] Only your DLL, `manifest.json` and third-party dependencies in the folder (no `Chronicle.*`, `Microsoft.*`, `System.*`)
- [ ] `Configure` tolerates missing settings; `HealthCheckAsync` returns false (not an exception) when unconfigured
- [ ] Every field the service gives you is returned, not trimmed
- [ ] No secrets in logs; no hard-coded paths, hosts or names
- [ ] Rate limits respected; `CancellationToken` honoured
- [ ] Tested with `--accept-plugin-changes` after the last rebuild, then loaded, configured and health-checked in the UI
- [ ] README says what it does, what it needs (account/API key), and how to install it
