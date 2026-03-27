# Unified Metadata Enrichment Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the split `MetadataRefreshService` + `MetadataEnrichmentService` with a single unified enrichment pipeline that uses one table, one service, and one code path for all callers at all hierarchy levels.

**Architecture:** A single `EnrichItemCoreAsync` method handles every case — background fill-gaps, user-triggered force refresh, and Fix Match — by resolving an external ID in priority order (override → stored → parent-derived → search), fetching the full provider response, merging it losslessly into the item, then cascading to children. `IMetadataProvider.SearchAsync` is updated to accept a `MediaSearchContext` so plugins own their query construction and scoring; Chronicle only applies the threshold. The `media_enrichment` table replaces `enrichment_statuses`, `media_external_ids`, and `media_item_refresh_logs`.

**Tech Stack:** .NET 9, EF Core 9 (SQLite + InMemory), Moq + FluentAssertions + xUnit for tests, `dotnet test` to run.

**Design doc:** `docs/plans/2026-03-27-unified-enrichment-design.md`

---

## Phase 1 — Plugin Interface

### Task 1: Add MediaSearchContext and ScoredCandidate to Chronicle.Plugins

**Files:**
- Create: `src/Chronicle.Plugins/Models/MediaSearchContext.cs`
- Create: `src/Chronicle.Plugins/Models/ScoredCandidate.cs`

**Step 1: Create MediaSearchContext**

```csharp
// src/Chronicle.Plugins/Models/MediaSearchContext.cs
namespace Chronicle.Plugins.Models;

/// <summary>
/// Context passed to <see cref="IMetadataProvider.SearchAsync"/> so the plugin
/// can construct its own query and score candidates without Chronicle knowing
/// provider-specific syntax (Lucene, etc.).
/// </summary>
public record MediaSearchContext(
    /// <summary>Item name, pre-normalised by Chronicle (punctuation stripped, lowercased).</summary>
    string  Name,
    int?    Year,
    /// <summary>Parent item name — artist for an album, show for a season.</summary>
    string? ParentName        = null,
    /// <summary>Grandparent item name — artist for a track.</summary>
    string? GrandparentName   = null,
    /// <summary>Position within parent — season number, track number, episode number.</summary>
    int?    ItemNumber        = null,
    /// <summary>
    /// Number of direct children already in Chronicle for this item.
    /// Allows structural validation: does the provider's season count match?
    /// </summary>
    int?    ChildCount        = null,
    /// <summary>0 = root (show/artist/movie), 1 = season/album, 2 = episode/track.</summary>
    int     HierarchyLevel   = 0
);
```

**Step 2: Create ScoredCandidate**

```csharp
// src/Chronicle.Plugins/Models/ScoredCandidate.cs
namespace Chronicle.Plugins.Models;

/// <summary>
/// A search candidate returned by <see cref="IMetadataProvider.SearchAsync"/>.
/// The plugin assigns the score; Chronicle applies the threshold.
/// </summary>
public record ScoredCandidate(
    /// <summary>Full metadata for this candidate. Must have a non-empty ExternalId.</summary>
    MediaMetadata Metadata,
    /// <summary>Confidence score 0–100, plugin-computed.</summary>
    int           Score,
    /// <summary>Human-readable explanation: which signals fired and why.</summary>
    string?       ScoreReason = null
);
```

**Step 3: Verify build**

```bash
cd W:/Scripts/Chronicle && dotnet build src/Chronicle.Plugins/Chronicle.Plugins.csproj
```
Expected: no errors.

**Step 4: Commit**

```bash
git add src/Chronicle.Plugins/Models/MediaSearchContext.cs src/Chronicle.Plugins/Models/ScoredCandidate.cs
git commit -m "feat(plugins): add MediaSearchContext and ScoredCandidate models"
```

---

### Task 2: Update IMetadataProvider.SearchAsync signature

**Files:**
- Modify: `src/Chronicle.Plugins/IMetadataProvider.cs`

**Step 1: Write a failing compile-check test first**

Open `tests/Chronicle.Tests.Unit/Plugins/` (create directory if needed) and add:

```csharp
// tests/Chronicle.Tests.Unit/Plugins/IMetadataProviderContractTests.cs
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Plugins;

public class IMetadataProviderContractTests
{
    [Fact]
    public void SearchAsync_AcceptsMediaSearchContext()
    {
        var mock = new Mock<IMetadataProvider>();
        var ctx = new MediaSearchContext("test", 2001);
        // Compiles only if IMetadataProvider.SearchAsync takes MediaSearchContext
        mock.Setup(p => p.SearchAsync(ctx, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoredCandidate>());
        Assert.True(true);
    }
}
```

**Step 2: Run — expect compile failure**

```bash
cd W:/Scripts/Chronicle && dotnet build tests/Chronicle.Tests.Unit 2>&1 | head -20
```
Expected: error CS1503 — wrong argument type for SearchAsync.

**Step 3: Update IMetadataProvider**

Replace the existing `SearchAsync` declaration:

```csharp
// OLD — remove:
/// <summary>Searches for media matching <paramref name="query"/>.</summary>
/// <param name="mediaType">Hint for the provider (e.g. "movie", "tv").</param>
Task<MediaMetadata> SearchAsync(
    string query,
    string mediaType,
    CancellationToken ct = default);

// NEW — replace with:
/// <summary>
/// Searches for a media item using the supplied context.
/// The plugin constructs its own query (Lucene, text, etc.) from the context fields,
/// executes the search, and returns scored candidates ordered best-first.
/// Chronicle applies the configured threshold to decide whether to accept the top result.
/// </summary>
Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
    MediaSearchContext context,
    CancellationToken ct = default);
```

**Step 4: Build — expect errors on implementations (TMDB, MusicBrainz, any mocks)**

```bash
cd W:/Scripts/Chronicle && dotnet build src/ 2>&1 | grep "error CS"
```
Note all files that fail — you will fix them in Tasks 5 and 6.

**Step 5: Update mock implementations in unit tests to stub-compile**

Search for all mock setups of SearchAsync in test files:

```bash
grep -rn "SearchAsync" W:/Scripts/Chronicle/tests --include="*.cs"
```

For each mock setup, update to the new signature returning `List<ScoredCandidate>`:

```csharp
// Old pattern:
mockProvider.Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new MediaMetadata { ... });

// New pattern:
mockProvider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new List<ScoredCandidate>
    {
        new(new MediaMetadata { Title = "...", ExternalId = "..." }, Score: 80)
    });
```

**Step 6: Verify contract test passes**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit --filter "IMetadataProviderContractTests" -v normal
```
Expected: PASS.

**Step 7: Commit**

```bash
git add src/Chronicle.Plugins/IMetadataProvider.cs tests/
git commit -m "feat(plugins): update SearchAsync to accept MediaSearchContext, return ScoredCandidate list"
```

---

### Task 3: Retire ITvDetailProvider

**Files:**
- Delete: `src/Chronicle.Plugins/ITvDetailProvider.cs`
- Modify: `src/Chronicle.Plugins/Chronicle.Plugins.csproj` (nothing needed — deletion is enough)

**Step 1: Check all usages**

```bash
grep -rn "ITvDetailProvider\|TvSeasonDetail\|TvEpisodeDetail\|GetTvSeasonAsync\|GetTvEpisodeAsync" W:/Scripts/Chronicle/src --include="*.cs"
```

Note every file. You will update TMDB in Task 5 and the refresh service will be deleted in Task 12.

**Step 2: Delete the file**

```bash
rm "W:/Scripts/Chronicle/src/Chronicle.Plugins/ITvDetailProvider.cs"
```

**Step 3: Build to surface all broken references**

```bash
cd W:/Scripts/Chronicle && dotnet build src/ 2>&1 | grep "error CS"
```

If `MetadataRefreshService.cs` references `ITvDetailProvider` — leave those errors for now. You will delete that file in Task 12. Add `// TODO: delete with MetadataRefreshService` comments if the build errors are blocking other tasks.

**Step 4: Commit**

```bash
git add -A
git commit -m "feat(plugins): retire ITvDetailProvider — TV hierarchy moves into TMDB plugin"
```

---

## Phase 2 — Data Model

### Task 4: Add MediaItemEnrichment EF model

**Files:**
- Create: `src/Chronicle.Core/Models/MediaItemEnrichment.cs`
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`

**Step 1: Write a failing test for the new model**

```csharp
// tests/Chronicle.Tests.Unit/Data/MediaItemEnrichmentModelTests.cs
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Data;

public class MediaItemEnrichmentModelTests
{
    [Fact]
    public async Task CanSaveAndRetrieveEnrichmentRow()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(opts);

        var mediaType = new MediaType { Name = "movies", DisplayName = "Movies" };
        db.MediaTypes.Add(mediaType);
        var item = new MediaItem { Name = "Blade Runner", Year = 1982,
            MediaTypeId = mediaType.Id, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var row = new MediaItemEnrichment
        {
            MediaItemId     = item.Id,
            PluginId        = "chronicle.plugin.tmdb",
            ExternalId      = "movie:78",
            Status          = EnrichmentStatus.Completed,
            RetryCount      = 0,
            MaxRetries      = 3,
            LastCompletedAt = DateTime.UtcNow,
        };
        db.MediaEnrichments.Add(row);
        await db.SaveChangesAsync();

        var saved = await db.MediaEnrichments
            .FirstAsync(e => e.PluginId == "chronicle.plugin.tmdb");
        saved.ExternalId.Should().Be("movie:78");
        saved.Status.Should().Be(EnrichmentStatus.Completed);
    }
}
```

**Step 2: Run — expect compile failure**

```bash
cd W:/Scripts/Chronicle && dotnet build tests/Chronicle.Tests.Unit 2>&1 | grep "error CS"
```
Expected: `MediaItemEnrichment` not found, `MediaEnrichments` not found.

**Step 3: Create the model**

```csharp
// src/Chronicle.Core/Models/MediaItemEnrichment.cs
namespace Chronicle.Core.Models;

/// <summary>
/// Single source of truth for one item's enrichment state from one plugin.
/// Replaces both MediaItemEnrichmentStatus and MediaExternalId.
/// </summary>
public class MediaItemEnrichment
{
    public int     Id          { get; set; }
    public int     MediaItemId { get; set; }
    public string  PluginId    { get; set; } = string.Empty;

    /// <summary>The provider's external ID for this item. Null until matched.</summary>
    public string? ExternalId  { get; set; }

    public EnrichmentStatus Status          { get; set; } = EnrichmentStatus.Pending;
    public int              RetryCount      { get; set; }
    public int              MaxRetries      { get; set; } = 3;
    public DateTime?        LastAttemptedAt { get; set; }
    public DateTime?        LastCompletedAt { get; set; }
    public string?          ErrorMessage    { get; set; }

    /// <summary>
    /// JSON blob: search candidates considered, scores, signals used, threshold at match time.
    /// </summary>
    public string? DiagnosticsJson { get; set; }

    // Navigation
    public MediaItem? MediaItem { get; set; }
}
```

**Step 4: Add DbSet to ChronicleDbContext**

In `src/Chronicle.Data/ChronicleDbContext.cs`, add alongside the existing DbSets:

```csharp
public DbSet<MediaItemEnrichment> MediaEnrichments { get; set; } = null!;
```

Also add in `OnModelCreating` (find the existing enrichment status configuration as a guide):

```csharp
modelBuilder.Entity<MediaItemEnrichment>(e =>
{
    e.ToTable("media_enrichment");
    e.HasKey(x => x.Id);
    e.HasIndex(x => new { x.MediaItemId, x.PluginId }).IsUnique();
    e.HasOne(x => x.MediaItem)
     .WithMany()
     .HasForeignKey(x => x.MediaItemId)
     .OnDelete(DeleteBehavior.Cascade);
});
```

**Step 5: Run test**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit --filter "MediaItemEnrichmentModelTests" -v normal
```
Expected: PASS.

**Step 6: Commit**

```bash
git add src/Chronicle.Core/Models/MediaItemEnrichment.cs src/Chronicle.Data/ChronicleDbContext.cs tests/Chronicle.Tests.Unit/Data/MediaItemEnrichmentModelTests.cs
git commit -m "feat(data): add MediaItemEnrichment model and DbSet"
```

---

### Task 5: EF migration — create media_enrichment table

**Files:**
- Create: migration file (auto-generated)

**Step 1: Create the migration**

```bash
cd W:/Scripts/Chronicle/src/Chronicle.API && dotnet ef migrations add AddMediaEnrichmentTable --project ../Chronicle.Data
```

**Step 2: Review the generated migration**

Open the new migration file. Verify it creates a `media_enrichment` table with all expected columns and a unique index on `(media_item_id, plugin_id)`. It should NOT yet drop the old tables.

**Step 3: Apply to local dev DB**

```bash
cd W:/Scripts/Chronicle/src/Chronicle.API && dotnet ef database update
```

**Step 4: Commit**

```bash
git add src/Chronicle.Data/Migrations/
git commit -m "feat(data): migration — create media_enrichment table"
```

---

## Phase 3 — Service Core

### Task 6: Add EnrichmentOptions and update IMetadataEnrichmentService

**Files:**
- Modify: `src/Chronicle.Services/IMetadataEnrichmentService.cs`

**Step 1: Add EnrichmentMode and EnrichmentOptions**

Add to the top of `IMetadataEnrichmentService.cs` (above the interface):

```csharp
public enum EnrichmentMode
{
    /// <summary>Skip items already Completed — background task behaviour.</summary>
    FillGaps,
    /// <summary>Always re-fetch — user-triggered refresh behaviour.</summary>
    Force
}

public record EnrichmentOptions(
    EnrichmentMode Mode,
    /// <summary>Fix Match: user-supplied external ID. Bypasses scoring entirely.</summary>
    string?        IdOverride = null,
    /// <summary>When true, recurse into direct children after enriching self.</summary>
    bool           Cascade    = true
);
```

**Step 2: Add new methods to the interface**

```csharp
// ── Main entry point — all callers use one of these ──────────────────────
/// <summary>Enrich one item for one plugin, then optionally cascade to children.</summary>
Task EnrichItemAsync(int mediaItemId, string pluginId,
                     EnrichmentOptions options, CancellationToken ct = default);

/// <summary>Enrich one item across ALL applicable plugins (e.g. "Refresh All").</summary>
Task EnrichItemAsync(int mediaItemId,
                     EnrichmentOptions options, CancellationToken ct = default);

/// <summary>Returns the enrichment row per plugin for a given item (replaces GetRefreshLogsAsync).</summary>
Task<IReadOnlyList<EnrichmentRecord>> GetEnrichmentRecordsAsync(
    int mediaItemId, CancellationToken ct = default);
```

**Step 3: Add EnrichmentRecord DTO** (below the interface in the same file):

```csharp
public record EnrichmentRecord(
    string          PluginId,
    string?         ExternalId,
    EnrichmentStatus Status,
    DateTime?       LastCompletedAt,
    string?         ErrorMessage,
    string?         DiagnosticsJson
);
```

**Step 4: Build**

```bash
cd W:/Scripts/Chronicle && dotnet build src/Chronicle.Services/
```
Expected: no errors (interface additions are non-breaking until implementations are checked).

**Step 5: Commit**

```bash
git add src/Chronicle.Services/IMetadataEnrichmentService.cs
git commit -m "feat(services): add EnrichmentOptions, EnrichmentMode, EnrichItemAsync overloads to interface"
```

---

### Task 7: Implement EnrichItemCoreAsync — ID resolution

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`
- Modify: `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`

This is the heart of the refactor. Write one test per ID resolution path.

**Step 1: Write failing tests for ID resolution paths**

Add to `MetadataEnrichmentServiceTests.cs`:

```csharp
// Path A: IdOverride bypasses all other resolution
[Fact]
public async Task EnrichItemAsync_UsesIdOverrideDirectly()
{
    var item = await SeedRootItem("Wrong Movie", 2000);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

    var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
    provider.Setup(p => p.GetByIdAsync("movie:999", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:999" });

    var opts = new EnrichmentOptions(EnrichmentMode.Force, IdOverride: "movie:999", Cascade: false);
    await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

    var row = await _db.MediaEnrichments
        .FirstAsync(e => e.MediaItemId == item.Id && e.PluginId == "chronicle.plugin.tmdb");
    row.ExternalId.Should().Be("movie:999");
    row.Status.Should().Be(EnrichmentStatus.Completed);
}

// Path B: FillGaps skips Completed items
[Fact]
public async Task EnrichItemAsync_FillGaps_SkipsCompleted()
{
    var item = await SeedRootItem("Blade Runner", 1982);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", "movie:78", EnrichmentStatus.Completed);

    var provider = SetupProvider("chronicle.plugin.tmdb", "movies");

    var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
    await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

    // Provider should never have been called
    provider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
}

// Path C: stored ExternalId used when present
[Fact]
public async Task EnrichItemAsync_Force_UsesStoredExternalId()
{
    var item = await SeedRootItem("Blade Runner", 1982);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", "movie:78", EnrichmentStatus.Completed);

    var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
    provider.Setup(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" });

    var opts = new EnrichmentOptions(EnrichmentMode.Force, Cascade: false);
    await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

    provider.Verify(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()), Times.Once);
}

// Path D: root with no ID falls through to search
[Fact]
public async Task EnrichItemAsync_SearchesWhenNoStoredId()
{
    var item = await SeedRootItem("Blade Runner", 1982);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

    var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
    provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<ScoredCandidate>
        {
            new(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" }, Score: 80)
        });
    provider.Setup(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" });

    var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
    await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

    var row = await _db.MediaEnrichments.FirstAsync(e => e.MediaItemId == item.Id);
    row.ExternalId.Should().Be("movie:78");
    row.Status.Should().Be(EnrichmentStatus.Completed);
}

// Path D: search result below threshold → NotFound
[Fact]
public async Task EnrichItemAsync_SearchBelowThreshold_SetsNotFound()
{
    var item = await SeedRootItem("Xyzzy Unmatched", null);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

    var provider = SetupProvider("chronicle.plugin.tmdb", "movies");
    provider.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<ScoredCandidate>
        {
            new(new MediaMetadata { Title = "Something Else", ExternalId = "movie:1" }, Score: 20)
        });

    var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
    await _svc.EnrichItemAsync(item.Id, "chronicle.plugin.tmdb", opts);

    var row = await _db.MediaEnrichments.FirstAsync(e => e.MediaItemId == item.Id);
    row.Status.Should().Be(EnrichmentStatus.NotFound);
    row.ExternalId.Should().BeNull();
}
```

Also add helpers to the test class:

```csharp
private async Task<MediaItem> SeedRootItem(string name, int? year)
{
    var item = new MediaItem
    {
        Name = name, Year = year, MediaTypeId = _mediaType.Id,
        HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
    _db.MediaItems.Add(item);
    await _db.SaveChangesAsync();
    return item;
}

private async Task<MediaItemEnrichment> SeedEnrichmentRow(
    int itemId, string pluginId, string? externalId, EnrichmentStatus status)
{
    var row = new MediaItemEnrichment
    {
        MediaItemId = itemId, PluginId = pluginId,
        ExternalId = externalId, Status = status, MaxRetries = 3
    };
    _db.MediaEnrichments.Add(row);
    await _db.SaveChangesAsync();
    return row;
}

private Mock<IMetadataProvider> SetupProvider(string pluginId, string mediaTypeName)
{
    var mock = new Mock<IMetadataProvider>();
    mock.Setup(p => p.PluginId).Returns(pluginId);
    mock.Setup(p => p.GetSupportedMediaTypes())
        .Returns(new[] { new MediaTypeSupport { MediaTypeName = mediaTypeName } });
    _registry.Setup(r => r.GetMetadataProvider(pluginId)).Returns(mock.Object);
    _registry.Setup(r => r.GetMetadataProviderEntries())
        .Returns(new[] { (pluginId, mock.Object) });
    return mock;
}
```

**Step 2: Run — expect failures**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit --filter "MetadataEnrichmentServiceTests" -v normal 2>&1 | tail -20
```

**Step 3: Implement EnrichItemAsync(int, string, EnrichmentOptions, CancellationToken)**

In `MetadataEnrichmentService.cs`, add the public method and its private core. Key logic:

```csharp
public async Task EnrichItemAsync(
    int mediaItemId, string pluginId,
    EnrichmentOptions options, CancellationToken ct = default)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

    var item = await db.MediaItems
        .Include(m => m.MediaType)
        .Include(m => m.Parent)
        .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
    if (item is null) return;

    var provider = registry.GetMetadataProvider(pluginId);
    if (provider is null) return;

    await EnrichItemCoreAsync(db, provider, pluginId, item, options, ct);
}
```

Add the private core method. Confidence threshold constant (default 50):

```csharp
private const int DefaultConfidenceThreshold = 50;

private async Task EnrichItemCoreAsync(
    ChronicleDbContext db,
    IMetadataProvider provider,
    string pluginId,
    MediaItem item,
    EnrichmentOptions options,
    CancellationToken ct)
{
    // 1. Load or create enrichment row
    var row = await db.MediaEnrichments
        .FirstOrDefaultAsync(e => e.MediaItemId == item.Id && e.PluginId == pluginId, ct);
    if (row is null)
    {
        row = new MediaItemEnrichment
            { MediaItemId = item.Id, PluginId = pluginId, MaxRetries = 3 };
        db.MediaEnrichments.Add(row);
    }

    // 2. FillGaps skip
    if (options.Mode == EnrichmentMode.FillGaps
        && row.Status == EnrichmentStatus.Completed
        && options.IdOverride is null)
    {
        if (options.Cascade)
            await CascadeToChildrenAsync(db, provider, pluginId, item, options, ct);
        return;
    }

    row.LastAttemptedAt = DateTime.UtcNow;
    MediaMetadata? result = null;
    string? resolvedId   = null;

    try
    {
        // 3a. IdOverride
        if (options.IdOverride is not null)
        {
            resolvedId = options.IdOverride.Trim();
        }
        // 3b. Stored ID — validate hierarchy level prefix
        else if (!string.IsNullOrEmpty(row.ExternalId) && IsIdValidForLevel(row.ExternalId, item))
        {
            resolvedId = row.ExternalId;
        }
        // 3c. Parent-derived ID
        else if (item.ParentId is not null)
        {
            resolvedId = await TryDeriveFromParentAsync(db, pluginId, item, ct);
        }

        // Fetch if we have a resolved ID from 3a/3b/3c
        if (resolvedId is not null)
        {
            result = await provider.GetByIdAsync(resolvedId, ct);
            if (result is null) resolvedId = null; // provider returned nothing — fall through to search
        }

        // 3d. Search (root items with no resolved ID only)
        if (result is null && item.ParentId is null)
        {
            var childCount = await db.MediaItems.CountAsync(m => m.ParentId == item.Id, ct);
            var ctx = new MediaSearchContext(
                Name:           NormalizeName(item.Name),
                Year:           item.Year,
                ChildCount:     childCount > 0 ? childCount : null,
                HierarchyLevel: item.HierarchyLevel);

            var candidates = await provider.SearchAsync(ctx, ct);
            var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();

            StoreDiagnostics(row, ctx.Name, candidates);

            if (best is null || best.Score < DefaultConfidenceThreshold
                || string.IsNullOrEmpty(best.Metadata.ExternalId))
            {
                row.Status = EnrichmentStatus.NotFound;
                await db.SaveChangesAsync(ct);
                return;
            }

            resolvedId = best.Metadata.ExternalId;
            // Fetch full metadata (search results are often lightweight)
            result = await provider.GetByIdAsync(resolvedId, ct);
            result ??= best.Metadata;
        }

        if (result is null || string.IsNullOrEmpty(resolvedId))
        {
            row.Status = EnrichmentStatus.NotFound;
            await db.SaveChangesAsync(ct);
            return;
        }

        // 6. Merge
        MergeIntoItem(item, pluginId, result);

        // 7. Update row
        row.ExternalId      = resolvedId;
        row.Status          = EnrichmentStatus.Completed;
        row.LastCompletedAt = DateTime.UtcNow;
        row.ErrorMessage    = null;
        row.RetryCount      = 0;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception ex)
    {
        var isExpected = ex is HttpRequestException or TaskCanceledException
                                                     or TimeoutException
                                                     or OperationCanceledException;
        if (isExpected)
            _logger.LogWarning(
                "Enrichment failed for item {ItemId} plugin {PluginId}: {Type}: {Msg}",
                item.Id, pluginId, ex.GetType().Name, ex.Message);
        else
            _logger.LogWarning(ex, "Enrichment failed for item {ItemId} plugin {PluginId}",
                item.Id, pluginId);

        row.RetryCount++;
        row.ErrorMessage = ex.Message;
        row.Status = row.RetryCount >= row.MaxRetries
            ? EnrichmentStatus.Exhausted
            : EnrichmentStatus.Failed;
    }

    await db.SaveChangesAsync(ct);

    // 8. Cascade
    if (options.Cascade)
        await CascadeToChildrenAsync(db, provider, pluginId, item, options, ct);
}
```

**Step 4: Add required helpers to MetadataEnrichmentService**

```csharp
private static bool IsIdValidForLevel(string externalId, MediaItem item)
{
    var sep = externalId.IndexOf(':');
    if (sep <= 0) return true; // can't validate, assume ok
    var prefix = externalId[..sep];
    if (item.ParentId is null)
        return prefix is "artist" or "movie" or "tv";
    // child-level validation: reject bare show-level IDs on season/episode rows
    if (prefix == "tv" && !externalId.Contains('/'))
        return false;
    return true;
}

private async Task<string?> TryDeriveFromParentAsync(
    ChronicleDbContext db, string pluginId, MediaItem item, CancellationToken ct)
{
    var parentRow = await db.MediaEnrichments
        .FirstOrDefaultAsync(e => e.MediaItemId == item.ParentId && e.PluginId == pluginId, ct);

    if (parentRow?.ExternalId is null || parentRow.Status != EnrichmentStatus.Completed)
        return null;

    if (item.Number is null) return null;

    var parentId = parentRow.ExternalId;

    // Level 1 (season/album): parent is root
    if (item.HierarchyLevel == 1)
        return $"{parentId}/season:{item.Number}";

    // Level 2 (episode/track): grandparent is root, parent is season/album
    if (item.HierarchyLevel == 2)
    {
        var grandparentRow = await db.MediaEnrichments
            .Include(e => e.MediaItem)
            .FirstOrDefaultAsync(e => e.MediaItem!.Id == item.Parent!.ParentId
                                   && e.PluginId == pluginId, ct);
        if (grandparentRow?.ExternalId is null) return null;
        var seasonNum = item.Parent?.Number;
        if (seasonNum is null) return null;
        return $"{grandparentRow.ExternalId}/season:{seasonNum}/episode:{item.Number}";
    }

    return null;
}

private async Task CascadeToChildrenAsync(
    ChronicleDbContext db, IMetadataProvider provider, string pluginId,
    MediaItem parent, EnrichmentOptions options, CancellationToken ct)
{
    var children = await db.MediaItems
        .Include(m => m.MediaType)
        .Include(m => m.Parent)
        .Where(m => m.ParentId == parent.Id)
        .OrderBy(m => m.Number).ThenBy(m => m.Name)
        .ToListAsync(ct);

    foreach (var child in children)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            await EnrichItemCoreAsync(db, provider, pluginId, child,
                options with { IdOverride = null }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cascade: failed enriching child {Id} '{Name}'",
                child.Id, child.Name);
        }
    }
}

private static string NormalizeName(string name) =>
    System.Text.RegularExpressions.Regex
        .Replace(name, @"[:\-,\.']", " ")
        .Replace("  ", " ")
        .Trim()
        .ToLowerInvariant()
        .TrimStart(["the ", "a ", "an "]);

private static void StoreDiagnostics(
    MediaItemEnrichment row, string query, IReadOnlyList<ScoredCandidate> candidates)
{
    var top5 = candidates.OrderByDescending(c => c.Score).Take(5)
        .Select(c => new { c.Metadata.Title, c.Metadata.Year,
                           c.Metadata.ExternalId, c.Score, c.ScoreReason })
        .ToList();
    row.DiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(new
    {
        query,
        threshold          = DefaultConfidenceThreshold,
        candidatesReturned = candidates.Count,
        topCandidates      = top5
    });
}
```

**Step 5: Add MergeIntoItem (lossless — stores everything)**

```csharp
private static void MergeIntoItem(MediaItem item, string pluginId, MediaMetadata meta)
{
    // Store complete provider response — nothing discarded
    var existing = System.Text.Json.JsonSerializer
        .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            item.MetadataJson ?? "{}") ?? [];

    var shortId = pluginId.Contains('.') ? pluginId.Split('.').Last() : null;
    if (shortId is not null) existing.Remove(shortId); // clean up legacy short-key entries

    var savedResults = meta.Results;
    var savedTotal   = meta.TotalResults;
    meta.Results      = null;
    meta.TotalResults = 0;
    try   { existing[pluginId] = System.Text.Json.JsonSerializer.SerializeToElement(meta); }
    finally { meta.Results = savedResults; meta.TotalResults = savedTotal; }
    item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(existing);

    // First-class fields
    if (!string.IsNullOrWhiteSpace(meta.PosterUrl))  item.PosterUrl      = meta.PosterUrl;
    if (!string.IsNullOrWhiteSpace(meta.Overview))   item.Overview       = meta.Overview;
    if (meta.RuntimeMinutes.HasValue)                 item.RuntimeMinutes = meta.RuntimeMinutes;

    // Name + Year: only update root items, and only with non-generic values
    if (item.HierarchyLevel == 0)
    {
        if (!string.IsNullOrWhiteSpace(meta.Title)) item.Name = meta.Title;
        if (meta.Year.HasValue)                      item.Year = meta.Year;
    }

    item.UpdatedAt = DateTime.UtcNow;
}
```

**Step 6: Run tests**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit --filter "MetadataEnrichmentServiceTests" -v normal
```
Expected: new tests pass; existing tests may need minor adjustments for the new table name.

**Step 7: Commit**

```bash
git add src/Chronicle.Services/MetadataEnrichmentService.cs tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs
git commit -m "feat(services): implement EnrichItemCoreAsync with unified ID resolution and cascade"
```

---

### Task 8: Implement all-plugins overload and update EnrichPendingAsync

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`

**Step 1: Add failing test for all-plugins overload**

```csharp
[Fact]
public async Task EnrichItemAsync_AllPlugins_CallsEachApplicablePlugin()
{
    var item = await SeedRootItem("Blade Runner", 1982);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb", null, EnrichmentStatus.Pending);

    var tmdb = SetupProvider("chronicle.plugin.tmdb", "movies");
    tmdb.Setup(p => p.SearchAsync(It.IsAny<MediaSearchContext>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<ScoredCandidate>
            { new(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" }, 90) });
    tmdb.Setup(p => p.GetByIdAsync("movie:78", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new MediaMetadata { Title = "Blade Runner", ExternalId = "movie:78" });

    var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
    await _svc.EnrichItemAsync(item.Id, opts);

    var row = await _db.MediaEnrichments.FirstAsync(e => e.PluginId == "chronicle.plugin.tmdb");
    row.Status.Should().Be(EnrichmentStatus.Completed);
}
```

**Step 2: Implement**

```csharp
public async Task EnrichItemAsync(
    int mediaItemId, EnrichmentOptions options, CancellationToken ct = default)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

    var item = await db.MediaItems
        .Include(m => m.MediaType)
        .Include(m => m.Parent)
        .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
    if (item is null) return;

    var mediaTypeName = NormalizeMediaTypeName(item.MediaType?.Name ?? string.Empty);

    foreach (var (pluginId, provider) in registry.GetMetadataProviderEntries())
    {
        ct.ThrowIfCancellationRequested();
        var supported = provider.GetSupportedMediaTypes()
            .Any(t => string.Equals(
                NormalizeMediaTypeName(t.MediaTypeName), mediaTypeName,
                StringComparison.OrdinalIgnoreCase));
        if (!supported) continue;

        try { await EnrichItemCoreAsync(db, provider, pluginId, item, options, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnrichItemAsync all-plugins: plugin {P} failed for item {Id}",
                pluginId, mediaItemId);
        }
    }
}
```

**Step 3: Update EnrichPendingAsync to use the new table**

Replace the existing `EnrichPendingAsync` body. It now queries `MediaEnrichments` instead of `EnrichmentStatuses`:

```csharp
public async Task EnrichPendingAsync(string pluginId, CancellationToken ct = default)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db       = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

    var provider = registry.GetMetadataProvider(pluginId);
    if (provider is null)
    {
        _logger.LogWarning("EnrichPendingAsync: plugin {PluginId} not found", pluginId);
        return;
    }

    var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
    var rows = await db.MediaEnrichments
        .Include(e => e.MediaItem).ThenInclude(m => m!.MediaType)
        .Include(e => e.MediaItem).ThenInclude(m => m!.Parent)
        .Where(e => e.PluginId == pluginId &&
                    (e.Status == EnrichmentStatus.Pending ||
                     (e.Status == EnrichmentStatus.Failed &&
                      (e.LastAttemptedAt == null || e.LastAttemptedAt < cutoff))))
        .ToListAsync(ct);

    _logger.LogInformation("EnrichPendingAsync: {Count} items for {PluginId}", rows.Count, pluginId);

    foreach (var row in rows)
    {
        ct.ThrowIfCancellationRequested();
        if (row.MediaItem is null) continue;
        var opts = new EnrichmentOptions(EnrichmentMode.FillGaps, Cascade: false);
        try
        {
            await EnrichItemCoreAsync(db, provider, pluginId, row.MediaItem, opts, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnrichPendingAsync: item {Id} plugin {P}", row.MediaItemId, pluginId);
        }
    }
}
```

**Step 4: Run all unit tests**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit -v normal 2>&1 | tail -30
```

**Step 5: Commit**

```bash
git add src/Chronicle.Services/MetadataEnrichmentService.cs tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs
git commit -m "feat(services): implement all-plugins EnrichItemAsync overload and migrate EnrichPendingAsync to new table"
```

---

### Task 9: Implement GetEnrichmentRecordsAsync

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`

**Step 1: Write failing test**

```csharp
[Fact]
public async Task GetEnrichmentRecordsAsync_ReturnsOneRowPerPlugin()
{
    var item = await SeedRootItem("Test", 2020);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.tmdb",    "movie:1", EnrichmentStatus.Completed);
    await SeedEnrichmentRow(item.Id, "chronicle.plugin.musicbrainz", null, EnrichmentStatus.NotFound);

    var records = await _svc.GetEnrichmentRecordsAsync(item.Id);

    records.Should().HaveCount(2);
    records.Should().Contain(r => r.PluginId == "chronicle.plugin.tmdb"
                                && r.ExternalId == "movie:1"
                                && r.Status == EnrichmentStatus.Completed);
    records.Should().Contain(r => r.PluginId == "chronicle.plugin.musicbrainz"
                                && r.Status == EnrichmentStatus.NotFound);
}
```

**Step 2: Implement**

```csharp
public async Task<IReadOnlyList<EnrichmentRecord>> GetEnrichmentRecordsAsync(
    int mediaItemId, CancellationToken ct = default)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

    var rows = await db.MediaEnrichments
        .Where(e => e.MediaItemId == mediaItemId)
        .ToListAsync(ct);

    return rows.Select(r => new EnrichmentRecord(
        r.PluginId, r.ExternalId, r.Status,
        r.LastCompletedAt, r.ErrorMessage, r.DiagnosticsJson))
        .ToList();
}
```

**Step 3: Run test, commit**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit --filter "GetEnrichmentRecordsAsync" -v normal
git add src/Chronicle.Services/MetadataEnrichmentService.cs tests/
git commit -m "feat(services): implement GetEnrichmentRecordsAsync"
```

---

## Phase 4 — Plugin Updates

### Task 10: Update TMDB plugin SearchAsync

**Files:**
- Modify: `src/Chronicle.Plugins.TMDB/TmdbMetadataProvider.cs`

**Step 1: Locate the existing SearchAsync**

```bash
grep -n "SearchAsync\|ITvDetailProvider\|GetTvSeasonAsync" W:/Scripts/Chronicle/src/Chronicle.Plugins.TMDB/TmdbMetadataProvider.cs | head -20
```

**Step 2: Replace the SearchAsync signature and body**

The TMDB plugin now receives a `MediaSearchContext`, constructs its own query, and returns scored candidates. Move all Lucene/query construction that the service was doing into this method. Implement scoring using:

- Title exact match (normalized): 60 pts
- Title contains match (normalized): 30 pts
- Year exact match: 20 pts
- Year ±1 year: 10 pts
- ChildCount match (if provider reports season count and context has ChildCount): 10 pts

```csharp
public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
    MediaSearchContext context, CancellationToken ct = default)
{
    // Determine TMDB media type hint from hierarchy and context
    var mediaTypeHint = context.HierarchyLevel == 0
        ? (context.Name.Contains("season") ? "tv" : "multi")
        : "multi";

    var searchResult = await _client.SearchAsync(context.Name, mediaTypeHint, ct);
    if (searchResult?.Results is null) return [];

    return searchResult.Results
        .Select(r => ScoreCandidate(context, r))
        .Where(c => c.Metadata.ExternalId is not null)
        .OrderByDescending(c => c.Score)
        .Take(10)
        .ToList();
}

private static ScoredCandidate ScoreCandidate(MediaSearchContext ctx, MediaMetadata candidate)
{
    int score = 0;
    var reasons = new List<string>();

    var cn = Normalize(candidate.Title ?? string.Empty);
    var qn = Normalize(ctx.Name);

    if (string.Equals(cn, qn, StringComparison.Ordinal))
        { score += 60; reasons.Add("title exact"); }
    else if (cn.Contains(qn) || qn.Contains(cn))
        { score += 30; reasons.Add("title contains"); }

    if (ctx.Year.HasValue && candidate.Year.HasValue)
    {
        if (ctx.Year == candidate.Year)
            { score += 20; reasons.Add("year exact"); }
        else if (Math.Abs(ctx.Year.Value - candidate.Year.Value) == 1)
            { score += 10; reasons.Add("year ±1"); }
    }

    // Structural: if candidate reports season count and we have child count
    if (ctx.ChildCount.HasValue && candidate.ExtendedData is not null
        && candidate.ExtendedData.TryGetValue("number_of_seasons", out var ns)
        && int.TryParse(ns, out var providerSeasons)
        && providerSeasons == ctx.ChildCount.Value)
        { score += 10; reasons.Add($"season count {providerSeasons} matches"); }

    return new ScoredCandidate(candidate, score, string.Join(", ", reasons));
}

private static string Normalize(string s) =>
    System.Text.RegularExpressions.Regex.Replace(s, @"[:\-,\.']", " ")
        .Replace("  ", " ").Trim().ToLowerInvariant();
```

**Step 3: Remove ITvDetailProvider — fold GetTvSeasonAsync / GetTvEpisodeAsync into GetByIdAsync**

TMDB's `GetByIdAsync` already handles compound IDs like `tv:314`. Extend it to also handle `tv:314/season:2` and `tv:314/season:2/episode:4` by routing to the season/episode endpoints internally:

```csharp
public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
{
    // tv:{id}/season:{s}/episode:{e}
    if (externalId.Contains("/episode:"))
    {
        // parse and call episode endpoint internally
        var (seriesId, season, episode) = ParseEpisodeId(externalId);
        return await FetchEpisodeAsync(seriesId, season, episode, ct);
    }
    // tv:{id}/season:{s}
    if (externalId.Contains("/season:"))
    {
        var (seriesId, season) = ParseSeasonId(externalId);
        return await FetchSeasonAsync(seriesId, season, ct);
    }
    // tv:{id} or movie:{id}
    // ... existing logic unchanged
}
```

The internal `FetchSeasonAsync` and `FetchEpisodeAsync` are the existing `GetTvSeasonAsync` / `GetTvEpisodeAsync` bodies, now private. Remove the `ITvDetailProvider` interface declaration from the class.

**Step 4: Build the plugin**

```bash
cd W:/Scripts/Chronicle && dotnet build src/Chronicle.Plugins.TMDB/
```

**Step 5: Commit**

```bash
git add src/Chronicle.Plugins.TMDB/
git commit -m "feat(tmdb): implement SearchAsync(MediaSearchContext), fold ITvDetailProvider into GetByIdAsync"
```

---

### Task 11: Update MusicBrainz plugin SearchAsync

**Files:**
- Modify: `src/Chronicle.Plugins.MusicBrainz/MusicBrainzMetadataProvider.cs` (adjust path if different)

**Step 1: Locate and read the existing SearchAsync**

```bash
grep -n "SearchAsync\|Lucene\|album:\|artist:\|track:" $(find W:/Scripts/Chronicle/src -name "MusicBrainzMetadataProvider.cs")
```

**Step 2: Replace SearchAsync body**

Move the Lucene query construction out of `MetadataEnrichmentService` (where it currently lives) and into the plugin. The plugin receives `MediaSearchContext` and builds the Lucene query internally:

```csharp
public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
    MediaSearchContext context, CancellationToken ct = default)
{
    string query = context.HierarchyLevel switch
    {
        0 => $"artist:{MbQuote(context.Name)}",
        1 => BuildAlbumQuery(context),
        2 => BuildTrackQuery(context),
        _ => context.Name
    };

    var results = await _client.SearchAsync(query, ct);
    if (results is null) return [];

    return results
        .Select(r => ScoreCandidate(context, r))
        .OrderByDescending(c => c.Score)
        .Take(10)
        .ToList();
}

private static string BuildAlbumQuery(MediaSearchContext ctx)
{
    var albumName  = StripYearPrefix(ctx.Name);
    var artistClause = ctx.ParentName is not null
        ? $" AND artist:{MbQuote(ctx.ParentName)}" : string.Empty;
    return $"album:{MbQuote(albumName)}{artistClause}";
}

private static string BuildTrackQuery(MediaSearchContext ctx)
{
    var artistClause  = ctx.GrandparentName is not null
        ? $" AND artist:{MbQuote(ctx.GrandparentName)}" : string.Empty;
    var releaseClause = ctx.ParentName is not null
        ? $" AND release:{MbQuote(StripYearPrefix(ctx.ParentName))}" : string.Empty;
    return $"track:{MbQuote(ctx.Name)}{artistClause}{releaseClause}";
}
```

Scoring for MusicBrainz follows the same pattern as TMDB (title exact/contains, year).

**Step 3: Build**

```bash
cd W:/Scripts/Chronicle && dotnet build src/Chronicle.Plugins.MusicBrainz/ 2>/dev/null || dotnet build src/ 2>&1 | grep "error CS" | head -20
```

**Step 4: Commit**

```bash
git add src/Chronicle.Plugins.MusicBrainz/
git commit -m "feat(musicbrainz): move Lucene query construction into plugin SearchAsync(MediaSearchContext)"
```

---

## Phase 5 — Wiring and Cleanup

### Task 12: Update PluginTaskRunner and delete MetadataRefreshService

**Files:**
- Modify: `src/Chronicle.Services/PluginTaskRunner.cs`
- Delete: `src/Chronicle.Services/MetadataRefreshService.cs`
- Delete: `src/Chronicle.Services/IMetadataRefreshService.cs`

**Step 1: Update PluginTaskRunner**

Replace `_refresh.RefreshForPluginAsync(pluginId, ct)` with a loop using the enrichment service. First update the constructor to drop `IMetadataRefreshService`:

```csharp
public PluginTaskRunner(IMetadataEnrichmentService enrichment, ChronicleDbContext db)
{ ... }

// resync-all-metadata: Force refresh all library root items for this plugin
case ResyncAll:
    var rootIds = await _db.UserLibraries
        .Select(ul => ul.MediaItemId).Distinct().ToListAsync(ct);
    var roots = await _db.MediaItems
        .Where(m => rootIds.Contains(m.Id) && m.HierarchyLevel == 0)
        .Select(m => m.Id).ToListAsync(ct);
    foreach (var id in roots)
    {
        ct.ThrowIfCancellationRequested();
        await _enrichment.EnrichItemAsync(id, pluginId,
            new EnrichmentOptions(EnrichmentMode.Force, Cascade: true), ct);
    }
    return;
```

**Step 2: Update PluginTaskRunner tests**

```bash
grep -n "IMetadataRefreshService\|RefreshForPluginAsync" W:/Scripts/Chronicle/tests/Chronicle.Tests.Unit/Services/PluginTaskRunnerTests.cs
```
Update mocks to use `IMetadataEnrichmentService`.

**Step 3: Delete the old files**

```bash
rm W:/Scripts/Chronicle/src/Chronicle.Services/MetadataRefreshService.cs
rm W:/Scripts/Chronicle/src/Chronicle.Services/IMetadataRefreshService.cs
```

**Step 4: Build — fix any remaining references**

```bash
cd W:/Scripts/Chronicle && dotnet build src/ 2>&1 | grep "error CS"
```

Common places that reference `IMetadataRefreshService`:
- `src/Chronicle.API/Program.cs` — DI registration
- `src/Chronicle.API/Controllers/MediaController.cs` — inject and use
- `tests/Chronicle.Tests.Unit/Services/MetadataRefreshServiceTests.cs` — delete this file

Delete `MetadataRefreshServiceTests.cs` — the new enrichment tests cover the same behaviour.

**Step 5: Run all tests**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Unit -v normal 2>&1 | tail -30
```

**Step 6: Commit**

```bash
git add -A
git commit -m "feat(services): wire PluginTaskRunner to unified enrichment service, delete MetadataRefreshService"
```

---

### Task 13: Update API controllers

**Files:**
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`
- Modify: `src/Chronicle.API/Program.cs`

**Step 1: Find all RefreshService usages in controllers**

```bash
grep -rn "IMetadataRefreshService\|RefreshItemAsync\|RefreshItemForPluginAsync\|GetRefreshLogsAsync" W:/Scripts/Chronicle/src/Chronicle.API --include="*.cs"
```

**Step 2: Update MediaController.cs**

Replace `IMetadataRefreshService` injection with `IMetadataEnrichmentService`.

```csharp
// POST /media/{id}/refresh  →  Force refresh, all plugins, cascade
[HttpPost("{id:int}/refresh")]
public async Task<IActionResult> RefreshMetadata(int id, CancellationToken ct)
{
    try
    {
        await _enrichment.EnrichItemAsync(id,
            new EnrichmentOptions(EnrichmentMode.Force, Cascade: true), ct);
        var item = await _mediaService.GetByIdAsync(id);
        if (item is null) return NotFound(...);
        var records = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
        // build DTO using records instead of refresh logs
        return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, records, ...)));
    }
    catch (Exception ex) { return StatusCode(502, ...); }
}

// POST /media/{id}/refresh/{pluginId}  →  per-plugin, with optional Fix Match body
[HttpPost("{id:int}/refresh/{pluginId}")]
public async Task<IActionResult> RefreshForPlugin(
    int id, string pluginId, [FromBody] RefreshRequest? body, CancellationToken ct)
{
    try
    {
        var opts = new EnrichmentOptions(
            EnrichmentMode.Force,
            IdOverride: body?.Input?.Trim(),
            Cascade: false);
        var item = await _enrichment.EnrichItemAsync(id, pluginId, opts, ct); // returns void now
        // re-fetch item for response
        ...
    }
    ...
}
```

**Step 3: Update Program.cs DI registrations**

Remove `builder.Services.AddScoped<IMetadataRefreshService, MetadataRefreshService>();`

**Step 4: Run integration tests**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/Chronicle.Tests.Integration -v normal 2>&1 | tail -30
```

Fix any integration test failures before proceeding.

**Step 5: Commit**

```bash
git add src/Chronicle.API/
git commit -m "feat(api): update controllers to use unified IMetadataEnrichmentService"
```

---

## Phase 6 — Database Migration and Data Migration

### Task 14: Migrate existing data and drop old tables

**Files:**
- Create: migration file (auto-generated)
- Create: `scripts/Migrate-EnrichmentData.sql` (data migration for production)

**Step 1: Write the data migration SQL**

```sql
-- scripts/Migrate-EnrichmentData.sql
-- Migrate enrichment_statuses into media_enrichment
INSERT INTO media_enrichment (media_item_id, plugin_id, external_id, status,
    retry_count, max_retries, last_attempted_at, last_completed_at,
    error_message, diagnostics_json)
SELECT
    es.media_item_id,
    es.plugin_id,
    -- Prefer media_external_ids.external_id if available for this item+plugin
    COALESCE(
        (SELECT mei.external_id
         FROM media_external_ids mei
         WHERE mei.media_item_id = es.media_item_id
           AND LOWER(mei.source) = LOWER(es.plugin_id)
         LIMIT 1),
        es.external_id
    ) AS external_id,
    es.status,
    es.retry_count,
    es.max_retries,
    es.last_attempted_at,
    es.last_completed_at,
    es.error_message,
    es.diagnostics_json
FROM enrichment_statuses es
ON CONFLICT(media_item_id, plugin_id) DO NOTHING;

-- Items that only have a media_external_ids entry (no enrichment_statuses row)
-- get a Completed row so they won't be re-searched
INSERT INTO media_enrichment (media_item_id, plugin_id, external_id, status, retry_count, max_retries)
SELECT
    mei.media_item_id,
    mei.source,
    mei.external_id,
    'Completed',
    0,
    3
FROM media_external_ids mei
WHERE NOT EXISTS (
    SELECT 1 FROM media_enrichment me
    WHERE me.media_item_id = mei.media_item_id
      AND LOWER(me.plugin_id) = LOWER(mei.source)
)
ON CONFLICT(media_item_id, plugin_id) DO NOTHING;
```

**Step 2: Create EF migration to drop old tables**

```bash
cd W:/Scripts/Chronicle/src/Chronicle.API && dotnet ef migrations add DropLegacyEnrichmentTables --project ../Chronicle.Data
```

Review the generated migration. It should drop:
- `enrichment_statuses`
- `media_external_ids`
- `media_item_refresh_logs`

**Step 3: Apply locally**

First run the data migration script, then apply the schema migration:

```bash
# Run data migration against dev DB
sqlite3 chronicle.db < scripts/Migrate-EnrichmentData.sql

# Apply EF migration
cd W:/Scripts/Chronicle/src/Chronicle.API && dotnet ef database update
```

**Step 4: Verify data survived**

```bash
sqlite3 chronicle.db "SELECT COUNT(*), status FROM media_enrichment GROUP BY status;"
```

**Step 5: Remove old DbSets from ChronicleDbContext**

Remove:
```csharp
public DbSet<MediaItemEnrichmentStatus> EnrichmentStatuses { get; set; }
public DbSet<MediaExternalId>           MediaExternalIds   { get; set; }
public DbSet<MediaItemRefreshLog>       MediaItemRefreshLogs { get; set; }
```

**Step 6: Delete old model files** (if no longer referenced anywhere)

```bash
grep -rn "MediaItemEnrichmentStatus\|MediaExternalId\|MediaItemRefreshLog" W:/Scripts/Chronicle/src --include="*.cs"
```

Delete files with no remaining usages.

**Step 7: Full test suite**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/ -v normal 2>&1 | tail -40
```

All tests must pass before committing.

**Step 8: Commit**

```bash
git add -A
git commit -m "feat(data): drop legacy enrichment/external-id/refresh-log tables, data migration script"
```

---

## Phase 7 — Final Verification

### Task 15: Full test suite and smoke test

**Step 1: Run all tests**

```bash
cd W:/Scripts/Chronicle && dotnet test tests/ -v normal
```
Expected: all passing.

**Step 2: Build release**

```bash
cd W:/Scripts/Chronicle && dotnet build src/Chronicle.sln -c Release
```

**Step 3: Manual smoke test** (requires running API)

```bash
# Start API
cd W:/Scripts/Chronicle/src/Chronicle.API && dotnet run
```

Verify in the UI:
- Media detail page loads enrichment records (plugin header shows "Last refreshed" time)
- Refresh button on a root item cascades through seasons and episodes
- Fix Match sets a specific ID, saves, and shows the correct metadata
- Background task "Fetch Missing Metadata" runs without dying on timeout
- Background task "Resync All Metadata" forces a full re-fetch
- Items already Completed are skipped in FillGaps mode

**Step 4: Commit any final fixes, then push**

```bash
git add -A
git commit -m "fix: post-refactor cleanup after unified enrichment implementation"
git push origin feature/fix-refresh-multi-provider
```

---

## Quick Reference — Key Files

| Layer | File | Change |
|---|---|---|
| Plugin models | `src/Chronicle.Plugins/Models/MediaSearchContext.cs` | NEW |
| Plugin models | `src/Chronicle.Plugins/Models/ScoredCandidate.cs` | NEW |
| Plugin interface | `src/Chronicle.Plugins/IMetadataProvider.cs` | SearchAsync signature |
| Plugin interface | `src/Chronicle.Plugins/ITvDetailProvider.cs` | DELETED |
| Core model | `src/Chronicle.Core/Models/MediaItemEnrichment.cs` | NEW |
| Core model | `src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs` | DELETED |
| Core model | `src/Chronicle.Core/Models/MediaExternalId.cs` | DELETED |
| Core model | `src/Chronicle.Core/Models/MediaItemRefreshLog.cs` | DELETED |
| Data | `src/Chronicle.Data/ChronicleDbContext.cs` | Add MediaEnrichments, remove old DbSets |
| Services | `src/Chronicle.Services/IMetadataEnrichmentService.cs` | Add new methods |
| Services | `src/Chronicle.Services/MetadataEnrichmentService.cs` | Major expansion |
| Services | `src/Chronicle.Services/MetadataRefreshService.cs` | DELETED |
| Services | `src/Chronicle.Services/IMetadataRefreshService.cs` | DELETED |
| Services | `src/Chronicle.Services/PluginTaskRunner.cs` | Drop refresh dependency |
| Plugin impl | `src/Chronicle.Plugins.TMDB/TmdbMetadataProvider.cs` | New SearchAsync, compound GetByIdAsync |
| Plugin impl | `src/Chronicle.Plugins.MusicBrainz/MusicBrainzMetadataProvider.cs` | New SearchAsync |
| API | `src/Chronicle.API/Controllers/MediaController.cs` | Swap service |
| API | `src/Chronicle.API/Program.cs` | DI registration |
| Tests | `tests/Chronicle.Tests.Unit/Services/MetadataRefreshServiceTests.cs` | DELETED |
| Tests | `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs` | Expanded |
