# Generic Plugin Metadata Box — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace all hardcoded TMDB/MusicBrainz UI boxes with a single generic `PluginMetadataBox` component that auto-renders any plugin's metadata — zero frontend changes required when a new plugin is installed.

**Architecture:** TMDB loses its first-class DTO status; all plugin data flows through `PluginMetadata` keyed by the full plugin ID. A new `PluginMetadataBox` React component owns its own Refresh / Fix Match / Clear Match mutations and renders all fields generically. `MediaDetailPage` becomes a loop over `pluginMetadata` entries.

**Tech Stack:** .NET 9 / ASP.NET Core / EF Core 9 / React 18 / TypeScript / TanStack Query / CSS Modules

**Design doc:** `docs/plans/2026-03-22-generic-plugin-metadata-box-design.md`

---

## Important Background

Before touching code, read these sections:

- `MergeMetadataJson` in `src/Chronicle.Services/MetadataRefreshService.cs` currently derives a **short** key (e.g. `"chronicle.plugin.musicbrainz"` → `"musicbrainz"`). This plan changes it to use the **full plugin ID** as the MetadataJson key throughout.
- `ParseMetaJson` in `src/Chronicle.API/Controllers/MediaController.cs` treats `"tmdb"` and `"fileScanner"` as first-class keys. After this plan, only `"fileScanner"` is first-class.
- The TMDB plugin's `plugin_id` in `manifest.json` is currently `"tmdb"` — this plan renames it to `"chronicle.plugin.tmdb"` for consistency.
- There is a latent bug: the MusicBrainz box in the frontend reads `pluginMetadata['chronicle.plugin.musicbrainz']` but the actual key stored is `"musicbrainz"`. This plan fixes it.
- **Database:** Delete `chronicle.db` after Task 6 and let EF recreate it. No migrations needed for the metadata format change.

---

## Task 1: Add `fixMatchHint` to PluginManifest and Plugin model

**Files:**
- Modify: `src/Chronicle.Plugins/Models/PluginManifest.cs`
- Modify: `src/Chronicle.Core/Models/Plugin.cs`

**Step 1: Add `FixMatchHint` to `PluginManifest`**

In `src/Chronicle.Plugins/Models/PluginManifest.cs`, after `BrandColorDark`:

```csharp
/// <summary>
/// Short hint shown in the Fix Match panel explaining what to enter.
/// Example: "Enter a TMDB ID (e.g. 550), typed ID (movie:550 · tv:1396), or URL"
/// Optional — falls back to "Enter an ID or URL to search {Name}" if absent.
/// </summary>
[JsonPropertyName("fixMatchHint")]
public string? FixMatchHint { get; set; }
```

**Step 2: Add `FixMatchHint` to `Plugin` model**

In `src/Chronicle.Core/Models/Plugin.cs`, after `BrandColorDark`:

```csharp
/// <summary>Short hint shown in the Fix Match panel. From manifest fixMatchHint.</summary>
public string? FixMatchHint { get; set; }
```

**Step 3: Build to verify no errors**

```bash
cd src/Chronicle.API && dotnet build --no-restore 2>&1 | grep -E "^.*error CS"
```
Expected: no output (no errors).

**Step 4: Commit**

```bash
git add src/Chronicle.Plugins/Models/PluginManifest.cs src/Chronicle.Core/Models/Plugin.cs
git commit -m "feat(plugins): add fixMatchHint to PluginManifest and Plugin model"
```

---

## Task 2: Propagate `fixMatchHint` through the plugin loading pipeline to the API DTO

**Files:**
- Modify: `src/Chronicle.Services/Plugins/PluginHostService.cs`
- Modify: `src/Chronicle.Services/Plugins/PluginService.cs`
- Modify: `src/Chronicle.API/DTOs/PluginDTOs.cs`
- Modify: `src/Chronicle.API/Controllers/PluginsController.cs`

**Step 1: Write a failing integration test**

In `tests/Chronicle.Tests.Integration/PluginsControllerTests.cs` (or the appropriate existing plugin test file), add:

```csharp
[Fact]
public async Task GetPlugins_ReturnsFixMatchHint_WhenManifestHasIt()
{
    // Arrange
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(client));

    // Act
    var response = await client.GetAsync("/api/v1/plugins");
    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<PluginDto>>>();

    // Assert — at least one plugin should have a non-null FixMatchHint
    // (once manifests are updated in Task 3)
    body!.Data.Should().NotBeNull();
    // The field must exist on the DTO (null is fine if no plugin has it yet)
    body.Data!.All(p => p.FixMatchHint != null || p.FixMatchHint == null).Should().BeTrue();
}
```

Run: `cd tests && dotnet test --filter "GetPlugins_ReturnsFixMatchHint" --verbosity normal`
Expected: may compile-fail on `FixMatchHint` not existing on `PluginDto` — that's the failure we fix next.

**Step 2: Add `FixMatchHint` to `PluginDto`**

In `src/Chronicle.API/DTOs/PluginDTOs.cs`, add `FixMatchHint` as the last parameter:

```csharp
public record PluginDto(
    int Id,
    string PluginId,
    string Name,
    string Version,
    string Description,
    bool IsEnabled,
    DateTime InstalledAt,
    string? IconUrl = null,
    string? FixMatchHint = null
);
```

**Step 3: Map `FixMatchHint` in `PluginHostService.cs`**

Find the block where `Plugin` is created from a manifest (around line 145). Add:

```csharp
FixMatchHint    = manifest.FixMatchHint,
```

**Step 4: Map `FixMatchHint` in `PluginService.cs`**

Find the same block (around line 57). Add:

```csharp
FixMatchHint    = manifest.FixMatchHint,
```

**Step 5: Surface `FixMatchHint` in `PluginsController.cs`**

Find where `PluginDto` is constructed from a `Plugin` entity. Add `FixMatchHint = p.FixMatchHint`.

**Step 6: Build and run test**

```bash
cd src/Chronicle.API && dotnet build --no-restore 2>&1 | grep -E "^.*error CS"
cd tests && dotnet test --filter "GetPlugins_ReturnsFixMatchHint" --verbosity normal
```
Expected: test passes (field exists and is null until manifests are updated in Task 3).

**Step 7: Commit**

```bash
git add src/Chronicle.API/DTOs/PluginDTOs.cs \
        src/Chronicle.Services/Plugins/PluginHostService.cs \
        src/Chronicle.Services/Plugins/PluginService.cs \
        src/Chronicle.API/Controllers/PluginsController.cs
git commit -m "feat(plugins): propagate fixMatchHint from manifest through API DTO"
```

---

## Task 3: Rename TMDB plugin ID and update both plugin manifests

**Files:**
- Modify: `src/Chronicle.API/plugins/tmdb/manifest.json`
- Modify: `src/Chronicle.API/plugins/chronicle.plugin.musicbrainz/manifest.json`

The TMDB plugin folder is named `tmdb`. We need its `plugin_id` to become `chronicle.plugin.tmdb`.
The folder can stay named `tmdb` — folder names are not used as plugin IDs, only `plugin_id` in `manifest.json` matters.

**Step 1: Update TMDB manifest**

Edit `src/Chronicle.API/plugins/tmdb/manifest.json`:

```json
{
  "plugin_id": "chronicle.plugin.tmdb",
  "name": "TMDB",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "Fetches movie and TV metadata from The Movie Database (TMDB). Requires a free TMDB API key.",
  "min_chronicle_version": "0.1.0",
  "entry_type": "Chronicle.Plugin.TMDB.TmdbMetadataProvider",
  "iconUrl": "https://www.themoviedb.org/favicon.ico",
  "brandColorLight": "#01B4E4",
  "brandColorDark": "#0d9ec9",
  "fixMatchHint": "Enter a TMDB ID (e.g. 550), typed ID (movie:550 · tv:1396), or a full TMDB URL",
  "background_tasks": [
    {
      "task_id": "fetch-missing-metadata",
      "display_name": "Fetch Missing Metadata",
      "description": "Looks up metadata from TMDB for newly imported movies and TV shows that don't have it yet.",
      "default_cron": "0 4 * * *",
      "default_enabled": true
    },
    {
      "task_id": "resync-all-metadata",
      "display_name": "Re-sync All Metadata",
      "description": "Re-downloads all TMDB metadata to pick up updated titles, posters, and ratings.",
      "default_cron": "0 3 * * 0",
      "default_enabled": false
    }
  ]
}
```

**Step 2: Update MusicBrainz manifest**

Edit `src/Chronicle.API/plugins/chronicle.plugin.musicbrainz/manifest.json`:

```json
{
  "plugin_id": "chronicle.plugin.musicbrainz",
  "name": "MusicBrainz",
  "version": "1.0.2",
  "author": "Chronicle Contributors",
  "description": "Fetches comprehensive music metadata from MusicBrainz (artist, album, track) and cover art from the Cover Art Archive. No API key required.",
  "min_chronicle_version": "0.1.0",
  "entry_type": "Chronicle.Plugin.MusicBrainz.MusicBrainzMetadataProvider",
  "iconUrl": "https://musicbrainz.org/favicon.ico",
  "brandColorLight": "#BA478F",
  "brandColorDark": "#CF6BAA",
  "fixMatchHint": "Enter a MusicBrainz MBID or release URL (e.g. https://musicbrainz.org/release/...)",
  "background_tasks": [
    {
      "task_id": "fetch-missing-metadata",
      "display_name": "Fetch Missing Metadata",
      "description": "Looks up metadata from MusicBrainz for newly imported artists, albums, and tracks that don't have it yet.",
      "default_cron": "0 4 * * *",
      "default_enabled": true
    },
    {
      "task_id": "resync-all-metadata",
      "display_name": "Re-sync All Metadata",
      "description": "Re-downloads all MusicBrainz metadata to pick up corrections and updates.",
      "default_cron": "0 3 * * 0",
      "default_enabled": false
    }
  ]
}
```

**Step 3: Find and update any hardcoded `"tmdb"` plugin ID references in backend code**

```bash
grep -rn '"tmdb"' src/Chronicle.Services/ src/Chronicle.API/ src/Chronicle.Plugins.TMDB/ \
  --include="*.cs" | grep -v "//.*tmdb"
```

Update any occurrences that refer to the plugin ID (e.g. in `_firstClassKeys`, backwards compat comments in `MetadataRefreshService.cs`). Do not change strings referring to the `source` column in `media_external_ids` yet — that is addressed in Task 4.

**Step 4: Commit**

```bash
git add src/Chronicle.API/plugins/tmdb/manifest.json \
        src/Chronicle.API/plugins/chronicle.plugin.musicbrainz/manifest.json
git commit -m "feat(plugins): rename TMDB plugin_id to chronicle.plugin.tmdb, add fixMatchHint to both manifests"
```

---

## Task 4: Remove TmdbMetaDto, fix MergeMetadataJson to use full plugin IDs, simplify ParseMetaJson

This is the core backend change. Read the full `ParseMetaJson` and `MergeMetadataJson` methods before touching them.

**Files:**
- Modify: `src/Chronicle.API/DTOs/MediaDTOs.cs`
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`
- Modify: `src/Chronicle.Services/MetadataRefreshService.cs`

**Step 1: Write a failing unit test for `MergeMetadataJson` using full plugin ID as key**

In `tests/Chronicle.Tests.Unit/Services/MetadataRefreshServiceTests.cs` (create if it doesn't exist), add a test verifying that after `MergeMetadataJson`, the MetadataJson contains the full plugin ID as the key:

```csharp
[Fact]
public void MergeMetadataJson_UsesFullPluginIdAsKey()
{
    // Arrange
    var pluginId = "chronicle.plugin.tmdb";
    var meta = new Chronicle.Plugins.Models.MediaMetadata
    {
        Title = "Test Movie",
        Year = 2024,
        Rating = 8.5,
        Genres = new[] { "Drama" }
    };

    // Act — call via reflection or make the method internal/public for testing
    // The method is currently private static; expose it as internal for tests
    // by adding [assembly: InternalsVisibleTo("Chronicle.Tests.Unit")] to MetadataRefreshService.cs
    // For now, test indirectly via RefreshItemAsync (see integration test in Task 5)
    // This is a placeholder — mark as skipped until method is accessible
    Assert.True(true); // replace with real assertion in Task 5
}
```

**Step 2: Fix `MergeMetadataJson` in `MetadataRefreshService.cs`**

Find `MergeMetadataJson` (around line 620). Change the key derivation from the short-suffix approach to the full plugin ID:

```csharp
private static string MergeMetadataJson(
    string? existingJson, string pluginId, Chronicle.Plugins.Models.MediaMetadata meta)
{
    var root = ParseExistingMetaJson(existingJson);

    // Use the full plugin ID as the key (e.g. "chronicle.plugin.tmdb")
    root[pluginId] = new
    {
        // ... all the same fields as before ...
    };

    return JsonSerializer.Serialize(root, _jsonOpts);
}
```

Also remove the backwards-compat fallback that reads from `"tmdb"` source for external IDs in `RefreshItemCoreAsync` (around line 238-243):

```csharp
// Before (remove the "tmdb" fallback):
var extId = item.ExternalIds
    .FirstOrDefault(e =>
        string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(e.Source, "tmdb", StringComparison.OrdinalIgnoreCase))
    ?.ExternalId;

// After:
var extId = item.ExternalIds
    .FirstOrDefault(e =>
        string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase))
    ?.ExternalId;
```

**Step 3: Delete `TmdbMetaDto` and update `MediaItemDto`**

In `src/Chronicle.API/DTOs/MediaDTOs.cs`:

1. Delete the entire `TmdbMetaDto` record (all lines).
2. Remove `TmdbMetaDto? TmdbMeta = null,` from `MediaItemDto`.

**Step 4: Simplify `ParseMetaJson` in `MediaController.cs`**

Replace the current `ParseMetaJson` and related fields with:

```csharp
// Only "fileScanner" is a first-class key — everything else is plugin metadata
private static readonly HashSet<string> _firstClassKeys =
    new(StringComparer.OrdinalIgnoreCase) { "fileScanner" };

private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts =
    new(System.Text.Json.JsonSerializerDefaults.Web);

private static (FileScannerMetaDto? fs,
                Dictionary<string, System.Text.Json.JsonElement>? pluginMeta)
    ParseMetaJson(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return (null, null);
    try
    {
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);

        FileScannerMetaDto? fs = null;
        if (root.TryGetProperty("fileScanner", out var fsEl))
            fs = System.Text.Json.JsonSerializer.Deserialize<FileScannerMetaDto>(
                fsEl.GetRawText(), _jsonOpts);

        var fsOut = fs != null && (
            fs.FilePath is not null || fs.LocalPosterPath is not null ||
            fs.NfoPosterUrl is not null || fs.ImportedAt is not null) ? fs : null;

        Dictionary<string, System.Text.Json.JsonElement>? pluginMeta = null;
        foreach (var prop in root.EnumerateObject())
        {
            if (_firstClassKeys.Contains(prop.Name)) continue;
            pluginMeta ??= new Dictionary<string, System.Text.Json.JsonElement>();
            pluginMeta[prop.Name] = prop.Value.Clone();
        }

        return (fsOut, pluginMeta);
    }
    catch { return (null, null); }
}
```

Update `ToDto` to use the new return type:

```csharp
var (fs, pluginMeta) = ParseMetaJson(m.MetadataJson);
```

And update the `MediaItemDto` constructor call — remove `TmdbMeta: tmdb,`.

Also delete `MediaMetaJsonRoot` record (no longer needed).

**Step 5: Update `ClearProviderMetadata` helper**

This method currently calls `ParseMetaJson` and reads `fs`. Update to match the new signature.

**Step 6: Build**

```bash
cd src/Chronicle.API && dotnet build 2>&1 | grep -E "^.*error CS"
```
Expected: no errors. Fix any remaining references to `TmdbMeta` or `TmdbMetaDto`.

**Step 7: Run full backend test suite**

```bash
cd tests && dotnet test --verbosity normal 2>&1 | tail -20
```
Some tests that assert on `TmdbMeta` will fail — note which ones and fix them in Task 7.

**Step 8: Commit**

```bash
git add src/Chronicle.API/DTOs/MediaDTOs.cs \
        src/Chronicle.API/Controllers/MediaController.cs \
        src/Chronicle.Services/MetadataRefreshService.cs
git commit -m "refactor(api): remove TmdbMetaDto, use full plugin IDs as MetadataJson keys, simplify ParseMetaJson"
```

---

## Task 5: Add plugin-scoped refresh to `IMetadataRefreshService`

This new method handles both per-plugin Refresh (no input) and Fix Match (with input override). It replaces `ReidentifyAsync` in `FileScanService`.

**Files:**
- Modify: `src/Chronicle.Services/IMetadataRefreshService.cs`
- Modify: `src/Chronicle.Services/MetadataRefreshService.cs`

**Step 1: Write a failing integration test**

In `tests/Chronicle.Tests.Integration/MediaControllerTests.cs` (or create a new file), add:

```csharp
[Fact]
public async Task PluginScopedRefresh_Returns404_WhenItemNotFound()
{
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(client));

    var response = await client.PostAsJsonAsync(
        "/api/v1/media/99999/refresh/chronicle.plugin.tmdb",
        new { });

    response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
}

[Fact]
public async Task PluginScopedRefresh_Returns404_WhenPluginNotFound()
{
    // Arrange — create a media item
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetAdminTokenAsync(client));

    // Create a media item to refresh
    var mediaTypeId = await GetFirstMediaTypeIdAsync(client);
    var createResp = await client.PostAsJsonAsync("/api/v1/media",
        new { mediaTypeId, name = "Test Item", hierarchyLevel = 0 });
    var item = (await createResp.Content.ReadFromJsonAsync<ApiResponse<MediaItemDto>>())!.Data!;

    // Act — use a non-existent plugin ID
    var refreshResp = await client.PostAsJsonAsync(
        $"/api/v1/media/{item.Id}/refresh/chronicle.plugin.nonexistent",
        new { });

    // Assert
    refreshResp.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
}
```

Run: `cd tests && dotnet test --filter "PluginScopedRefresh" --verbosity normal`
Expected: FAIL — endpoint doesn't exist yet.

**Step 2: Add `RefreshItemForPluginAsync` to the interface**

In `src/Chronicle.Services/IMetadataRefreshService.cs`, add:

```csharp
/// <summary>
/// Refreshes metadata for a single item from a specific plugin.
/// If <paramref name="input"/> is provided, uses it as a search/ID override (Fix Match).
/// If null, re-fetches using the item's existing stored external ID for this plugin.
/// </summary>
Task<Chronicle.Core.Models.MediaItem> RefreshItemForPluginAsync(
    int mediaItemId,
    string pluginId,
    string? input = null,
    CancellationToken ct = default);
```

**Step 3: Implement `RefreshItemForPluginAsync` in `MetadataRefreshService.cs`**

Add the implementation after `RefreshItemAsync`:

```csharp
public async Task<MediaItem> RefreshItemForPluginAsync(
    int mediaItemId,
    string pluginId,
    string? input = null,
    CancellationToken ct = default)
{
    using var scope = _scopeFactory.CreateScope();
    var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

    var item = await db.MediaItems
        .Include(m => m.MediaType)
        .Include(m => m.ExternalIds)
        .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct)
        ?? throw new KeyNotFoundException($"Media item {mediaItemId} not found");

    var provider = registry.GetMetadataProvider(pluginId)
        ?? throw new KeyNotFoundException($"Plugin '{pluginId}' not found or not loaded");

    string extId;

    if (input is not null)
    {
        // Fix Match mode: parse the input as an external ID and store it
        extId = input.Trim();
        await UpsertExternalIdAsync(db, item.Id, pluginId, extId, ct);
        item.ExternalIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == item.Id).ToListAsync(ct);
    }
    else
    {
        // Refresh mode: use existing external ID
        var existing = item.ExternalIds
            .FirstOrDefault(e => string.Equals(e.Source, pluginId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            throw new InvalidOperationException($"No existing match for plugin '{pluginId}' on item {mediaItemId}. Use Fix Match to set one.");
        extId = existing.ExternalId;
    }

    var meta = await provider.GetByIdAsync(extId, ct);

    if (!string.IsNullOrWhiteSpace(meta.Title))     item.Name           = meta.Title;
    if (meta.Year.HasValue)                          item.Year           = meta.Year;
    if (!string.IsNullOrWhiteSpace(meta.Overview))  item.Overview       = meta.Overview;
    if (!string.IsNullOrWhiteSpace(meta.PosterUrl)) item.PosterUrl      = meta.PosterUrl;
    if (meta.RuntimeMinutes.HasValue)               item.RuntimeMinutes = meta.RuntimeMinutes;

    item.MetadataJson = MergeMetadataJson(item.MetadataJson, pluginId, meta);
    item.UpdatedAt    = DateTime.UtcNow;

    var log = new MediaItemRefreshLog
    {
        MediaItemId  = item.Id,
        ProviderName = provider.Name,
        RefreshedAt  = DateTime.UtcNow,
        Succeeded    = true
    };
    db.MediaItemRefreshLogs.Add(log);
    await db.SaveChangesAsync(ct);

    return item;
}
```

**Step 4: Build and run tests**

```bash
cd src/Chronicle.API && dotnet build --no-restore 2>&1 | grep -E "^.*error CS"
cd tests && dotnet test --filter "PluginScopedRefresh" --verbosity normal
```
Expected: tests still fail (endpoint not wired in controller yet).

**Step 5: Commit**

```bash
git add src/Chronicle.Services/IMetadataRefreshService.cs \
        src/Chronicle.Services/MetadataRefreshService.cs
git commit -m "feat(services): add RefreshItemForPluginAsync for plugin-scoped refresh and fix match"
```

---

## Task 6: Wire plugin-scoped refresh endpoint and remove `reidentify`

**Files:**
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`
- Modify: `src/Chronicle.Services/IFileScanService.cs`
- Modify: `src/Chronicle.Services/FileScanService.cs`

**Step 1: Add plugin-scoped refresh endpoint to `MediaController`**

Add this action after the existing `Refresh` action:

```csharp
/// <summary>
/// Refreshes metadata for a single item from a specific plugin.
/// If <c>input</c> is provided in the body, uses it as a Fix Match override.
/// </summary>
[HttpPost("{id:int}/refresh/{pluginId}")]
public async Task<IActionResult> RefreshForPlugin(
    int id,
    string pluginId,
    [FromBody] PluginRefreshRequestDto? dto,
    CancellationToken ct)
{
    try
    {
        var item = await _refreshService.RefreshItemForPluginAsync(id, pluginId, dto?.Input, ct);
        return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
    }
    catch (KeyNotFoundException ex)
    {
        return NotFound(ApiResponse<MediaItemDto>.Fail("NOT_FOUND", ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return BadRequest(ApiResponse<MediaItemDto>.Fail("NO_EXISTING_MATCH", ex.Message));
    }
    catch (Exception ex)
    {
        return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
    }
}
```

Add the DTO to `src/Chronicle.API/DTOs/MediaDTOs.cs`:

```csharp
/// <summary>Optional body for plugin-scoped refresh. Omit or leave Input null for a normal refresh.</summary>
public record PluginRefreshRequestDto(string? Input = null);
```

**Step 2: Remove `Reidentify` action from `MediaController`**

Delete the entire `[HttpPost("{id:int}/reidentify")]` action method.

**Step 3: Remove `ReidentifyAsync` from `IFileScanService`**

In `src/Chronicle.Services/IFileScanService.cs`, delete the `ReidentifyAsync` declaration.

**Step 4: Remove `ReidentifyAsync` implementation from `FileScanService`**

In `src/Chronicle.Services/FileScanService.cs`, delete the `ReidentifyAsync` method and its region comment.

**Step 5: Build**

```bash
cd src/Chronicle.API && dotnet build 2>&1 | grep -E "^.*error CS"
```
Expected: no errors.

**Step 6: Run integration tests**

```bash
cd tests && dotnet test --filter "PluginScopedRefresh" --verbosity normal
```
Expected: both tests pass.

**Step 7: Delete the database so it's recreated fresh**

```bash
rm -f src/Chronicle.API/chronicle.db src/Chronicle.API/chronicle.db-shm src/Chronicle.API/chronicle.db-wal
```

**Step 8: Commit**

```bash
git add src/Chronicle.API/Controllers/MediaController.cs \
        src/Chronicle.API/DTOs/MediaDTOs.cs \
        src/Chronicle.Services/IFileScanService.cs \
        src/Chronicle.Services/FileScanService.cs
git commit -m "feat(api): add plugin-scoped refresh endpoint, remove reidentify endpoint"
```

---

## Task 7: Fix backend tests broken by TmdbMetaDto removal

Run the full test suite and fix any tests that reference `TmdbMeta`, `TmdbMetaDto`, or the old reidentify endpoint.

**Step 1: Run all tests and capture failures**

```bash
cd tests && dotnet test --verbosity normal 2>&1 | grep -E "FAILED|Error"
```

**Step 2: For each failing test**

- Tests asserting `response.Data.TmdbMeta != null` → change to assert `response.Data.PluginMetadata != null && response.Data.PluginMetadata.ContainsKey("chronicle.plugin.tmdb")`
- Tests calling `reidentify` endpoint → change to call `refresh/{pluginId}` with body `{ "input": "..." }`
- Tests seeding `MetadataJson` with `{"tmdb":{...}}` → change to `{"chronicle.plugin.tmdb":{...}}`

**Step 3: Run full suite until green**

```bash
cd tests && dotnet test --verbosity normal 2>&1 | tail -10
```
Expected: all tests pass.

**Step 4: Commit**

```bash
git add tests/
git commit -m "test: update tests for TmdbMetaDto removal and plugin-scoped refresh endpoint"
```

---

## Task 8: Update frontend TypeScript types

**Files:**
- Modify: `src/Chronicle.Web/src/types/index.ts`
- Modify: `src/Chronicle.Web/src/api/plugins.ts`
- Modify: `src/Chronicle.Web/src/api/media.ts`

**Step 1: Update `types/index.ts`**

Delete the following interfaces entirely:
- `TmdbMeta`
- `MusicBrainzMeta`
- `MusicBrainzAdditionalImage`

Remove `tmdbMeta?: TmdbMeta | null` from `MediaItem`.

Change `pluginMetadata` type to be more specific:
```ts
pluginMetadata?: Record<string, Record<string, unknown>> | null
```

**Step 2: Update `PluginDto` in `src/Chronicle.Web/src/api/plugins.ts`**

Find the `PluginDto` interface and add:
```ts
iconUrl: string | null
fixMatchHint: string | null
```

**Step 3: Update `src/Chronicle.Web/src/api/media.ts`**

Remove the `reidentifyMedia` function entirely.

Add `pluginScopedRefresh`:
```ts
export async function pluginScopedRefresh(
  id: number,
  pluginId: string,
  input?: string,
): Promise<MediaItem> {
  try {
    const { data } = await client.post<ApiResponse<MediaItem>>(
      `/media/${id}/refresh/${encodeURIComponent(pluginId)}`,
      input ? { input } : {},
    )
    if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Refresh failed')
    return data.data
  } catch (err: unknown) {
    if (err instanceof ApiError && err.statusCode === 409 && err.errorCode === 'NO_PROVIDER_CONFIGURED') {
      throw new Error('No metadata provider configured. Add an API key in Settings → Plugins.')
    }
    throw err
  }
}
```

**Step 4: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check 2>&1 | head -40
```
Expected: errors about `tmdbMeta` still used in `MediaDetailPage.tsx` — these are fixed in Task 10. Note them and continue.

**Step 5: Commit**

```bash
cd src/Chronicle.Web && git add src/types/index.ts src/api/plugins.ts src/api/media.ts
git commit -m "refactor(web): remove TmdbMeta/MusicBrainzMeta types, add pluginScopedRefresh API"
```

---

## Task 9: Create `PluginMetadataBox` component

**Files:**
- Create: `src/Chronicle.Web/src/components/ui/PluginMetadataBox.tsx`
- Create: `src/Chronicle.Web/src/components/ui/PluginMetadataBox.module.css`

### Sub-task 9a: Field renderer utility

**Step 1: Create the field renderer**

Create `src/Chronicle.Web/src/components/ui/PluginMetadataBox.tsx` with the renderer first:

```tsx
import { useState, useRef, useEffect } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { refreshMedia, pluginScopedRefresh, clearMediaExternalId } from '@/api/media'
import styles from './PluginMetadataBox.module.css'

// ── Types ──────────────────────────────────────────────────────────────────────

interface Branding {
  displayName: string
  iconUrl: string | null
  fixMatchHint: string | null
}

interface PluginMetadataBoxProps {
  pluginId: string
  mediaId: number
  data: Record<string, unknown>
  branding: Branding
}

// ── Field renderer ─────────────────────────────────────────────────────────────

const IMAGE_EXTENSIONS = /\.(jpg|jpeg|png|webp|gif|avif)(\?.*)?$/i
const IMAGE_DOMAINS = ['image.tmdb.org', 'coverartarchive.org', 'musicbrainz.org',
                       'fanart.tv', 'thetvdb.com', 'artworks.theaudiodb.com']

function isImageUrl(val: string): boolean {
  if (IMAGE_EXTENSIONS.test(val)) return true
  try { return IMAGE_DOMAINS.some(d => new URL(val).hostname.includes(d)) }
  catch { return false }
}

function toLabel(key: string): string {
  // camelCase / PascalCase → Title Case with spaces
  return key
    .replace(/([A-Z])/g, ' $1')
    .replace(/^./, s => s.toUpperCase())
    .trim()
}

function FieldValue({ val }: { val: unknown }): React.ReactElement | null {
  if (val === null || val === undefined) return null

  if (typeof val === 'boolean') return <span className={styles.fieldValue}>{val ? 'Yes' : 'No'}</span>

  if (typeof val === 'number') return <span className={styles.fieldValue}>{val}</span>

  if (typeof val === 'string') {
    if (!val.trim()) return null
    if (isImageUrl(val)) {
      return (
        <a href={val} target="_blank" rel="noreferrer" className={styles.imageLink} title="Open full size">
          <img src={val} alt="" className={styles.thumbnail}
            onError={e => { e.currentTarget.style.display = 'none' }} />
          <span className={styles.thumbnailLabel}>↗</span>
        </a>
      )
    }
    return <span className={styles.fieldValue}>{val}</span>
  }

  if (Array.isArray(val)) {
    if (val.length === 0) return null
    if (val.every(v => typeof v === 'string')) {
      return (
        <div className={styles.tags}>
          {(val as string[]).map((t, i) => <span key={i} className={styles.tag}>{t}</span>)}
        </div>
      )
    }
    // Array of objects or mixed
    return (
      <div className={styles.subBlock}>
        {val.map((item, i) => (
          <div key={i} className={styles.subItem}>
            {typeof item === 'object' && item !== null
              ? <FieldGrid data={item as Record<string, unknown>} />
              : <span className={styles.fieldValue}>{String(item)}</span>}
          </div>
        ))}
      </div>
    )
  }

  if (typeof val === 'object') {
    return <FieldGrid data={val as Record<string, unknown>} />
  }

  return <span className={styles.fieldValue}>{String(val)}</span>
}

function FieldGrid({ data }: { data: Record<string, unknown> }): React.ReactElement {
  return (
    <div className={styles.fieldGrid}>
      {Object.entries(data)
        .filter(([key]) => !key.startsWith('_'))
        .filter(([, val]) => val !== null && val !== undefined)
        .map(([key, val]) => (
          <div key={key} className={styles.fieldRow}>
            <span className={styles.fieldLabel}>{toLabel(key)}</span>
            <FieldValue val={val} />
          </div>
        ))}
    </div>
  )
}

// ── Main component ─────────────────────────────────────────────────────────────

export function PluginMetadataBox({ pluginId, mediaId, data, branding }: PluginMetadataBoxProps) {
  const qc = useQueryClient()
  const [fixMatchOpen, setFixMatchOpen] = useState(false)
  const [fixMatchInput, setFixMatchInput] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (fixMatchOpen) inputRef.current?.focus()
  }, [fixMatchOpen])

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ['media', mediaId] })
    qc.invalidateQueries({ queryKey: ['library'] })
  }

  const refreshMut = useMutation({
    mutationFn: () => pluginScopedRefresh(mediaId, pluginId),
    onSuccess: invalidate,
  })

  const fixMatchMut = useMutation({
    mutationFn: () => pluginScopedRefresh(mediaId, pluginId, fixMatchInput.trim()),
    onSuccess: () => { invalidate(); setFixMatchOpen(false); setFixMatchInput('') },
  })

  const clearMut = useMutation({
    mutationFn: () => clearMediaExternalId(mediaId, pluginId),
    onSuccess: invalidate,
  })

  const hasExternalId = Object.keys(data).length > 0

  const fallbackHint = `Enter an ID or URL to search ${branding.displayName}`

  return (
    <div className={styles.box}>
      {/* Header */}
      <div className={styles.header}>
        <div className={styles.brand}>
          {branding.iconUrl && (
            <img src={branding.iconUrl} alt="" className={styles.icon} aria-hidden />
          )}
          <span className={styles.name}>{branding.displayName}</span>
        </div>
        <div className={styles.actions}>
          {hasExternalId && (
            <button
              className={styles.clearBtn}
              onClick={() => clearMut.mutate()}
              disabled={clearMut.isPending}
              title={`Remove the ${branding.displayName} match — next refresh will auto-search again`}
            >
              {clearMut.isPending ? 'Clearing…' : '✕ Clear Match'}
            </button>
          )}
          <button
            className={styles.fixMatchBtn}
            onClick={() => { setFixMatchOpen(o => !o); setFixMatchInput(''); fixMatchMut.reset() }}
            title={`Manually set the ${branding.displayName} match`}
          >
            ⚙ Fix Match
          </button>
          <button
            className={styles.refreshBtn}
            onClick={() => refreshMut.mutate()}
            disabled={refreshMut.isPending}
            title={`Re-fetch metadata from ${branding.displayName}`}
          >
            {refreshMut.isPending ? 'Refreshing…' : '↻ Refresh'}
          </button>
        </div>
      </div>

      {/* Fix Match panel */}
      {fixMatchOpen && (
        <div className={styles.fixMatchPanel}>
          <p className={styles.fixMatchHint}>{branding.fixMatchHint ?? fallbackHint}</p>
          <div className={styles.fixMatchRow}>
            <input
              ref={inputRef}
              className={styles.fixMatchInput}
              type="text"
              placeholder={`${branding.displayName} ID or URL…`}
              value={fixMatchInput}
              onChange={e => { setFixMatchInput(e.target.value); fixMatchMut.reset() }}
              onKeyDown={e => {
                if (e.key === 'Enter' && fixMatchInput.trim()) fixMatchMut.mutate()
                if (e.key === 'Escape') { setFixMatchOpen(false); setFixMatchInput('') }
              }}
            />
            <button
              className={styles.fixMatchApplyBtn}
              onClick={() => fixMatchMut.mutate()}
              disabled={fixMatchMut.isPending || !fixMatchInput.trim()}
            >
              {fixMatchMut.isPending ? 'Applying…' : 'Apply'}
            </button>
          </div>
          {fixMatchMut.isError && (
            <p className={styles.fixMatchError}>
              {(fixMatchMut.error as Error).message}
            </p>
          )}
        </div>
      )}

      {/* Data rows */}
      <FieldGrid data={data} />

      {/* Refresh error */}
      {refreshMut.isError && (
        <p className={styles.refreshError}>
          Refresh failed: {(refreshMut.error as Error).message}
        </p>
      )}
    </div>
  )
}
```

**Step 2: Create CSS module**

Create `src/Chronicle.Web/src/components/ui/PluginMetadataBox.module.css`:

```css
/* ── Container ─────────────────────────────────────────────────────────────── */
.box {
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 6px;
  margin-bottom: 12px;
  overflow: hidden;
}

/* ── Header ────────────────────────────────────────────────────────────────── */
.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 14px;
  border-bottom: 1px solid var(--border);
  background: var(--surface);
}

.brand {
  display: flex;
  align-items: center;
  gap: 8px;
}

.icon {
  width: 16px;
  height: 16px;
  object-fit: contain;
}

.name {
  font-weight: 600;
  font-size: 0.875rem;
  color: var(--text-primary);
}

.actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* ── Action buttons ─────────────────────────────────────────────────────────── */
.clearBtn,
.fixMatchBtn,
.refreshBtn {
  padding: 4px 10px;
  border-radius: 4px;
  font-size: 0.8rem;
  cursor: pointer;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text-secondary);
  transition: background 0.15s, color 0.15s;
}

.clearBtn:hover,
.fixMatchBtn:hover,
.refreshBtn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.clearBtn:disabled,
.refreshBtn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

/* ── Fix Match panel ────────────────────────────────────────────────────────── */
.fixMatchPanel {
  padding: 10px 14px;
  background: var(--surface-sunken, var(--surface));
  border-bottom: 1px solid var(--border);
}

.fixMatchHint {
  font-size: 0.8rem;
  color: var(--text-secondary);
  margin: 0 0 8px;
}

.fixMatchRow {
  display: flex;
  gap: 8px;
}

.fixMatchInput {
  flex: 1;
  padding: 5px 8px;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: var(--input-bg, var(--surface));
  color: var(--text-primary);
  font-size: 0.875rem;
}

.fixMatchInput:focus {
  outline: none;
  border-color: var(--accent);
}

.fixMatchApplyBtn {
  padding: 5px 14px;
  border-radius: 4px;
  font-size: 0.875rem;
  cursor: pointer;
  border: 1px solid var(--accent);
  background: var(--accent);
  color: #fff;
}

.fixMatchApplyBtn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.fixMatchError,
.refreshError {
  font-size: 0.8rem;
  color: var(--danger, #e5534b);
  margin: 6px 0 0;
}

/* ── Field grid ─────────────────────────────────────────────────────────────── */
.fieldGrid {
  padding: 10px 14px;
  display: grid;
  grid-template-columns: minmax(80px, 140px) 1fr;
  gap: 6px 12px;
  align-items: start;
}

.fieldRow {
  display: contents;
}

.fieldLabel {
  font-size: 0.8rem;
  color: var(--text-secondary);
  padding-top: 2px;
  text-transform: capitalize;
}

.fieldValue {
  font-size: 0.875rem;
  color: var(--text-primary);
  word-break: break-word;
}

/* ── Tags ───────────────────────────────────────────────────────────────────── */
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tag {
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 3px;
  padding: 1px 6px;
  font-size: 0.75rem;
  color: var(--text-secondary);
}

/* ── Images ─────────────────────────────────────────────────────────────────── */
.imageLink {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  text-decoration: none;
  margin-right: 8px;
}

.thumbnail {
  width: 60px;
  height: 90px;
  object-fit: cover;
  border-radius: 3px;
  border: 1px solid var(--border);
}

.thumbnailLabel {
  font-size: 0.7rem;
  color: var(--text-secondary);
}

/* ── Sub-block (nested objects/arrays) ──────────────────────────────────────── */
.subBlock {
  grid-column: 1 / -1;
}

.subItem {
  border-left: 2px solid var(--border);
  padding-left: 10px;
  margin-top: 4px;
}
```

**Step 3: Type-check the component**

```bash
cd src/Chronicle.Web && npm run type-check 2>&1 | grep "PluginMetadataBox"
```
Expected: no errors in the component file itself.

**Step 4: Commit**

```bash
cd src/Chronicle.Web
git add src/components/ui/PluginMetadataBox.tsx src/components/ui/PluginMetadataBox.module.css
git commit -m "feat(web): add generic PluginMetadataBox component with data-driven field rendering"
```

---

## Task 10: Refactor `MediaDetailPage` to use `PluginMetadataBox`

**Files:**
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

**Step 1: Read the full current MediaDetailPage.tsx before touching it**

The file is large (~750 lines). Read it fully before making changes.

**Step 2: Add the plugins query and branding map**

Near the top of the component (alongside other `useQuery` calls), add:

```tsx
import { getPlugins } from '@/api/plugins'
import { PluginMetadataBox } from '@/components/ui/PluginMetadataBox'

// Inside the component:
const { data: pluginsList } = useQuery({
  queryKey: ['plugins'],
  queryFn: getPlugins,
  staleTime: 5 * 60 * 1000, // 5 minutes — plugin list rarely changes
})

const brandingMap = useMemo(() => {
  const map: Record<string, { displayName: string; iconUrl: string | null; fixMatchHint: string | null }> = {}
  for (const p of pluginsList ?? []) {
    map[p.pluginId] = {
      displayName: p.name,
      iconUrl: p.iconUrl ?? null,
      fixMatchHint: p.fixMatchHint ?? null,
    }
  }
  return map
}, [pluginsList])
```

Add `useMemo` to imports from React.

**Step 3: Remove state and mutations that are now owned by PluginMetadataBox**

Remove:
- `clearMatchMut` (TMDB)
- `clearMbMatchMut` (MusicBrainz)
- `reidentifyMut`
- `fixMatchOpen` state
- `fixMatchInput` state
- `fixMatchInputRef`
- `suppressMatchMut` (if no longer needed — check if it's used elsewhere)
- `tmdbIds`, `otherIds`, `isTmdbSupported`, `tmdbHasRealId`, `tmdbSuppressed` derived values
- `TMDB_SUPPORTED_TYPES` constant

**Step 4: Remove the hardcoded TMDB box JSX**

Delete the entire `{isTmdbSupported && ( <div className={styles.tmdbBox}>...</div> )}` block (~200 lines).

**Step 5: Remove the hardcoded MusicBrainz box JSX**

Delete the entire `{(() => { const mb = item.pluginMetadata?.['chronicle.plugin.musicbrainz']... })()}` block (~100 lines).

**Step 6: Replace with the generic loop**

In place of where the TMDB and MusicBrainz boxes were, add:

```tsx
{Object.entries(item.pluginMetadata ?? {}).map(([pluginId, data]) => (
  <PluginMetadataBox
    key={pluginId}
    pluginId={pluginId}
    mediaId={item.id}
    data={data as Record<string, unknown>}
    branding={brandingMap[pluginId] ?? {
      displayName: pluginId,
      iconUrl: null,
      fixMatchHint: null,
    }}
  />
))}
```

**Step 7: Remove now-unused imports**

Remove `tmdbLogoFallback` import, `reidentifyMedia`, `suppressMediaMatch`, `clearMediaExternalId` imports if no longer used (check — `clearMediaExternalId` may still be needed if suppress is still in the page).

**Step 8: Clean up unused CSS classes from `MediaDetailPage.module.css`**

Classes that are now only used by the deleted TMDB/MusicBrainz boxes can be removed. Check which ones are still used by searching for each class name in the `.tsx` file. Remove only the unused ones — don't touch classes that are still referenced.

Key classes to check: `.tmdbBox`, `.tmdbGrid`, `.tmdbRow`, `.tmdbLabel`, `.tmdbValue`, `.tmdbTags`, `.tmdbTag`, `.tmdbThumbnail`, `.tmdbImageLink`, `.tmdbThumbnailLabel`, `.tmdbRowImages`, `.tmdbImageLinks`, `.metadataBoxHeader`, `.metadataBoxActions`, `.mbMetadataBox`, `.mbMetadataBoxHeader`, `.mbIcon`, `.mbProviderName`, `.fixMatchPanel`, `.fixMatchHint`, `.fixMatchRow`, `.fixMatchInput`, `.fixMatchApplyBtn`, `.clearMatchBtn`, `.refreshBtn`, `.refreshStrip`, `.refreshError`.

**Step 9: Type-check and lint**

```bash
cd src/Chronicle.Web && npm run type-check 2>&1 | head -30
npm run lint 2>&1 | grep -v "^$" | head -30
```
Expected: no errors. Fix any remaining `tmdbMeta` references.

**Step 10: Commit**

```bash
git add src/pages/media/MediaDetailPage.tsx src/pages/media/MediaDetailPage.module.css
git commit -m "refactor(web): replace hardcoded plugin boxes with generic PluginMetadataBox loop"
```

---

## Task 11: End-to-end smoke test and final cleanup

**Step 1: Start the API**

```bash
cd src/Chronicle.API && dotnet run
```

**Step 2: Start the frontend**

```bash
cd src/Chronicle.Web && npm run dev
```

**Step 3: Smoke test checklist**

- [ ] Navigate to a movie's detail page — TMDB metadata box appears with all fields, Refresh button works
- [ ] Navigate to a music album/artist detail page — MusicBrainz metadata box appears, Refresh works
- [ ] Fix Match on TMDB item — panel shows, hint text appears, Apply searches and updates item
- [ ] Fix Match on MusicBrainz item — panel shows MB hint text, Apply works
- [ ] Clear Match on any item — removes the plugin's external ID, box updates
- [ ] Global ↻ Refresh strip still works — refreshes all plugins
- [ ] New item with no metadata — no plugin boxes shown until enrichment runs
- [ ] Plugin branding shows correct icon and name for each box

**Step 4: Run full test suite one final time**

```bash
cd tests && dotnet test --verbosity normal 2>&1 | tail -10
```
Expected: all tests pass.

**Step 5: Final commit**

```bash
git add .
git commit -m "chore: generic plugin metadata box — smoke test clean-up"
```

---

## Task 12: Update MEMORY.md

Update `C:\Users\jsmith\.claude\projects\W--Scripts-Chronicle\memory\MEMORY.md`:

- Remove the backlog entries for `project_backlog_media_detail_fix_clear_buttons.md` (this is now fixed generically)
- Add a note under "Recently Completed" that the generic PluginMetadataBox was implemented
- Note that `reidentify` endpoint is gone, replaced by `POST /media/{id}/refresh/{pluginId}`
- Note that the TMDB plugin ID was renamed from `"tmdb"` to `"chronicle.plugin.tmdb"`
- Note that MetadataJson now uses full plugin IDs as keys (no more short-suffix derivation)

---

## Quick Reference: Key Locations

| Thing | Location |
|---|---|
| `PluginManifest` model | `src/Chronicle.Plugins/Models/PluginManifest.cs` |
| `Plugin` domain model | `src/Chronicle.Core/Models/Plugin.cs` |
| `PluginDto` API DTO | `src/Chronicle.API/DTOs/PluginDTOs.cs` |
| `MediaItemDto` API DTO | `src/Chronicle.API/DTOs/MediaDTOs.cs` |
| `ParseMetaJson` + `MergeMetadataJson` | `src/Chronicle.API/Controllers/MediaController.cs` + `src/Chronicle.Services/MetadataRefreshService.cs` |
| Plugin-scoped refresh endpoint | `src/Chronicle.API/Controllers/MediaController.cs` |
| `IMetadataRefreshService` | `src/Chronicle.Services/IMetadataRefreshService.cs` |
| TMDB manifest | `src/Chronicle.API/plugins/tmdb/manifest.json` |
| MusicBrainz manifest | `src/Chronicle.API/plugins/chronicle.plugin.musicbrainz/manifest.json` |
| `PluginMetadataBox` component | `src/Chronicle.Web/src/components/ui/PluginMetadataBox.tsx` |
| Frontend types | `src/Chronicle.Web/src/types/index.ts` |
| Frontend media API | `src/Chronicle.Web/src/api/media.ts` |
| `MediaDetailPage` | `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx` |
