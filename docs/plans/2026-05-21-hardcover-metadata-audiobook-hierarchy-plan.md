# Hardcover Metadata Provider + Audiobook Hierarchy Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a three-level Author → Series → Book hierarchy for books and audiobooks, add Hardcover as a metadata provider, and update MusicBrainz audiobooks to match.

**Architecture:** The existing `Chronicle.Plugin.Hardcover` DLL already has an `IImportProvider`; we add `HardcoverMetadataProvider : IMetadataProvider` to the same DLL — `PluginRegistry` auto-discovers both. `ImportedWatchEvent` gains three parent-context fields so `SyncOrchestrationService` can create Author/Series parent stubs before matching the Book. The file scanner gets a new post-collapse grouping step that organises books into Author → Series → Book trees using tags already extracted by TagLibSharp.

**Tech Stack:** .NET 9 / C#, React 18 + TypeScript, SQLite, TagLibSharp (already in Chronicle.Services), Hardcover GraphQL API (Bearer token), MusicBrainz REST API.

---

## Repo paths

- Main repo: `W:\Scripts\Chronicle\`
- Hardcover plugin: `W:\Scripts\Chronicle.Plugin.Hardcover\`
- MusicBrainz plugin: `W:\Scripts\Chronicle.Plugin.MusicBrainz\`

---

## Task 1: Add parent-context fields to import record types

The orchestrator needs author/series info from import events to build the hierarchy.

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Plugins\IImportProvider.cs`

**Step 1: Add fields to `ImportedWatchEvent`**

Find the `ImportedWatchEvent` record (line ~54) and add three optional fields after `EpisodeNumber`:

```csharp
public record ImportedWatchEvent(
    string ExternalId,
    IReadOnlyDictionary<string, string> AdditionalIds,
    string MediaType,
    string Title,
    int? Year,
    DateTimeOffset WatchedAt,
    double? ProgressPercent,
    string? ShowExternalId  = null,
    string? ShowTitle       = null,
    int?    SeasonNumber    = null,
    int?    EpisodeNumber   = null,
    // ── Book/audiobook parent context ────────────────────────────────────────
    /// <summary>Author name — used to find/create the Level-0 Author parent stub.</summary>
    string? AuthorName     = null,
    /// <summary>Series name — used to find/create the Level-1 Series parent stub. Null = standalone book.</summary>
    string? SeriesName     = null,
    /// <summary>Numeric position within the series (e.g. 1.0, 2.5).</summary>
    double? SeriesPosition = null
);
```

**Step 2: Add same fields to `ImportedRating`**

```csharp
public record ImportedRating(
    string ExternalId,
    IReadOnlyDictionary<string, string> AdditionalIds,
    string MediaType,
    string Title,
    int? Year,
    int Rating,
    DateTimeOffset RatedAt,
    string? AuthorName     = null,
    string? SeriesName     = null,
    double? SeriesPosition = null
);
```

**Step 3: Add same fields to `ImportedWatchlistEntry`**

```csharp
public record ImportedWatchlistEntry(
    string ExternalId,
    IReadOnlyDictionary<string, string> AdditionalIds,
    string MediaType,
    string Title,
    int? Year,
    DateTimeOffset AddedAt,
    string? AuthorName     = null,
    string? SeriesName     = null,
    double? SeriesPosition = null
);
```

**Step 4: Build to confirm no compile errors**

```powershell
cd W:\Scripts\Chronicle\src
dotnet build Chronicle.Plugins\Chronicle.Plugins.csproj
```
Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Plugins/IImportProvider.cs
git commit -m "feat(plugins): add AuthorName/SeriesName/SeriesPosition to import record types"
```

---

## Task 2: Rename "book" → "books" everywhere

The Hardcover import provider emits `MediaType: "book"` (singular). This must match Chronicle's `"books"` type name.

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.Hardcover\HardcoverImportProvider.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\SyncOrchestrationService.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Web\src\pages\media\MediaDetailPage.tsx`

**Step 1: Fix import provider — three occurrences**

In `HardcoverImportProvider.cs`, replace all three `MediaType: "book"` strings:

```csharp
// GetWatchHistoryAsync (~line 126), GetRatingsAsync (~line 159), GetWatchlistAsync (~line 187)
// Change:  MediaType: "book",
// To:      MediaType: "books",
```

**Step 2: Fix `MapMediaType` in `SyncOrchestrationService.cs`**

The switch at line ~567 passes unknown types through unchanged. Add a `"book"` → `"books"` mapping:

```csharp
private static string MapMediaType(string importType) => importType switch
{
    "movie"         => "movies",
    "tv_show"       => "tv",
    "tv_episode"    => "tv",
    "anime_episode" => "anime",
    "book"          => "books",    // ← add this line
    _               => importType,
};
```

**Step 3: Fix `MediaDetailPage.tsx` — `isBook` check already handles both**

Line 24 already reads:
```tsx
const isBook = t === 'book' || t === 'books'
```
No change needed here. Verify `isMusic` on line 23 includes `'audiobooks'` — it does.

**Step 4: Grep for any remaining `"book"` media type strings**

```powershell
cd W:\Scripts\Chronicle
grep -rn '"book"' src/ --include="*.cs" --include="*.ts" --include="*.tsx" --include="*.json"
```

Address any additional hits. Common places to check:
- `MetadataEnrichmentService.cs` — string comparisons for audiobooks/books
- Any seed migration SQL files
- Any React pages with hardcoded type names

**Step 5: Build and test**

```powershell
cd W:\Scripts\Chronicle\src && dotnet build
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity minimal
```
Expected: all 342 tests green.

**Step 6: Commit**

```powershell
cd W:\Scripts\Chronicle
git add -p   # stage changes file-by-file
git commit -m "fix: rename media type 'book' to 'books' everywhere"
```

---

## Task 3: Add `TotalDurationSeconds` to `ScannedFile`

The scanner needs to carry total audiobook duration (sum across all chapter files) forward to Chronicle.

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Plugins\ScannedFile.cs`

**Step 1: Add field after `DurationSeconds`**

```csharp
/// <summary>Duration of the media file in whole seconds, as reported by the container. Null for formats TagLib# cannot probe.</summary>
public int? DurationSeconds { get; set; }

/// <summary>
/// Total duration in seconds across all files that were collapsed into this representative entry.
/// Set by <c>CollapseAudiobooksToFolders</c> when merging a multi-file audiobook.
/// For single-file items this equals <see cref="DurationSeconds"/>.
/// </summary>
public int? TotalDurationSeconds { get; set; }
```

**Step 2: Build**

```powershell
cd W:\Scripts\Chronicle\src && dotnet build Chronicle.Plugins\Chronicle.Plugins.csproj
```

**Step 3: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Plugins/ScannedFile.cs
git commit -m "feat(plugins): add TotalDurationSeconds to ScannedFile"
```

---

## Task 4: Sum durations in `CollapseAudiobooksToFolders`

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\FileScanService.cs`

**Step 1: Write a unit test first**

In `W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\`, find or create a test file for `FileScanService`. Add:

```csharp
[Fact]
public void CollapseAudiobooksToFolders_SumsDurationsAcrossGroup()
{
    var root = @"C:\Books\Brandon Sanderson";
    var bookFolder = @"C:\Books\Brandon Sanderson\Stormlight - 1 - (2010) - The Way of Kings";
    var files = new List<ScannedFile>
    {
        new() { FilePath = bookFolder + @"\01.mp3", DurationSeconds = 1800, AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
        new() { FilePath = bookFolder + @"\02.mp3", DurationSeconds = 2100, AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
        new() { FilePath = bookFolder + @"\03.mp3", DurationSeconds = 900,  AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
    };

    // CollapseAudiobooksToFolders is private static — test via reflection or make it internal
    var result = FileScanServiceTestHelper.CollapseAudiobooksToFolders(files, root);

    Assert.Single(result);
    Assert.Equal(4800, result[0].TotalDurationSeconds); // 1800+2100+900
}
```

Note: `CollapseAudiobooksToFolders` is currently `private static`. Change its access to `internal static` to allow testing, and add `[assembly: InternalsVisibleTo("Chronicle.Tests.Unit")]` to `Chronicle.Services.csproj` if not already present.

**Step 2: Run test — confirm it fails**

```powershell
cd W:\Scripts\Chronicle\tests
dotnet test --filter "CollapseAudiobooksToFolders_Sums" --verbosity normal
```

**Step 3: Implement — sum `DurationSeconds` in the collapse loop**

In `CollapseAudiobooksToFolders`, in the subfolder branch (after picking `rep`), add:

```csharp
// Sum durations across all parts of this multi-file audiobook.
var totalDuration = group
    .Select(f => f.DurationSeconds)
    .Where(d => d.HasValue)
    .Sum(d => d!.Value);
rep.TotalDurationSeconds = totalDuration > 0 ? totalDuration : rep.DurationSeconds;
```

For the root-level single-file branch (where `result.AddRange(group)` is called), set on each file:

```csharp
foreach (var f in group)
    f.TotalDurationSeconds ??= f.DurationSeconds;
result.AddRange(group);
```

**Step 4: Run test — confirm it passes**

```powershell
cd W:\Scripts\Chronicle\tests
dotnet test --filter "CollapseAudiobooksToFolders_Sums" --verbosity normal
```

**Step 5: Also update the caller that reads `DurationSeconds` for audiobook import**

Search `FileScanService.cs` for any place that reads `scannedFile.DurationSeconds` to set `RuntimeMinutes` on a media item and change it to prefer `TotalDurationSeconds`:

```csharp
var runtimeMinutes = scannedFile.TotalDurationSeconds.HasValue
    ? (int)Math.Round(scannedFile.TotalDurationSeconds.Value / 60.0)
    : scannedFile.DurationSeconds.HasValue
        ? (int)Math.Round(scannedFile.DurationSeconds.Value / 60.0)
        : (int?)null;
```

**Step 6: Run all tests**

```powershell
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity minimal
```

**Step 7: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/FileScanService.cs src/Chronicle.Plugins/ScannedFile.cs tests/
git commit -m "feat(scanner): sum multi-file audiobook durations in CollapseAudiobooksToFolders"
```

---

## Task 5: `GroupAudiobooksByAuthorAndSeries` in FileScanService

This new step runs after `CollapseAudiobooksToFolders` and organises the flat list of one-per-book entries into an Author → Series → Book tree that Chronicle's import step can walk.

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\FileScanService.cs`

**Step 1: Write failing test**

```csharp
[Fact]
public void GroupAudiobooksByAuthorAndSeries_CreatesAuthorSeriesBookTree()
{
    var files = new List<ScannedFile>
    {
        new() { FilePath = @"C:\Books\B Sanderson\SA-1-(2010)-Way of Kings", ParsedTitle = "The Way of Kings",
                AudioAlbumArtist = "Brandon Sanderson", AudioGrouping = "Stormlight Archive",
                ParsedYear = 2010, TotalDurationSeconds = 3600 },
        new() { FilePath = @"C:\Books\B Sanderson\SA-2-(2014)-Words of Radiance", ParsedTitle = "Words of Radiance",
                AudioAlbumArtist = "Brandon Sanderson", AudioGrouping = "Stormlight Archive",
                ParsedYear = 2014, TotalDurationSeconds = 4200 },
        new() { FilePath = @"C:\Books\B Sanderson\(2005)-Elantris", ParsedTitle = "Elantris",
                AudioAlbumArtist = "Brandon Sanderson", AudioGrouping = null,
                ParsedYear = 2005, TotalDurationSeconds = 1800 },
    };

    var groups = FileScanServiceTestHelper.GroupAudiobooksByAuthorAndSeries(files);

    // One author group
    Assert.Single(groups);
    var author = groups[0];
    Assert.Equal("Brandon Sanderson", author.Name);
    Assert.Equal(0, author.HierarchyLevel);

    // Two children: one series, one standalone book
    Assert.Equal(2, author.Children.Count);

    var series = author.Children.First(c => c.Name == "Stormlight Archive");
    Assert.Equal(1, series.HierarchyLevel);
    Assert.Equal(2, series.Children.Count);

    var standalone = author.Children.First(c => c.Name == "Elantris");
    Assert.Equal(1, standalone.HierarchyLevel);
    Assert.Empty(standalone.Children);
    Assert.Single(standalone.Files);
}
```

**Step 2: Run — confirm FAIL**

```powershell
cd W:\Scripts\Chronicle\tests
dotnet test --filter "GroupAudiobooksByAuthorAndSeries_Creates" --verbosity normal
```

**Step 3: Implement `GroupAudiobooksByAuthorAndSeries`**

Add as an `internal static` method in `FileScanService.cs`:

```csharp
/// <summary>
/// Groups a flat list of collapsed audiobook entries (one per book folder) into a
/// three-level Author → Series? → Book tree for use by the audiobook import pipeline.
/// </summary>
internal static List<ScanGroup> GroupAudiobooksByAuthorAndSeries(
    IEnumerable<ScannedFile> collapsed)
{
    var authorGroups = new Dictionary<string, ScanGroup>(StringComparer.OrdinalIgnoreCase);

    foreach (var file in collapsed)
    {
        var authorName = !string.IsNullOrWhiteSpace(file.AudioAlbumArtist) ? file.AudioAlbumArtist.Trim()
                       : !string.IsNullOrWhiteSpace(file.AudioArtist)      ? file.AudioArtist.Trim()
                       : "Unknown";

        var seriesName = !string.IsNullOrWhiteSpace(file.AudioGrouping)
            ? file.AudioGrouping.Trim()
            : null;

        // Find or create Author node (level 0)
        if (!authorGroups.TryGetValue(authorName, out var authorGroup))
        {
            authorGroup = new ScanGroup
            {
                GroupKey       = authorName.ToLowerInvariant(),
                Name           = authorName,
                HierarchyLevel = 0,
                ConfidenceScore = 0.75,
                SignalSources  = ["tags"],
            };
            authorGroups[authorName] = authorGroup;
        }

        // Build the leaf ScanGroup for the book itself
        var bookName = file.ParsedTitle ?? Path.GetFileName(file.FilePath);
        var book = new ScanGroup
        {
            GroupKey        = Normalize(authorName + "/" + (seriesName ?? "") + "/" + bookName),
            Name            = bookName,
            Year            = file.ParsedYear,
            HierarchyLevel  = seriesName is not null ? 2 : 1,
            ConfidenceScore = file.ConfidenceScore / 100.0,
            SignalSources   = ["tags"],
            Files           = [file.FilePath],
            FolderPath      = file.FilePath,
        };

        if (seriesName is not null)
        {
            // Find or create Series node (level 1) under this author
            var seriesKey = Normalize(authorName + "/" + seriesName);
            var seriesGroup = authorGroup.Children
                .FirstOrDefault(c => c.GroupKey == seriesKey);

            if (seriesGroup is null)
            {
                seriesGroup = new ScanGroup
                {
                    GroupKey        = seriesKey,
                    Name            = seriesName,
                    HierarchyLevel  = 1,
                    ConfidenceScore = 0.75,
                    SignalSources   = ["tags"],
                };
                authorGroup.Children.Add(seriesGroup);
            }
            seriesGroup.Children.Add(book);
        }
        else
        {
            // Standalone book — attach directly under author at level 1
            authorGroup.Children.Add(book);
        }
    }

    return authorGroups.Values.ToList();
}

private static string Normalize(string s) => s.Trim().ToLowerInvariant();
```

**Step 4: Wire into the audiobook scan path**

In `AddFromScanAsync` (the main scan method), after the existing `CollapseAudiobooksToFolders` call:

```csharp
if (string.Equals(mediaType.Name, "audiobooks", StringComparison.OrdinalIgnoreCase))
{
    scannedFiles = CollapseAudiobooksToFolders(scannedFiles, request.Path);
    // NEW: organise into Author → Series → Book tree for 3-level import
    var audiobookTree = GroupAudiobooksByAuthorAndSeries(scannedFiles);
    return await ImportAudiobookTreeAsync(audiobookTree, mediaType, request, ct);
}
```

You will need to create `ImportAudiobookTreeAsync` that walks the Author → Series → Book tree and calls the existing stub-creation logic for each level. Mirror the existing `ImportShowGroupAsync` pattern used for TV shows. This is the most involved part of the task — each Author becomes a MediaItem at level 0, each Series at level 1, each Book at level 2 (or level 1 if standalone).

**Step 5: Run tests**

```powershell
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity minimal
```

**Step 6: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/FileScanService.cs tests/
git commit -m "feat(scanner): group audiobooks into Author→Series→Book tree for 3-level import"
```

---

## Task 6: `SyncOrchestrationService` — book parent context

When Hardcover inbound sync imports a book, create Author and Series parent stubs before matching the Book item.

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\SyncOrchestrationService.cs`

**Step 1: Add `MatchOrCreateBookAsync` routing**

In `MatchOrCreateAsync`, add routing for books/audiobooks (before the existing TV episode routing):

```csharp
// Route books/audiobooks to hierarchy builder when author context is available.
if ((evt.MediaType == "books" || evt.MediaType == "audiobooks") && evt.AuthorName is not null)
    return await MatchOrCreateBookAsync(db, evt, pluginId, ct);
```

**Step 2: Implement `MatchOrCreateBookAsync`**

```csharp
private async Task<(MediaItem item, bool isNew)> MatchOrCreateBookAsync(
    ChronicleDbContext db, ImportedWatchEvent evt, string pluginId, CancellationToken ct)
{
    var mediaTypeName = MapMediaType(evt.MediaType);
    var mediaType = await db.MediaTypes
        .FirstOrDefaultAsync(t => t.Name == mediaTypeName, ct)
        ?? throw new InvalidOperationException($"Media type '{mediaTypeName}' not found.");

    // Level 0 — Author
    var authorName = evt.AuthorName ?? "Unknown";
    var author = await db.MediaItems
        .FirstOrDefaultAsync(i => i.MediaTypeId == mediaType.Id
            && i.HierarchyLevel == 0
            && i.Name == authorName
            && i.ParentId == null, ct);
    if (author is null)
    {
        author = new MediaItem
        {
            Name           = authorName,
            MediaTypeId    = mediaType.Id,
            HierarchyLevel = 0,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        db.MediaItems.Add(author);
        await db.SaveChangesAsync(ct);
    }

    // Level 1 — Series (optional)
    MediaItem? seriesItem = null;
    if (evt.SeriesName is not null)
    {
        seriesItem = await db.MediaItems
            .FirstOrDefaultAsync(i => i.ParentId == author.Id
                && i.HierarchyLevel == 1
                && i.Name == evt.SeriesName, ct);
        if (seriesItem is null)
        {
            seriesItem = new MediaItem
            {
                Name           = evt.SeriesName,
                MediaTypeId    = mediaType.Id,
                HierarchyLevel = 1,
                ParentId       = author.Id,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            db.MediaItems.Add(seriesItem);
            await db.SaveChangesAsync(ct);
        }
    }

    // Level 2 (or 1 if standalone) — Book
    // Re-use the standard 4-stage matching for the book itself, but pass the resolved parent.
    var bookParentId = seriesItem?.Id ?? author.Id;
    var bookLevel    = seriesItem is not null ? 2 : 1;

    // Stage 1: own ExternalId
    var byOwn = await db.MediaExternalIds
        .Where(e => e.Source == SourceFromPluginId(pluginId) && e.ExternalId == evt.ExternalId)
        .Select(e => e.MediaItemId).FirstOrDefaultAsync(ct);
    if (byOwn != 0)
        return (await db.MediaItems.FindAsync([byOwn], ct)!, false);

    // Stage 2: AdditionalIds
    foreach (var (source, extId) in evt.AdditionalIds)
    {
        var byAdditional = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == extId)
            .Select(e => e.MediaItemId).FirstOrDefaultAsync(ct);
        if (byAdditional != 0)
            return (await db.MediaItems.FindAsync([byAdditional], ct)!, false);
    }

    // Stage 3: title + year under the resolved parent
    if (evt.Title is not null && evt.Year.HasValue)
    {
        var byTitle = await db.MediaItems
            .FirstOrDefaultAsync(i => i.ParentId == bookParentId
                && i.Year == evt.Year && i.Name == evt.Title, ct);
        if (byTitle is not null)
        {
            await GraftExternalIdAsync(db, byTitle.Id, pluginId, evt.ExternalId, ct);
            return (byTitle, false);
        }
    }

    // Stage 4: create stub
    var stub = new MediaItem
    {
        Name           = evt.Year.HasValue ? $"{evt.Title} ({evt.Year})" : (evt.Title ?? "Unknown"),
        Year           = evt.Year,
        MediaTypeId    = mediaType.Id,
        HierarchyLevel = bookLevel,
        ParentId       = bookParentId,
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow,
    };
    db.MediaItems.Add(stub);
    await db.SaveChangesAsync(ct);

    db.MediaExternalIds.Add(new MediaExternalId
    {
        MediaItemId = stub.Id,
        Source      = SourceFromPluginId(pluginId),
        ExternalId  = evt.ExternalId,
    });
    foreach (var (s, v) in evt.AdditionalIds)
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = stub.Id, Source = s, ExternalId = v });

    // Seed enrichment rows
    foreach (var (mpPluginId, mp, _) in _registry.GetMetadataProviderEntries())
    {
        var supported = mp.GetSupportedMediaTypes()
            .Any(t => string.Equals(t.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
        if (!supported) continue;
        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = stub.Id,
            PluginId    = mpPluginId,
            Status      = EnrichmentStatus.Pending,
            MaxRetries  = 3,
        });
    }
    await db.SaveChangesAsync(ct);

    return (stub, true);
}
```

**Step 3: Run tests**

```powershell
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity minimal
```

**Step 4: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/SyncOrchestrationService.cs
git commit -m "feat(sync): create Author/Series parent stubs for book import events"
```

---

## Task 7: MusicBrainz — 3-level audiobook hierarchy

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs`
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzSearcher.cs`

**Step 1: Update `GetSupportedMediaTypes()` in `MusicBrainzMetadataProvider.cs`**

Find the `audiobooks` `MediaTypeSupport` entry and change it:

```csharp
new MediaTypeSupport
{
    MediaTypeName   = "audiobooks",
    DisplayName     = "Audiobooks",
    HierarchyLevels = 3,
    HierarchyLabels = ["Author", "Series", "Book"],
    InteractionVerb = "listened",
    DefaultPriority = 10,
    SupportedFields = ["title", "overview", "year", "poster_url", "genres",
                       "cast", "rating", "runtime_minutes", "tags"],
},
```

**Step 2: Update `SearchAsync` routing for audiobooks**

In `RunCascadeAsync`, the `isAudiobook` branch currently treats level 0 as the book (because HierarchyLevels was 1). Now levels 0/1/2 have meaning:

```csharp
if (isAudiobook)
{
    return context.HierarchyLevel switch
    {
        0 => await RunAudiobookAuthorSearchAsync(context, titles, year, ct),
        1 => await RunAudiobookSeriesSearchAsync(context, titles, year, ct),  // nice-to-have
        _ => await RunAudiobookBookSearchAsync(context, titles, year, ct),    // existing logic
    };
}
```

For level 0 (Author), use the existing `SearchArtistsAsync`.
For level 1 (Series), add a new `SearchAudiobookSeriesAsync` (see Step 3).
For level 2 (Book), use the existing release-group search with `secondarytype:Audiobook`.

**Step 3: Add `SearchAudiobookSeriesAsync` to `MusicBrainzSearcher.cs`**

```csharp
/// <summary>
/// Searches MusicBrainz for audiobook series by name.
/// Returns an empty list (not an error) when nothing is found — the caller
/// gracefully falls back to leaving the book at Level 1 under its author.
/// </summary>
public static async Task<IReadOnlyList<MediaMetadata>> SearchAudiobookSeriesAsync(
    MusicBrainzClient client, string query, CancellationToken ct)
{
    try
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"series?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSeriesSearchResult>(json, MusicBrainzJsonOptions.Opts);
        return (result?.Series ?? [])
            .Select(s => new MediaMetadata
            {
                ExternalId = $"series:{s.Id}",
                Source     = "MusicBrainz",
                Title      = s.Name ?? string.Empty,
            })
            .ToList();
    }
    catch
    {
        // MusicBrainz series coverage is incomplete — silently return empty rather than failing enrichment.
        return [];
    }
}
```

Add the corresponding model to `MbModels.cs`:
```csharp
internal class MbSeriesSearchResult
{
    [JsonPropertyName("series")]
    public MbSeries[]? Series { get; set; }
}

internal class MbSeries
{
    [JsonPropertyName("id")]   public string? Id   { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}
```

**Step 4: Handle `series:{mbid}` in `GetByIdAsync`**

Add a `"series"` case to the existing switch:

```csharp
"series" => await MusicBrainzEntityFetcher.FetchSeriesAsync(_client!, mbid, ct),
```

Implement `FetchSeriesAsync` in `MusicBrainzEntityFetcher.cs`:

```csharp
public static async Task<MediaMetadata> FetchSeriesAsync(
    MusicBrainzClient client, string mbid, CancellationToken ct)
{
    var json = await client.GetAsync($"series/{mbid}?fmt=json", ct);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    return new MediaMetadata
    {
        ExternalId = $"series:{mbid}",
        Source     = "MusicBrainz",
        Title      = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
    };
}
```

**Step 5: Build MusicBrainz plugin**

```powershell
cd W:\Scripts\Chronicle.Plugin.MusicBrainz
dotnet build
```

**Step 6: Run MusicBrainz tests**

```powershell
cd W:\Scripts\Chronicle.Plugin.MusicBrainz\tests
dotnet test --verbosity minimal
```

**Step 7: Deploy updated DLL**

```powershell
cd W:\Scripts\Chronicle.Plugin.MusicBrainz
dotnet publish -c Release -o bin/publish
Copy-Item bin/publish/Chronicle.Plugin.MusicBrainz.dll W:\Scripts\Chronicle\plugins\chronicle.plugin.musicbrainz\
```

**Step 8: Commit MusicBrainz repo**

```powershell
cd W:\Scripts\Chronicle.Plugin.MusicBrainz
git add -A
git commit -m "feat: 3-level audiobook hierarchy (Author→Series→Book); add series search"
```

---

## Task 8: Hardcover — new models (`HardcoverModels.cs`)

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.Hardcover\HardcoverModels.cs`

Add the new rich models for metadata queries (append to the existing file, keeping existing models):

```csharp
// ── Search result shapes (results field is jsonb — use JsonElement) ──────────

internal class SearchData
{
    [JsonPropertyName("search")]
    public SearchOutput? Search { get; set; }
}

internal class SearchOutput
{
    [JsonPropertyName("results")]
    public JsonElement Results { get; set; }
}

// ── Author ────────────────────────────────────────────────────────────────────

internal class AuthorData
{
    [JsonPropertyName("authors")]
    public HcAuthor[]? Authors { get; set; }
}

internal class HcAuthor
{
    [JsonPropertyName("id")]    public int     Id   { get; set; }
    [JsonPropertyName("name")]  public string  Name { get; set; } = string.Empty;
    [JsonPropertyName("bio")]   public string? Bio  { get; set; }
    [JsonPropertyName("slug")]  public string? Slug { get; set; }
    [JsonPropertyName("image")] public HcImage? Image { get; set; }
}

// ── Series ────────────────────────────────────────────────────────────────────

internal class SeriesData
{
    [JsonPropertyName("series")]
    public HcSeries[]? Series { get; set; }
}

internal class HcSeries
{
    [JsonPropertyName("id")]           public int     Id           { get; set; }
    [JsonPropertyName("name")]         public string  Name         { get; set; } = string.Empty;
    [JsonPropertyName("description")]  public string? Description  { get; set; }
    [JsonPropertyName("slug")]         public string? Slug         { get; set; }
    [JsonPropertyName("is_completed")] public bool?   IsCompleted  { get; set; }
    [JsonPropertyName("book_series")]  public HcBookSeriesEntry[]? BookSeries { get; set; }
}

internal class HcBookSeriesEntry
{
    [JsonPropertyName("position")] public double?     Position { get; set; }
    [JsonPropertyName("book")]     public HcBookStub? Book     { get; set; }
}

internal class HcBookStub
{
    [JsonPropertyName("id")]    public int     Id    { get; set; }
    [JsonPropertyName("title")] public string  Title { get; set; } = string.Empty;
    [JsonPropertyName("image")] public HcImage? Image { get; set; }
}

// ── Book detail ───────────────────────────────────────────────────────────────

internal class BookDetailData
{
    [JsonPropertyName("books")]
    public HcBookDetail[]? Books { get; set; }
}

internal class HcBookDetail
{
    [JsonPropertyName("id")]               public int               Id            { get; set; }
    [JsonPropertyName("title")]            public string            Title         { get; set; } = string.Empty;
    [JsonPropertyName("subtitle")]         public string?           Subtitle      { get; set; }
    [JsonPropertyName("description")]      public string?           Description   { get; set; }
    [JsonPropertyName("release_year")]     public int?              ReleaseYear   { get; set; }
    [JsonPropertyName("pages")]            public int?              Pages         { get; set; }
    [JsonPropertyName("rating")]           public double?           Rating        { get; set; }
    [JsonPropertyName("ratings_count")]    public int?              RatingsCount  { get; set; }
    [JsonPropertyName("cached_tags")]      public JsonElement?      CachedTags    { get; set; }
    [JsonPropertyName("image")]            public HcImage?          Image         { get; set; }
    [JsonPropertyName("contributions")]    public HcContribution[]? Contributions { get; set; }
    [JsonPropertyName("book_series")]      public HcBookSeries[]?   BookSeries    { get; set; }
    [JsonPropertyName("book_mappings")]    public HcBookMapping[]?  BookMappings  { get; set; }
    [JsonPropertyName("default_physical_edition")] public HcEdition? DefaultEdition { get; set; }
}

internal class HcImage
{
    [JsonPropertyName("url")] public string? Url { get; set; }
}

internal class HcContribution
{
    [JsonPropertyName("author")]       public HcAuthorStub? Author       { get; set; }
    [JsonPropertyName("contribution")] public string?       Contribution { get; set; }
}

internal class HcAuthorStub
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

internal class HcBookSeries
{
    [JsonPropertyName("position")] public double?    Position { get; set; }
    [JsonPropertyName("series")]   public HcSeriesStub? Series { get; set; }
}

internal class HcSeriesStub
{
    [JsonPropertyName("id")]   public int    Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

internal class HcEdition
{
    [JsonPropertyName("audio_seconds")]  public int?           AudioSeconds { get; set; }
    [JsonPropertyName("narrations")]     public HcNarration[]? Narrations   { get; set; }
}

internal class HcNarration
{
    [JsonPropertyName("narrator")] public HcNarratorStub? Narrator { get; set; }
}

internal class HcNarratorStub
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

// ── Slug resolution ───────────────────────────────────────────────────────────

internal class SlugLookupData<T>
{
    [JsonPropertyName("books")]   public T[]? Books   { get; set; }
    [JsonPropertyName("series")]  public T[]? Series  { get; set; }
    [JsonPropertyName("authors")] public T[]? Authors { get; set; }
}

internal class HcIdOnly
{
    [JsonPropertyName("id")] public int Id { get; set; }
}
```

**Step: Build**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover && dotnet build
```

**Step: Commit**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover
git add HardcoverModels.cs
git commit -m "feat: add rich metadata models for author/series/book detail queries"
```

---

## Task 9: Hardcover — new GraphQL queries in `HardcoverClient.cs`

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.Hardcover\HardcoverClient.cs`

Add six new public methods. The existing `QueryAsync<T>` private method handles auth, retry, and JSON deserialization — just add the query string.

```csharp
// ── Search ────────────────────────────────────────────────────────────────────

public Task<SearchData?> SearchAuthorsAsync(string query, int perPage = 10, CancellationToken ct = default) =>
    QueryAsync<SearchData>("""
        query SearchAuthors($q: String!, $n: Int!) {
          search(query: $q, query_type: "Author", per_page: $n) { results }
        }
        """, new { q = query, n = perPage }, ct);

public Task<SearchData?> SearchSeriesAsync(string query, int perPage = 10, CancellationToken ct = default) =>
    QueryAsync<SearchData>("""
        query SearchSeries($q: String!, $n: Int!) {
          search(query: $q, query_type: "Series", per_page: $n) { results }
        }
        """, new { q = query, n = perPage }, ct);

public Task<SearchData?> SearchBooksAsync(string query, int perPage = 10, CancellationToken ct = default) =>
    QueryAsync<SearchData>("""
        query SearchBooks($q: String!, $n: Int!) {
          search(query: $q, query_type: "book", per_page: $n) { results }
        }
        """, new { q = query, n = perPage }, ct);

// ── Detail fetches ────────────────────────────────────────────────────────────

public Task<AuthorData?> GetAuthorByIdAsync(int id, CancellationToken ct = default) =>
    QueryAsync<AuthorData>("""
        query GetAuthor($id: Int!) {
          authors(where: { id: { _eq: $id } }) {
            id name bio slug
            image { url }
          }
        }
        """, new { id }, ct);

public Task<SeriesData?> GetSeriesByIdAsync(int id, CancellationToken ct = default) =>
    QueryAsync<SeriesData>("""
        query GetSeries($id: Int!) {
          series(where: { id: { _eq: $id } }) {
            id name description slug is_completed
            book_series(order_by: { position: asc }, limit: 1) {
              book { id title image { url } }
            }
          }
        }
        """, new { id }, ct);

public Task<BookDetailData?> GetBookByIdAsync(int id, CancellationToken ct = default) =>
    QueryAsync<BookDetailData>("""
        query GetBook($id: Int!) {
          books(where: { id: { _eq: $id } }) {
            id title subtitle description release_year pages rating ratings_count
            cached_tags
            image { url }
            contributions { author { id name } contribution }
            book_series { position series { id name } }
            book_mappings { isbn_13 isbn_10 }
            default_physical_edition {
              audio_seconds
              narrations { narrator { name } }
            }
          }
        }
        """, new { id }, ct);

// ── Slug resolution (Fix Match) ───────────────────────────────────────────────

public Task<SlugLookupData<HcIdOnly>?> GetBookBySlugAsync(string slug, CancellationToken ct = default) =>
    QueryAsync<SlugLookupData<HcIdOnly>>("""
        query GetBookBySlug($slug: String!) {
          books(where: { slug: { _eq: $slug } }, limit: 1) { id }
        }
        """, new { slug }, ct);

public Task<SlugLookupData<HcIdOnly>?> GetSeriesBySlugAsync(string slug, CancellationToken ct = default) =>
    QueryAsync<SlugLookupData<HcIdOnly>>("""
        query GetSeriesBySlug($slug: String!) {
          series(where: { slug: { _eq: $slug } }, limit: 1) { id }
        }
        """, new { slug }, ct);

public Task<SlugLookupData<HcIdOnly>?> GetAuthorBySlugAsync(string slug, CancellationToken ct = default) =>
    QueryAsync<SlugLookupData<HcIdOnly>>("""
        query GetAuthorBySlug($slug: String!) {
          authors(where: { slug: { _eq: $slug } }, limit: 1) { id }
        }
        """, new { slug }, ct);
```

**Step: Build**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover && dotnet build
```

**Step: Commit**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover
git add HardcoverClient.cs
git commit -m "feat: add author/series/book search and detail query methods to HardcoverClient"
```

---

## Task 10: Hardcover — `HardcoverMetadataProvider.cs` (new file)

This is the main implementation task. The provider is large; structure it carefully.

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.Hardcover\HardcoverMetadataProvider.cs`

**Step 1: Skeleton + `GetSupportedMediaTypes` + `GetSettingsSchema` + `Configure`**

```csharp
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Hardcover;

public sealed class HardcoverMetadataProvider : IMetadataProvider
{
    public string PluginId => "hardcover";
    public string Name     => "Hardcover";
    public string Version  => "1.1.0";
    public string Author   => "Chronicle Contributors";

    private const string KeyApiToken = "api_token";   // shared with HardcoverImportProvider
    private HardcoverClient? _client;

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "books",
            DisplayName     = "Books",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url",
                               "genres", "cast", "rating", "tags"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "audiobooks",
            DisplayName     = "Audiobooks",
            HierarchyLevels = 3,
            HierarchyLabels = ["Author", "Series", "Book"],
            InteractionVerb = "listened",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url",
                               "genres", "cast", "rating", "runtime_minutes", "tags"],
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyApiToken,
                Label       = "Hardcover API Token",
                Description = "Your personal API token from hardcover.app/account/api.",
                Type        = SettingType.Password,
                Required    = true,
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyApiToken, out var token);
        _client?.Dispose();
        _client = string.IsNullOrWhiteSpace(token) ? null : new HardcoverClient(token.Trim());
    }

    // ... (SearchAsync, GetByIdAsync, GetImageAsync, HealthCheckAsync follow below)
}
```

**Step 2: Implement `SearchAsync`**

```csharp
public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
    MediaSearchContext context, CancellationToken ct = default)
{
    EnsureConfigured();

    var titles = context.AltTitles?.Count > 0
        ? context.AltTitles
        : (IReadOnlyList<string>)[context.Name];

    return context.HierarchyLevel switch
    {
        0 => await SearchAuthorsAsync(context, titles, ct),
        1 => await SearchSeriesAsync(context, titles, ct),
        _ => await SearchBooksAsync(context, titles, ct),
    };
}

private async Task<IReadOnlyList<ScoredCandidate>> SearchAuthorsAsync(
    MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
{
    foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
    {
        var data = await _client!.SearchAuthorsAsync(title, ct: ct);
        var hits = ParseSearchResults(data?.Search?.Results);
        if (hits.Count == 0) continue;

        var candidates = hits
            .Select(h => ScoreAuthorCandidate(ctx, h))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
        if (candidates.Any(c => c.Score >= 65)) return candidates;
        if (candidates.Count > 0) return candidates;
    }
    return [];
}

private async Task<IReadOnlyList<ScoredCandidate>> SearchSeriesAsync(
    MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
{
    foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)))
    {
        var query = ctx.ParentName is not null ? $"{title} {ctx.ParentName}" : title;
        var data  = await _client!.SearchSeriesAsync(query, ct: ct);
        var hits  = ParseSearchResults(data?.Search?.Results);
        if (hits.Count == 0)
        {
            // Retry with title only if combined search returned nothing
            if (ctx.ParentName is not null)
            {
                data = await _client!.SearchSeriesAsync(title, ct: ct);
                hits = ParseSearchResults(data?.Search?.Results);
            }
        }
        if (hits.Count == 0) continue;

        var candidates = hits
            .Select(h => ScoreSeriesCandidate(ctx, h))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
        if (candidates.Count > 0) return candidates;
    }
    return [];
}

private async Task<IReadOnlyList<ScoredCandidate>> SearchBooksAsync(
    MediaSearchContext ctx, IReadOnlyList<string> titles, CancellationToken ct)
{
    // 4-stage cascade: PreciseName+year → AltTitles+year → AltTitles → FilenameStem
    var allCandidates = new List<ScoredCandidate>();

    async Task<bool> TryQuery(string queryTitle, bool useYear)
    {
        var q = ctx.ParentName is not null ? $"{queryTitle} {ctx.ParentName}" : queryTitle;
        var data = await _client!.SearchBooksAsync(q, ct: ct);
        var hits = ParseSearchResults(data?.Search?.Results);
        if (hits.Count == 0 && ctx.ParentName is not null)
        {
            // Retry without author
            data = await _client!.SearchBooksAsync(queryTitle, ct: ct);
            hits = ParseSearchResults(data?.Search?.Results);
        }
        if (hits.Count == 0) return false;

        var scored = hits
            .Select(h => ScoreBookCandidate(ctx, h, useYear))
            .Where(c => c.Score > 0)
            .ToList();
        allCandidates.AddRange(scored);
        return scored.Any(c => c.Score >= 65);
    }

    // Stage 1a: PreciseName + year
    if (ctx.PreciseName is not null && ctx.Year.HasValue)
        if (await TryQuery(ctx.PreciseName, true)) goto done;

    // Stage 1b: AltTitles + year
    if (ctx.Year.HasValue)
        foreach (var t in titles)
            if (!string.IsNullOrWhiteSpace(t) && await TryQuery(t, true)) goto done;

    // Stage 2a: AltTitles, no year
    foreach (var t in titles)
        if (!string.IsNullOrWhiteSpace(t)) await TryQuery(t, false);

    // Stage 2b: FilenameStem
    if (ctx.FilenameStem is not null &&
        !string.Equals(ctx.FilenameStem, ctx.Name, StringComparison.OrdinalIgnoreCase))
        await TryQuery(ctx.FilenameStem, false);

    done:
    return allCandidates
        .GroupBy(c => c.Metadata.ExternalId)
        .Select(g => g.OrderByDescending(c => c.Score).First())
        .OrderByDescending(c => c.Score)
        .ThenByDescending(c => GetRatingsCount(c.Metadata))
        .Take(10)
        .ToList();
}
```

**Step 3: Scoring helpers**

```csharp
private static ScoredCandidate ScoreAuthorCandidate(MediaSearchContext ctx, Dictionary<string, JsonElement> hit)
{
    var id    = GetInt(hit, "id");
    var name  = GetStr(hit, "name") ?? string.Empty;
    var photo = GetStr(hit, "image_url") ?? GetStr(hit, "image");
    var meta  = new MediaMetadata
    {
        ExternalId = id > 0 ? $"hardcover:author:{id}" : null,
        Source     = "hardcover",
        Title      = name,
        PosterUrl  = photo,
    };
    if (meta.ExternalId is null) return new ScoredCandidate(meta, 0, "no id");
    var (score, reason) = ScoreTitle(ctx, name);
    return new ScoredCandidate(meta, score, reason);
}

private static ScoredCandidate ScoreSeriesCandidate(MediaSearchContext ctx, Dictionary<string, JsonElement> hit)
{
    var id   = GetInt(hit, "id");
    var name = GetStr(hit, "name") ?? string.Empty;
    var meta = new MediaMetadata
    {
        ExternalId = id > 0 ? $"hardcover:series:{id}" : null,
        Source     = "hardcover",
        Title      = name,
    };
    if (meta.ExternalId is null) return new ScoredCandidate(meta, 0, "no id");
    var (score, reason) = ScoreTitle(ctx, name);
    return new ScoredCandidate(meta, score, reason);
}

private static ScoredCandidate ScoreBookCandidate(MediaSearchContext ctx, Dictionary<string, JsonElement> hit, bool useYear)
{
    var id        = GetInt(hit, "id");
    var title     = GetStr(hit, "title") ?? string.Empty;
    var year      = GetInt(hit, "release_year");
    var imageUrl  = GetStr(hit, "image") ?? GetStr(hit, "cached_image");
    var authorStr = GetStr(hit, "author_names") ?? string.Empty;

    var meta = new MediaMetadata
    {
        ExternalId = id > 0 ? $"hardcover:{id}" : null,
        Source     = "hardcover",
        Title      = title,
        Year       = year > 0 ? year : null,
        PosterUrl  = imageUrl,
        ExtendedData = JsonSerializer.SerializeToElement(new { ratings_count = GetInt(hit, "ratings_count") }),
    };
    if (meta.ExternalId is null) return new ScoredCandidate(meta, 0, "no id");

    var (score, reasons) = ScoreTitle(ctx, title);
    var reasonList = new List<string> { reasons };

    // Year signals
    if (useYear && ctx.Year.HasValue && meta.Year.HasValue)
    {
        if (ctx.Year == meta.Year)        { score += 20; reasonList.Add("year exact"); }
        else if (Math.Abs(ctx.Year.Value - meta.Year.Value) == 1) { score += 10; reasonList.Add("year ±1"); }
        else                              { score -= 10; reasonList.Add("year mismatch"); }
    }

    // PreciseName bonus
    if (ctx.PreciseName is not null)
    {
        if (string.Equals(ctx.PreciseName, title, StringComparison.OrdinalIgnoreCase)) { score += 15; reasonList.Add("precise exact"); }
        else if (title.Contains(ctx.PreciseName, StringComparison.OrdinalIgnoreCase))   { score += 5;  reasonList.Add("precise partial"); }
    }

    // Author match
    if (ctx.ParentName is not null && !string.IsNullOrEmpty(authorStr))
    {
        var pn = Normalize(ctx.ParentName);
        var an = Normalize(authorStr);
        if (an.Contains(pn, StringComparison.Ordinal))        { score += 20; reasonList.Add("author exact"); }
        else if (an.Split(' ').Any(w => pn.Contains(w)))      { score += 10; reasonList.Add("author partial"); }
    }

    return new ScoredCandidate(meta, Math.Max(0, score), string.Join(", ", reasonList));
}

private static (int score, string reason) ScoreTitle(MediaSearchContext ctx, string candidateTitle)
{
    var cn = Normalize(candidateTitle);
    var qn = Normalize(ctx.AltTitles?.FirstOrDefault() ?? ctx.Name);
    if (string.Equals(cn, qn, StringComparison.Ordinal))                                return (60, "title exact");
    if (cn.Contains(qn, StringComparison.Ordinal) || qn.Contains(cn, StringComparison.Ordinal)) return (30, "title contains");
    return (0, "no title match");
}

private static string Normalize(string s) =>
    Regex.Replace(s.Trim(), @"[:\-,\.']", " ").Replace("  ", " ").Trim().ToLowerInvariant();

private static int GetRatingsCount(MediaMetadata m)
{
    if (m.ExtendedData is not { } ext) return 0;
    if (ext.TryGetProperty("ratings_count", out var p) && p.ValueKind == JsonValueKind.Number)
        return p.GetInt32();
    return 0;
}

// Parses the jsonb search results array into a list of property dictionaries.
private static List<Dictionary<string, JsonElement>> ParseSearchResults(JsonElement results)
{
    var list = new List<Dictionary<string, JsonElement>>();
    if (results.ValueKind != JsonValueKind.Array) return list;
    foreach (var item in results.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.Object) continue;
        var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in item.EnumerateObject())
            d[prop.Name] = prop.Value;
        list.Add(d);
    }
    return list;
}

private static string? GetStr(Dictionary<string, JsonElement> d, string key) =>
    d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

private static int GetInt(Dictionary<string, JsonElement> d, string key) =>
    d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
```

**Step 4: Implement `GetByIdAsync`**

```csharp
public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
{
    EnsureConfigured();

    // Normalise Hardcover URLs to typed IDs
    if (externalId.StartsWith("https://hardcover.app/", StringComparison.OrdinalIgnoreCase))
        externalId = await ResolveHardcoverUrlAsync(externalId, ct);

    if (externalId.StartsWith("hardcover:author:", StringComparison.OrdinalIgnoreCase))
    {
        var id = int.Parse(externalId["hardcover:author:".Length..]);
        return await FetchAuthorAsync(id, ct);
    }
    if (externalId.StartsWith("hardcover:series:", StringComparison.OrdinalIgnoreCase))
    {
        var id = int.Parse(externalId["hardcover:series:".Length..]);
        return await FetchSeriesAsync(id, ct);
    }
    {
        var id = int.Parse(externalId["hardcover:".Length..]);
        return await FetchBookAsync(id, ct);
    }
}

private async Task<string> ResolveHardcoverUrlAsync(string url, CancellationToken ct)
{
    // https://hardcover.app/books/{slug}   → hardcover:{id}
    // https://hardcover.app/series/{slug}  → hardcover:series:{id}
    // https://hardcover.app/authors/{slug} → hardcover:author:{id}
    var uri      = new Uri(url);
    var segments = uri.AbsolutePath.Trim('/').Split('/');
    if (segments.Length < 2) return url;

    var (entityType, slug) = (segments[0], segments[1]);
    return entityType switch
    {
        "books"   => $"hardcover:{(await _client!.GetBookBySlugAsync(slug, ct))?.Books?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Book slug '{slug}' not found")}",
        "series"  => $"hardcover:series:{(await _client!.GetSeriesBySlugAsync(slug, ct))?.Series?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Series slug '{slug}' not found")}",
        "authors" => $"hardcover:author:{(await _client!.GetAuthorBySlugAsync(slug, ct))?.Authors?.FirstOrDefault()?.Id ?? throw new ArgumentException($"Author slug '{slug}' not found")}",
        _ => url
    };
}

private async Task<MediaMetadata> FetchAuthorAsync(int id, CancellationToken ct)
{
    var data   = await _client!.GetAuthorByIdAsync(id, ct);
    var author = data?.Authors?.FirstOrDefault()
        ?? throw new InvalidOperationException($"Hardcover author {id} not found.");
    return new MediaMetadata
    {
        ExternalId = $"hardcover:author:{author.Id}",
        Source     = "hardcover",
        Title      = author.Name,
        Overview   = author.Bio,
        PosterUrl  = author.Image?.Url,
    };
}

private async Task<MediaMetadata> FetchSeriesAsync(int id, CancellationToken ct)
{
    var data   = await _client!.GetSeriesByIdAsync(id, ct);
    var series = data?.Series?.FirstOrDefault()
        ?? throw new InvalidOperationException($"Hardcover series {id} not found.");
    var posterUrl = series.BookSeries?.FirstOrDefault()?.Book?.Image?.Url;
    return new MediaMetadata
    {
        ExternalId   = $"hardcover:series:{series.Id}",
        Source       = "hardcover",
        Title        = series.Name,
        Overview     = series.Description,
        PosterUrl    = posterUrl,
        ExtendedData = JsonSerializer.SerializeToElement(new { is_completed = series.IsCompleted }),
    };
}

private async Task<MediaMetadata> FetchBookAsync(int id, CancellationToken ct)
{
    var data = await _client!.GetBookByIdAsync(id, ct);
    var book = data?.Books?.FirstOrDefault()
        ?? throw new InvalidOperationException($"Hardcover book {id} not found.");

    var genres = ExtractGenres(book.CachedTags);
    var cast   = BuildCast(book.Contributions, book.DefaultEdition?.Narrations);
    var seriesEntry = book.BookSeries?.FirstOrDefault();
    var isbn13 = book.BookMappings?.FirstOrDefault()?.Isbn13;
    var isbn10 = book.BookMappings?.FirstOrDefault()?.Isbn10;

    var extData = new Dictionary<string, object?>();
    if (book.Pages.HasValue)      extData["pages"]          = book.Pages;
    if (seriesEntry is not null)  extData["series_name"]    = seriesEntry.Series?.Name;
    if (seriesEntry is not null)  extData["series_position"] = seriesEntry.Position;
    if (isbn13 is not null)       extData["isbn13"]         = isbn13;
    if (isbn10 is not null)       extData["isbn10"]         = isbn10;
    if (book.RatingsCount.HasValue) extData["ratings_count"] = book.RatingsCount;

    return new MediaMetadata
    {
        ExternalId     = $"hardcover:{book.Id}",
        Source         = "hardcover",
        Title          = book.Title,
        Overview       = book.Description,
        Year           = book.ReleaseYear,
        PosterUrl      = book.Image?.Url,
        Rating         = book.Rating,
        Genres         = genres,
        Cast           = cast,
        RuntimeMinutes = book.DefaultEdition?.AudioSeconds.HasValue == true
                         ? (int)Math.Round(book.DefaultEdition.AudioSeconds.Value / 60.0)
                         : null,
        ExtendedData   = JsonSerializer.SerializeToElement(extData),
    };
}

private static List<string> BuildCast(HcContribution[]? contributions, HcNarration[]? narrations)
{
    // Cast list is role-prefixed so the UI can display Author/Narrator differently.
    // Format: "Author:Patrick Rothfuss", "Narrator:Nick Podehl"
    // The MediaDetailPage reads the role prefix to display narrators prominently.
    var list = new List<string>();
    foreach (var c in contributions ?? [])
        if (c.Author is not null)
            list.Add($"{(c.Contribution ?? "Author")}:{c.Author.Name}");
    foreach (var n in narrations ?? [])
        if (n.Narrator is not null)
            list.Add($"Narrator:{n.Narrator.Name}");
    return list;
}

private static List<string> ExtractGenres(JsonElement? cachedTags)
{
    var genres = new List<string>();
    if (cachedTags is not { } tags || tags.ValueKind != JsonValueKind.Array) return genres;
    foreach (var tag in tags.EnumerateArray())
    {
        // cached_tags structure varies — try common shapes
        if (tag.TryGetProperty("tag", out var t) && t.ValueKind == JsonValueKind.String)
            genres.Add(t.GetString()!);
        else if (tag.ValueKind == JsonValueKind.String)
            genres.Add(tag.GetString()!);
    }
    return genres;
}
```

**Step 5: `GetImageAsync` and `HealthCheckAsync`**

```csharp
public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
{
    EnsureConfigured();
    return await _client!.GetBytesAsync(url, ct);
}

public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
{
    if (_client is null) return false;
    try
    {
        var me = await _client.GetMeAsync(ct);
        return me?.Me is { Length: > 0 };
    }
    catch { return false; }
}

private void EnsureConfigured()
{
    if (_client is null)
        throw new InvalidOperationException(
            "HardcoverMetadataProvider has not been configured. Call Configure() first.");
}
```

Note: `GetBytesAsync` doesn't exist on `HardcoverClient` yet — add it:

In `HardcoverClient.cs`:
```csharp
public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
{
    using var resp = await _http.GetAsync(url, ct);
    resp.EnsureSuccessStatusCode();
    return await resp.Content.ReadAsByteArrayAsync(ct);
}
```

**Step 6: Build**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover && dotnet build
```
Fix any compilation errors before continuing.

**Step 7: Commit**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover
git add HardcoverMetadataProvider.cs HardcoverClient.cs
git commit -m "feat: add HardcoverMetadataProvider with 3-level Author→Series→Book hierarchy"
```

---

## Task 11: Update `HardcoverImportProvider` — "book" → "books" and parent fields

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.Hardcover\HardcoverImportProvider.cs`

**Step 1: Rename media type (3 occurrences)**

```csharp
// GetWatchHistoryAsync
MediaType: "books",

// GetRatingsAsync
MediaType: "books",

// GetWatchlistAsync
MediaType: "books",
```

**Step 2: Populate `AuthorName` and `SeriesName` in `GetWatchHistoryAsync`**

The current `UserBook.Book` model (`HcBook`) is lightweight. The import queries need enriched data. Option A: add `contributions` and `book_series` to the existing `GetReadBooksAsync` query. Option B: fetch enriched data lazily per item. Choose **Option A** (one query, include needed fields):

Update the `GetReadBooksAsync` query in `HardcoverClient.cs`:

```graphql
query GetReadBooks {
  user_books(where: { status_id: { _eq: 3 } }, limit: 1000) {
    id
    book {
      id title release_year
      book_mappings { isbn_13 isbn_10 }
      contributions(limit: 1) { author { name } }
      book_series(limit: 1) { position series { name } }
    }
    rating
    user_book_reads(order_by: { finished_at: desc }, limit: 1) {
      finished_at started_at
    }
  }
}
```

Add `Contributions` and `BookSeries` to `HcBook` in `HardcoverModels.cs` (the lightweight import model):

```csharp
internal class HcBook
{
    // ... existing fields ...
    [JsonPropertyName("contributions")]  public HcContribution[]? Contributions { get; set; }
    [JsonPropertyName("book_series")]    public HcBookSeries[]?   BookSeries    { get; set; }
}
```

**Step 3: Pass author/series into `ImportedWatchEvent`**

```csharp
result.Add(new ImportedWatchEvent(
    ExternalId:      $"hardcover:{ub.Book.Id}",
    AdditionalIds:   BuildIds(ub),
    MediaType:       "books",
    Title:           ub.Book.Title,
    Year:            ub.Book.ReleaseYear,
    WatchedAt:       watchedAt,
    ProgressPercent: 100.0,
    AuthorName:      ub.Book.Contributions?.FirstOrDefault()?.Author?.Name,
    SeriesName:      ub.Book.BookSeries?.FirstOrDefault()?.Series?.Name,
    SeriesPosition:  ub.Book.BookSeries?.FirstOrDefault()?.Position
));
```

Apply the same pattern to `GetRatingsAsync` and `GetWatchlistAsync` (update their queries and constructors similarly).

**Step 4: Build**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover && dotnet build
```

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover
git add HardcoverImportProvider.cs HardcoverClient.cs HardcoverModels.cs
git commit -m "fix: rename media type to books; populate AuthorName/SeriesName in import events"
```

---

## Task 12: Update `manifest.json`

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.Hardcover\manifest.json`

Replace entirely:

```json
{
  "plugin_id": "hardcover",
  "name": "Hardcover",
  "version": "1.1.0",
  "author": "Chronicle Contributors",
  "description": "Book and audiobook metadata from Hardcover.app, plus reading history import.",
  "entry_type": "Chronicle.Plugin.Hardcover.HardcoverImportProvider",
  "min_chronicle_version": "0.1.0",
  "repository": "https://github.com/thegoddamnbeckster/Chronicle.Plugin.Hardcover",
  "iconUrl": "https://hardcover.app/favicon.ico",
  "brandColorLight": "#8b5cf6",
  "brandColorDark": "#7c3aed",
  "fixMatchHint": "Paste a Hardcover book, series, or author URL (e.g. https://hardcover.app/books/the-way-of-kings)",
  "background_tasks": [
    {
      "task_id": "import-all",
      "display_name": "Import All",
      "description": "Imports full reading history, ratings, and want-to-read list from Hardcover.",
      "default_cron": "0 3 * * *",
      "default_enabled": false
    },
    {
      "task_id": "delta-sync",
      "display_name": "Delta Sync",
      "description": "Imports reading activity added since the last sync.",
      "default_cron": "0 * * * *",
      "default_enabled": true
    }
  ]
}
```

**Step: Commit**

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover
git add manifest.json
git commit -m "chore: bump version to 1.1.0; add brandColor and fixMatchHint"
```

---

## Task 13: Build and deploy the Hardcover plugin

```powershell
cd W:\Scripts\Chronicle.Plugin.Hardcover
dotnet publish -c Release -o bin/publish

# Deploy DLL and manifest
$dest = "W:\Scripts\Chronicle\plugins\hardcover"
New-Item -ItemType Directory -Force $dest
Copy-Item bin/publish/Chronicle.Plugin.Hardcover.dll $dest\
Copy-Item manifest.json $dest\
```

Restart the Chronicle API and verify the plugin loads:

```powershell
# In browser: GET http://localhost:7979/api/v1/plugins
# Expect: hardcover plugin listed with both import + metadata capabilities
```

---

## Task 14: UI — narrator prominence in `MediaDetailPage.tsx`

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Web\src\pages\media\MediaDetailPage.tsx`

The narrator lives in the item's cast credits. The cast is accessible via `item.cast` in the API response (populated from `media_credits` table). Check what the API actually returns for cast on a book/audiobook item — look at the `MediaItemDto` or the response from `GET /api/v1/media/{id}`.

**Step 1: Extract narrators from cast**

In the component body, after the item query:

```tsx
// Parse narrator(s) from cast — format is "Narrator:Name" (set by Hardcover plugin)
const narrators = useMemo(() => {
  if (!item?.cast) return []
  return item.cast
    .filter((c: string) => c.startsWith('Narrator:'))
    .map((c: string) => c.replace('Narrator:', ''))
}, [item?.cast])

const isAudiobookType = item?.mediaTypeName?.toLowerCase() === 'audiobooks'
```

**Step 2: Render narrator line**

Find where the item title is rendered in the JSX. Immediately after the title/year block and after any author line, add:

```tsx
{isAudiobookType && narrators.length > 0 && (
  <p className={styles.narratorLine}>
    Narrated by {narrators.join(', ')}
  </p>
)}
```

**Step 3: Add `narratorLine` style to `MediaDetailPage.module.css`**

```css
.narratorLine {
  margin: 4px 0 12px;
  font-size: 0.95rem;
  color: var(--text-secondary);
  font-style: italic;
}
```

**Step 4: Type-check**

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web
npm run type-check
```

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx
git add src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css
git commit -m "feat(ui): show narrator prominently on audiobook MediaDetailPage"
```

---

## Task 15: Run all tests and push

```powershell
# Main repo tests
cd W:\Scripts\Chronicle\tests
dotnet test --verbosity normal

# MusicBrainz plugin tests
cd W:\Scripts\Chronicle.Plugin.MusicBrainz\tests
dotnet test --verbosity normal

# Frontend type-check
cd W:\Scripts\Chronicle\src\Chronicle.Web
npm run type-check && npm run lint
```

All green. Then push both repos:

```powershell
cd W:\Scripts\Chronicle
git push

cd W:\Scripts\Chronicle.Plugin.Hardcover
git push

cd W:\Scripts\Chronicle.Plugin.MusicBrainz
git push
```

---

## Smoke test checklist

After starting the API and frontend:

1. `GET /api/v1/plugins` — hardcover shows both `isImportProvider: true` and `isMetadataProvider: true`
2. Add a scan folder with type **Books** — confirm the media type selector shows "Books" (not "Book")
3. Scan an audiobook folder with multiple files — confirm one item is created, `RuntimeMinutes` is set to total duration
4. Scan an audiobook folder with author/series structure — confirm Author → Series → Book tree in Chronicle
5. Trigger Fix Match on a book item, paste a Hardcover URL (`https://hardcover.app/books/...`) — confirm it resolves to the correct book
6. Open a matched audiobook item — confirm **"Narrated by …"** line appears below the title
7. Run Hardcover delta-sync — confirm imported books land under Author (and Series if applicable) parents
8. Open the MusicBrainz enrichment on an audiobook item — confirm it searches at the correct hierarchy level (no errors if series not found)
