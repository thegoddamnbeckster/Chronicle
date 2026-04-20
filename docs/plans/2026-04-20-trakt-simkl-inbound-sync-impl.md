# Trakt & SIMKL Inbound Sync — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement full inbound sync from Trakt and SIMKL — pulling watch history, ratings, watchlist, credits, and creating stub items for unrecognised media.

**Architecture:** Extend `IImportProvider` with two optional methods (`GetCreditsAsync`, `GetItemMetadataAsync`). A new `SyncOrchestrationService` orchestrates matching, stub creation, InteractionEvent recording, LibraryStatus updates, and credits storage. Background tasks in `PluginTaskRunner` route `import-all` and `delta-sync` task IDs to the new service.

**Tech Stack:** C# / .NET 9, EF Core 9 (SQLite), ASP.NET Core, xUnit + FluentAssertions + Moq.

**Design doc:** `docs/plans/2026-04-20-trakt-simkl-inbound-sync-design.md`

---

## Task 1: New model records in Chronicle.Plugins

Add `ImportedCredit` and `ImportedItemMetadata` records to the plugin models, then add two default-implementation methods to `IImportProvider`.

**Files:**
- Modify: `src/Chronicle.Plugins/IImportProvider.cs`

**Step 1: Add the new record types**

At the bottom of `IImportProvider.cs`, before the `IImportProvider` interface declaration, add:

```csharp
public record ImportedCredit(
    string  PersonName,
    string  Role,              // "Director" | "Writer" | "Actor" | "Composer" | "Producer" …
    string? CharacterName,     // actors only
    int?    BillingOrder,      // 1 = top-billed
    string? ExternalPersonId   // source-specific person ID for future dedup
);

public record ImportedItemMetadata(
    string  Title,
    int?    Year,
    string? Overview,
    string? PosterUrl,
    int?    RuntimeMinutes,
    IReadOnlyDictionary<string, string> AdditionalIds
);
```

**Step 2: Add default interface methods to `IImportProvider`**

Inside the `IImportProvider` interface, after `HealthCheckAsync`, add:

```csharp
// ── Optional enrichment hooks ─────────────────────────────────────────────

/// <summary>
/// Returns cast and crew for a specific item the provider knows about.
/// Called after stub creation to populate media_credits.
/// Default: empty list (no credits data available from this provider).
/// </summary>
Task<List<ImportedCredit>> GetCreditsAsync(
    string externalId,
    string mediaType,
    CancellationToken ct = default)
    => Task.FromResult(new List<ImportedCredit>());

/// <summary>
/// Returns full item metadata used to create a stub MediaItem when Chronicle
/// doesn't already know about this item.
/// Default: null — stub will be created with title/year from the watch event only.
/// </summary>
Task<ImportedItemMetadata?> GetItemMetadataAsync(
    string externalId,
    string mediaType,
    CancellationToken ct = default)
    => Task.FromResult<ImportedItemMetadata?>(null);
```

**Step 3: Build to verify no compilation errors**

```bash
dotnet build src/Chronicle.Plugins/Chronicle.Plugins.csproj
```

Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/Chronicle.Plugins/IImportProvider.cs
git commit -m "feat(plugins): add GetCreditsAsync and GetItemMetadataAsync to IImportProvider"
```

---

## Task 2: MediaCredit entity and EF migration

**Files:**
- Create: `src/Chronicle.Core/Models/MediaCredit.cs`
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`
- Create: `src/Chronicle.Data/Migrations/<timestamp>_AddMediaCredits.cs` (via CLI)

**Step 1: Create the entity**

```csharp
// src/Chronicle.Core/Models/MediaCredit.cs
namespace Chronicle.Core.Models;

public class MediaCredit
{
    public int     Id               { get; set; }
    public int     MediaItemId      { get; set; }
    public string  PersonName       { get; set; } = string.Empty;
    public string  Role             { get; set; } = string.Empty;  // "Director" | "Actor" | …
    public string? CharacterName    { get; set; }
    public int?    BillingOrder     { get; set; }
    public string  Source           { get; set; } = string.Empty;  // "trakt" | "tmdb" | …
    public string? ExternalPersonId { get; set; }
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;

    // Navigation
    public MediaItem MediaItem { get; set; } = null!;
}
```

**Step 2: Register in DbContext**

In `ChronicleDbContext.cs`, add:

```csharp
public DbSet<MediaCredit> MediaCredits { get; set; } = null!;
```

In `OnModelCreating`, add:

```csharp
modelBuilder.Entity<MediaCredit>(e =>
{
    e.ToTable("media_credits");
    e.HasKey(c => c.Id);
    e.Property(c => c.Id).HasColumnName("id");
    e.Property(c => c.MediaItemId).HasColumnName("media_item_id");
    e.Property(c => c.PersonName).HasColumnName("person_name");
    e.Property(c => c.Role).HasColumnName("role");
    e.Property(c => c.CharacterName).HasColumnName("character_name");
    e.Property(c => c.BillingOrder).HasColumnName("billing_order");
    e.Property(c => c.Source).HasColumnName("source");
    e.Property(c => c.ExternalPersonId).HasColumnName("external_person_id");
    e.Property(c => c.CreatedAt).HasColumnName("created_at");

    e.HasOne(c => c.MediaItem)
     .WithMany()
     .HasForeignKey(c => c.MediaItemId)
     .OnDelete(DeleteBehavior.Cascade);

    e.HasIndex(c => c.MediaItemId).HasDatabaseName("idx_media_credits_item");
    e.HasIndex(c => c.PersonName).HasDatabaseName("idx_media_credits_person");
});
```

**Step 3: Create the migration**

```bash
cd src/Chronicle.API
dotnet ef migrations add AddMediaCredits --project ../Chronicle.Data --startup-project .
```

Expected: New migration file created in `src/Chronicle.Data/Migrations/`.

**Step 4: Apply migration locally to verify SQL**

```bash
dotnet ef database update --project ../Chronicle.Data --startup-project .
```

Expected: Database updated.

**Step 5: Build the solution**

```bash
dotnet build src/Chronicle.sln
```

Expected: 0 errors.

**Step 6: Commit**

```bash
git add src/Chronicle.Core/Models/MediaCredit.cs
git add src/Chronicle.Data/ChronicleDbContext.cs
git add src/Chronicle.Data/Migrations/
git commit -m "feat(data): add MediaCredit entity and media_credits migration"
```

---

## Task 3: Cascade-reset in UpsertExternalIdForEnrichmentAsync

When any plugin changes the canonical external ID for a media item, all sibling enrichment rows for that item should be reset to `Pending` so they re-enrich against the corrected identity.

**Files:**
- Modify: `src/Chronicle.Services/MetadataEnrichmentService.cs`
- Modify: `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`

**Step 1: Write failing unit tests**

Add to `MetadataEnrichmentServiceTests.cs` (or create if missing):

```csharp
[Fact]
public async Task UpsertExternalId_WhenIdChanges_ResetsOtherPluginEnrichmentRows()
{
    // Arrange – item 1 has two enrichment rows: tmdb (Completed) and musicbrainz (Completed)
    var options = new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    await using var db = new ChronicleDbContext(options);
    db.MediaTypes.Add(new MediaType { Id = 1, Name = "movie", HierarchyLabels = "[]", InteractionVerbs = "{}", ProgressUnits = "{}" });
    db.MediaItems.Add(new MediaItem { Id = 1, Name = "Fight Club", MediaTypeId = 1 });
    db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = 1, Source = "tmdb", ExternalId = "movie:550" });
    db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = 1, PluginId = "chronicle.plugin.tmdb",        Status = EnrichmentStatus.Completed, MaxRetries = 3 });
    db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = 1, PluginId = "chronicle.plugin.musicbrainz", Status = EnrichmentStatus.Completed, MaxRetries = 3 });
    await db.SaveChangesAsync();

    var service = new MetadataEnrichmentService(db, /* other deps mocked */ ...);

    // Act – TMDB changes the external ID from movie:550 → movie:999
    await service.UpsertExternalIdForEnrichmentAsync(db, 1, "movie:999", CancellationToken.None);

    // Assert – tmdb row updated; musicbrainz row reset to Pending
    var tmdbRow = await db.MediaExternalIds.FirstAsync(e => e.MediaItemId == 1 && e.Source == "tmdb");
    tmdbRow.ExternalId.Should().Be("movie:999");

    var mbRow = await db.MediaEnrichments.FirstAsync(e => e.MediaItemId == 1 && e.PluginId == "chronicle.plugin.musicbrainz");
    mbRow.Status.Should().Be(EnrichmentStatus.Pending);
    mbRow.RetryCount.Should().Be(0);
}

[Fact]
public async Task UpsertExternalId_WhenIdUnchanged_DoesNotResetSiblingRows()
{
    // Same setup but upsert with same ID → sibling row stays Completed
    // ... arrange same as above ...
    
    await service.UpsertExternalIdForEnrichmentAsync(db, 1, "movie:550", CancellationToken.None);

    var mbRow = await db.MediaEnrichments.FirstAsync(e => e.MediaItemId == 1 && e.PluginId == "chronicle.plugin.musicbrainz");
    mbRow.Status.Should().Be(EnrichmentStatus.Completed);  // unchanged
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/Chronicle.Tests.Unit --filter "UpsertExternalId_WhenIdChanges" --verbosity normal
```

Expected: FAIL — cascade reset not yet implemented.

**Step 3: Extend `UpsertExternalIdForEnrichmentAsync` in MetadataEnrichmentService**

Locate the existing method and replace it with:

```csharp
private async Task UpsertExternalIdForEnrichmentAsync(
    ChronicleDbContext db, int mediaItemId, string rawExternalId, CancellationToken ct)
{
    var (source, extId) = ParseExternalId(rawExternalId);

    var existing = await db.MediaExternalIds
        .FirstOrDefaultAsync(e => e.MediaItemId == mediaItemId && e.Source == source, ct);

    bool idChanged = false;

    if (existing is null)
    {
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = mediaItemId,
            Source      = source,
            ExternalId  = extId,
        });
    }
    else if (existing.ExternalId != extId)
    {
        existing.ExternalId = extId;
        idChanged = true;
    }

    // When the canonical ID changes, invalidate all other plugins so they
    // re-enrich against the corrected identity.
    if (idChanged)
    {
        await db.MediaEnrichments
            .Where(e => e.MediaItemId == mediaItemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status,     EnrichmentStatus.Pending)
                .SetProperty(r => r.RetryCount, 0)
                .SetProperty(r => r.ExternalId, (string?)null),
                ct);
    }

    await db.SaveChangesAsync(ct);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Chronicle.Tests.Unit --filter "UpsertExternalId" --verbosity normal
```

Expected: Both tests PASS.

**Step 5: Run full unit test suite**

```bash
dotnet test tests/Chronicle.Tests.Unit --verbosity normal
```

Expected: All tests pass (no regressions).

**Step 6: Commit**

```bash
git add src/Chronicle.Services/MetadataEnrichmentService.cs
git add tests/Chronicle.Tests.Unit/
git commit -m "feat(enrichment): cascade-reset sibling enrichment rows when external ID changes"
```

---

## Task 4: ISyncOrchestrationService interface

**Files:**
- Create: `src/Chronicle.Services/ISyncOrchestrationService.cs`

**Step 1: Create the interface**

```csharp
// src/Chronicle.Services/ISyncOrchestrationService.cs
namespace Chronicle.Services;

public record SyncSummary(
    int  ItemsMatched,
    int  StubsCreated,
    int  WatchEventsAdded,
    int  CreditsAdded,
    IReadOnlyList<string> Errors
);

public interface ISyncOrchestrationService
{
    /// <summary>
    /// Syncs all available data from the specified import provider.
    /// </summary>
    /// <param name="pluginId">The plugin ID declared in the provider's manifest (e.g. "chronicle.plugin.trakt").</param>
    /// <param name="fullSync">When true, ignore last_synced_at and pull all history.</param>
    Task<SyncSummary> SyncAsync(string pluginId, bool fullSync = false, CancellationToken ct = default);
}
```

**Step 2: Build**

```bash
dotnet build src/Chronicle.sln
```

Expected: 0 errors.

**Step 3: Commit**

```bash
git add src/Chronicle.Services/ISyncOrchestrationService.cs
git commit -m "feat(sync): add ISyncOrchestrationService interface"
```

---

## Task 5: SyncOrchestrationService — item matching

The `MatchOrCreateAsync` helper tries to find an existing `MediaItem` for an imported watch event before creating a stub.

**Files:**
- Create: `src/Chronicle.Services/SyncOrchestrationService.cs`
- Create: `tests/Chronicle.Tests.Unit/Services/SyncOrchestrationServiceTests.cs`

**Step 1: Write failing tests for item matching**

```csharp
// tests/Chronicle.Tests.Unit/Services/SyncOrchestrationServiceTests.cs
public class SyncOrchestrationServiceMatchTests
{
    private ChronicleDbContext BuildDb(string name)
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(name).Options;
        var db = new ChronicleDbContext(opts);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "movie", HierarchyLabels = "[]", InteractionVerbs = "{}", ProgressUnits = "{}" });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task MatchOrCreate_FindsByExternalId()
    {
        await using var db = BuildDb(nameof(MatchOrCreate_FindsByExternalId));
        db.MediaItems.Add(new MediaItem { Id = 1, Name = "Fight Club", Year = 1999, MediaTypeId = 1 });
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = 1, Source = "trakt", ExternalId = "12345" });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var evt = new ImportedWatchEvent("trakt:12345", new Dictionary<string,string>(), "movie", "Fight Club", 1999, DateTimeOffset.UtcNow, 100);

        var (item, isNew) = await service.MatchOrCreateAsync(db, evt, "chronicle.plugin.trakt", CancellationToken.None);

        item.Id.Should().Be(1);
        isNew.Should().BeFalse();
    }

    [Fact]
    public async Task MatchOrCreate_FindsByAdditionalId()
    {
        await using var db = BuildDb(nameof(MatchOrCreate_FindsByAdditionalId));
        db.MediaItems.Add(new MediaItem { Id = 1, Name = "Fight Club", Year = 1999, MediaTypeId = 1 });
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = 1, Source = "tmdb", ExternalId = "movie:550" });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var evt = new ImportedWatchEvent(
            "trakt:12345",
            new Dictionary<string,string> { ["tmdb"] = "movie:550" },
            "movie", "Fight Club", 1999,
            DateTimeOffset.UtcNow, 100);

        var (item, isNew) = await service.MatchOrCreateAsync(db, evt, "chronicle.plugin.trakt", CancellationToken.None);

        item.Id.Should().Be(1);
        isNew.Should().BeFalse();
    }

    [Fact]
    public async Task MatchOrCreate_CreateStub_WhenNoMatch()
    {
        await using var db = BuildDb(nameof(MatchOrCreate_CreateStub_WhenNoMatch));
        var mockProvider = new Mock<IImportProvider>();
        mockProvider.Setup(p => p.GetItemMetadataAsync("trakt:99999", "movie", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImportedItemMetadata("Unknown Movie", 2020, "An overview.", null, 90,
                new Dictionary<string,string> { ["tmdb"] = "movie:88888" }));

        var service = BuildService(db, mockProvider.Object);
        var evt = new ImportedWatchEvent(
            "trakt:99999",
            new Dictionary<string,string> { ["tmdb"] = "movie:88888" },
            "movie", "Unknown Movie", 2020,
            DateTimeOffset.UtcNow, 100);

        var (item, isNew) = await service.MatchOrCreateAsync(db, evt, "chronicle.plugin.trakt", CancellationToken.None);

        isNew.Should().BeTrue();
        item.Name.Should().Be("Unknown Movie");
        db.MediaExternalIds.Should().Contain(e => e.Source == "trakt" && e.ExternalId == "trakt:99999");
        db.MediaExternalIds.Should().Contain(e => e.Source == "tmdb"  && e.ExternalId == "movie:88888");
    }
}
```

**Step 2: Run to verify they fail**

```bash
dotnet test tests/Chronicle.Tests.Unit --filter "SyncOrchestrationService" --verbosity normal
```

Expected: FAIL — service class does not exist yet.

**Step 3: Create SyncOrchestrationService with MatchOrCreateAsync**

```csharp
// src/Chronicle.Services/SyncOrchestrationService.cs
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services;

public class SyncOrchestrationService : ISyncOrchestrationService
{
    private const string SyncStateKeyPrefix = "sync_state.";

    private readonly IDbContextFactory<ChronicleDbContext> _dbFactory;
    private readonly IPluginRegistry _registry;
    private readonly IAppSettingsService _appSettings;
    private readonly ILogger _log = Log.ForContext<SyncOrchestrationService>();

    public SyncOrchestrationService(
        IDbContextFactory<ChronicleDbContext> dbFactory,
        IPluginRegistry registry,
        IAppSettingsService appSettings)
    {
        _dbFactory = dbFactory;
        _registry  = registry;
        _appSettings = appSettings;
    }

    public async Task<SyncSummary> SyncAsync(
        string pluginId, bool fullSync = false, CancellationToken ct = default)
    {
        var loaded = _registry.GetLoadedPlugins()
            .FirstOrDefault(lp => lp.PluginId == pluginId);

        if (loaded is null)
            throw new InvalidOperationException($"Plugin '{pluginId}' is not loaded.");

        var provider = loaded.ImportProviders.FirstOrDefault()
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' has no IImportProvider.");

        if (!await provider.IsAuthenticatedAsync(ct))
            throw new InvalidOperationException($"Plugin '{pluginId}' is not authenticated.");

        var syncKey = $"{SyncStateKeyPrefix}{pluginId}.last_synced_at";
        DateTimeOffset? since = null;

        if (!fullSync)
        {
            var raw = await _appSettings.GetAsync(syncKey, ct);
            if (raw is not null && DateTimeOffset.TryParse(raw, out var parsed))
                since = parsed;
        }

        _log.Information("Starting {Mode} sync for {PluginId} (since={Since})",
            fullSync ? "full" : "delta", pluginId, since);

        // Fetch data
        var historyTask  = provider.GetWatchHistoryAsync(since, ct);
        var ratingsTask  = provider.GetRatingsAsync(ct);
        var watchlistTask = provider.GetWatchlistAsync(ct);
        await Task.WhenAll(historyTask, ratingsTask, watchlistTask);

        var history   = historyTask.Result;
        var ratings   = ratingsTask.Result;
        var watchlist = watchlistTask.Result;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        int itemsMatched = 0, stubsCreated = 0, watchEventsAdded = 0, creditsAdded = 0;
        var errors = new List<string>();

        // Process watch events
        foreach (var evt in history)
        {
            try
            {
                var (item, isNew) = await MatchOrCreateAsync(db, evt, pluginId, provider, ct);
                if (isNew) stubsCreated++; else itemsMatched++;
                watchEventsAdded += await UpsertWatchEventAsync(db, item.Id, evt, ct);
                await UpsertLibraryStatusAsync(db, item.Id, evt, ct);

                if (isNew)
                    creditsAdded += await FetchAndStoreCreditsAsync(db, item.Id, evt.ExternalId, evt.MediaType, pluginId, provider, ct);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Sync error for item {ExternalId}", evt.ExternalId);
                errors.Add($"{evt.ExternalId}: {ex.Message}");
            }
        }

        // Process ratings (upsert rating on library entry)
        foreach (var rating in ratings)
        {
            try { await UpsertRatingAsync(db, rating, pluginId, ct); }
            catch (Exception ex) { errors.Add($"rating {rating.ExternalId}: {ex.Message}"); }
        }

        // Process watchlist
        foreach (var entry in watchlist)
        {
            try { await UpsertWatchlistStatusAsync(db, entry, pluginId, ct); }
            catch (Exception ex) { errors.Add($"watchlist {entry.ExternalId}: {ex.Message}"); }
        }

        await _appSettings.SetAsync(syncKey, DateTimeOffset.UtcNow.ToString("O"), ct);

        _log.Information(
            "Sync complete for {PluginId}: {Matched} matched, {Created} stubs, {Events} events, {Credits} credits, {Errors} errors",
            pluginId, itemsMatched, stubsCreated, watchEventsAdded, creditsAdded, errors.Count);

        return new SyncSummary(itemsMatched, stubsCreated, watchEventsAdded, creditsAdded, errors);
    }

    // ── Item matching ─────────────────────────────────────────────────────────

    internal async Task<(MediaItem item, bool isNew)> MatchOrCreateAsync(
        ChronicleDbContext db,
        ImportedWatchEvent evt,
        string pluginId,
        IImportProvider provider,
        CancellationToken ct)
    {
        // 1. ExternalId match
        var providerSource = pluginId.Split('.').Last(); // "chronicle.plugin.trakt" → "trakt"
        var rawId = evt.ExternalId.Contains(':') ? evt.ExternalId.Split(':')[1] : evt.ExternalId;

        var byOwn = await db.MediaExternalIds
            .Where(e => e.Source == providerSource && e.ExternalId == evt.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (byOwn != 0)
            return (await db.MediaItems.FindAsync(new object[] { byOwn }, ct) ?? throw new InvalidOperationException(), false);

        // 2. AdditionalIds match
        foreach (var (source, extId) in evt.AdditionalIds)
        {
            var byAdditional = await db.MediaExternalIds
                .Where(e => e.Source == source && e.ExternalId == extId)
                .Select(e => e.MediaItemId)
                .FirstOrDefaultAsync(ct);
            if (byAdditional != 0)
                return (await db.MediaItems.FindAsync(new object[] { byAdditional }, ct) ?? throw new InvalidOperationException(), false);
        }

        // 3. Title + year fuzzy match
        if (evt.Title is not null)
        {
            var normalised = NormaliseTitle(evt.Title);
            var byTitle = await db.MediaItems
                .Where(i => i.Year == evt.Year && EF.Functions.Like(i.Name, normalised))
                .FirstOrDefaultAsync(ct);
            if (byTitle is not null)
                return (byTitle, false);
        }

        // 4. Create stub
        return (await CreateStubAsync(db, evt, pluginId, provider, ct), true);
    }

    private async Task<MediaItem> CreateStubAsync(
        ChronicleDbContext db,
        ImportedWatchEvent evt,
        string pluginId,
        IImportProvider provider,
        CancellationToken ct)
    {
        ImportedItemMetadata? meta = null;
        try { meta = await provider.GetItemMetadataAsync(evt.ExternalId, evt.MediaType, ct); }
        catch (Exception ex) { _log.Warning(ex, "GetItemMetadataAsync failed for {Id}", evt.ExternalId); }

        var mediaType = await db.MediaTypes.FirstOrDefaultAsync(t => t.Name == MapMediaType(evt.MediaType), ct)
            ?? throw new InvalidOperationException($"Media type '{evt.MediaType}' not found in database.");

        var item = new MediaItem
        {
            Name           = meta?.Title ?? evt.Title ?? "Unknown",
            Year           = meta?.Year ?? evt.Year,
            MediaTypeId    = mediaType.Id,
            HierarchyLevel = 0,
            MetadataJson   = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object> { [pluginId] = new { raw = evt } }),
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync(ct);

        // Store all known IDs
        var allIds = new Dictionary<string, string>(evt.AdditionalIds)
        {
            [pluginId.Split('.').Last()] = evt.ExternalId
        };
        if (meta?.AdditionalIds is not null)
            foreach (var (s, v) in meta.AdditionalIds)
                allIds.TryAdd(s, v);

        foreach (var (source, extId) in allIds)
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = source, ExternalId = extId });

        // Seed enrichment rows for all loaded metadata plugins
        foreach (var lp in _registry.GetLoadedPlugins())
        {
            foreach (var mp in lp.MetadataProviders)
            {
                var supported = mp.GetSupportedMediaTypes().Any(t => t.MediaTypeName == MapMediaType(evt.MediaType));
                if (!supported) continue;

                var exists = await db.MediaEnrichments
                    .AnyAsync(e => e.MediaItemId == item.Id && e.PluginId == lp.PluginId, ct);
                if (exists) continue;

                // Pre-seed the known external ID so the enrichment service can call GetByIdAsync directly
                allIds.TryGetValue(lp.PluginId.Split('.').Last(), out var knownId);

                db.MediaEnrichments.Add(new MediaItemEnrichment
                {
                    MediaItemId = item.Id,
                    PluginId    = lp.PluginId,
                    Status      = EnrichmentStatus.Pending,
                    MaxRetries  = 3,
                    ExternalId  = knownId,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return item;
    }

    // ── Watch event recording ─────────────────────────────────────────────────

    private async Task<int> UpsertWatchEventAsync(
        ChronicleDbContext db, int mediaItemId, ImportedWatchEvent evt, CancellationToken ct)
    {
        var ts = evt.WatchedAt.UtcDateTime;
        var exists = await db.InteractionEvents
            .AnyAsync(e => e.MediaItemId == mediaItemId && e.Timestamp == ts, ct);
        if (exists) return 0;

        db.InteractionEvents.Add(new InteractionEvent
        {
            MediaItemId      = mediaItemId,
            Timestamp        = ts,
            ProgressPercent  = evt.ProgressPercent ?? 100,
            MarkedAsWatched  = true,
        });
        await db.SaveChangesAsync(ct);
        return 1;
    }

    // ── Library status ────────────────────────────────────────────────────────

    private async Task UpsertLibraryStatusAsync(
        ChronicleDbContext db, int mediaItemId, ImportedWatchEvent evt, CancellationToken ct)
    {
        // Chronicle status wins — don't overwrite user-set statuses like Dropped/OnHold
        var entry = await db.UserLibraries
            .FirstOrDefaultAsync(l => l.MediaItemId == mediaItemId, ct);

        var newStatus = evt.MediaType == "tv_episode" ? "Watching" : "Completed";

        if (entry is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                MediaItemId = mediaItemId,
                Status      = newStatus,
                AddedAt     = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (entry.Status is "PlanToWatch" or null)
        {
            entry.Status = newStatus;
            await db.SaveChangesAsync(ct);
        }
        // else: leave existing status alone
    }

    private async Task UpsertWatchlistStatusAsync(
        ChronicleDbContext db, ImportedWatchlistEntry entry, string pluginId, CancellationToken ct)
    {
        // Find item by external ID — only process items already in Chronicle
        var source = pluginId.Split('.').Last();
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == entry.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return;

        var lib = await db.UserLibraries.FirstOrDefaultAsync(l => l.MediaItemId == mediaItemId, ct);
        if (lib is null)
        {
            db.UserLibraries.Add(new UserLibrary
            {
                MediaItemId = mediaItemId,
                Status      = "PlanToWatch",
                AddedAt     = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task UpsertRatingAsync(
        ChronicleDbContext db, ImportedRating rating, string pluginId, CancellationToken ct)
    {
        var source = pluginId.Split('.').Last();
        var mediaItemId = await db.MediaExternalIds
            .Where(e => e.Source == source && e.ExternalId == rating.ExternalId)
            .Select(e => e.MediaItemId)
            .FirstOrDefaultAsync(ct);
        if (mediaItemId == 0) return;

        var lib = await db.UserLibraries.FirstOrDefaultAsync(l => l.MediaItemId == mediaItemId, ct);
        if (lib is null) return;

        lib.UserRating = rating.Rating;
        await db.SaveChangesAsync(ct);
    }

    // ── Credits ───────────────────────────────────────────────────────────────

    private async Task<int> FetchAndStoreCreditsAsync(
        ChronicleDbContext db, int mediaItemId, string externalId,
        string mediaType, string pluginId, IImportProvider provider, CancellationToken ct)
    {
        List<ImportedCredit> credits;
        try { credits = await provider.GetCreditsAsync(externalId, mediaType, ct); }
        catch (Exception ex)
        {
            _log.Warning(ex, "GetCreditsAsync failed for {Id}", externalId);
            return 0;
        }

        if (credits.Count == 0) return 0;

        var source = pluginId.Split('.').Last();

        // Replace all credits for this item+source
        var old = db.MediaCredits.Where(c => c.MediaItemId == mediaItemId && c.Source == source);
        db.MediaCredits.RemoveRange(old);

        foreach (var credit in credits)
        {
            db.MediaCredits.Add(new MediaCredit
            {
                MediaItemId      = mediaItemId,
                PersonName       = credit.PersonName,
                Role             = credit.Role,
                CharacterName    = credit.CharacterName,
                BillingOrder     = credit.BillingOrder,
                Source           = source,
                ExternalPersonId = credit.ExternalPersonId,
            });
        }

        await db.SaveChangesAsync(ct);
        return credits.Count;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormaliseTitle(string title) =>
        title.Replace(" - ", ": ", StringComparison.OrdinalIgnoreCase).Trim();

    private static string MapMediaType(string importType) => importType switch
    {
        "movie"      => "movies",
        "tv_show"    => "tv",
        "tv_episode" => "tv",
        _            => importType,
    };
}
```

**Step 4: Run the matching tests**

```bash
dotnet test tests/Chronicle.Tests.Unit --filter "SyncOrchestrationService" --verbosity normal
```

Expected: All matching tests PASS.

**Step 5: Build solution**

```bash
dotnet build src/Chronicle.sln
```

Expected: 0 errors.

**Step 6: Commit**

```bash
git add src/Chronicle.Services/
git add tests/Chronicle.Tests.Unit/
git commit -m "feat(sync): SyncOrchestrationService with matching, stub creation, watch events, and credits"
```

---

## Task 6: Register service and wire PluginTaskRunner

**Files:**
- Modify: `src/Chronicle.API/Program.cs`
- Modify: `src/Chronicle.Services/PluginTaskRunner.cs`

**Step 1: Register in DI**

In `Program.cs`, add alongside other service registrations:

```csharp
builder.Services.AddScoped<ISyncOrchestrationService, SyncOrchestrationService>();
```

**Step 2: Add task IDs to PluginTaskRunner**

```csharp
private const string ImportAll  = "import-all";
private const string DeltaSync  = "delta-sync";

// Add to constructor:
private readonly ISyncOrchestrationService _sync;

public PluginTaskRunner(IMetadataEnrichmentService enrichment, ISyncOrchestrationService sync)
{
    _enrichment = enrichment;
    _sync       = sync;
}

// Add to switch in RunAsync:
case ImportAll:
    _log.Information("PluginTaskRunner: running import-all for plugin {PluginId}", pluginId);
    await _sync.SyncAsync(pluginId, fullSync: true, ct);
    return;

case DeltaSync:
    _log.Information("PluginTaskRunner: running delta-sync for plugin {PluginId}", pluginId);
    await _sync.SyncAsync(pluginId, fullSync: false, ct);
    return;
```

**Step 3: Build and run all tests**

```bash
dotnet build src/Chronicle.sln
dotnet test tests/ --verbosity normal
```

Expected: 0 errors, all tests pass.

**Step 4: Commit**

```bash
git add src/Chronicle.API/Program.cs
git add src/Chronicle.Services/PluginTaskRunner.cs
git commit -m "feat(sync): register SyncOrchestrationService and wire PluginTaskRunner"
```

---

## Task 7: SyncController (manual trigger endpoint)

**Files:**
- Create: `src/Chronicle.API/Controllers/SyncController.cs`
- Create: `tests/Chronicle.Tests.Integration/Controllers/SyncControllerTests.cs`

**Step 1: Write failing integration test**

```csharp
[Fact]
public async Task PostSync_ReturnsOk_WhenPluginLoaded()
{
    // Requires authenticated admin client + a mock sync service
    // This is a smoke test — full flow tested via unit tests above
    var client = await CreateAdminClientAsync();
    var response = await client.PostAsync("/api/v1/sync/chronicle.plugin.trakt?fullSync=true", null);
    // 200 OK or 422 (not authenticated) are both acceptable — 404/500 are not
    response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity);
}
```

**Step 2: Create controller**

```csharp
// src/Chronicle.API/Controllers/SyncController.cs
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/sync")]
[Authorize]
public class SyncController : ControllerBase
{
    private readonly ISyncOrchestrationService _sync;

    public SyncController(ISyncOrchestrationService sync)
    {
        _sync = sync;
    }

    /// <summary>Manually trigger an import or delta sync for a plugin.</summary>
    [HttpPost("{pluginId}")]
    public async Task<IActionResult> TriggerSync(
        string pluginId,
        [FromQuery] bool fullSync = false,
        CancellationToken ct = default)
    {
        try
        {
            var summary = await _sync.SyncAsync(pluginId, fullSync, ct);
            return Ok(ApiResponse<SyncSummary>.Ok(summary));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not authenticated"))
        {
            return UnprocessableEntity(ApiResponse<object>.Fail("NOT_AUTHENTICATED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<object>.Fail("SYNC_FAILED", ex.Message));
        }
    }
}
```

**Step 3: Build and run integration tests**

```bash
dotnet build src/Chronicle.sln
dotnet test tests/Chronicle.Tests.Integration --filter "SyncController" --verbosity normal
```

Expected: Tests pass.

**Step 4: Commit**

```bash
git add src/Chronicle.API/Controllers/SyncController.cs
git add tests/Chronicle.Tests.Integration/
git commit -m "feat(api): add SyncController for manual import/delta-sync triggers"
```

---

## Task 8: Trakt plugin — GetItemMetadataAsync and GetCreditsAsync

**Files (separate repo `W:\Scripts\Chronicle.Plugin.Trakt\`):**
- Modify: `TraktClient.cs`
- Modify: `TraktPlugin.cs`
- Modify: `manifest.json`

**Step 1: Add API methods to TraktClient**

Add to `TraktClient.cs`:

```csharp
public async Task<TraktMovieFull?> GetMovieAsync(string traktId, CancellationToken ct = default)
{
    var response = await _http.GetAsync($"movies/{traktId}?extended=full", ct);
    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadFromJsonAsync<TraktMovieFull>(ct: ct);
}

public async Task<TraktShowFull?> GetShowAsync(string traktId, CancellationToken ct = default)
{
    var response = await _http.GetAsync($"shows/{traktId}?extended=full", ct);
    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadFromJsonAsync<TraktShowFull>(ct: ct);
}

public async Task<TraktCastCrew?> GetMoviePeopleAsync(string traktId, CancellationToken ct = default)
{
    var response = await _http.GetAsync($"movies/{traktId}/people", ct);
    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadFromJsonAsync<TraktCastCrew>(ct: ct);
}

public async Task<TraktCastCrew?> GetShowPeopleAsync(string traktId, CancellationToken ct = default)
{
    var response = await _http.GetAsync($"shows/{traktId}/people", ct);
    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadFromJsonAsync<TraktCastCrew>(ct: ct);
}
```

Add API model types to `TraktApiModels.cs`:

```csharp
public record TraktMovieFull(
    string Title, int Year, string? Overview,
    int? Runtime, TraktIds Ids);

public record TraktShowFull(
    string Title, int Year, string? Overview,
    int? Runtime, TraktIds Ids);

public record TraktCastCrew(
    List<TraktCastMember> Cast,
    TraktCrewRoles Crew);

public record TraktCastMember(
    string Character, int ListOrder,
    TraktPerson Person);

public record TraktCrewRoles(
    List<TraktCrewMember>? Directing,
    List<TraktCrewMember>? Writing,
    List<TraktCrewMember>? Production);

public record TraktCrewMember(string Job, TraktPerson Person);

public record TraktPerson(string Name, TraktPersonIds Ids);
public record TraktPersonIds(int? Trakt, string? Imdb, int? Tmdb);
```

**Step 2: Override IImportProvider methods in TraktPlugin**

Add to `TraktPlugin.cs`:

```csharp
public override async Task<ImportedItemMetadata?> GetItemMetadataAsync(
    string externalId, string mediaType, CancellationToken ct = default)
{
    var traktId = externalId.Contains(':') ? externalId.Split(':')[1] : externalId;

    if (mediaType == "movie")
    {
        var m = await _client.GetMovieAsync(traktId, ct);
        if (m is null) return null;
        return new ImportedItemMetadata(
            m.Title, m.Year, m.Overview, null, m.Runtime,
            BuildAdditionalIds(m.Ids));
    }
    else
    {
        var s = await _client.GetShowAsync(traktId, ct);
        if (s is null) return null;
        return new ImportedItemMetadata(
            s.Title, s.Year, s.Overview, null, s.Runtime,
            BuildAdditionalIds(s.Ids));
    }
}

public override async Task<List<ImportedCredit>> GetCreditsAsync(
    string externalId, string mediaType, CancellationToken ct = default)
{
    var traktId = externalId.Contains(':') ? externalId.Split(':')[1] : externalId;
    var castCrew = mediaType == "movie"
        ? await _client.GetMoviePeopleAsync(traktId, ct)
        : await _client.GetShowPeopleAsync(traktId, ct);

    if (castCrew is null) return [];

    var credits = new List<ImportedCredit>();

    foreach (var actor in castCrew.Cast ?? [])
        credits.Add(new ImportedCredit(
            actor.Person.Name, "Actor", actor.Character,
            actor.ListOrder, actor.Person.Ids.Trakt?.ToString()));

    foreach (var director in castCrew.Crew?.Directing ?? [])
        credits.Add(new ImportedCredit(
            director.Person.Name, "Director", null, null,
            director.Person.Ids.Trakt?.ToString()));

    foreach (var writer in castCrew.Crew?.Writing ?? [])
        credits.Add(new ImportedCredit(
            writer.Person.Name, "Writer", null, null,
            writer.Person.Ids.Trakt?.ToString()));

    return credits;
}

private static Dictionary<string, string> BuildAdditionalIds(TraktIds ids)
{
    var d = new Dictionary<string, string>();
    if (ids.Tmdb.HasValue) d["tmdb"] = $"movie:{ids.Tmdb}";
    if (ids.Imdb is not null) d["imdb"] = ids.Imdb;
    if (ids.Tvdb.HasValue) d["tvdb"] = ids.Tvdb.ToString()!;
    return d;
}
```

**Step 3: Add background tasks to manifest.json**

```json
"background_tasks": [
  {
    "task_id":                  "import-all",
    "display_name":             "Import All from Trakt",
    "description":              "One-time full import of your entire Trakt watch history, ratings, and watchlist.",
    "default_cron":             "",
    "default_enabled":          false,
    "schedulable":              false,
    "run_confirmation_title":   "Import everything from Trakt?",
    "run_confirmation_message": "This pulls your full Trakt history and may take several minutes. Existing records will not be duplicated."
  },
  {
    "task_id":         "delta-sync",
    "display_name":    "Delta Sync",
    "description":     "Pulls new watch events, ratings, and watchlist changes from Trakt since the last sync.",
    "default_cron":    "0 2 * * *",
    "default_enabled": true,
    "schedulable":     true
  }
]
```

**Step 4: Build the plugin**

```bash
dotnet build W:\Scripts\Chronicle.Plugin.Trakt\Chronicle.Plugin.Trakt.csproj -c Debug
```

Expected: 0 errors.

**Step 5: Commit (in the Trakt plugin repo)**

```bash
cd W:\Scripts\Chronicle.Plugin.Trakt
git add -A
git commit -m "feat(trakt): add GetItemMetadataAsync, GetCreditsAsync, and sync background tasks"
```

---

## Task 9: SIMKL plugin — GetItemMetadataAsync and manifest tasks

**Files (separate repo `W:\Scripts\Chronicle.Plugin.SIMKL\`):**
- Modify: `SimklClient.cs`
- Modify: `SimklImportProvider.cs`
- Modify: `manifest.json`

**Step 1: Add API method to SimklClient**

```csharp
public async Task<SimklMovieFull?> GetMovieAsync(string simklId, CancellationToken ct = default)
{
    var response = await _http.GetAsync($"movies/{simklId}?extended=full", ct);
    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadFromJsonAsync<SimklMovieFull>(ct: ct);
}

public async Task<SimklShowFull?> GetShowAsync(string simklId, CancellationToken ct = default)
{
    var response = await _http.GetAsync($"shows/{simklId}?extended=full", ct);
    if (!response.IsSuccessStatusCode) return null;
    return await response.Content.ReadFromJsonAsync<SimklShowFull>(ct: ct);
}
```

Add model types to `SimklModels.cs`:

```csharp
public record SimklMovieFull(
    string Title, int Year, string? Overview,
    int? Runtime, SimklIds Ids);

public record SimklShowFull(
    string Title, int Year, string? Overview,
    int? Runtime, SimklIds Ids);
```

**Step 2: Override GetItemMetadataAsync in SimklImportProvider**

```csharp
public override async Task<ImportedItemMetadata?> GetItemMetadataAsync(
    string externalId, string mediaType, CancellationToken ct = default)
{
    var simklId = externalId.Contains(':') ? externalId.Split(':')[1] : externalId;

    if (mediaType == "movie")
    {
        var m = await _client.GetMovieAsync(simklId, ct);
        if (m is null) return null;
        return new ImportedItemMetadata(
            m.Title, m.Year, m.Overview, null, m.Runtime,
            BuildIds(m.Ids));
    }
    else
    {
        var s = await _client.GetShowAsync(simklId, ct);
        if (s is null) return null;
        return new ImportedItemMetadata(
            s.Title, s.Year, s.Overview, null, s.Runtime,
            BuildIds(s.Ids));
    }
}

// GetCreditsAsync intentionally not overridden — default empty list is correct.
// TMDB enrichment (triggered by pre-seeded enrichment row) supplies cast/crew instead.

private static Dictionary<string, string> BuildIds(SimklIds ids)
{
    var d = new Dictionary<string, string>();
    if (ids.Tmdb.HasValue) d["tmdb"] = $"movie:{ids.Tmdb}";
    if (ids.Imdb is not null) d["imdb"] = ids.Imdb;
    if (ids.Trakt.HasValue) d["trakt"] = ids.Trakt.ToString()!;
    return d;
}
```

**Step 3: Add background tasks to manifest.json**

Same structure as Trakt manifest but SIMKL-flavoured display text.

**Step 4: Build**

```bash
dotnet build W:\Scripts\Chronicle.Plugin.SIMKL\Chronicle.Plugin.SIMKL.csproj -c Debug
```

Expected: 0 errors.

**Step 5: Commit (in SIMKL repo)**

```bash
cd W:\Scripts\Chronicle.Plugin.SIMKL
git add -A
git commit -m "feat(simkl): add GetItemMetadataAsync and sync background tasks"
```

---

## Task 10: Deploy plugins, run full test suite, smoke test

**Step 1: Run full Chronicle test suite**

```bash
dotnet test tests/ --verbosity normal
```

Expected: All tests pass (no regressions).

**Step 2: Rebuild and deploy plugin DLLs**

```bash
dotnet build W:\Scripts\Chronicle.Plugin.Trakt\Chronicle.Plugin.Trakt.csproj -c Debug
dotnet build W:\Scripts\Chronicle.Plugin.SIMKL\Chronicle.Plugin.SIMKL.csproj -c Debug
cp W:\Scripts\Chronicle.Plugin.Trakt\bin\Debug\net9.0\Chronicle.Plugin.Trakt.dll W:\Scripts\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.trakt\
cp W:\Scripts\Chronicle.Plugin.SIMKL\bin\Debug\net9.0\Chronicle.Plugin.SIMKL.dll W:\Scripts\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.simkl\
```

**Step 3: Start the API and verify**

Run `scripts/RunTestEnvironment.ps1` then:
- Navigate to Background Tasks page → verify Trakt and SIMKL each show their own plugin group with Import All and Delta Sync tasks
- Click "Import All" on Trakt → confirm modal shows, run completes
- Check library for newly imported items

**Step 4: Final commit (Chronicle repo)**

```bash
cd W:\Scripts\Chronicle
git add -A
git commit -m "feat(sync): Trakt/SIMKL inbound sync — media_credits, SyncOrchestrationService, PluginTaskRunner wiring"
```

---

## Quick-reference: key file locations

| Component | Path |
|---|---|
| Plugin interface additions | `src/Chronicle.Plugins/IImportProvider.cs` |
| MediaCredit entity | `src/Chronicle.Core/Models/MediaCredit.cs` |
| DbContext | `src/Chronicle.Data/ChronicleDbContext.cs` |
| Migration | `src/Chronicle.Data/Migrations/<timestamp>_AddMediaCredits.cs` |
| Sync service interface | `src/Chronicle.Services/ISyncOrchestrationService.cs` |
| Sync service impl | `src/Chronicle.Services/SyncOrchestrationService.cs` |
| Cascade reset | `src/Chronicle.Services/MetadataEnrichmentService.cs` |
| Task runner wiring | `src/Chronicle.Services/PluginTaskRunner.cs` |
| API controller | `src/Chronicle.API/Controllers/SyncController.cs` |
| Unit tests | `tests/Chronicle.Tests.Unit/Services/SyncOrchestrationServiceTests.cs` |
| Integration tests | `tests/Chronicle.Tests.Integration/Controllers/SyncControllerTests.cs` |
| Trakt plugin (separate repo) | `W:\Scripts\Chronicle.Plugin.Trakt\` |
| SIMKL plugin (separate repo) | `W:\Scripts\Chronicle.Plugin.SIMKL\` |
