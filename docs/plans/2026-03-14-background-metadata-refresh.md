# Background Metadata Refresh Service — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A background service that cycles every 4 hours (configurable) through every library item and refreshes its metadata from every active, applicable metadata plugin, with per-plugin refresh timestamps stored in the DB and surfaced on the media detail page.

**Architecture:** A new `MetadataRefreshService : BackgroundService` hosts the timer loop and dispatches per-item, per-provider refresh calls. It reads the interval from a new `app_settings` key/value table. Refresh history is written to a new `media_item_refresh_log` table and returned with the media item DTO. The existing manual-refresh endpoint delegates to this service so all refresh paths share one code path.

**Tech Stack:** .NET 9 BackgroundService, EF Core 9 (SQLite), React 18 / TypeScript, existing PluginRegistry / IMetadataProvider plugin system.

---

## Guiding rules

- Every task ends with a commit.
- Write the failing test first for every new service method. Only write enough code to make the test pass.
- `MetadataRefreshService` is a `BackgroundService` — it cannot take scoped services in its constructor. Use `IServiceScopeFactory` and create a scope per work unit.
- Keep the existing `FileScanService.RefreshMetadataAsync` intact — the manual endpoint will delegate to the new service, but don't delete the old implementation until tests pass.

---

## Task 1: AppSetting model + migration

**Files:**
- Create: `src/Chronicle.Core/Models/AppSetting.cs`
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`
- Run: `dotnet ef migrations add AddAppSettings`

**Step 1: Create the model**

```csharp
// src/Chronicle.Core/Models/AppSetting.cs
namespace Chronicle.Core.Models;

public class AppSetting
{
    public string Key   { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
```

**Step 2: Add DbSet to DbContext**

In `ChronicleDbContext.cs`, add alongside the other DbSet declarations:
```csharp
public DbSet<AppSetting> AppSettings => Set<AppSetting>();
```

In `OnModelCreating`, configure it:
```csharp
modelBuilder.Entity<AppSetting>(e =>
{
    e.ToTable("app_settings");
    e.HasKey(s => s.Key);
    e.Property(s => s.Key).HasMaxLength(200);
    e.Property(s => s.Value).IsRequired();
    // Seed default refresh interval
    e.HasData(new AppSetting { Key = "metadata_refresh_interval_hours", Value = "4" });
});
```

**Step 3: Generate migration**

```bash
cd src/Chronicle.API
dotnet ef migrations add AddAppSettings --project ../Chronicle.Data --startup-project .
```

Expected: new migration file created in `src/Chronicle.Data/Migrations/`.

**Step 4: Verify migration SQL looks correct**

Open the generated migration file and confirm it creates `app_settings` with `Key` as PK and inserts the seed row.

**Step 5: Apply migration**

```bash
dotnet ef database update --project ../Chronicle.Data --startup-project .
```

**Step 6: Build to confirm no errors**

```bash
cd src/Chronicle.API && dotnet build
```

Expected: Build succeeded, 0 error(s).

**Step 7: Commit**

```bash
git add src/Chronicle.Core/Models/AppSetting.cs src/Chronicle.Data/ChronicleDbContext.cs src/Chronicle.Data/Migrations/
git commit -m "feat(db): add app_settings table with configurable metadata refresh interval"
```

---

## Task 2: MediaItemRefreshLog model + migration

**Files:**
- Create: `src/Chronicle.Core/Models/MediaItemRefreshLog.cs`
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`
- Run: `dotnet ef migrations add AddMediaItemRefreshLog`

**Step 1: Create the model**

```csharp
// src/Chronicle.Core/Models/MediaItemRefreshLog.cs
namespace Chronicle.Core.Models;

public class MediaItemRefreshLog
{
    public int      Id           { get; set; }
    public int      MediaItemId  { get; set; }
    public string   ProviderName { get; set; } = string.Empty;
    public DateTime RefreshedAt  { get; set; }
    public bool     Succeeded    { get; set; }
    public string?  ErrorMessage { get; set; }

    // Navigation
    public MediaItem? MediaItem { get; set; }
}
```

**Step 2: Add DbSet and configure**

In `ChronicleDbContext.cs`:
```csharp
public DbSet<MediaItemRefreshLog> MediaItemRefreshLogs => Set<MediaItemRefreshLog>();
```

In `OnModelCreating`:
```csharp
modelBuilder.Entity<MediaItemRefreshLog>(e =>
{
    e.ToTable("media_item_refresh_log");
    e.HasKey(l => l.Id);
    e.Property(l => l.ProviderName).HasMaxLength(200).IsRequired();
    e.HasIndex(l => new { l.MediaItemId, l.ProviderName });
    e.HasOne(l => l.MediaItem)
     .WithMany()
     .HasForeignKey(l => l.MediaItemId)
     .OnDelete(DeleteBehavior.Cascade);
});
```

Also add navigation collection to `MediaItem.cs`:
```csharp
public ICollection<MediaItemRefreshLog> RefreshLogs { get; set; } = new List<MediaItemRefreshLog>();
```

**Step 3: Generate and apply migration**

```bash
cd src/Chronicle.API
dotnet ef migrations add AddMediaItemRefreshLog --project ../Chronicle.Data --startup-project .
dotnet ef database update --project ../Chronicle.Data --startup-project .
```

**Step 4: Build**

```bash
dotnet build
```

**Step 5: Commit**

```bash
git add src/Chronicle.Core/Models/MediaItemRefreshLog.cs src/Chronicle.Core/Models/MediaItem.cs src/Chronicle.Data/ChronicleDbContext.cs src/Chronicle.Data/Migrations/
git commit -m "feat(db): add media_item_refresh_log table for per-plugin refresh timestamps"
```

---

## Task 3: IMetadataRefreshService interface

**Files:**
- Create: `src/Chronicle.Services/IMetadataRefreshService.cs`

**Step 1: Write the interface**

```csharp
// src/Chronicle.Services/IMetadataRefreshService.cs
using Chronicle.Core.Models;

namespace Chronicle.Services;

public interface IMetadataRefreshService
{
    /// <summary>
    /// Refreshes metadata for a single item from every active, applicable provider.
    /// Writes results to media_item_refresh_log.
    /// </summary>
    Task RefreshItemAsync(int mediaItemId, CancellationToken ct = default);

    /// <summary>
    /// Runs a full library refresh pass: all root items, all active providers.
    /// Called by the background timer and exposed for manual trigger via API.
    /// </summary>
    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Returns the most-recent refresh log entry per provider for the given item.</summary>
    Task<IReadOnlyList<MediaItemRefreshLog>> GetRefreshLogsAsync(int mediaItemId, CancellationToken ct = default);
}
```

**Step 2: Build**

```bash
cd src/Chronicle.API && dotnet build
```

**Step 3: Commit**

```bash
git add src/Chronicle.Services/IMetadataRefreshService.cs
git commit -m "feat(services): add IMetadataRefreshService interface"
```

---

## Task 4: MetadataRefreshService — unit tests (write first)

**Files:**
- Create: `tests/Chronicle.Tests.Unit/Services/MetadataRefreshServiceTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Chronicle.Tests.Unit/Services/MetadataRefreshServiceTests.cs
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class MetadataRefreshServiceTests
{
    private static ChronicleDbContext CreateDb(string name)
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        var db = new ChronicleDbContext(opts);

        // Required seed: media type
        db.MediaTypes.Add(new MediaType
        {
            Id = 1, Name = "Movies", DisplayName = "Movies",
            HierarchyLevels = 1, InteractionVerb = "watch", ProgressUnit = "minutes"
        });
        // Required seed: app setting for interval
        db.AppSettings.Add(new AppSetting
        {
            Key = "metadata_refresh_interval_hours", Value = "4"
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task RefreshItemAsync_CallsProviderAndWritesLog()
    {
        // Arrange
        var db = CreateDb("refresh_writes_log");
        var item = new MediaItem
        {
            Id = 1, MediaTypeId = 1, Name = "Fight Club", Year = 1999,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        db.MediaExternalIds.Add(new MediaExternalId
            { MediaItemId = 1, Source = "tmdb", ExternalId = "movie:550" });
        await db.SaveChangesAsync();

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.tmdb");
        mockProvider.Setup(p => p.Name).Returns("TMDB");
        mockProvider.Setup(p => p.GetSupportedMediaTypes()).Returns(
            [new MediaTypeSupport { MediaTypeName = "Movies" }]);
        mockProvider.Setup(p => p.GetByIdAsync("movie:550", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata
            {
                Title = "Fight Club", Year = 1999, ExternalId = "movie:550",
                Results = []
            });

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviders()).Returns([mockProvider.Object]);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(registry.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var svc = new MetadataRefreshService(scopeFactory);

        // Act
        await svc.RefreshItemAsync(1);

        // Assert
        var logs = await db.MediaItemRefreshLogs
            .Where(l => l.MediaItemId == 1)
            .ToListAsync();
        logs.Should().HaveCount(1);
        logs[0].ProviderName.Should().Be("TMDB");
        logs[0].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshItemAsync_SkipsProviderThatDoesNotSupportMediaType()
    {
        // Arrange
        var db = CreateDb("refresh_skips_wrong_type");
        var item = new MediaItem
        {
            Id = 1, MediaTypeId = 1, Name = "Fight Club", Year = 1999,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var musicProvider = new Mock<IMetadataProvider>();
        musicProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.lastfm");
        musicProvider.Setup(p => p.Name).Returns("LastFM");
        musicProvider.Setup(p => p.GetSupportedMediaTypes()).Returns(
            [new MediaTypeSupport { MediaTypeName = "Music" }]);

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviders()).Returns([musicProvider.Object]);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(registry.Object);
        var sp = services.BuildServiceProvider();
        var svc = new MetadataRefreshService(sp.GetRequiredService<IServiceScopeFactory>());

        // Act
        await svc.RefreshItemAsync(1);

        // Assert: no log written because no provider was applicable
        var logs = await db.MediaItemRefreshLogs.Where(l => l.MediaItemId == 1).ToListAsync();
        logs.Should().BeEmpty();
        musicProvider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshItemAsync_SearchesByNameWhenNoExternalId()
    {
        // Arrange
        var db = CreateDb("refresh_searches_by_name");
        var item = new MediaItem
        {
            Id = 1, MediaTypeId = 1, Name = "Fight Club", Year = 1999,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.tmdb");
        mockProvider.Setup(p => p.Name).Returns("TMDB");
        mockProvider.Setup(p => p.GetSupportedMediaTypes()).Returns(
            [new MediaTypeSupport { MediaTypeName = "Movies" }]);
        mockProvider.Setup(p => p.SearchAsync("Fight Club", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata
            {
                ExternalId = "movie:550",
                Results = [new MediaSearchResult { ExternalId = "movie:550", Title = "Fight Club", Year = 1999 }]
            });
        mockProvider.Setup(p => p.GetByIdAsync("movie:550", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata
            {
                Title = "Fight Club", Year = 1999, ExternalId = "movie:550", Results = []
            });

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviders()).Returns([mockProvider.Object]);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(registry.Object);
        var sp = services.BuildServiceProvider();
        var svc = new MetadataRefreshService(sp.GetRequiredService<IServiceScopeFactory>());

        // Act
        await svc.RefreshItemAsync(1);

        // Assert: provider was searched then fetched, external ID stored
        mockProvider.Verify(p => p.SearchAsync("Fight Club", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mockProvider.Verify(p => p.GetByIdAsync("movie:550", It.IsAny<CancellationToken>()), Times.Once);
        var ext = await db.MediaExternalIds.FirstOrDefaultAsync(e => e.MediaItemId == 1);
        ext.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRefreshLogsAsync_ReturnsLatestPerProvider()
    {
        // Arrange
        var db = CreateDb("refresh_get_logs");
        var item = new MediaItem
        {
            Id = 1, MediaTypeId = 1, Name = "Test", Year = 2024,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        db.MediaItemRefreshLogs.AddRange(
            new MediaItemRefreshLog { MediaItemId = 1, ProviderName = "TMDB", RefreshedAt = DateTime.UtcNow.AddHours(-2), Succeeded = true },
            new MediaItemRefreshLog { MediaItemId = 1, ProviderName = "TMDB", RefreshedAt = DateTime.UtcNow.AddHours(-1), Succeeded = true },
            new MediaItemRefreshLog { MediaItemId = 1, ProviderName = "LastFM", RefreshedAt = DateTime.UtcNow.AddHours(-3), Succeeded = false, ErrorMessage = "Not found" }
        );
        await db.SaveChangesAsync();

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviders()).Returns([]);
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(registry.Object);
        var sp = services.BuildServiceProvider();
        var svc = new MetadataRefreshService(sp.GetRequiredService<IServiceScopeFactory>());

        // Act
        var logs = await svc.GetRefreshLogsAsync(1);

        // Assert: one entry per provider (the most recent)
        logs.Should().HaveCount(2);
        logs.Single(l => l.ProviderName == "TMDB").RefreshedAt.Should()
            .BeCloseTo(DateTime.UtcNow.AddHours(-1), TimeSpan.FromSeconds(5));
    }
}
```

**Step 2: Run tests — expect compilation failure (MetadataRefreshService doesn't exist yet)**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "MetadataRefreshServiceTests" -v n 2>&1 | tail -20
```

Expected: build error — `MetadataRefreshService` not found. That's correct.

**Step 3: Commit failing tests**

```bash
git add tests/Chronicle.Tests.Unit/Services/MetadataRefreshServiceTests.cs
git commit -m "test(services): add failing tests for MetadataRefreshService"
```

---

## Task 5: MetadataRefreshService implementation

**Files:**
- Create: `src/Chronicle.Services/MetadataRefreshService.cs`

**Step 1: Implement the service**

```csharp
// src/Chronicle.Services/MetadataRefreshService.cs
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Chronicle.Services;

/// <summary>
/// Hosted background service that periodically refreshes metadata for all
/// library items using every active, applicable IMetadataProvider plugin.
/// </summary>
public sealed class MetadataRefreshService : BackgroundService, IMetadataRefreshService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _log = Log.ForContext<MetadataRefreshService>();

    private static readonly TimeSpan StartupDelay  = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);

    public MetadataRefreshService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Information("MetadataRefreshService starting (startup delay {Delay}s)", StartupDelay.TotalSeconds);
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _log.Information("MetadataRefreshService: starting full library refresh pass");
            try
            {
                await RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "MetadataRefreshService: unhandled error in refresh pass");
            }

            var interval = await GetIntervalAsync(stoppingToken);
            _log.Information("MetadataRefreshService: next pass in {Hours}h", interval.TotalHours);
            await Task.Delay(interval, stoppingToken);
        }
    }

    // ── IMetadataRefreshService ───────────────────────────────────────────────

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        // Only root items (HierarchyLevel == 0) that are in at least one library
        var itemIds = await db.UserLibraries
            .Select(ul => ul.MediaItemId)
            .Distinct()
            .ToListAsync(ct);

        var rootItems = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .Where(m => itemIds.Contains(m.Id) && m.HierarchyLevel == 0)
            .ToListAsync(ct);

        _log.Information("MetadataRefreshService: {Count} root items to refresh", rootItems.Count);

        foreach (var item in rootItems)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await RefreshItemCoreAsync(db, registry, item, ct);
                await Task.Delay(500, ct); // Rate limiting between items
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "MetadataRefreshService: error refreshing item {Id} '{Name}'", item.Id, item.Name);
            }
        }
    }

    public async Task RefreshItemAsync(int mediaItemId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var item = await db.MediaItems
            .Include(m => m.MediaType)
            .Include(m => m.ExternalIds)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);

        if (item is null)
        {
            _log.Warning("MetadataRefreshService: item {Id} not found", mediaItemId);
            return;
        }

        await RefreshItemCoreAsync(db, registry, item, ct);
    }

    public async Task<IReadOnlyList<MediaItemRefreshLog>> GetRefreshLogsAsync(
        int mediaItemId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Return the most recent log entry per provider
        var all = await db.MediaItemRefreshLogs
            .Where(l => l.MediaItemId == mediaItemId)
            .OrderByDescending(l => l.RefreshedAt)
            .ToListAsync(ct);

        return all
            .GroupBy(l => l.ProviderName)
            .Select(g => g.First())
            .ToList();
    }

    // ── Core refresh logic ────────────────────────────────────────────────────

    private async Task RefreshItemCoreAsync(
        ChronicleDbContext db,
        IPluginRegistry registry,
        MediaItem item,
        CancellationToken ct)
    {
        var providers = registry.GetMetadataProviders();
        var mediaTypeName = item.MediaType?.Name ?? string.Empty;

        foreach (var provider in providers)
        {
            var supported = provider.GetSupportedMediaTypes()
                .Any(m => string.Equals(m.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));

            if (!supported)
            {
                _log.Debug("MetadataRefreshService: provider {Provider} does not support '{Type}', skipping item {Id}",
                    provider.Name, mediaTypeName, item.Id);
                continue;
            }

            var log = new MediaItemRefreshLog
            {
                MediaItemId  = item.Id,
                ProviderName = provider.Name,
                RefreshedAt  = DateTime.UtcNow,
                Succeeded    = false
            };

            try
            {
                // Resolve external ID for this provider
                var extId = item.ExternalIds
                    .FirstOrDefault(e => string.Equals(e.Source, provider.PluginId, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(e.Source, "tmdb", StringComparison.OrdinalIgnoreCase))
                    ?.ExternalId;

                if (extId is null)
                {
                    // Search by name
                    var hint = ToMediaTypeHint(mediaTypeName);
                    var searchResult = await provider.SearchAsync(item.Name, hint, ct);
                    var best = searchResult.Results
                        .Select(r => new { r, Score = ScoreByNameYear(r.Title, r.Year, item.Name, item.Year) })
                        .OrderByDescending(x => x.Score)
                        .FirstOrDefault();

                    if (best is null)
                    {
                        _log.Information("MetadataRefreshService: no match from {Provider} for '{Name}'", provider.Name, item.Name);
                        log.ErrorMessage = "No search results matched";
                        db.MediaItemRefreshLogs.Add(log);
                        await db.SaveChangesAsync(ct);
                        continue;
                    }

                    extId = best.r.ExternalId;
                    await UpsertExternalIdAsync(db, item.Id, provider.PluginId, extId, ct);
                    // Reload to pick up new external ID
                    item.ExternalIds = await db.MediaExternalIds
                        .Where(e => e.MediaItemId == item.Id)
                        .ToListAsync(ct);
                }

                var meta = await provider.GetByIdAsync(extId, ct);

                // Update top-level fields only if this is a primary provider (has poster/title/etc)
                if (!string.IsNullOrWhiteSpace(meta.Title))
                    item.Name = meta.Title;
                if (meta.Year.HasValue)
                    item.Year = meta.Year;
                if (!string.IsNullOrWhiteSpace(meta.Overview))
                    item.Overview = meta.Overview;
                if (!string.IsNullOrWhiteSpace(meta.PosterUrl))
                    item.PosterUrl = meta.PosterUrl;
                if (meta.RuntimeMinutes.HasValue)
                    item.RuntimeMinutes = meta.RuntimeMinutes;

                // Merge into MetadataJson under the provider's namespace key
                item.MetadataJson = MergeMetadataJson(item.MetadataJson, provider.PluginId, meta);
                item.UpdatedAt = DateTime.UtcNow;

                log.Succeeded = true;
                _log.Information("MetadataRefreshService: refreshed '{Name}' via {Provider}", item.Name, provider.Name);
            }
            catch (Exception ex)
            {
                log.ErrorMessage = ex.Message;
                _log.Warning(ex, "MetadataRefreshService: {Provider} failed for item {Id}", provider.Name, item.Id);
            }
            finally
            {
                db.MediaItemRefreshLogs.Add(log);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<TimeSpan> GetIntervalAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var setting = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == "metadata_refresh_interval_hours", ct);
            if (setting is not null && double.TryParse(setting.Value, out var hours) && hours > 0)
                return TimeSpan.FromHours(hours);
        }
        catch { /* fall back to default */ }
        return DefaultInterval;
    }

    private static async Task UpsertExternalIdAsync(
        ChronicleDbContext db, int mediaItemId, string source, string externalId, CancellationToken ct)
    {
        var existing = await db.MediaExternalIds
            .FirstOrDefaultAsync(e => e.MediaItemId == mediaItemId && e.Source == source, ct);
        if (existing is null)
        {
            db.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = mediaItemId,
                Source      = source,
                ExternalId  = externalId
            });
        }
        else
        {
            existing.ExternalId = externalId;
        }
        await db.SaveChangesAsync(ct);
    }

    private static string MergeMetadataJson(string? existingJson, string pluginId, Chronicle.Plugins.Models.MediaMetadata meta)
    {
        var root = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(existingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson);
                if (parsed is not null)
                    foreach (var kv in parsed)
                        root[kv.Key] = kv.Value;
            }
            catch { /* discard unparseable existing JSON */ }
        }

        // Use a short namespace key: "tmdb", "lastfm", etc. derived from plugin ID suffix
        var ns = pluginId.Contains('.') ? pluginId.Split('.').Last() : pluginId;

        root[ns] = new
        {
            rating       = meta.Rating,
            genres       = meta.Genres,
            cast         = meta.Cast,
            directors    = meta.Directors,
            posterUrl    = meta.PosterUrl,
            backdropUrl  = meta.BackdropUrl,
            overview     = meta.Overview
        };

        return JsonSerializer.Serialize(root);
    }

    private static string ToMediaTypeHint(string mediaTypeName) =>
        mediaTypeName.ToLowerInvariant() switch
        {
            "movies" or "movie" => "movie",
            "tv" or "tv shows"  => "tv",
            "music"             => "music",
            _                   => mediaTypeName.ToLowerInvariant()
        };

    private static int ScoreByNameYear(string? candidateTitle, int? candidateYear,
        string itemName, int? itemYear)
    {
        int score = 0;
        if (string.Equals(candidateTitle, itemName, StringComparison.OrdinalIgnoreCase))
            score += 60;
        else if (candidateTitle?.Contains(itemName, StringComparison.OrdinalIgnoreCase) == true)
            score += 30;
        if (itemYear.HasValue && candidateYear == itemYear)
            score += 40;
        return score;
    }
}
```

**Step 2: Run the failing tests**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "MetadataRefreshServiceTests" -v n 2>&1 | tail -30
```

Expected: tests pass. If any fail, read the error carefully and fix only the failing code.

**Step 3: Run full unit test suite**

```bash
dotnet test tests/Chronicle.Tests.Unit -v n
```

Expected: all previously passing tests still pass.

**Step 4: Commit**

```bash
git add src/Chronicle.Services/MetadataRefreshService.cs
git commit -m "feat(services): implement MetadataRefreshService background refresh across all active plugins"
```

---

## Task 6: Register service in DI + wire MediaController

**Files:**
- Modify: `src/Chronicle.API/Program.cs`
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`

**Step 1: Register in Program.cs**

Find the hosted-service registration block and add:
```csharp
// MetadataRefreshService is both a BackgroundService (timer) and IMetadataRefreshService (injectable)
builder.Services.AddSingleton<MetadataRefreshService>();
builder.Services.AddSingleton<IMetadataRefreshService>(sp => sp.GetRequiredService<MetadataRefreshService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetadataRefreshService>());
```

Also ensure `ChronicleDbContext` is registered (it's `AddDbContext<>`, not `AddScoped` directly), so it's scope-safe. The existing registration is fine.

**Step 2: Update MediaController to inject IMetadataRefreshService**

In `MediaController.cs`, add the constructor parameter:
```csharp
private readonly IMetadataRefreshService _refreshService;

public MediaController(IMediaService mediaService, IFileScanService fileScanService,
    IMetadataRefreshService refreshService)
{
    _mediaService    = mediaService;
    _fileScanService = fileScanService;
    _refreshService  = refreshService;
}
```

Replace the body of `RefreshMetadata`:
```csharp
[HttpPost("{id:int}/refresh")]
[Authorize]
public async Task<IActionResult> RefreshMetadata(int id, CancellationToken ct)
{
    try
    {
        await _refreshService.RefreshItemAsync(id, ct);
        var item = await _mediaService.GetByIdAsync(id);
        if (item is null) return NotFound();
        return Ok(ApiResponse<MediaItemDto>.Ok(MapToDto(item)));
    }
    catch (Exception ex) when (ex.Message.Contains("No metadata provider"))
    {
        return BadRequest(ApiResponse<object>.Fail("NO_PROVIDER", ex.Message));
    }
}
```

**Step 3: Build**

```bash
cd src/Chronicle.API && dotnet build
```

**Step 4: Run integration tests**

```bash
dotnet test tests/Chronicle.Tests.Integration -v n
```

Expected: all passing.

**Step 5: Commit**

```bash
git add src/Chronicle.API/Program.cs src/Chronicle.API/Controllers/MediaController.cs
git commit -m "feat(api): wire MetadataRefreshService into DI and manual refresh endpoint"
```

---

## Task 7: Settings endpoint for refresh interval

**Files:**
- Modify: `src/Chronicle.API/Controllers/SettingsController.cs`

**Step 1: Add GET/PUT app-settings endpoints**

Add to `SettingsController`:
```csharp
private readonly ChronicleDbContext _db;

public SettingsController(ChronicleDbContext db)
{
    _db = db;
}

/// <summary>Returns all app settings as a key/value dictionary.</summary>
[HttpGet("app")]
public async Task<IActionResult> GetAppSettings()
{
    var settings = await _db.AppSettings.ToListAsync();
    var dict = settings.ToDictionary(s => s.Key, s => s.Value);
    return Ok(dict);
}

/// <summary>Updates a single app setting.</summary>
[HttpPut("app/{key}")]
[Authorize(Roles = "Admin")]
public async Task<IActionResult> PutAppSetting(string key, [FromBody] AppSettingUpdateRequest body)
{
    var setting = await _db.AppSettings.FindAsync(key);
    if (setting is null)
    {
        _db.AppSettings.Add(new AppSetting { Key = key, Value = body.Value });
    }
    else
    {
        setting.Value = body.Value;
    }
    await _db.SaveChangesAsync();
    return NoContent();
}
```

Add the request DTO in `SettingsController.cs` (below the file):
```csharp
public record AppSettingUpdateRequest([Required] string Value);
```

Add the required usings at the top:
```csharp
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
```

**Step 2: Build and test**

```bash
cd src/Chronicle.API && dotnet build
dotnet test tests/Chronicle.Tests.Integration -v n
```

**Step 3: Commit**

```bash
git add src/Chronicle.API/Controllers/SettingsController.cs
git commit -m "feat(api): add GET/PUT /api/v1/settings/app for configurable app settings"
```

---

## Task 8: Add refresh logs to MediaItemDto

**Files:**
- Modify: `src/Chronicle.API/DTOs/MediaDtos.cs`
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`

**Step 1: Add RefreshLogDto and update MediaItemDto**

In `MediaDtos.cs`:
```csharp
public record RefreshLogDto(string ProviderName, DateTime RefreshedAt, bool Succeeded, string? ErrorMessage);
```

Add `List<RefreshLogDto>? RefreshLogs = null` as an optional parameter to `MediaItemDto`:
```csharp
public record MediaItemDto(
    int Id,
    // ... all existing parameters ...
    TmdbMetaDto? TmdbMeta = null,
    FileScannerMetaDto? FileScannerMeta = null,
    List<RefreshLogDto>? RefreshLogs = null   // NEW
);
```

**Step 2: Update MediaController.GetById to include refresh logs**

In `MediaController.cs`, update `GetById`:
```csharp
[HttpGet("{id:int}")]
[Authorize]
public async Task<IActionResult> GetById(int id, CancellationToken ct)
{
    var item = await _mediaService.GetByIdAsync(id);
    if (item is null) return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

    var refreshLogs = await _refreshService.GetRefreshLogsAsync(id, ct);
    var dto = MapToDto(item, refreshLogs);
    return Ok(ApiResponse<MediaItemDto>.Ok(dto));
}
```

Update `MapToDto` to accept and map logs:
```csharp
private static MediaItemDto MapToDto(MediaItem m, IReadOnlyList<MediaItemRefreshLog>? refreshLogs = null)
{
    // ... existing parsing ...
    var logDtos = refreshLogs?
        .Select(l => new RefreshLogDto(l.ProviderName, l.RefreshedAt, l.Succeeded, l.ErrorMessage))
        .ToList();

    return new MediaItemDto(
        // ... existing fields ...
        TmdbMeta: tmdb,
        FileScannerMeta: fs,
        RefreshLogs: logDtos
    );
}
```

**Step 3: Build**

```bash
cd src/Chronicle.API && dotnet build
```

**Step 4: Commit**

```bash
git add src/Chronicle.API/DTOs/MediaDtos.cs src/Chronicle.API/Controllers/MediaController.cs
git commit -m "feat(api): include per-plugin refresh timestamps in MediaItemDto"
```

---

## Task 9: Frontend — show last-refreshed timestamps in metadata boxes

**Files:**
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css`
- Modify: `src/Chronicle.Web/src/api/media.ts` (add `refreshLogs` to the type)

**Step 1: Update the API type**

In `media.ts` (or wherever `MediaItem` is typed), add:
```ts
export interface RefreshLog {
  providerName: string
  refreshedAt: string   // ISO datetime string
  succeeded: boolean
  errorMessage?: string
}

// Add to MediaItem interface:
refreshLogs?: RefreshLog[]
```

**Step 2: Show timestamp in TMDB box header**

In `MediaDetailPage.tsx`, find the `metadataBoxHeader` div inside the TMDB box and add below the brand/actions row:

```tsx
{(() => {
  const tmdbLog = item.refreshLogs?.find(l => l.providerName === 'TMDB')
  if (!tmdbLog) return null
  const dt = new Date(tmdbLog.refreshedAt)
  const label = tmdbLog.succeeded
    ? `Last refreshed ${dt.toLocaleDateString()} ${dt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`
    : `Last refresh failed: ${tmdbLog.errorMessage ?? 'unknown error'}`
  return <p className={styles.refreshTimestamp}>{label}</p>
})()}
```

**Step 3: Add the CSS class**

In `MediaDetailPage.module.css`:
```css
.refreshTimestamp {
  font-size: 11px;
  color: var(--text-muted);
  margin: 4px 0 0;
}
```

**Step 4: Build frontend**

```bash
cd src/Chronicle.Web && npm run type-check && npm run build
```

Expected: no type errors, build succeeds.

**Step 5: Commit**

```bash
git add src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx src/Chronicle.Web/src/pages/media/MediaDetailPage.module.css src/Chronicle.Web/src/api/media.ts
git commit -m "feat(ui): show per-plugin last-refresh timestamp in metadata provider boxes"
```

---

## Task 10: Final integration test pass + PR prep

**Step 1: Run all tests**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: all passing.

**Step 2: Manual smoke test**
1. Start API (`dotnet run`) and frontend (`npm run dev`)
2. Navigate to a media item with TMDB metadata
3. Verify "Last refreshed [date]" appears in the TMDB box header
4. Click the ↻ Refresh button — verify timestamp updates
5. Check `GET /api/v1/settings/app` returns `{"metadata_refresh_interval_hours":"4"}`

**Step 3: Update BACKLOG.md — move item to Completed**

In `BACKLOG.md` under `## Completed (recent)`, add:
```
- Background metadata refresh v2: MetadataRefreshService runs every 4h (configurable via app_settings), cycles all library items × all active metadata plugins, writes per-plugin timestamps to media_item_refresh_log, surfaces last-refresh date in each provider's metadata box
```

**Step 4: Final commit and push**

```bash
git add BACKLOG.md
git commit -m "docs(backlog): mark background metadata refresh as completed"
git push
```
