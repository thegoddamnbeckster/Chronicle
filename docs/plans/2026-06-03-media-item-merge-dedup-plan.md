# Media Item Merge & Deduplication Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a full media item deduplication system: candidate detection, manual merge with side-by-side winner selection, lossless AKA-preserving merge, structural unmerge with auto re-enrichment, and a Settings → Duplicates page — all applied across every media type.

**Architecture:** Four new DB tables (`media_item_aliases`, `media_item_merges`, `media_item_duplicate_candidates`, `media_item_duplicate_dismissals`) plus a `normalized_name` column on `media_items`. A new `MergeService` handles merge/unmerge logic. A new `DuplicateCandidateScanService` background task populates candidates. New `DuplicatesController` and additions to `MediaController` expose the API. React frontend adds a Settings → Duplicates page, a reusable merge modal, and merge history on the media detail page.

**Tech Stack:** .NET 9 / C#, EF Core 9 (SQLite), React 18 + TypeScript

---

## Repo paths

- Main repo: `W:\Scripts\Chronicle\`
- Core models: `src/Chronicle.Core/Models/`
- EF context + migrations: `src/Chronicle.Data/`
- Services: `src/Chronicle.Services/`
- API controllers + DTOs: `src/Chronicle.API/`
- Frontend: `src/Chronicle.Web/src/`
- Unit tests: `tests/Chronicle.Tests.Unit/`
- Integration tests: `tests/Chronicle.Tests.Integration/`

---

## Task 1: EF Core migration — new tables and `normalized_name` column

**Files:**
- Create: `src/Chronicle.Data/Migrations/20260603120000_AddMergeAndDedupTables.cs`
- Modify: `src/Chronicle.Data/Migrations/ChronicleDbContextModelSnapshot.cs` (auto-generated — do not hand-edit; use `dotnet ef migrations add`)

**Step 1: Add new model classes to `src/Chronicle.Core/Models/`**

Create `src/Chronicle.Core/Models/MediaItemAlias.cs`:
```csharp
namespace Chronicle.Core.Models;

public class MediaItemAlias
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // "merge", "plugin:hardcover", etc.
    public DateTime CreatedAt { get; set; }

    public MediaItem? MediaItem { get; set; }
}
```

Create `src/Chronicle.Core/Models/MediaItemMerge.cs`:
```csharp
namespace Chronicle.Core.Models;

public class MediaItemMerge
{
    public int Id { get; set; }
    public int WinnerId { get; set; }
    public int LoserOriginalId { get; set; }
    public string LoserName { get; set; } = string.Empty;
    public int LoserMediaTypeId { get; set; }
    public int LoserHierarchyLevel { get; set; }
    public int? LoserParentId { get; set; }
    /// <summary>JSON array of {Source, ExternalId} objects.</summary>
    public string LoserExternalIdsJson { get; set; } = "[]";
    /// <summary>JSON array of child MediaItem IDs that were re-parented to winner.</summary>
    public string LoserChildIdsJson { get; set; } = "[]";
    public DateTime MergedAt { get; set; }
    public int? MergedByUserId { get; set; }

    public MediaItem? Winner { get; set; }
}
```

Create `src/Chronicle.Core/Models/MediaItemDuplicateCandidate.cs`:
```csharp
namespace Chronicle.Core.Models;

public class MediaItemDuplicateCandidate
{
    public int Id { get; set; }
    public int ItemAId { get; set; }
    public int ItemBId { get; set; }
    public DateTime DetectedAt { get; set; }

    public MediaItem? ItemA { get; set; }
    public MediaItem? ItemB { get; set; }
}
```

Create `src/Chronicle.Core/Models/MediaItemDuplicateDismissal.cs`:
```csharp
namespace Chronicle.Core.Models;

public class MediaItemDuplicateDismissal
{
    public int Id { get; set; }
    public int ItemAId { get; set; }
    public int ItemBId { get; set; }
    public DateTime DismissedAt { get; set; }
}
```

**Step 2: Add `NormalizedName` to `MediaItem`**

In `src/Chronicle.Core/Models/MediaItem.cs`, add after `SortName`:
```csharp
/// <summary>
/// Lowercased, punctuation-stripped name used for duplicate detection.
/// Populated at creation/update time by MediaItemExtensions.NormalizeName().
/// </summary>
public string? NormalizedName { get; set; }
```

Also add navigation collections on `MediaItem`:
```csharp
public ICollection<MediaItemAlias> Aliases { get; set; } = new List<MediaItemAlias>();
public ICollection<MediaItemMerge> MergesAsWinner { get; set; } = new List<MediaItemMerge>();
```

**Step 3: Register in `ChronicleDbContext`**

In `src/Chronicle.Data/ChronicleDbContext.cs`, add DbSets after existing ones:
```csharp
public DbSet<MediaItemAlias>              MediaItemAliases              { get; set; } = null!;
public DbSet<MediaItemMerge>             MediaItemMerges               { get; set; } = null!;
public DbSet<MediaItemDuplicateCandidate> MediaItemDuplicateCandidates  { get; set; } = null!;
public DbSet<MediaItemDuplicateDismissal> MediaItemDuplicateDismissals  { get; set; } = null!;
```

In `OnModelCreating`, add configuration:
```csharp
modelBuilder.Entity<MediaItemAlias>(e =>
{
    e.ToTable("media_item_aliases");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
    e.Property(x => x.MediaItemId).HasColumnName("media_item_id").IsRequired();
    e.Property(x => x.Alias).HasColumnName("alias").IsRequired();
    e.Property(x => x.Source).HasColumnName("source").IsRequired();
    e.Property(x => x.CreatedAt).HasColumnName("created_at");
    e.HasIndex(x => x.MediaItemId).HasDatabaseName("idx_aliases_media_item_id");
    e.HasIndex(x => x.Alias).HasDatabaseName("idx_aliases_alias");
    e.HasOne(x => x.MediaItem).WithMany(m => m.Aliases)
        .HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<MediaItemMerge>(e =>
{
    e.ToTable("media_item_merges");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
    e.Property(x => x.WinnerId).HasColumnName("winner_id").IsRequired();
    e.Property(x => x.LoserOriginalId).HasColumnName("loser_original_id").IsRequired();
    e.Property(x => x.LoserName).HasColumnName("loser_name").IsRequired();
    e.Property(x => x.LoserMediaTypeId).HasColumnName("loser_media_type_id").IsRequired();
    e.Property(x => x.LoserHierarchyLevel).HasColumnName("loser_hierarchy_level").IsRequired();
    e.Property(x => x.LoserParentId).HasColumnName("loser_parent_id");
    e.Property(x => x.LoserExternalIdsJson).HasColumnName("loser_external_ids_json").HasDefaultValue("[]");
    e.Property(x => x.LoserChildIdsJson).HasColumnName("loser_child_ids_json").HasDefaultValue("[]");
    e.Property(x => x.MergedAt).HasColumnName("merged_at");
    e.Property(x => x.MergedByUserId).HasColumnName("merged_by_user_id");
    e.HasIndex(x => x.WinnerId).HasDatabaseName("idx_merges_winner_id");
    e.HasOne(x => x.Winner).WithMany(m => m.MergesAsWinner)
        .HasForeignKey(x => x.WinnerId).OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<MediaItemDuplicateCandidate>(e =>
{
    e.ToTable("media_item_duplicate_candidates");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
    e.Property(x => x.ItemAId).HasColumnName("item_a_id").IsRequired();
    e.Property(x => x.ItemBId).HasColumnName("item_b_id").IsRequired();
    e.Property(x => x.DetectedAt).HasColumnName("detected_at");
    e.HasIndex(x => new { x.ItemAId, x.ItemBId }).IsUnique()
        .HasDatabaseName("idx_dup_candidates_unique");
    e.HasOne(x => x.ItemA).WithMany().HasForeignKey(x => x.ItemAId)
        .OnDelete(DeleteBehavior.Cascade);
    e.HasOne(x => x.ItemB).WithMany().HasForeignKey(x => x.ItemBId)
        .OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<MediaItemDuplicateDismissal>(e =>
{
    e.ToTable("media_item_duplicate_dismissals");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
    e.Property(x => x.ItemAId).HasColumnName("item_a_id").IsRequired();
    e.Property(x => x.ItemBId).HasColumnName("item_b_id").IsRequired();
    e.Property(x => x.DismissedAt).HasColumnName("dismissed_at");
    e.HasIndex(x => new { x.ItemAId, x.ItemBId }).IsUnique()
        .HasDatabaseName("idx_dup_dismissals_unique");
    // ON DELETE CASCADE via SQLite FK — EF does not emit this automatically for owned-side configs.
    // Add raw SQL in migration instead (see Step 4).
});

// Add NormalizedName to MediaItem configuration (find existing entity config block)
modelBuilder.Entity<MediaItem>(e => {
    // ... (existing config unchanged) ...
    e.Property(x => x.NormalizedName).HasColumnName("normalized_name");
    e.HasIndex(x => x.NormalizedName).HasDatabaseName("idx_media_items_normalized_name");
});
```

**Step 4: Generate migration**

```powershell
cd W:\Scripts\Chronicle\src
dotnet ef migrations add AddMergeAndDedupTables --project Chronicle.Data --startup-project Chronicle.API
```

Open the generated migration file and verify it created all 4 tables and the `normalized_name` column. If the dismissals table doesn't have `ON DELETE CASCADE` on both FK columns, add it manually in the `Up` method:

```csharp
migrationBuilder.Sql(@"
    CREATE TABLE IF NOT EXISTS media_item_duplicate_dismissals_new (
        id            INTEGER PRIMARY KEY AUTOINCREMENT,
        item_a_id     INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
        item_b_id     INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
        dismissed_at  DATETIME NOT NULL
    );
");
```

Actually, SQLite EF migrations handle this via `OnDelete(DeleteBehavior.Cascade)` — verify the generated migration has `onDelete: ReferentialAction.Cascade` for both FK columns on the dismissals table.

**Step 5: Apply migration**

```powershell
cd W:\Scripts\Chronicle\src
dotnet ef database update --project Chronicle.Data --startup-project Chronicle.API
```

Expected: migration applied, no errors.

**Step 6: Build**

```powershell
dotnet build W:\Scripts\Chronicle\src\Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 5
```

Expected: 0 errors.

**Step 7: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Core/Models/ src/Chronicle.Data/ 
git commit -m "feat(data): add merge/dedup tables, MediaItemAlias/Merge/Candidate/Dismissal entities, normalized_name column"
```

---

## Task 2: `NormalizeName` helper + startup backfill + maintenance

**Files:**
- Create: `src/Chronicle.Services/MediaItemNormalizer.cs`
- Modify: `src/Chronicle.API/Program.cs`
- Create: `tests/Chronicle.Tests.Unit/Services/MediaItemNormalizerTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Chronicle.Tests.Unit/Services/MediaItemNormalizerTests.cs
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MediaItemNormalizerTests
{
    [Theory]
    [InlineData("James S. A. Corey",  "james sa corey")]
    [InlineData("James S.A. Corey",   "james sa corey")]
    [InlineData("James S.A.Corey",    "james sacorey")]   // no space → stays as-is after strip
    [InlineData("Brandon Sanderson",  "brandon sanderson")]
    [InlineData("The Way of Kings",   "the way of kings")]
    [InlineData("Abbey Road",         "abbey road")]
    [InlineData("",                   "")]
    [InlineData(null,                 "")]
    public void NormalizeName_VariousInputs_CorrectResult(string? input, string expected)
    {
        MediaItemNormalizer.NormalizeName(input).Should().Be(expected);
    }
}
```

**Step 2: Run — confirm FAIL**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MediaItemNormalizer" --verbosity normal 2>&1 | Select-Object -Last 10
```

**Step 3: Implement**

```csharp
// src/Chronicle.Services/MediaItemNormalizer.cs
using System.Text.RegularExpressions;

namespace Chronicle.Services;

public static class MediaItemNormalizer
{
    private static readonly Regex _strip =
        new(@"[.\-,':!?()]", RegexOptions.Compiled);
    private static readonly Regex _spaces =
        new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Produces a canonical lowercase string for duplicate detection.
    /// Strips common punctuation, collapses whitespace, trims.
    /// "James S. A. Corey" → "james s a corey"
    /// "James S.A. Corey"  → "james sa corey"
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var stripped = _strip.Replace(name, " ");
        var collapsed = _spaces.Replace(stripped, " ").Trim().ToLowerInvariant();
        return collapsed;
    }
}
```

**Step 4: Run — confirm PASS**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MediaItemNormalizer" --verbosity normal 2>&1 | Select-Object -Last 10
```

**Step 5: Add startup backfill to `Program.cs`**

Find the `BackfillFolderPathsAsync` startup call pattern (around line 313) and add after it:

```csharp
// Backfill normalized_name for all existing MediaItem rows that don't have it yet.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
    var items = await db.MediaItems
        .Where(m => m.NormalizedName == null)
        .ToListAsync();
    foreach (var item in items)
        item.NormalizedName = MediaItemNormalizer.NormalizeName(item.Name);
    if (items.Count > 0)
        await db.SaveChangesAsync();
}
```

**Step 6: Run full suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 10
```

**Step 7: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/MediaItemNormalizer.cs src/Chronicle.API/Program.cs tests/
git commit -m "feat(services): add MediaItemNormalizer and startup normalized_name backfill"
```

---

## Task 3: `IMergeService` + `MergeService` — merge logic

This is the core of the feature. Read `src/Chronicle.Services/DuplicateCleanupService.cs` first — particularly `MergeAndDeleteAsync` — to understand the existing consolidation pattern. `MergeService` reuses that logic and extends it.

**Files:**
- Create: `src/Chronicle.Services/IMergeService.cs`
- Create: `src/Chronicle.Services/MergeService.cs`
- Modify: `src/Chronicle.API/Program.cs` (register)
- Create: `tests/Chronicle.Tests.Unit/Services/MergeServiceTests.cs`

**Step 1: Write failing unit tests**

```csharp
// tests/Chronicle.Tests.Unit/Services/MergeServiceTests.cs
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MergeServiceTests
{
    [Theory]
    [InlineData("James S. A. Corey", "James S.A. Corey", true)]   // differ by punctuation
    [InlineData("Brandon Sanderson", "brandon sanderson", true)]   // differ by case
    [InlineData("Brandon Sanderson", "Patrick Rothfuss",  false)]  // genuinely different
    [InlineData("Abbey Road",        "Abbey Road",        false)]  // identical — no AKA needed
    public void NamesRequireAka_VariousInputs_CorrectResult(string winner, string loser, bool expected)
    {
        MergeService.NamesRequireAka(winner, loser).Should().Be(expected);
    }
}
```

**Step 2: Run — confirm FAIL**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MergeService" --verbosity normal 2>&1 | Select-Object -Last 10
```

**Step 3: Create interface**

```csharp
// src/Chronicle.Services/IMergeService.cs
namespace Chronicle.Services;

public interface IMergeService
{
    /// <summary>
    /// Merges <paramref name="loserId"/> into <paramref name="winnerId"/>.
    /// Both items must share the same MediaTypeId and HierarchyLevel.
    /// The loser is deleted; winner absorbs all its data.
    /// </summary>
    Task MergeAsync(int winnerId, int loserId, int? mergedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Reverses a previous merge. Recreates the loser as a stub, restores its
    /// external IDs and children, and queues re-enrichment.
    /// </summary>
    Task UnmergeAsync(int mergeId, CancellationToken ct = default);
}
```

**Step 4: Implement `MergeService`**

```csharp
// src/Chronicle.Services/MergeService.cs
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MergeService(
    ChronicleDbContext db,
    IMetadataResolutionService resolutionService,
    ILogger<MergeService> logger) : IMergeService
{
    public async Task MergeAsync(int winnerId, int loserId, int? mergedByUserId, CancellationToken ct = default)
    {
        // ── Guard checks ──────────────────────────────────────────────────────
        if (winnerId == loserId)
            throw new InvalidOperationException("Winner and loser must be different items.");

        var winner = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.Aliases)
            .FirstOrDefaultAsync(m => m.Id == winnerId, ct)
            ?? throw new InvalidOperationException($"Winner item {winnerId} not found.");

        var loser = await db.MediaItems
            .Include(m => m.MediaType)
            .FirstOrDefaultAsync(m => m.Id == loserId, ct)
            ?? throw new InvalidOperationException($"Loser item {loserId} not found.");

        if (winner.MediaTypeId != loser.MediaTypeId || winner.HierarchyLevel != loser.HierarchyLevel)
            throw new InvalidOperationException("Items must share the same media type and hierarchy level.");

        // Reject if either item is referenced as a loser_original_id (already deleted in a past merge)
        var alreadyMerged = await db.MediaItemMerges
            .AnyAsync(m => m.LoserOriginalId == winnerId || m.LoserOriginalId == loserId, ct);
        if (alreadyMerged)
            throw new InvalidOperationException("One of these items has already been merged and deleted.");

        // ── Snapshot for merge log ─────────────────────────────────────────────
        var loserExternalIds = await db.MediaExternalIds
            .Where(e => e.MediaItemId == loserId)
            .ToListAsync(ct);

        var loserChildren = await db.MediaItems
            .Where(m => m.ParentId == loserId)
            .ToListAsync(ct);

        var mergeLog = new MediaItemMerge
        {
            WinnerId           = winnerId,
            LoserOriginalId    = loserId,
            LoserName          = loser.Name,
            LoserMediaTypeId   = loser.MediaTypeId,
            LoserHierarchyLevel = loser.HierarchyLevel,
            LoserParentId      = loser.ParentId,
            LoserExternalIdsJson = JsonSerializer.Serialize(
                loserExternalIds.Select(e => new { e.Source, e.ExternalId })),
            LoserChildIdsJson  = JsonSerializer.Serialize(loserChildren.Select(c => c.Id)),
            MergedAt           = DateTime.UtcNow,
            MergedByUserId     = mergedByUserId,
        };
        db.MediaItemMerges.Add(mergeLog);

        // ── AKA ───────────────────────────────────────────────────────────────
        if (NamesRequireAka(winner.Name, loser.Name))
        {
            db.MediaItemAliases.Add(new MediaItemAlias
            {
                MediaItemId = winnerId,
                Alias       = loser.Name,
                Source      = "merge",
                CreatedAt   = DateTime.UtcNow,
            });
        }

        // ── Consolidate data onto winner ──────────────────────────────────────
        // External IDs
        foreach (var eid in loserExternalIds)
            eid.MediaItemId = winnerId;

        // Children
        foreach (var child in loserChildren)
        {
            child.ParentId = winnerId;
            child.NormalizedName = MediaItemNormalizer.NormalizeName(child.Name);
        }

        // UserLibrary
        var loserLibEntries = await db.UserLibraries.Where(l => l.MediaItemId == loserId).ToListAsync(ct);
        foreach (var lib in loserLibEntries)
        {
            var winnerLib = await db.UserLibraries
                .FirstOrDefaultAsync(l => l.MediaItemId == winnerId && l.UserId == lib.UserId, ct);
            if (winnerLib is not null)
            {
                if (StatusRank(lib.Status) > StatusRank(winnerLib.Status))
                {
                    winnerLib.Status      = lib.Status;
                    winnerLib.CompletedAt = lib.CompletedAt ?? winnerLib.CompletedAt;
                    winnerLib.UserRating  = lib.UserRating  ?? winnerLib.UserRating;
                    winnerLib.UpdatedAt   = DateTime.UtcNow;
                }
                db.UserLibraries.Remove(lib);
            }
            else lib.MediaItemId = winnerId;
        }

        // InteractionEvents
        await db.InteractionEvents.Where(e => e.MediaItemId == loserId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.MediaItemId, winnerId), ct);

        // MediaListItems
        await db.MediaListItems.Where(li => li.MediaItemId == loserId)
            .ExecuteUpdateAsync(s => s.SetProperty(li => li.MediaItemId, winnerId), ct);

        // MediaCredits — re-point; deduplicate by (person_name, role)
        var loserCredits = await db.MediaCredits.Where(c => c.MediaItemId == loserId).ToListAsync(ct);
        var winnerCreditKeys = (await db.MediaCredits
            .Where(c => c.MediaItemId == winnerId)
            .Select(c => new { c.PersonName, c.Role })
            .ToListAsync(ct))
            .ToHashSet(EqualityComparer<dynamic>.Default);
        foreach (var credit in loserCredits)
        {
            if (winnerCreditKeys.Any(k => k.PersonName == credit.PersonName && k.Role == credit.Role))
                db.MediaCredits.Remove(credit);
            else
                credit.MediaItemId = winnerId;
        }

        // metadata_json — merge blobs (winner blobs take precedence)
        if (!string.IsNullOrEmpty(loser.MetadataJson))
        {
            var winnerBlobs = string.IsNullOrEmpty(winner.MetadataJson)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(winner.MetadataJson) ?? [];
            var loserBlobs  = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(loser.MetadataJson) ?? [];
            foreach (var (key, val) in loserBlobs)
                if (!winnerBlobs.ContainsKey(key) && key != "_resolved")
                    winnerBlobs[key] = val;
            winner.MetadataJson = JsonSerializer.Serialize(winnerBlobs);
        }

        // Recompute _resolved
        await resolutionService.ResolveAsync(winner, db, ct);

        // Reset enrichment rows for plugins newly introduced by loser IDs
        var newSources = loserExternalIds.Select(e => e.Source).Distinct().ToList();
        var enrichmentRows = await db.MediaEnrichments
            .Where(e => e.MediaItemId == winnerId)
            .ToListAsync(ct);
        foreach (var row in enrichmentRows)
        {
            // Reset if this plugin maps to one of the loser's new sources
            var pluginShortId = row.PluginId.Contains('.') ? row.PluginId.Split('.').Last() : row.PluginId;
            if (newSources.Contains(pluginShortId) &&
                row.Status is EnrichmentStatus.Completed or EnrichmentStatus.NotFound or EnrichmentStatus.Exhausted)
            {
                row.Status = EnrichmentStatus.Pending;
                row.RetryCount = 0;
                row.ErrorMessage = null;
            }
        }

        // NormalizedName on winner
        winner.NormalizedName = MediaItemNormalizer.NormalizeName(winner.Name);
        winner.UpdatedAt = DateTime.UtcNow;

        // Remove from duplicate candidates
        var candidates = await db.MediaItemDuplicateCandidates
            .Where(c => (c.ItemAId == winnerId || c.ItemAId == loserId) &&
                        (c.ItemBId == winnerId || c.ItemBId == loserId))
            .ToListAsync(ct);
        db.MediaItemDuplicateCandidates.RemoveRange(candidates);

        // Delete loser
        db.MediaItems.Remove(loser);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Merged item {LoserId} ({LoserName}) into {WinnerId} ({WinnerName})",
            loserId, loser.Name, winnerId, winner.Name);
    }

    public async Task UnmergeAsync(int mergeId, CancellationToken ct = default)
    {
        var log = await db.MediaItemMerges
            .Include(m => m.Winner)
            .FirstOrDefaultAsync(m => m.Id == mergeId, ct)
            ?? throw new InvalidOperationException($"Merge record {mergeId} not found.");

        var winner = log.Winner!;

        // ── Create stub for the loser ─────────────────────────────────────────
        var stub = new MediaItem
        {
            MediaTypeId    = log.LoserMediaTypeId,
            Name           = log.LoserName,
            HierarchyLevel = log.LoserHierarchyLevel,
            ParentId       = log.LoserParentId,
            NormalizedName = MediaItemNormalizer.NormalizeName(log.LoserName),
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        db.MediaItems.Add(stub);
        await db.SaveChangesAsync(ct); // need stub.Id

        // ── Split external IDs back ───────────────────────────────────────────
        var loserIds = JsonSerializer.Deserialize<List<LoserExternalId>>(log.LoserExternalIdsJson) ?? [];
        foreach (var lid in loserIds)
        {
            var eid = await db.MediaExternalIds
                .FirstOrDefaultAsync(e => e.MediaItemId == winner.Id &&
                                          e.Source == lid.Source &&
                                          e.ExternalId == lid.ExternalId, ct);
            if (eid is not null)
                eid.MediaItemId = stub.Id;
        }

        // ── Re-parent children ────────────────────────────────────────────────
        var childIds = JsonSerializer.Deserialize<List<int>>(log.LoserChildIdsJson) ?? [];
        foreach (var childId in childIds)
        {
            var child = await db.MediaItems.FindAsync([childId], ct);
            if (child is not null)
                child.ParentId = stub.Id;
        }

        // ── Clean winner metadata_json of loser plugin blobs ─────────────────
        if (!string.IsNullOrEmpty(winner.MetadataJson))
        {
            var blobs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(winner.MetadataJson) ?? [];
            var loserSources = loserIds.Select(l => l.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var src in loserSources)
            {
                // Remove plugin blobs whose short ID matches the loser source
                var keysToRemove = blobs.Keys
                    .Where(k => k != "_resolved" &&
                                (k.EndsWith("." + src, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(k, src, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                foreach (var k in keysToRemove) blobs.Remove(k);
            }
            winner.MetadataJson = JsonSerializer.Serialize(blobs);
        }
        await resolutionService.ResolveAsync(winner, db, ct);

        // ── Remove AKA created by this merge ─────────────────────────────────
        var aka = await db.MediaItemAliases
            .FirstOrDefaultAsync(a => a.MediaItemId == winner.Id &&
                                       a.Alias == log.LoserName &&
                                       a.Source == "merge", ct);
        if (aka is not null) db.MediaItemAliases.Remove(aka);

        // ── Update cascading merge logs ───────────────────────────────────────
        // Any merge where winner_id pointed to the loser's original ID
        // (impossible since loser was deleted, but defensive for chain updates)
        // More importantly: if winner was itself merged from something (winner was
        // a loser in an earlier merge that pointed to it), those records already
        // used winner.Id which is still valid. No update needed there.
        // The only case to handle: old merge records where loser_original_id
        // matches one of the re-parented child IDs — but child IDs are unchanged,
        // they just have a different ParentId now. Merge logs for those children
        // reference their own Id which is unchanged.
        // → No cascading update needed for this simplified case.

        // ── Seed enrichment on stub ───────────────────────────────────────────
        var mediaTypeName = (await db.MediaTypes.FindAsync([stub.MediaTypeId], ct))?.Name ?? string.Empty;
        // (Use the pattern from SyncOrchestrationService for seeding enrichment rows)
        // Get all metadata providers that support this media type from the registry.
        // Since MergeService doesn't have direct access to IPluginRegistry here,
        // we create Pending enrichment rows for any plugin that already has a row
        // on the winner for this media type — a reasonable approximation.
        var winnerEnrichments = await db.MediaEnrichments
            .Where(e => e.MediaItemId == winner.Id)
            .ToListAsync(ct);
        foreach (var row in winnerEnrichments)
        {
            db.MediaEnrichments.Add(new MediaItemEnrichment
            {
                MediaItemId  = stub.Id,
                PluginId     = row.PluginId,
                Status       = EnrichmentStatus.Pending,
                MaxRetries   = 3,
            });
        }

        // ── Delete merge log ──────────────────────────────────────────────────
        db.MediaItemMerges.Remove(log);

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Unmerged merge #{MergeId}: recreated stub {StubId} ({Name}) from winner {WinnerId}",
            mergeId, stub.Id, stub.Name, winner.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the two names are different enough to warrant an AKA entry.
    /// Identical normalized names → no AKA needed.
    /// </summary>
    internal static bool NamesRequireAka(string winnerName, string loserName)
    {
        var wn = MediaItemNormalizer.NormalizeName(winnerName);
        var ln = MediaItemNormalizer.NormalizeName(loserName);
        return !string.Equals(wn, ln, StringComparison.Ordinal);
    }

    private static int StatusRank(string status) => status switch
    {
        "Completed"   => 4,
        "Watching"    => 3,
        "Dropped"     => 2,
        "Unwatched"   => 1,
        _             => 0,
    };

    private record LoserExternalId(string Source, string ExternalId);
}
```

**Step 5: Register in `Program.cs`**

```csharp
builder.Services.AddScoped<IMergeService, MergeService>();
```

**Step 6: Run failing tests — confirm they now PASS**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MergeService" --verbosity normal 2>&1 | Select-Object -Last 10
```

**Step 7: Run full suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 10
```

**Step 8: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/ src/Chronicle.API/Program.cs tests/
git commit -m "feat(services): add IMergeService and MergeService with full merge/unmerge logic"
```

---

## Task 4: `DuplicateCandidateScanService` background task

**Files:**
- Create: `src/Chronicle.Services/DuplicateCandidateScanService.cs`
- Modify: `src/Chronicle.API/Program.cs` (register as scheduled task)

**Step 1: Implement**

```csharp
// src/Chronicle.Services/DuplicateCandidateScanService.cs
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Nightly background task that scans for probable duplicate MediaItem pairs
/// (same media type + hierarchy level + parent + normalized name)
/// and caches them in media_item_duplicate_candidates for the Duplicates UI page.
/// </summary>
public sealed class DuplicateCandidateScanService(
    IServiceScopeFactory scopeFactory,
    ILogger<DuplicateCandidateScanService> logger) : IScheduledTask
{
    public string TaskId      => "duplicate_candidate_scan";
    public string DisplayName => "Duplicate Candidate Scan";
    public string Description => "Scans for probable duplicate media items by normalised name and caches the results for the Duplicates page.";
    public string DefaultCron => "0 2 * * *"; // 2 AM nightly
    public bool   DefaultEnabled => true;
    public string? RunConfirmationTitle   => null;
    public string? RunConfirmationMessage => null;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting duplicate candidate scan");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Load all items with their grouping key
        var items = await db.MediaItems
            .Where(m => m.NormalizedName != null && m.NormalizedName != string.Empty)
            .Select(m => new { m.Id, m.NormalizedName, m.MediaTypeId, m.HierarchyLevel, m.ParentId })
            .ToListAsync(ct);

        // Load existing dismissals to exclude
        var dismissedPairs = await db.MediaItemDuplicateDismissals
            .Select(d => new { d.ItemAId, d.ItemBId })
            .ToListAsync(ct);
        var dismissedSet = new HashSet<(int, int)>(
            dismissedPairs.Select(d => (Math.Min(d.ItemAId, d.ItemBId), Math.Max(d.ItemAId, d.ItemBId))));

        var newCandidates = new List<(int, int)>();

        // Group by (MediaTypeId, HierarchyLevel, ParentId) then find pairs with same normalized name
        var groups = items.GroupBy(m => (m.MediaTypeId, m.HierarchyLevel, m.ParentId));
        foreach (var group in groups)
        {
            var byName = group.GroupBy(m => m.NormalizedName);
            foreach (var nameGroup in byName)
            {
                var list = nameGroup.ToList();
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count - 1; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = Math.Min(list[i].Id, list[j].Id);
                    var b = Math.Max(list[i].Id, list[j].Id);
                    if (!dismissedSet.Contains((a, b)))
                        newCandidates.Add((a, b));
                }
            }
        }

        // Replace candidates table
        var existing = await db.MediaItemDuplicateCandidates.ToListAsync(ct);
        db.MediaItemDuplicateCandidates.RemoveRange(existing);
        foreach (var (a, b) in newCandidates.Distinct())
            db.MediaItemDuplicateCandidates.Add(new Core.Models.MediaItemDuplicateCandidate
            {
                ItemAId     = a,
                ItemBId     = b,
                DetectedAt  = DateTime.UtcNow,
            });

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Duplicate candidate scan complete: {Count} candidates stored", newCandidates.Count);
    }
}
```

**Step 2: Register in `Program.cs`**

Find the existing `AddSingleton<DuplicateCleanupService>()` pattern and add alongside it:

```csharp
builder.Services.AddSingleton<DuplicateCandidateScanService>();
builder.Services.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<DuplicateCandidateScanService>());
```

**Step 3: Build and run tests**

```powershell
dotnet build W:\Scripts\Chronicle\src\Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 5
dotnet test W:\Scripts\Chronicle\src\Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 10
```

**Step 4: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/DuplicateCandidateScanService.cs src/Chronicle.API/Program.cs
git commit -m "feat(services): add DuplicateCandidateScanService nightly background task"
```

---

## Task 5: Update `DuplicateCleanupService.MergeAndDeleteAsync` for credits + merge log

The existing automatic background merge needs to record a merge log entry and handle `media_credits` — it currently doesn't do either.

**Files:**
- Modify: `src/Chronicle.Services/DuplicateCleanupService.cs`

**Step 1: Add `media_credits` consolidation to `MergeAndDeleteAsync`**

After the existing `MediaListItems` block (around line 260), add:

```csharp
// ── MediaCredits ──────────────────────────────────────────────────────────
var loserCredits = await context.MediaCredits
    .Where(c => c.MediaItemId == loser.Id)
    .ToListAsync(ct);
var winnerCreditKeys = await context.MediaCredits
    .Where(c => c.MediaItemId == winner.Id)
    .Select(c => new { c.PersonName, c.Role })
    .ToListAsync(ct);
foreach (var credit in loserCredits)
{
    if (winnerCreditKeys.Any(k => k.PersonName == credit.PersonName && k.Role == credit.Role))
        context.MediaCredits.Remove(credit);
    else
        credit.MediaItemId = winner.Id;
}
```

**Step 2: Add merge log recording to `MergeAndDeleteAsync`**

Before `context.MediaItems.Remove(loser)`, add:

```csharp
// ── Record merge log (enables unmerge) ───────────────────────────────────
var loserExtIds = await context.MediaExternalIds
    .Where(e => e.MediaItemId == loser.Id)
    .ToListAsync(ct);
var loserChildIds = await context.MediaItems
    .Where(m => m.ParentId == loser.Id)
    .Select(m => m.Id)
    .ToListAsync(ct);
context.MediaItemMerges.Add(new Chronicle.Core.Models.MediaItemMerge
{
    WinnerId           = winner.Id,
    LoserOriginalId    = loser.Id,
    LoserName          = loser.Name,
    LoserMediaTypeId   = loser.MediaTypeId,
    LoserHierarchyLevel = loser.HierarchyLevel,
    LoserParentId      = loser.ParentId,
    LoserExternalIdsJson = System.Text.Json.JsonSerializer.Serialize(
        loserExtIds.Select(e => new { e.Source, e.ExternalId })),
    LoserChildIdsJson  = System.Text.Json.JsonSerializer.Serialize(loserChildIds),
    MergedAt           = DateTime.UtcNow,
    MergedByUserId     = null, // automatic
});
```

**Step 3: Add AKA recording**

Before the merge log block, add:

```csharp
// ── AKA ───────────────────────────────────────────────────────────────────
if (MergeService.NamesRequireAka(winner.Name, loser.Name))
{
    context.MediaItemAliases.Add(new Chronicle.Core.Models.MediaItemAlias
    {
        MediaItemId = winner.Id,
        Alias       = loser.Name,
        Source      = "merge",
        CreatedAt   = DateTime.UtcNow,
    });
}
```

**Step 4: Run full suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 10
```

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/DuplicateCleanupService.cs
git commit -m "feat(services): add media_credits consolidation and merge log to DuplicateCleanupService"
```

---

## Task 6: `DuplicatesController` + merge/unmerge endpoints on `MediaController`

**Files:**
- Create: `src/Chronicle.API/Controllers/DuplicatesController.cs`
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`
- Modify: `src/Chronicle.API/DTOs/MediaDTOs.cs`

**Step 1: Add DTOs to `MediaDTOs.cs`**

```csharp
public record MergeRequestDto(int TargetId, int WinnerId);

public record DismissDuplicateDto(int ItemAId, int ItemBId);

public record MergeHistoryDto(
    int    MergeId,
    int    LoserOriginalId,
    string LoserName,
    DateTime MergedAt,
    int?   MergedByUserId
);

public record DuplicateCandidateDto(
    MediaItemDto ItemA,
    MediaItemDto ItemB
);
```

Add to `MediaItemDto` (after `ResolvedMetadata`):
```csharp
List<string>?        Aliases      = null,
List<MergeHistoryDto>? MergeHistory = null
```

**Step 2: Create `DuplicatesController`**

```csharp
// src/Chronicle.API/Controllers/DuplicatesController.cs
using Chronicle.API.DTOs;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/duplicates")]
[Authorize(Roles = "Admin")]
public class DuplicatesController(
    ChronicleDbContext db,
    DuplicateCandidateScanService scanner) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCandidates(
        [FromQuery] string? mediaType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var q = db.MediaItemDuplicateCandidates
            .Include(c => c.ItemA).ThenInclude(m => m!.MediaType)
            .Include(c => c.ItemA).ThenInclude(m => m!.ExternalIds)
            .Include(c => c.ItemB).ThenInclude(m => m!.MediaType)
            .Include(c => c.ItemB).ThenInclude(m => m!.ExternalIds)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(mediaType))
            q = q.Where(c => c.ItemA!.MediaType!.Name == mediaType);

        var total = await q.CountAsync(ct);
        var candidates = await q
            .OrderBy(c => c.DetectedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // Map to DTOs using the same MapToMediaItemDto helper from MediaController
        // For simplicity, return lightweight data here
        var data = candidates.Select(c => new
        {
            candidateId = c.Id,
            itemA = new { c.ItemA!.Id, c.ItemA.Name, c.ItemA.PosterUrl, c.ItemA.HierarchyLevel,
                          mediaType = c.ItemA.MediaType?.Name },
            itemB = new { c.ItemB!.Id, c.ItemB.Name, c.ItemB.PosterUrl, c.ItemB.HierarchyLevel,
                          mediaType = c.ItemB.MediaType?.Name },
        }).ToList();

        return Ok(ApiResponse<object>.Ok(data,
            new PaginationInfo(total, page, pageSize)));
    }

    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss(
        [FromBody] DismissDuplicateDto dto,
        CancellationToken ct)
    {
        var a = Math.Min(dto.ItemAId, dto.ItemBId);
        var b = Math.Max(dto.ItemAId, dto.ItemBId);

        var exists = await db.MediaItemDuplicateDismissals
            .AnyAsync(d => d.ItemAId == a && d.ItemBId == b, ct);
        if (!exists)
        {
            db.MediaItemDuplicateDismissals.Add(new Core.Models.MediaItemDuplicateDismissal
            {
                ItemAId     = a,
                ItemBId     = b,
                DismissedAt = DateTime.UtcNow,
            });
        }

        // Remove from candidates
        var candidate = await db.MediaItemDuplicateCandidates
            .FirstOrDefaultAsync(c => (c.ItemAId == a && c.ItemBId == b) ||
                                       (c.ItemAId == b && c.ItemBId == a), ct);
        if (candidate is not null) db.MediaItemDuplicateCandidates.Remove(candidate);

        await db.SaveChangesAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { dismissed = true }));
    }

    [HttpPost("scan")]
    public IActionResult TriggerScan()
    {
        _ = Task.Run(() => scanner.ExecuteAsync(CancellationToken.None));
        return Accepted(ApiResponse<object>.Ok(new { message = "Duplicate candidate scan started." }));
    }
}
```

**Step 3: Add merge/unmerge endpoints to `MediaController`**

In `MediaController.cs`, after the last existing endpoint, add:

```csharp
/// <summary>Merges two items. winnerId must equal id or targetId.</summary>
[HttpPost("{id:int}/merge")]
[Authorize(Roles = "Admin")]
public async Task<IActionResult> Merge(
    int id,
    [FromBody] MergeRequestDto dto,
    CancellationToken ct)
{
    if (dto.WinnerId != id && dto.WinnerId != dto.TargetId)
        return BadRequest(ApiResponse<object>.Fail("INVALID_WINNER",
            "winnerId must be either the source item id or targetId."));

    var userId = GetCurrentUserId();
    try
    {
        await _mergeService.MergeAsync(dto.WinnerId,
            dto.WinnerId == id ? dto.TargetId : id,
            userId, ct);
        return Ok(ApiResponse<object>.Ok(new { merged = true }));
    }
    catch (InvalidOperationException ex)
    {
        return BadRequest(ApiResponse<object>.Fail("MERGE_ERROR", ex.Message));
    }
}

/// <summary>Returns merge history for this item (for unmerge UI).</summary>
[HttpGet("{id:int}/merges")]
public async Task<IActionResult> GetMerges(int id, CancellationToken ct)
{
    var merges = await _db.MediaItemMerges
        .Where(m => m.WinnerId == id)
        .OrderByDescending(m => m.MergedAt)
        .ToListAsync(ct);

    var dtos = merges.Select(m => new MergeHistoryDto(
        m.Id, m.LoserOriginalId, m.LoserName, m.MergedAt, m.MergedByUserId)).ToList();
    return Ok(ApiResponse<List<MergeHistoryDto>>.Ok(dtos));
}

/// <summary>Unmerges a specific merge, recreating the loser as a stub.</summary>
[HttpDelete("{id:int}/merges/{mergeId:int}")]
[Authorize(Roles = "Admin")]
public async Task<IActionResult> Unmerge(int id, int mergeId, CancellationToken ct)
{
    // Verify the merge belongs to this item
    var merge = await _db.MediaItemMerges.FindAsync([mergeId], ct);
    if (merge is null || merge.WinnerId != id)
        return NotFound(ApiResponse<object>.Fail("MERGE_NOT_FOUND",
            $"Merge record {mergeId} not found for item {id}."));

    try
    {
        await _mergeService.UnmergeAsync(mergeId, ct);
        return Ok(ApiResponse<object>.Ok(new { unmerged = true }));
    }
    catch (InvalidOperationException ex)
    {
        return BadRequest(ApiResponse<object>.Fail("UNMERGE_ERROR", ex.Message));
    }
}
```

Inject `IMergeService _mergeService` and `ChronicleDbContext _db` into `MediaController`'s constructor (add to existing constructor parameters). Add a helper to get current user ID:

```csharp
private int? GetCurrentUserId()
{
    var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    return int.TryParse(claim, out var id) ? id : null;
}
```

**Step 4: Populate `aliases` and `mergeHistory` in the media item mapper**

In `MediaController.cs`, in the `MapToMediaItemDto` static helper, add before the `return`:

```csharp
// Aliases
var aliases = m.Aliases.Select(a => a.Alias).ToList();

// MergeHistory
var mergeHistory = m.MergesAsWinner.Select(mr => new MergeHistoryDto(
    mr.Id, mr.LoserOriginalId, mr.LoserName, mr.MergedAt, mr.MergedByUserId)).ToList();
```

Update the query that loads items for `MapToMediaItemDto` to include `.Include(m => m.Aliases).Include(m => m.MergesAsWinner)`.

Add `Aliases: aliases.Count > 0 ? aliases : null` and `MergeHistory: mergeHistory.Count > 0 ? mergeHistory : null` to the `new MediaItemDto(...)` call.

**Step 5: Update global search in `MediaService.cs`**

Change the search query to also match on aliases:

```csharp
if (!string.IsNullOrWhiteSpace(query))
{
    q = q.Where(m =>
        EF.Functions.Like(m.Name, $"%{query}%") ||
        m.Aliases.Any(a => EF.Functions.Like(a.Alias, $"%{query}%")));
}
```

**Step 6: Build**

```powershell
dotnet build W:\Scripts\Chronicle\src\Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 5
```

**Step 7: Run full suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 10
```

**Step 8: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.API/ 
git commit -m "feat(api): add DuplicatesController, merge/unmerge endpoints, aliases in search and MediaItemDto"
```

---

## Task 7: Frontend — Settings → Duplicates page

**Files:**
- Create: `src/Chronicle.Web/src/pages/settings/DuplicatesPage.tsx`
- Create: `src/Chronicle.Web/src/pages/settings/DuplicatesPage.module.css`
- Modify: `src/Chronicle.Web/src/App.tsx` (add route)
- Modify: `src/Chronicle.Web/src/components/layout/Layout.tsx` (add nav link)
- Modify: `src/Chronicle.Web/src/api/media.ts` (add API calls)

**Step 1: Add API functions to `src/Chronicle.Web/src/api/media.ts`** (or create `src/Chronicle.Web/src/api/duplicates.ts`):

```typescript
// src/Chronicle.Web/src/api/duplicates.ts
import client from './client'

export interface DuplicateCandidate {
  candidateId: number
  itemA: { id: number; name: string; posterUrl: string | null; hierarchyLevel: number; mediaType: string }
  itemB: { id: number; name: string; posterUrl: string | null; hierarchyLevel: number; mediaType: string }
}

export async function getDuplicateCandidates(page = 1, mediaType?: string): Promise<{ data: DuplicateCandidate[]; pagination: unknown }> {
  const params = new URLSearchParams({ page: String(page) })
  if (mediaType) params.set('mediaType', mediaType)
  const res = await client.get<{ success: true; data: DuplicateCandidate[]; pagination: unknown }>(`/duplicates?${params}`)
  return res.data
}

export async function dismissDuplicate(itemAId: number, itemBId: number): Promise<void> {
  await client.post('/duplicates/dismiss', { itemAId, itemBId })
}

export async function triggerDuplicateScan(): Promise<void> {
  await client.post('/duplicates/scan')
}

export async function mergeItems(id: number, targetId: number, winnerId: number): Promise<void> {
  await client.post(`/media/${id}/merge`, { targetId, winnerId })
}

export async function getMergeHistory(id: number): Promise<{ id: number; loserName: string; mergedAt: string }[]> {
  const res = await client.get<{ success: true; data: { id: number; loserName: string; mergedAt: string }[] }>(`/media/${id}/merges`)
  return res.data.data
}

export async function unmergeItem(id: number, mergeId: number): Promise<void> {
  await client.delete(`/media/${id}/merges/${mergeId}`)
}
```

**Step 2: Create `DuplicatesPage.tsx`**

```tsx
// src/Chronicle.Web/src/pages/settings/DuplicatesPage.tsx
import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getDuplicateCandidates, dismissDuplicate, triggerDuplicateScan,
  type DuplicateCandidate
} from '@/api/duplicates'
import MergModal from '@/components/MergeModal'
import styles from './DuplicatesPage.module.css'

export default function DuplicatesPage() {
  const qc = useQueryClient()
  const [page, setPage] = useState(1)
  const [mergeTarget, setMergeTarget] = useState<DuplicateCandidate | null>(null)

  const { data, isLoading } = useQuery({
    queryKey: ['duplicates', page],
    queryFn: () => getDuplicateCandidates(page),
  })

  const dismiss = useMutation({
    mutationFn: ({ a, b }: { a: number; b: number }) => dismissDuplicate(a, b),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['duplicates'] }),
  })

  const scan = useMutation({
    mutationFn: triggerDuplicateScan,
    onSuccess: () => setTimeout(() => qc.invalidateQueries({ queryKey: ['duplicates'] }), 2000),
  })

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1>Duplicate Candidates</h1>
        <button onClick={() => scan.mutate()} disabled={scan.isPending} className={styles.scanBtn}>
          {scan.isPending ? 'Scanning…' : 'Rescan'}
        </button>
      </div>

      {isLoading && <p>Loading…</p>}
      {!isLoading && (!data?.data || data.data.length === 0) && (
        <p className={styles.empty}>No duplicate candidates found.</p>
      )}

      <div className={styles.list}>
        {data?.data?.map(candidate => (
          <div key={candidate.candidateId} className={styles.row}>
            <ItemCard item={candidate.itemA} />
            <span className={styles.vs}>vs</span>
            <ItemCard item={candidate.itemB} />
            <div className={styles.actions}>
              <button className={styles.mergeBtn} onClick={() => setMergeTarget(candidate)}>
                Merge
              </button>
              <button
                className={styles.dismissBtn}
                onClick={() => dismiss.mutate({ a: candidate.itemA.id, b: candidate.itemB.id })}
              >
                Dismiss
              </button>
            </div>
          </div>
        ))}
      </div>

      {mergeTarget && (
        <MergeModal
          itemA={mergeTarget.itemA}
          itemB={mergeTarget.itemB}
          onClose={() => setMergeTarget(null)}
          onMerged={() => {
            setMergeTarget(null)
            qc.invalidateQueries({ queryKey: ['duplicates'] })
          }}
        />
      )}
    </div>
  )
}

function ItemCard({ item }: { item: DuplicateCandidate['itemA'] }) {
  return (
    <div className={styles.card}>
      {item.posterUrl
        ? <img src={item.posterUrl} alt={item.name} className={styles.poster} />
        : <div className={styles.posterPlaceholder} />}
      <p className={styles.name}>{item.name}</p>
      <p className={styles.meta}>{item.mediaType} · Level {item.hierarchyLevel}</p>
    </div>
  )
}
```

**Step 3: Create `DuplicatesPage.module.css`** with styles matching the existing Sonarr/Radarr aesthetic. Use the same design tokens (`var(--bg-secondary)`, `var(--text-primary)`, etc.) used elsewhere in the app.

**Step 4: Add route in `App.tsx`**

Find the settings routes section and add:
```tsx
<Route path="settings/duplicates" element={<DuplicatesPage />} />
```

**Step 5: Add nav link in `Layout.tsx`**

Find the settings nav section and add after the "Metadata Assignment" link:
```tsx
<NavLink to="/settings/duplicates" className={...}>Duplicates</NavLink>
```

**Step 6: Type-check**

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web
npm run type-check 2>&1 | Select-Object -Last 15
```

**Step 7: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Web/src/
git commit -m "feat(ui): add Settings → Duplicates page with candidate list and dismiss"
```

---

## Task 8: Frontend — `MergeModal` reusable component

**Files:**
- Create: `src/Chronicle.Web/src/components/MergeModal.tsx`
- Create: `src/Chronicle.Web/src/components/MergeModal.module.css`

**Step 1: Create `MergeModal.tsx`**

```tsx
// src/Chronicle.Web/src/components/MergeModal.tsx
import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { mergeItems } from '@/api/duplicates'
import styles from './MergeModal.module.css'

interface Item {
  id: number
  name: string
  posterUrl: string | null
}

interface Props {
  itemA: Item
  itemB: Item
  onClose: () => void
  onMerged: () => void
}

export default function MergeModal({ itemA, itemB, onClose, onMerged }: Props) {
  const [winnerId, setWinnerId] = useState<number | null>(null)

  const merge = useMutation({
    mutationFn: () => mergeItems(itemA.id, itemB.id, winnerId!),
    onSuccess: onMerged,
  })

  const loser = winnerId === itemA.id ? itemB : itemA

  return (
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.modal} onClick={e => e.stopPropagation()}>
        <h2 className={styles.title}>Select the Canonical Record</h2>
        <p className={styles.subtitle}>
          The winner becomes the canonical entry. The other item's name will be saved as an AKA.
        </p>

        <div className={styles.items}>
          {[itemA, itemB].map(item => (
            <button
              key={item.id}
              className={`${styles.itemCard} ${winnerId === item.id ? styles.selected : ''}`}
              onClick={() => setWinnerId(item.id)}
            >
              {item.posterUrl
                ? <img src={item.posterUrl} alt={item.name} className={styles.poster} />
                : <div className={styles.posterPlaceholder} />}
              <p className={styles.name}>{item.name}</p>
              {winnerId === item.id && <span className={styles.winnerBadge}>Winner</span>}
            </button>
          ))}
        </div>

        {winnerId && (
          <p className={styles.preview}>
            <strong>"{loser.name}"</strong> will be saved as an AKA on the winner.
          </p>
        )}

        {merge.isError && (
          <p className={styles.error}>Merge failed. Please try again.</p>
        )}

        <div className={styles.footer}>
          <button className={styles.cancelBtn} onClick={onClose}>Cancel</button>
          <button
            className={styles.confirmBtn}
            disabled={!winnerId || merge.isPending}
            onClick={() => merge.mutate()}
          >
            {merge.isPending ? 'Merging…' : 'Confirm Merge'}
          </button>
        </div>
      </div>
    </div>
  )
}
```

**Step 2: Create `MergeModal.module.css`** with overlay/modal styles matching the existing modal pattern in the app (check existing modals for class names and `var(--*)` tokens to use).

**Step 3: Type-check**

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web
npm run type-check 2>&1 | Select-Object -Last 10
```

**Step 4: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Web/src/components/MergeModal.tsx src/Chronicle.Web/src/components/MergeModal.module.css
git commit -m "feat(ui): add reusable MergeModal component with side-by-side winner selection"
```

---

## Task 9: Frontend — Media detail page additions

**Files:**
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`

**Step 1: Add "Also known as" display**

In `MediaDetailPage.tsx`, after the item title/year block, add:

```tsx
{item.aliases && item.aliases.length > 0 && (
  <p className={styles.aliases}>Also known as: {item.aliases.join(', ')}</p>
)}
```

Add `.aliases` to `MediaDetailPage.module.css`:
```css
.aliases {
  margin: 2px 0 10px;
  font-size: 0.88rem;
  color: var(--text-secondary);
  font-style: italic;
}
```

**Step 2: Add "Merge with…" in the action menu**

Find the action menu / kebab menu on the media detail page. Add:

```tsx
<button onClick={() => setShowMergeSearch(true)}>Merge with…</button>
```

Add state: `const [showMergeSearch, setShowMergeSearch] = useState(false)`

When `showMergeSearch` is true, render a search-then-compare flow:
- A small search input that calls `GET /api/v1/media/search?q={query}` 
- Results shown as a list of item cards (name + poster)
- Clicking a result opens `MergeModal` with the current item and the selected item

```tsx
{showMergeSearch && (
  <MergeSearchFlow
    sourceItem={{ id: item.id, name: item.name, posterUrl: item.posterUrl }}
    onClose={() => setShowMergeSearch(false)}
    onMerged={() => {
      setShowMergeSearch(false)
      queryClient.invalidateQueries({ queryKey: ['media', item.id] })
    }}
  />
)}
```

Create `MergeSearchFlow` as a small inline component or in its own file — it's a search input + results list + hands off to `MergeModal`.

**Step 3: Add "Merge History" section**

At the bottom of `MediaDetailPage.tsx`, add a collapsible section:

```tsx
{item.mergeHistory && item.mergeHistory.length > 0 && (
  <details className={styles.mergeHistory}>
    <summary>Merge History ({item.mergeHistory.length})</summary>
    {item.mergeHistory.map(merge => (
      <div key={merge.mergeId} className={styles.mergeRow}>
        <span>Absorbed <strong>{merge.loserName}</strong> on {new Date(merge.mergedAt).toLocaleDateString()}</span>
        <button
          className={styles.unmergeBtn}
          onClick={() => handleUnmerge(item.id, merge.mergeId)}
        >
          Unmerge
        </button>
      </div>
    ))}
  </details>
)}
```

Add `handleUnmerge`:
```tsx
const unmerge = useMutation({
  mutationFn: ({ itemId, mergeId }: { itemId: number; mergeId: number }) =>
    unmergeItem(itemId, mergeId),
  onSuccess: () => queryClient.invalidateQueries({ queryKey: ['media', item.id] }),
})

const handleUnmerge = (itemId: number, mergeId: number) => {
  if (confirm('Unmerge this item? The absorbed record will be recreated as a stub and queued for re-enrichment.'))
    unmerge.mutate({ itemId, mergeId })
}
```

Add appropriate CSS for the merge history section.

**Step 4: Type-check and lint**

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web
npm run type-check && npm run lint 2>&1 | Select-Object -Last 15
```

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Web/src/pages/media/
git commit -m "feat(ui): add aliases display, Merge With, and Merge History to MediaDetailPage"
```

---

## Task 10: Integration tests + push

**Files:**
- Create: `tests/Chronicle.Tests.Integration/MergeServiceTests.cs`

**Step 1: Write integration tests**

```csharp
// tests/Chronicle.Tests.Integration/MergeServiceTests.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Data;
using Chronicle.Core.Models;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

public class MergeServiceIntegrationTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public MergeServiceIntegrationTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    [Fact]
    public async Task MergeAsync_HighPriorityWins_LoserNameBecomesAlias()
    {
        // Seed two author items with slightly different names
        int winnerId, loserId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var mt = db.MediaTypes.First(); // any media type
            var winner = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId = mt.Id, Name = "James S. A. Corey",
                NormalizedName = MediaItemNormalizer.NormalizeName("James S. A. Corey"),
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            }).Entity;
            var loser = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId = mt.Id, Name = "James S.A. Corey",
                NormalizedName = MediaItemNormalizer.NormalizeName("James S.A. Corey"),
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            }).Entity;
            db.SaveChanges();
            winnerId = winner.Id;
            loserId  = loser.Id;

            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            await svc.MergeAsync(winnerId, loserId, null);
        }

        // Verify loser is deleted, winner has alias
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaItems.Find(loserId).Should().BeNull();
            var aliases = db.MediaItemAliases.Where(a => a.MediaItemId == winnerId).ToList();
            aliases.Should().ContainSingle(a => a.Alias == "James S.A. Corey");
            var mergeLog = db.MediaItemMerges.FirstOrDefault(m => m.WinnerId == winnerId);
            mergeLog.Should().NotBeNull();
            mergeLog!.LoserOriginalId.Should().Be(loserId);
        }
    }

    [Fact]
    public async Task UnmergeAsync_RecreatesStubAndSeededEnrichment()
    {
        int winnerId, mergeId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            var mt  = db.MediaTypes.First();

            var winner = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId = mt.Id, Name = "Brandon Sanderson",
                NormalizedName = "brandon sanderson",
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            }).Entity;
            var loser = db.MediaItems.Add(new MediaItem
            {
                MediaTypeId = mt.Id, Name = "Brandon Sanderson",
                NormalizedName = "brandon sanderson",
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            }).Entity;
            db.SaveChanges();
            winnerId = winner.Id;

            await svc.MergeAsync(winnerId, loser.Id, null);
            mergeId = db.MediaItemMerges.First(m => m.WinnerId == winnerId).Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IMergeService>();
            await svc.UnmergeAsync(mergeId);

            // Merge log should be gone
            db.MediaItemMerges.Any(m => m.Id == mergeId).Should().BeFalse();
            // A new stub should exist
            db.MediaItems.Count(m => m.Name == "Brandon Sanderson" && m.Id != winnerId)
                .Should().Be(1);
        }
    }
}
```

**Step 2: Run integration tests**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MergeServiceIntegration" --verbosity normal 2>&1 | Select-Object -Last 20
```

**Step 3: Run full suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal 2>&1 | Select-Object -Last 10
```

**Step 4: Commit and push**

```powershell
cd W:\Scripts\Chronicle
git add tests/
git commit -m "test(integration): add MergeServiceIntegrationTests"
git push
```

---

## Smoke test checklist

- [ ] `DuplicateCandidateScanService` runs via Settings → Background Tasks → "Duplicate Candidate Scan"
- [ ] `GET /api/v1/duplicates` returns candidate pairs
- [ ] Dismiss removes a pair from the list and it doesn't reappear after rescan
- [ ] Merge two items via the Duplicates page → loser disappears → winner has AKA
- [ ] `GET /api/v1/media/{winnerId}` shows `aliases: ["..."]` and `mergeHistory: [...]`
- [ ] Global search for the AKA name finds the canonical item
- [ ] Unmerge from media detail page → stub recreated → enrichment queue shows Pending
- [ ] "Merge with…" on media detail page → search → side-by-side modal → merge works
- [ ] Existing `DuplicateCleanupService` background task still works (no regressions)
