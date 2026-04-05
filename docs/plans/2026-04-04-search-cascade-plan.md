# Generic Hierarchical Metadata Search Cascade — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Redesign the MusicBrainz (and future TMDB) plugin search cascade into a consistent four-stage progressive strategy: exact title → fuzzy title → fuzzy + sub-item list → fuzzy + sub-item metadata, each tried with year then without year, with cumulative confidence scoring.

**Architecture:** Year is always stripped from the title string and passed as a separate validated search parameter (1900..currentYear+3). AltTitles are pre-built and tried in each stage. Sub-item names and metadata feed Stages 3–4 to identify the correct release/show by comparing structure rather than just title. Confidence accumulates across signals; the highest-scoring candidate wins when it exceeds 50.

**Tech Stack:** .NET 9, C#, MusicBrainz Lucene query API, Chronicle.Plugins (IMetadataProvider), Chronicle.Services (MetadataEnrichmentService), xUnit, Moq, FluentAssertions.

---

## Task 1: Add `SiblingInfo` record and `MediaSearchContext` fields

**Files:**
- Create: `src/Chronicle.Plugins/Models/SiblingInfo.cs`
- Modify: `src/Chronicle.Plugins/Models/MediaSearchContext.cs`

### Step 1: Create `SiblingInfo.cs`

```csharp
namespace Chronicle.Plugins.Models;

/// <summary>
/// Structured metadata for one sibling or child item, used in Stage 4 sub-item
/// metadata comparison. Fields are populated progressively: filename/path first,
/// then duration, then full tags — only as much as needed to build confidence.
/// </summary>
public record SiblingInfo(
    /// <summary>Normalised display name (tag title or filename stem).</summary>
    string Name,
    /// <summary>Track number, episode number, etc. — from filename prefix or tag.</summary>
    int?   ItemNumber      = null,
    /// <summary>Disc or season number — from folder path or tag.</summary>
    int?   DiscNumber      = null,
    /// <summary>Duration in whole seconds. Match tolerance is configurable (default ±10 s).</summary>
    int?   DurationSeconds = null,
    /// <summary>
    /// Additional tag fields keyed by lowercase tag name (e.g. "isrc", "genre").
    /// Populated only in Tier 3 (full tag read). Null when Tier 3 was not reached.
    /// </summary>
    IReadOnlyDictionary<string, string>? Tags = null
);
```

### Step 2: Add new fields to `MediaSearchContext.cs`

Add three new optional parameters at the end of the record (after `SiblingNames`):

```csharp
    /// <summary>
    /// Ordered list of alternative title forms to try in each search stage:
    /// [PreciseName?, year-stripped name, filename stem?, version-qualifier-stripped?].
    /// Duplicates are removed. Null means the plugin should fall back to <see cref="Name"/>.
    /// </summary>
    IReadOnlyList<string>? AltTitles = null,

    /// <summary>
    /// Names of direct child items for HierarchyLevel 0 (artist → albums) or
    /// HierarchyLevel 1 (album → tracks, show → episodes). Used in Stage 3 to compare
    /// the provider's sub-item list against what Chronicle already has.
    /// Null or empty for leaf items (HierarchyLevel 2) — use <see cref="SiblingNames"/> instead.
    /// </summary>
    IReadOnlyList<string>? ChildNames = null,

    /// <summary>
    /// Structured metadata for sibling items (leaf level) or child items (parent levels).
    /// Used in Stage 4 sub-item metadata comparison against the provider's data.
    /// Populated in tiers: filename/path info first, then duration, then full tags.
    /// </summary>
    IReadOnlyList<SiblingInfo>? SubItemMetadata = null
```

### Step 3: Build and confirm no errors

```
cd W:\Scripts\Chronicle
dotnet build src/Chronicle.Plugins/Chronicle.Plugins.csproj -c Debug
```
Expected: Build succeeded, 0 errors.

### Step 4: Commit

```
git add src/Chronicle.Plugins/Models/SiblingInfo.cs src/Chronicle.Plugins/Models/MediaSearchContext.cs
git commit -m "feat(plugins): add SiblingInfo record and AltTitles/ChildNames/SubItemMetadata to MediaSearchContext"
```

---

## Task 2: Year validation and `BuildAltTitles` in `MetadataEnrichmentService`

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`

### Step 1: Write failing tests for year validation

Add to `MetadataEnrichmentServiceTests.cs`:

```csharp
[Theory]
[InlineData(1899, null)]       // too old
[InlineData(1900, 1900)]       // boundary: valid
[InlineData(2024, 2024)]       // typical
[InlineData(null, null)]       // no year stays null
public void ValidateYear_ReturnsExpected(int? input, int? expected)
{
    var result = MetadataEnrichmentService.ValidateYear(input);
    result.Should().Be(expected);
}
// Note: currentYear+3 boundary test uses DateTime.Now.Year + 3
[Fact]
public void ValidateYear_FuturePlusThree_IsValid()
{
    var farFuture = DateTime.Now.Year + 3;
    MetadataEnrichmentService.ValidateYear(farFuture).Should().Be(farFuture);
}
[Fact]
public void ValidateYear_FuturePlusFour_IsNull()
{
    var tooFar = DateTime.Now.Year + 4;
    MetadataEnrichmentService.ValidateYear(tooFar).Should().BeNull();
}
```

Run: `dotnet test tests/Chronicle.Tests.Unit -c Debug --filter "ValidateYear"`
Expected: FAIL — method does not exist.

### Step 2: Add `ValidateYear` as `internal static`

In `MetadataEnrichmentService.cs`, add near the existing helper methods:

```csharp
/// <summary>
/// Returns the year if it falls within the plausible media release range
/// (1900 to current year + 3). Returns null for values outside that range
/// so plugins do not waste a search attempt on a garbage year.
/// </summary>
internal static int? ValidateYear(int? year)
{
    if (year is null) return null;
    var maxYear = DateTime.UtcNow.Year + 3;
    return year >= 1900 && year <= maxYear ? year : null;
}
```

### Step 3: Apply `ValidateYear` when building `MediaSearchContext`

Find the line in `MetadataEnrichmentService` that constructs `MediaSearchContext` (around line 786):
```csharp
Year: row.MediaItem.Year,
```
Change to:
```csharp
Year: ValidateYear(row.MediaItem.Year),
```

There is a second construction site around line 1276 — apply the same change there.

### Step 4: Write failing tests for `BuildAltTitles`

```csharp
[Fact]
public void BuildAltTitles_YearPrefix_IsStripped()
{
    var result = MetadataEnrichmentService.BuildAltTitles(
        name: "(2014) Remixed", filenameStem: null, preciseName: null);
    result.Should().Contain("Remixed");
    result.Should().NotContain("(2014)");
    result.Should().NotContain("2014");
}

[Fact]
public void BuildAltTitles_YearSuffix_IsStripped()
{
    var result = MetadataEnrichmentService.BuildAltTitles(
        name: "The Better Life (2000)", filenameStem: null, preciseName: null);
    result[0].Should().Be("The Better Life");
}

[Fact]
public void BuildAltTitles_VersionQualifier_AddsStrippedVariant()
{
    var result = MetadataEnrichmentService.BuildAltTitles(
        name: "Kryptonite (LP version)", filenameStem: "Kryptonite", preciseName: null);
    result.Should().Contain("Kryptonite (LP version)");  // original (parens kept for phrase quoting)
    result.Should().Contain("Kryptonite");               // stripped variant
}

[Fact]
public void BuildAltTitles_Deduplicates()
{
    // filenameStem same as stripped name — should not appear twice
    var result = MetadataEnrichmentService.BuildAltTitles(
        name: "Kryptonite", filenameStem: "Kryptonite", preciseName: null);
    result.Should().HaveCount(1);
}

[Fact]
public void BuildAltTitles_PreciseName_PrependedFirst()
{
    var result = MetadataEnrichmentService.BuildAltTitles(
        name: "what if", filenameStem: null, preciseName: "What If...?");
    result[0].Should().Be("What If...?");
}
```

Run: `dotnet test tests/Chronicle.Tests.Unit -c Debug --filter "BuildAltTitles"`
Expected: FAIL — method does not exist.

### Step 5: Implement `BuildAltTitles` as `internal static`

```csharp
private static readonly System.Text.RegularExpressions.Regex VersionQualifierEnrichRe =
    new(@"\s*\([^)]+\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

/// <summary>
/// Builds an ordered, deduplicated list of title forms to try in each search stage.
/// Order: PreciseName (if any) → year-stripped name → filenameStem (if different) →
/// version-qualifier-stripped form (if different).
/// </summary>
internal static IReadOnlyList<string> BuildAltTitles(
    string name, string? filenameStem, string? preciseName)
{
    var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var results = new List<string>();

    void Add(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        var trimmed = s.Trim();
        if (seen.Add(trimmed)) results.Add(trimmed);
    }

    // 1. Precise name (NFO/reliable source) first
    Add(preciseName);

    // 2. Year-stripped canonical name
    var stripped = StripYearPrefix(StripYearSuffix(name)).Trim();
    Add(string.IsNullOrWhiteSpace(stripped) ? name : stripped);

    // 3. Filename stem (often cleaner than the tag title)
    Add(filenameStem);

    // 4. Version-qualifier-stripped form (e.g. "Kryptonite" from "Kryptonite (LP version)")
    var noQualifier = VersionQualifierEnrichRe.Replace(results.Count > 0 ? results[preciseName != null ? 1 : 0] : name, string.Empty).Trim();
    Add(noQualifier);

    return results.AsReadOnly();
}
```

Note: `StripYearSuffix` already exists in the file (check exact name and location — there may be a `StripYearPrefix` regex too, reuse it).

### Step 6: Wire `AltTitles` into `MediaSearchContext` construction

In the `MediaSearchContext` construction call (around line 786):
```csharp
var searchCtx = new MediaSearchContext(
    Name:            row.MediaItem.Name,
    Year:            ValidateYear(row.MediaItem.Year),
    ParentName:      row.MediaItem.Parent?.Name,
    GrandparentName: row.MediaItem.Parent?.Parent?.Name,
    ItemNumber:      row.MediaItem.Number,
    HierarchyLevel:  row.MediaItem.HierarchyLevel,
    FilenameStem:    ExtractFilenameStem(row.MediaItem),
    SiblingNames:    siblingNames,
    AltTitles:       BuildAltTitles(
                         row.MediaItem.Name,
                         ExtractFilenameStem(row.MediaItem),
                         null)); // PreciseName not yet wired — future task
```

### Step 7: Run all unit tests, fix any regressions

```
dotnet test tests/Chronicle.Tests.Unit -c Debug
```
Expected: all pass.

### Step 8: Commit

```
git add src/Chronicle.Services/MetadataEnrichmentService.cs tests/Chronicle.Tests.Unit/
git commit -m "feat(enrichment): year validation, BuildAltTitles, wire AltTitles into MediaSearchContext"
```

---

## Task 3: Populate `ChildNames` and `SubItemMetadata` in `MetadataEnrichmentService`

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`

### Step 1: Write failing test for `ChildNames` population

```csharp
[Fact]
public async Task SearchContext_AlbumLevel_ChildNamesPopulated()
{
    // Arrange: an album MediaItem with 3 child tracks
    // (Use in-memory DB, seed parent album + 3 child MediaItems)
    // Act: call the enrichment context-building path
    // Assert: context.ChildNames has 3 entries matching the child names
}
```

This is an integration-style unit test — seed an in-memory `ChronicleDbContext` with a parent album and children, invoke the private context-building path via reflection or extract it to an `internal` helper.

### Step 2: Implement `ChildNames` population

In `MetadataEnrichmentService`, alongside the existing `siblingNames` population (around line 770), add child names for HierarchyLevel 0 and 1:

```csharp
IReadOnlyList<string>? childNames = null;
if (row.MediaItem.HierarchyLevel <= 1)
{
    var children = await db.MediaItems
        .Where(m => m.ParentId == row.MediaItem.Id)
        .Select(m => m.Name)
        .Take(200)          // reasonable cap — accuracy over speed
        .ToListAsync(ct);
    if (children.Count > 0) childNames = children;
}
```

Add `ChildNames: childNames` to the `MediaSearchContext` construction call.

### Step 3: Write failing test for `SubItemMetadata` Tier 1 (filename/path)

```csharp
[Fact]
public void BuildSubItemMetadata_Tier1_ExtractsTrackNumberFromFilename()
{
    // A MediaItem whose MetadataJson contains:
    // { "fileScanner": { "filePaths": ["E:\\Music\\Artist\\Album\\01 - Song.mp3"] } }
    var item = new MediaItem { Name = "Song", MetadataJson = /* json above */ };
    var result = MetadataEnrichmentService.BuildSubItemMetadataTier1(item);
    result.ItemNumber.Should().Be(1);
    result.Name.Should().Be("Song");
}
```

### Step 4: Implement `BuildSubItemMetadataTier1`

```csharp
private static readonly System.Text.RegularExpressions.Regex TrackPrefixRe =
    new(@"^(\d{1,3})[\s\-\.]+", System.Text.RegularExpressions.RegexOptions.Compiled);

private static readonly System.Text.RegularExpressions.Regex DiscFolderRe =
    new(@"\b(?:disc|disk|cd)\s*(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

/// <summary>
/// Tier 1: extract what we can from filename and folder path alone — no file I/O.
/// </summary>
internal static SiblingInfo BuildSubItemMetadataTier1(Chronicle.Core.Models.MediaItem item)
{
    string? filePath = null;
    string? folderPath = null;

    if (!string.IsNullOrEmpty(item.MetadataJson))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
            if (doc.RootElement.TryGetProperty("fileScanner", out var fs))
            {
                if (fs.TryGetProperty("filePaths", out var fps) &&
                    fps.GetArrayLength() > 0)
                    filePath = fps[0].GetString();
                if (fs.TryGetProperty("folderPath", out var fp))
                    folderPath = fp.GetString();
            }
        }
        catch { /* corrupt JSON — ignore */ }
    }

    int? trackNumber = null;
    int? discNumber  = null;

    if (filePath is not null)
    {
        var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
        var tm = TrackPrefixRe.Match(fileName);
        if (tm.Success && int.TryParse(tm.Groups[1].Value, out var tn))
            trackNumber = tn;
    }

    if (folderPath is not null)
    {
        var dm = DiscFolderRe.Match(folderPath);
        if (dm.Success && int.TryParse(dm.Groups[1].Value, out var dn))
            discNumber = dn;
    }

    return new SiblingInfo(
        Name:       item.Name,
        ItemNumber: trackNumber,
        DiscNumber: discNumber);
}
```

### Step 5: Implement Tier 2 (duration from MetadataJson)

Duration is often already stored in `fileScanner.duration` by the file scanner:

```csharp
/// <summary>
/// Tier 2: add duration from fileScanner metadata (already scanned, no extra I/O).
/// </summary>
internal static SiblingInfo AddDurationTier2(
    SiblingInfo tier1, Chronicle.Core.Models.MediaItem item)
{
    if (string.IsNullOrEmpty(item.MetadataJson)) return tier1;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(item.MetadataJson);
        if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
            fs.TryGetProperty("duration", out var dur) &&
            dur.TryGetInt32(out var seconds))
        {
            return tier1 with { DurationSeconds = seconds };
        }
    }
    catch { }
    return tier1;
}
```

### Step 6: Wire `SubItemMetadata` into context construction

For HierarchyLevel == 2 (tracks): build from the item itself and its siblings' metadata.
For HierarchyLevel == 1 (albums): build from child items' metadata.

In the context-building section:

```csharp
IReadOnlyList<SiblingInfo>? subItemMetadata = null;
if (row.MediaItem.HierarchyLevel == 2 && row.MediaItem.ParentId is not null)
{
    // Leaf item: use siblings as sub-item evidence
    var siblingItems = await db.MediaItems
        .Where(m => m.ParentId == row.MediaItem.ParentId && m.Id != row.MediaItem.Id)
        .Take(50)
        .ToListAsync(ct);
    if (siblingItems.Count > 0)
        subItemMetadata = siblingItems
            .Select(s => AddDurationTier2(BuildSubItemMetadataTier1(s), s))
            .ToList()
            .AsReadOnly();
}
else if (row.MediaItem.HierarchyLevel <= 1)
{
    // Parent item: use children as sub-item evidence
    var childItems = await db.MediaItems
        .Where(m => m.ParentId == row.MediaItem.Id)
        .Take(200)
        .ToListAsync(ct);
    if (childItems.Count > 0)
        subItemMetadata = childItems
            .Select(c => AddDurationTier2(BuildSubItemMetadataTier1(c), c))
            .ToList()
            .AsReadOnly();
}
```

Add `SubItemMetadata: subItemMetadata` to the `MediaSearchContext` construction call.

### Step 7: Run all unit tests

```
dotnet test tests/Chronicle.Tests.Unit -c Debug
```

### Step 8: Commit

```
git add src/Chronicle.Services/MetadataEnrichmentService.cs tests/Chronicle.Tests.Unit/
git commit -m "feat(enrichment): populate ChildNames and SubItemMetadata (Tier 1+2) in MediaSearchContext"
```

---

## Task 4: Rewrite MusicBrainz Stage 1 — Exact title + artist + year

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs`
- Test: `W:\Scripts\Chronicle.Plugin.MusicBrainz\tests\MusicBrainzProviderTests.cs`

### Context

The current `SearchAsync` for albums uses `BuildAlbumQuery` which already calls `MbQuote` and appends `AND artist:{...}`. Year is available in `context.Year` but is **not yet used in any MusicBrainz query**. Stage 1 of the new cascade must:
1. Iterate over `context.AltTitles` (falling back to `[context.Name]` if null)
2. For each alt title: build an exact (phrase-quoted) query with artist + year (`firstreleasedate:{year}` for release-groups, year on the track's release for recordings)
3. If no candidate exceeds threshold after all alt titles with year: retry all alt titles without year

### Step 1: Add year to `BuildAlbumQuery` (make year optional)

Current `BuildAlbumQuery`:
```csharp
private static string BuildAlbumQuery(MediaSearchContext ctx)
{
    var name = StripYearSuffix(ctx.Name);
    var artistClause = ctx.ParentName is not null
        ? $" AND artist:{MbQuote(ctx.ParentName)}" : string.Empty;
    return $"{MbQuote(name)}{artistClause}";
}
```

Add a `useYear` parameter and replace:

```csharp
private static string BuildAlbumQuery(MediaSearchContext ctx, string title, bool useYear)
{
    var artistClause = ctx.ParentName is not null
        ? $" AND artist:{MbQuote(ctx.ParentName)}" : string.Empty;
    var yearClause   = useYear && ctx.Year.HasValue
        ? $" AND firstreleasedate:{ctx.Year}" : string.Empty;
    return $"{MbQuote(title)}{artistClause}{yearClause}";
}
```

### Step 2: Add year to `BuildTrackQuery`

```csharp
private static string BuildTrackQuery(
    string trackName, MediaSearchContext ctx, bool includeRelease, bool useYear)
{
    var artistClause  = ctx.GrandparentName is not null
        ? $" AND artist:{MbQuote(ctx.GrandparentName)}" : string.Empty;
    var releaseClause = includeRelease && ctx.ParentName is not null
        ? $" AND release:{MbQuote(StripYearSuffix(ctx.ParentName))}" : string.Empty;
    // Recordings don't have their own year field — year filtering happens via release date.
    // For now, release name is the best proxy; MBID pinning in Stage 3/4 handles year precisely.
    return $"{MbQuote(trackName)}{artistClause}{releaseClause}";
}
```

Note: MusicBrainz recording search does not have a `firstreleasedate` Lucene field; that field is on release-groups. For track searches, year is used to score candidates post-search (Stage 1 year boost), not as a Lucene clause.

### Step 3: Write failing tests for Stage 1 with AltTitles + year

```csharp
[Fact]
public async Task SearchAsync_Album_Stage1_UsesFirsetReleaseDateWhenYearProvided()
{
    string? capturedQuery = null;
    var provider = BuildProvider(url =>
    {
        capturedQuery = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
        return Ok(OneReleaseGroup("rg-1", "Remixed", 2014));
    });

    await provider.SearchAsync(new MediaSearchContext(
        Name:           "Remixed",
        HierarchyLevel: 1,
        ParentName:     "3TEETH",
        Year:           2014,
        AltTitles:      ["Remixed"]), CancellationToken.None);

    capturedQuery.Should().Contain("firstreleasedate:2014");
    capturedQuery.Should().Contain("3TEETH");
}

[Fact]
public async Task SearchAsync_Album_Stage1_RetriesWithoutYear_WhenNoResultsWithYear()
{
    var queries = new List<string>();
    var provider = BuildProvider(url =>
    {
        var q = Uri.UnescapeDataString(new Uri("https://mb.test/" + url).Query);
        queries.Add(q);
        // Only return result when year is NOT in query
        return q.Contains("firstreleasedate")
            ? Ok(EmptyReleaseGroups)
            : Ok(OneReleaseGroup("rg-1", "Remixed", 2014));
    });

    var results = await provider.SearchAsync(new MediaSearchContext(
        Name:           "Remixed",
        HierarchyLevel: 1,
        ParentName:     "3TEETH",
        Year:           2014,
        AltTitles:      ["Remixed"]), CancellationToken.None);

    queries.Should().HaveCount(2); // with-year attempt + without-year attempt
    results.Should().NotBeEmpty();
}
```

Run tests — expected FAIL.

### Step 4: Rewrite `SearchAsync` album branch

Replace the `else if (context.HierarchyLevel == 1)` block (added earlier this session):

```csharp
else if (context.HierarchyLevel == 1)
{
    // Albums: Stage 1 exact, Stage 2 fuzzy — each with year then without.
    // Stages 3 and 4 are handled further below when early stages don't reach threshold.
    var altTitles = context.AltTitles?.Count > 0
        ? context.AltTitles
        : [StripYearSuffix(context.Name)];

    container = await TryAlbumSearchAsync(altTitles, context, exact: true,  useYear: true,  ct);
    if (BelowThreshold(container))
        container = await TryAlbumSearchAsync(altTitles, context, exact: true,  useYear: false, ct);
    if (BelowThreshold(container))
        container = await TryAlbumSearchAsync(altTitles, context, exact: false, useYear: true,  ct);
    if (BelowThreshold(container))
        container = await TryAlbumSearchAsync(altTitles, context, exact: false, useYear: false, ct);
}
```

Add `TryAlbumSearchAsync` helper:

```csharp
private async Task<MediaMetadata> TryAlbumSearchAsync(
    IReadOnlyList<string> altTitles,
    MediaSearchContext    ctx,
    bool                 exact,
    bool                 useYear,
    CancellationToken    ct)
{
    foreach (var title in altTitles)
    {
        var queryTitle = exact ? title : title + "~";
        var query      = BuildAlbumQuery(ctx, queryTitle, useYear);
        var result     = await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query, ct);
        if (!BelowThreshold(result)) return result;
    }
    return new MediaMetadata(); // empty
}
```

Add `BelowThreshold` helper:

```csharp
private static bool BelowThreshold(MediaMetadata result) =>
    !(result.Results?.Count > 0);
```

(For now `BelowThreshold` just checks for empty results — confidence threshold enforcement is in Task 8.)

### Step 5: Run tests

```
cd W:\Scripts\Chronicle.Plugin.MusicBrainz
dotnet test tests/ -c Debug
```
Expected: new tests pass, existing tests still pass.

### Step 6: Apply same AltTitles pattern to track Stage 1 and Stage 2

The existing track cascade (Stages 1-2) already iterates through `context.FilenameStem` as a fallback. Replace with `AltTitles`:

In the `if (context.HierarchyLevel == 2)` block, replace:
```csharp
// Stage 1 — tag title + artist + release
container = await MusicBrainzSearcher.SearchRecordingsAsync(
    _client!, BuildTrackQuery(context.Name, context, includeRelease: true), ct);

// Stage 2 — filename stem + artist + release
if (!(container.Results?.Count > 0) && !string.IsNullOrEmpty(context.FilenameStem))
    container = await MusicBrainzSearcher.SearchRecordingsAsync(
        _client!, BuildTrackQuery(context.FilenameStem, context, includeRelease: true), ct);
```

With:
```csharp
// Stage 1 — exact alt-title + artist + release, year-first then year-drop
var altTitles = context.AltTitles?.Count > 0
    ? context.AltTitles
    : (IReadOnlyList<string>)[context.Name];

container = await TryTrackSearchAsync(altTitles, context, includeRelease: true, ct);
```

Add `TryTrackSearchAsync`:
```csharp
private async Task<MediaMetadata> TryTrackSearchAsync(
    IReadOnlyList<string> altTitles,
    MediaSearchContext    ctx,
    bool                 includeRelease,
    CancellationToken    ct)
{
    foreach (var title in altTitles)
    {
        var result = await MusicBrainzSearcher.SearchRecordingsAsync(
            _client!, BuildTrackQuery(title, ctx, includeRelease, useYear: false), ct);
        if (!BelowThreshold(result)) return result;
    }
    return new MediaMetadata();
}
```

### Step 7: Run all plugin tests

```
dotnet test tests/ -c Debug
```
Expected: all pass.

### Step 8: Commit

```
cd W:\Scripts\Chronicle.Plugin.MusicBrainz
git add MusicBrainzMetadataProvider.cs tests/MusicBrainzProviderTests.cs
git commit -m "feat(musicbrainz): Stage 1 uses AltTitles and firstreleasedate year, with year-drop retry"
```

---

## Task 5: MusicBrainz Stage 3 — Fuzzy + sub-item list comparison

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs`
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzSearcher.cs`
- Test: `W:\Scripts\Chronicle.Plugin.MusicBrainz\tests\MusicBrainzProviderTests.cs`

### Context

Stage 3 runs when Stages 1-2 (exact + fuzzy title) don't yield a confident match. For each candidate, the plugin fetches the release-group's track listing (for albums) or the recording's releases' track listings (for tracks) and compares against `context.ChildNames` (albums) or `context.SiblingNames` (tracks).

MusicBrainz endpoints used:
- Release-group tracks: `GET /release?release-group={mbid}&inc=recordings&fmt=json`
- Recording releases: `GET /recording/{mbid}?inc=releases+recordings&fmt=json`

### Step 1: Add `FetchReleaseGroupTracksAsync` to `MusicBrainzSearcher`

```csharp
/// <summary>
/// Returns all track titles across all releases in a release-group.
/// Used in Stage 3 sub-item list comparison for album-level searches.
/// </summary>
public static async Task<IReadOnlyList<string>> FetchReleaseGroupTracksAsync(
    MusicBrainzClient client, string releaseGroupMbid, CancellationToken ct)
{
    var json = await client.GetAsync(
        $"release?release-group={Uri.EscapeDataString(releaseGroupMbid)}&inc=recordings&fmt=json", ct);
    var result = JsonSerializer.Deserialize<MbReleaseSearchResult>(json, MusicBrainzJsonOptions.Opts);
    return (result?.Releases ?? [])
        .SelectMany(r => r.Media ?? [])
        .SelectMany(m => m.Tracks ?? [])
        .Select(t => t.Title ?? string.Empty)
        .Where(t => !string.IsNullOrEmpty(t))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList()
        .AsReadOnly();
}
```

Note: you will need to ensure `MbReleaseSearchResult`, `MbRelease`, `MbMedium`, and `MbTrack` model classes exist in `MusicBrainzModels.cs`. Add any missing ones.

### Step 2: Write failing test for Stage 3 album

```csharp
[Fact]
public async Task SearchAsync_Album_Stage3_BoostsConfidence_WhenTrackCountMatches()
{
    // Stage 1 and 2 return a candidate but below threshold (score too low).
    // Stage 3 fetches tracklist, finds 12 matching tracks → boosts above threshold.
    var provider = BuildProvider(url =>
    {
        if (url.Contains("release-group?query="))
            return Ok(OneReleaseGroup("rg-1", "shutdown.exe", null, score: 30)); // low score
        if (url.Contains("release?release-group=rg-1"))
            return Ok(ReleaseWithTracks("rel-1", MakeTracks(12, "Track"))); // 12 tracks
        return Ok(EmptyReleaseGroups);
    });

    var ctx = new MediaSearchContext(
        Name:           "<shutdown.exe>",
        HierarchyLevel: 1,
        ParentName:     "3TEETH",
        ChildNames:     Enumerable.Range(1, 12).Select(i => $"Track {i:D2}").ToList());

    var results = await provider.SearchAsync(ctx, CancellationToken.None);
    results.Should().NotBeEmpty();
    results[0].Score.Should().BeGreaterThan(50);
}
```

Run — expected FAIL.

### Step 3: Implement Stage 3 in `SearchAsync` album branch

After the Stage 1-2 album search, add Stage 3:

```csharp
// Stage 3 — fuzzy title + artist + [year] + sub-item list comparison
if (BelowThreshold(container) || NeedsSubItemValidation(container, context))
{
    container = await TryAlbumStage3Async(altTitles, context, ct);
}
```

```csharp
private async Task<MediaMetadata> TryAlbumStage3Async(
    IReadOnlyList<string> altTitles,
    MediaSearchContext    ctx,
    CancellationToken    ct)
{
    // Get fuzzy candidates (with year, then without)
    var candidates = await TryAlbumSearchAsync(altTitles, ctx, exact: false, useYear: true,  ct);
    if (BelowThreshold(candidates))
        candidates = await TryAlbumSearchAsync(altTitles, ctx, exact: false, useYear: false, ct);

    if (candidates.Results is null || candidates.Results.Count == 0)
        return new MediaMetadata();

    // Fetch sub-item lists and boost scores
    var boosted = new List<ScoredCandidate>();
    foreach (var candidate in candidates.Results.Take(20)) // inspect top 20
    {
        var mbid = ExtractMbid(candidate.ExternalId);
        if (mbid is null) { boosted.Add(new ScoredCandidate(candidate, candidate.Score)); continue; }

        var providerTracks = await MusicBrainzSearcher.FetchReleaseGroupTracksAsync(_client!, mbid, ct);
        var boost          = ComputeSubItemBoost(providerTracks, ctx.ChildNames, ctx.ChildCount);
        boosted.Add(new ScoredCandidate(candidate, candidate.Score + boost));
    }

    var best = boosted.OrderByDescending(c => c.Score).FirstOrDefault();
    if (best?.Score > 0)
    {
        return new MediaMetadata { Results = boosted
            .OrderByDescending(c => c.Score)
            .Select(c => c.Metadata)
            .ToList() };
    }
    return new MediaMetadata();
}
```

Add `ComputeSubItemBoost`:
```csharp
private static int ComputeSubItemBoost(
    IReadOnlyList<string>  providerItems,
    IReadOnlyList<string>? chronicleItems,
    int?                   chronicleCount)
{
    int boost = 0;

    // Count match: within ±2 items
    var providerCount = providerItems.Count;
    var knownCount    = chronicleCount ?? chronicleItems?.Count;
    if (knownCount.HasValue && Math.Abs(providerCount - knownCount.Value) <= 2)
        boost += 15;

    // Name matches
    if (chronicleItems?.Count > 0)
    {
        var normalised = providerItems.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var matches    = chronicleItems.Count(ci => normalised.Contains(Normalize(ci)));
        boost += Math.Min(matches * 5, 25);
    }

    return boost;
}
```

`Normalize` already exists in the file — reuse it.

### Step 4: Add Stage 3 for tracks

For HierarchyLevel == 2, add after the existing Stage 4 (sibling reid:):

```csharp
// Stage 3 — sub-item list comparison using sibling names
// If we have a fuzzy match but SiblingNames, validate which release it belongs to
// by comparing the full tracklist rather than just searching a sibling.
if (!(container.Results?.Count > 0) && context.SiblingNames?.Count > 0)
{
    container = await TryTrackStage3Async(altTitles, context, ct);
}
```

`TryTrackStage3Async` fetches recording candidates, then for each gets the recording's releases
and compares their tracklists against `context.SiblingNames`:

```csharp
private async Task<MediaMetadata> TryTrackStage3Async(
    IReadOnlyList<string> altTitles,
    MediaSearchContext    ctx,
    CancellationToken    ct)
{
    var candidates = await TryTrackSearchAsync(altTitles, ctx, includeRelease: false, ct);
    if (BelowThreshold(candidates)) return new MediaMetadata();

    var boosted = new List<(MediaMetadataItem item, int score)>();
    foreach (var c in candidates.Results!.Take(20))
    {
        var mbid = ExtractMbid(c.ExternalId);
        if (mbid is null) continue;

        var releases = await MusicBrainzSearcher.FindReleasesForRecordingAsync(_client!, mbid, ct);
        foreach (var (_, releaseMbid, _) in releases)
        {
            var tracks = await MusicBrainzSearcher.FetchReleaseTracksAsync(_client!, releaseMbid, ct);
            var boost  = ComputeSubItemBoost(tracks, ctx.SiblingNames, ctx.SiblingNames?.Count);
            boosted.Add((c, c.Score + boost));
        }
    }

    var best = boosted.OrderByDescending(x => x.score).FirstOrDefault();
    if (best.item is not null)
        return new MediaMetadata { Results = [best.item] };
    return new MediaMetadata();
}
```

Add `FetchReleaseTracksAsync` to `MusicBrainzSearcher` (similar to `FetchReleaseGroupTracksAsync` but for a single release):
```csharp
public static async Task<IReadOnlyList<string>> FetchReleaseTracksAsync(
    MusicBrainzClient client, string releaseMbid, CancellationToken ct)
{
    var json = await client.GetAsync(
        $"release/{Uri.EscapeDataString(releaseMbid)}?inc=recordings&fmt=json", ct);
    var result = JsonSerializer.Deserialize<MbRelease>(json, MusicBrainzJsonOptions.Opts);
    return (result?.Media ?? [])
        .SelectMany(m => m.Tracks ?? [])
        .Select(t => t.Title ?? string.Empty)
        .Where(t => !string.IsNullOrEmpty(t))
        .ToList()
        .AsReadOnly();
}
```

### Step 5: Run all plugin tests

```
dotnet test tests/ -c Debug
```
Expected: all tests pass.

### Step 6: Commit

```
git add MusicBrainzMetadataProvider.cs MusicBrainzSearcher.cs tests/
git commit -m "feat(musicbrainz): Stage 3 sub-item list comparison boosts confidence via tracklist count and name matching"
```

---

## Task 6: MusicBrainz Stage 4 — Fuzzy + sub-item metadata comparison

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs`
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzSearcher.cs`
- Test: `W:\Scripts\Chronicle.Plugin.MusicBrainz\tests\MusicBrainzProviderTests.cs`

### Context

Stage 4 fetches full track metadata from MusicBrainz (track number, duration, title) and compares against `context.SubItemMetadata`. MusicBrainz is the source of truth — we are scoring how well our scanned data matches what MB says the release contains.

MusicBrainz returns duration in **milliseconds**. Convert to seconds for comparison.

### Step 1: Add `FetchReleaseTrackMetadataAsync` to `MusicBrainzSearcher`

```csharp
public record MbTrackMetadata(
    string Title,
    int?   TrackNumber,
    int?   DiscNumber,
    int?   DurationSeconds);

public static async Task<IReadOnlyList<MbTrackMetadata>> FetchReleaseTrackMetadataAsync(
    MusicBrainzClient client, string releaseMbid, CancellationToken ct)
{
    var json   = await client.GetAsync(
        $"release/{Uri.EscapeDataString(releaseMbid)}?inc=recordings&fmt=json", ct);
    var result = JsonSerializer.Deserialize<MbRelease>(json, MusicBrainzJsonOptions.Opts);

    var items = new List<MbTrackMetadata>();
    int discNum = 0;
    foreach (var medium in result?.Media ?? [])
    {
        discNum++;
        int trackPos = 0;
        foreach (var track in medium.Tracks ?? [])
        {
            trackPos++;
            items.Add(new MbTrackMetadata(
                Title:          track.Title ?? string.Empty,
                TrackNumber:    track.Position ?? trackPos,
                DiscNumber:     discNum,
                DurationSeconds: track.Length.HasValue ? track.Length.Value / 1000 : null));
        }
    }
    return items.AsReadOnly();
}
```

Note: `MbTrack` must have a `Position` (int?) and `Length` (int?, milliseconds) property. Add these to `MusicBrainzModels.cs` if missing.

### Step 2: Write failing test for Stage 4

```csharp
[Fact]
public async Task SearchAsync_Album_Stage4_BoostsConfidence_WhenTrackMetadataMatches()
{
    var provider = BuildProvider(url =>
    {
        if (url.Contains("release-group?query="))
            return Ok(OneReleaseGroup("rg-1", "shutdown.exe", null, score: 20)); // low
        if (url.Contains("release?release-group=rg-1"))
            return Ok(ReleaseWithTracks("rel-1", MakeTracksWithMeta(3)));
        if (url.Contains("release/rel-1"))
            return Ok(ReleaseFull("rel-1", MakeTracksWithMeta(3)));
        return Ok(EmptyReleaseGroups);
    });

    var ctx = new MediaSearchContext(
        Name:           "<shutdown.exe>",
        HierarchyLevel: 1,
        ParentName:     "3TEETH",
        SubItemMetadata: new[]
        {
            new SiblingInfo("Track One",   ItemNumber: 1, DurationSeconds: 200),
            new SiblingInfo("Track Two",   ItemNumber: 2, DurationSeconds: 185),
            new SiblingInfo("Track Three", ItemNumber: 3, DurationSeconds: 210),
        });

    var results = await provider.SearchAsync(ctx, CancellationToken.None);
    results.Should().NotBeEmpty();
    results[0].Score.Should().BeGreaterThan(50);
}
```

Run — expected FAIL.

### Step 3: Implement Stage 4 in `SearchAsync` album branch

Add after Stage 3:

```csharp
// Stage 4 — sub-item metadata comparison (track numbers + duration)
if (BelowThreshold(container) && context.SubItemMetadata?.Count > 0)
{
    container = await TryAlbumStage4Async(altTitles, context, ct);
}
```

```csharp
private async Task<MediaMetadata> TryAlbumStage4Async(
    IReadOnlyList<string> altTitles,
    MediaSearchContext    ctx,
    CancellationToken    ct)
{
    var candidates = await TryAlbumSearchAsync(altTitles, ctx, exact: false, useYear: true,  ct);
    if (BelowThreshold(candidates))
        candidates = await TryAlbumSearchAsync(altTitles, ctx, exact: false, useYear: false, ct);
    if (BelowThreshold(candidates)) return new MediaMetadata();

    var boosted = new List<(MediaMetadataItem item, int score)>();
    foreach (var candidate in candidates.Results!.Take(20))
    {
        var rgMbid = ExtractMbid(candidate.ExternalId);
        if (rgMbid is null) continue;

        // Get releases in this release-group
        var releasesJson = await _client!.GetAsync(
            $"release?release-group={Uri.EscapeDataString(rgMbid)}&fmt=json", ct);
        var releases = JsonSerializer.Deserialize<MbReleaseSearchResult>(
            releasesJson, MusicBrainzJsonOptions.Opts);

        foreach (var release in releases?.Releases?.Take(3) ?? [])
        {
            if (release.Id is null) continue;
            var tracks = await MusicBrainzSearcher.FetchReleaseTrackMetadataAsync(
                _client!, release.Id, ct);
            var boost = ComputeMetadataBoost(tracks, ctx.SubItemMetadata!,
                toleranceSeconds: 10); // TODO: make configurable
            boosted.Add((candidate, candidate.Score + boost));
        }
    }

    var best = boosted.OrderByDescending(x => x.score).FirstOrDefault();
    if (best.item is not null)
        return new MediaMetadata { Results = [best.item] };
    return new MediaMetadata();
}
```

Add `ComputeMetadataBoost`:

```csharp
private static int ComputeMetadataBoost(
    IReadOnlyList<MbTrackMetadata> providerTracks,
    IReadOnlyList<SiblingInfo>     chronicleTracks,
    int                            toleranceSeconds)
{
    int boost = 0;
    foreach (var ct in chronicleTracks)
    {
        // Try to find a matching provider track by track number first
        var candidate = ct.ItemNumber.HasValue
            ? providerTracks.FirstOrDefault(t => t.TrackNumber == ct.ItemNumber)
            : null;
        // Fall back to name match
        candidate ??= providerTracks.FirstOrDefault(
            t => string.Equals(Normalize(t.Title), Normalize(ct.Name),
                               StringComparison.Ordinal));

        if (candidate is null) continue;

        // Track number match
        if (ct.ItemNumber.HasValue && candidate.TrackNumber == ct.ItemNumber)
            boost += 10;
        // Duration match within tolerance
        if (ct.DurationSeconds.HasValue && candidate.DurationSeconds.HasValue &&
            Math.Abs(ct.DurationSeconds.Value - candidate.DurationSeconds.Value) <= toleranceSeconds)
            boost += 10;
        // Title match
        if (string.Equals(Normalize(candidate.Title), Normalize(ct.Name),
                          StringComparison.Ordinal))
            boost += 10;
    }
    return boost;
}
```

### Step 4: Add Stage 4 for tracks

Similar pattern: for each candidate recording, get its release's full track metadata and compare against `context.SubItemMetadata` (sibling metadata, including the current track itself). The track whose number and duration most precisely match a sibling set pointing to a known release wins.

```csharp
if (!(container.Results?.Count > 0) && context.SubItemMetadata?.Count > 0)
{
    container = await TryTrackStage4Async(altTitles, context, ct);
}
```

Implementation mirrors `TryAlbumStage4Async` but operates on recording → release → full track listing.

### Step 5: Run all plugin tests

```
dotnet test tests/ -c Debug
```

### Step 6: Commit

```
git add MusicBrainzMetadataProvider.cs MusicBrainzSearcher.cs tests/
git commit -m "feat(musicbrainz): Stage 4 sub-item metadata comparison using track numbers and duration"
```

---

## Task 7: Update Chronicle unit and integration tests

**Files:**
- Modify: `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`
- Run: `tests/Chronicle.Tests.Integration/`

### Step 1: Update any tests that construct `MediaSearchContext` without `AltTitles`

Search for all `new MediaSearchContext(` calls in unit tests and verify they still compile. The new fields are all optional — no changes required unless tests explicitly check the context contents.

```
cd W:\Scripts\Chronicle
dotnet test tests/ -c Debug
```
Expected: all 277 tests pass.

### Step 2: Add enrichment service tests for new context fields

Verify the enrichment service populates `AltTitles` and `ChildNames` correctly by adding integration test scenarios using `ChronicleApiFactory`. This exercises the full path from HTTP → enrichment service → context construction.

### Step 3: Commit

```
git add tests/
git commit -m "test(enrichment): cover AltTitles, ChildNames, SubItemMetadata context population"
```

---

## Task 8: Deploy and smoke-test

### Step 1: Build Chronicle

```
cd W:\Scripts\Chronicle
dotnet build src/Chronicle.sln -c Debug
```

### Step 2: Build and deploy MusicBrainz plugin

```
cd W:\Scripts\Chronicle.Plugin.MusicBrainz
dotnet build -c Debug
copy /Y bin\Debug\net9.0\Chronicle.Plugin.MusicBrainz.dll ..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.musicbrainz\
copy /Y bin\Debug\net9.0\Chronicle.Plugin.MusicBrainz.pdb ..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.musicbrainz\
```

### Step 3: Start the API (from `scripts/RunTestEnvironment.ps1`) and verify in Background Tasks

- Open Background Tasks → MusicBrainz enrichment should appear
- Reset enrichment status for 3TEETH → `<shutdown.exe>` and trigger refresh
- Verify `<shutdown.exe>` is now matched (no longer NotFound)
- Check `Kryptonite (LP version)` and `Smack (LP version)` are still matched
- Verify TMDB seasons with no TMDB record show `NotFound` rather than `Exhausted`

### Step 4: Push both repos

```
cd W:\Scripts\Chronicle && git push
cd W:\Scripts\Chronicle.Plugin.MusicBrainz && git push origin HEAD
```

---

## Follow-on: TMDB Cascade Redesign

The TMDB plugin source is only in the `goofy-nobel` git worktree and uses a stale `SearchAsync(string query, string mediaType)` signature. Before TMDB can implement this cascade:
1. Locate or reconstruct the current deployed TMDB source
2. Ensure it implements the current `SearchAsync(MediaSearchContext, CancellationToken)` interface
3. Apply the same four-stage pattern: exact title → fuzzy → fuzzy + season count/episode list → fuzzy + episode metadata
4. TMDB-specific fields: `primary_release_year` (movies), `first_air_date_year` (TV shows), season episode count from `/tv/{id}/season/{n}`
