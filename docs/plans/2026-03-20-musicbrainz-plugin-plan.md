# MusicBrainz Plugin + Generic Metadata Enrichment Infrastructure — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a standalone MusicBrainz metadata plugin and generic per-item/per-plugin enrichment infrastructure that works for all current and future metadata providers.

**Architecture:** A new `media_item_enrichment_status` table tracks enrichment state per media item per plugin (pending/completed/failed/exhausted/not_found/skipped with retry counting). A generic `MetadataEnrichmentService` in Chronicle.Services drives the background loop for any registered `IMetadataProvider`. The MusicBrainz plugin lives at `W:\Scripts\Chronicle.Plugin.MusicBrainz` as a standalone project with internal rate limiting (1 req/s anon, 5 req/s authenticated) and fetches every available field from the MusicBrainz API and Cover Art Archive.

**Tech Stack:** .NET 9, EF Core 9 (SQLite migrations), xUnit + FluentAssertions + Moq, MusicBrainz REST API v2 (JSON), Cover Art Archive API, System.Text.Json, Serilog

**Design doc:** `docs/plans/2026-03-20-musicbrainz-plugin-design.md`

---

## Task 1: EnrichmentStatus enum + MediaItemEnrichmentStatus model

**Files:**
- Create: `src/Chronicle.Core/Models/EnrichmentStatus.cs`
- Create: `src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs`

**Step 1: Create the enum**

```csharp
// src/Chronicle.Core/Models/EnrichmentStatus.cs
namespace Chronicle.Core.Models;

public enum EnrichmentStatus
{
    Pending,
    Completed,
    Failed,
    Exhausted,
    NotFound,
    Skipped
}
```

**Step 2: Create the domain model**

```csharp
// src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs
namespace Chronicle.Core.Models;

public class MediaItemEnrichmentStatus
{
    public int Id { get; set; }
    public int MediaItemId { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public EnrichmentStatus Status { get; set; } = EnrichmentStatus.Pending;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public DateTime? LastAttemptedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public MediaItem? MediaItem { get; set; }
}
```

**Step 3: Commit**

```bash
git add src/Chronicle.Core/Models/EnrichmentStatus.cs src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs
git commit -m "feat(core): add MediaItemEnrichmentStatus model and EnrichmentStatus enum"
```

---

## Task 2: DbContext + EF Core migration

**Files:**
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`
- Create: migration via `dotnet ef migrations add`

**Step 1: Add DbSet to ChronicleDbContext**

In `ChronicleDbContext.cs`, add after the existing DbSets:

```csharp
public DbSet<MediaItemEnrichmentStatus> EnrichmentStatuses { get; set; }
```

In `OnModelCreating`, add:

```csharp
modelBuilder.Entity<MediaItemEnrichmentStatus>(e =>
{
    e.ToTable("media_item_enrichment_status");
    e.HasKey(x => x.Id);
    e.HasIndex(x => new { x.MediaItemId, x.PluginId }).IsUnique();
    e.Property(x => x.Status).HasConversion<string>();
    e.HasOne(x => x.MediaItem)
     .WithMany()
     .HasForeignKey(x => x.MediaItemId)
     .OnDelete(DeleteBehavior.Cascade);
});
```

**Step 2: Create migration**

```bash
cd src/Chronicle.API
dotnet ef migrations add AddEnrichmentStatus --project ../Chronicle.Data/Chronicle.Data.csproj
```

Expected: new migration file in `src/Chronicle.Data/Migrations/`

**Step 3: Verify migration looks correct**

Open the generated migration file. It should create `media_item_enrichment_status` table with all columns and a unique index on `(media_item_id, plugin_id)`.

**Step 4: Apply migration locally**

```bash
dotnet ef database update --project ../Chronicle.Data/Chronicle.Data.csproj
```

**Step 5: Write unit test to verify DbContext wires up correctly**

```csharp
// tests/Chronicle.Tests.Unit/Data/EnrichmentStatusDbTests.cs
public class EnrichmentStatusDbTests : IDisposable
{
    private readonly ChronicleDbContext _db;

    public EnrichmentStatusDbTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ChronicleDbContext(opts);
    }

    [Fact]
    public async Task CanInsertAndRetrieveEnrichmentStatus()
    {
        var user = new User { Username = "u", PasswordHash = "h", Email = "e@e.com" };
        _db.Users.Add(user);
        var mediaType = new MediaType { Name = "music", DisplayName = "Music", HierarchyLevels = 3, HierarchyLabels = "Artist,Album,Track", InteractionVerb = "listened", ProgressUnit = "tracks" };
        _db.MediaTypes.Add(mediaType);
        await _db.SaveChangesAsync();

        var item = new MediaItem { Title = "Radiohead", MediaTypeId = mediaType.Id, HierarchyLevel = 0, AddedByUserId = user.Id };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var status = new MediaItemEnrichmentStatus
        {
            MediaItemId = item.Id,
            PluginId = "chronicle.plugin.musicbrainz",
            Status = EnrichmentStatus.Pending,
            MaxRetries = 3
        };
        _db.EnrichmentStatuses.Add(status);
        await _db.SaveChangesAsync();

        var retrieved = await _db.EnrichmentStatuses.FirstAsync(x => x.MediaItemId == item.Id);
        retrieved.Status.Should().Be(EnrichmentStatus.Pending);
        retrieved.PluginId.Should().Be("chronicle.plugin.musicbrainz");
    }

    public void Dispose() => _db.Dispose();
}
```

**Step 6: Run test**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "EnrichmentStatusDbTests" -v
```

Expected: PASS

**Step 7: Commit**

```bash
git add src/Chronicle.Data/ tests/Chronicle.Tests.Unit/Data/EnrichmentStatusDbTests.cs
git commit -m "feat(data): add media_item_enrichment_status table + EF migration"
```

---

## Task 3: IMetadataEnrichmentService interface + service registration

**Files:**
- Create: `src/Chronicle.Services/IMetadataEnrichmentService.cs`
- Modify: `src/Chronicle.API/Program.cs`

**Step 1: Create the interface**

```csharp
// src/Chronicle.Services/IMetadataEnrichmentService.cs
namespace Chronicle.Services;

public enum ResetScope { Single, AllExhausted, AllForPlugin }

public interface IMetadataEnrichmentService
{
    /// <summary>Run enrichment for all pending/retryable items for a specific plugin.</summary>
    Task EnrichPendingAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Run enrichment for all registered plugins.</summary>
    Task EnrichAllAsync(CancellationToken ct = default);

    /// <summary>Reset enrichment status rows.</summary>
    Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null, CancellationToken ct = default);

    /// <summary>Mark a specific item as skipped for a plugin.</summary>
    Task SkipAsync(int mediaItemId, string pluginId, CancellationToken ct = default);

    /// <summary>Get enrichment statistics per plugin.</summary>
    Task<IReadOnlyList<EnrichmentStats>> GetStatsAsync(CancellationToken ct = default);
}

public record EnrichmentStats(
    string PluginId,
    int Pending,
    int Completed,
    int Failed,
    int Exhausted,
    int NotFound,
    int Skipped
);
```

**Step 2: Register in Program.cs**

In `src/Chronicle.API/Program.cs`, add alongside other service registrations:

```csharp
builder.Services.AddScoped<IMetadataEnrichmentService, MetadataEnrichmentService>();
builder.Services.AddSingleton<IScheduledTask, MetadataEnrichmentScheduledTask>();
```

**Step 3: Commit**

```bash
git add src/Chronicle.Services/IMetadataEnrichmentService.cs src/Chronicle.API/Program.cs
git commit -m "feat(services): add IMetadataEnrichmentService interface"
```

---

## Task 4: MetadataEnrichmentService implementation

**Files:**
- Create: `src/Chronicle.Services/MetadataEnrichmentService.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`

**Step 1: Write failing tests first**

```csharp
// tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs
public class MetadataEnrichmentServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly Mock<IPluginRegistry> _registry;
    private readonly MetadataEnrichmentService _svc;

    public MetadataEnrichmentServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ChronicleDbContext(opts);
        _registry = new Mock<IPluginRegistry>();
        // MetadataEnrichmentService takes IServiceScopeFactory, so mock it
        var scopeFactory = CreateScopeFactory(_db, _registry.Object);
        _svc = new MetadataEnrichmentService(scopeFactory, Mock.Of<ILogger<MetadataEnrichmentService>>());
    }

    [Fact]
    public async Task EnrichPendingAsync_CallsGetByIdWhenExternalIdKnown()
    {
        // Arrange: seed item with known external ID
        var (item, status) = await SeedItemWithStatus("artist:abc-123", EnrichmentStatus.Pending);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetByIdAsync("artist:abc-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Radiohead", ExternalId = "artist:abc-123" });
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        // Act
        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        // Assert
        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Completed);
        updated.LastCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EnrichPendingAsync_IncrementsRetryCountOnFailure()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Pending);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        mockProvider.Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Failed);
        updated.RetryCount.Should().Be(1);
        updated.ErrorMessage.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task EnrichPendingAsync_SetsExhaustedWhenRetriesExceeded()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Failed, retryCount: 2, maxRetries: 3);

        var mockProvider = new Mock<IMetadataProvider>();
        mockProvider.Setup(p => p.PluginId).Returns("chronicle.plugin.musicbrainz");
        mockProvider.Setup(p => p.GetSupportedMediaTypes())
            .Returns([new MediaTypeSupport { MediaTypeName = "music" }]);
        mockProvider.Setup(p => p.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Still broken"));
        _registry.Setup(r => r.GetMetadataProvider("chronicle.plugin.musicbrainz"))
            .Returns(mockProvider.Object);

        await _svc.EnrichPendingAsync("chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Exhausted);
        updated.RetryCount.Should().Be(3);
    }

    [Fact]
    public async Task ResetAsync_Single_ResetsToPending()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Exhausted, retryCount: 3);

        await _svc.ResetAsync("chronicle.plugin.musicbrainz", ResetScope.Single, item.Id);

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Pending);
        updated.RetryCount.Should().Be(0);
        updated.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task SkipAsync_SetsSkippedStatus()
    {
        var (item, status) = await SeedItemWithStatus(null, EnrichmentStatus.Pending);

        await _svc.SkipAsync(item.Id, "chronicle.plugin.musicbrainz");

        var updated = await _db.EnrichmentStatuses.FindAsync(status.Id);
        updated!.Status.Should().Be(EnrichmentStatus.Skipped);
    }

    // helpers omitted for brevity — seed user/mediatype/item/status rows
}
```

**Step 2: Run tests to verify they fail**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "MetadataEnrichmentServiceTests" -v
```

Expected: FAIL — MetadataEnrichmentService does not exist yet

**Step 3: Implement MetadataEnrichmentService**

```csharp
// src/Chronicle.Services/MetadataEnrichmentService.cs
namespace Chronicle.Services;

public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);

    public async Task EnrichPendingAsync(string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();

        var provider = registry.GetMetadataProvider(pluginId);
        if (provider is null)
        {
            logger.LogWarning("Plugin {PluginId} not found in registry", pluginId);
            return;
        }

        var cutoff = DateTime.UtcNow - RetryWindow;
        var rows = await db.EnrichmentStatuses
            .Include(x => x.MediaItem)
            .Where(x => x.PluginId == pluginId &&
                        (x.Status == EnrichmentStatus.Pending ||
                         (x.Status == EnrichmentStatus.Failed && x.LastAttemptedAt < cutoff)))
            .ToListAsync(ct);

        logger.LogInformation("Enriching {Count} items for plugin {PluginId}", rows.Count, pluginId);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichOneAsync(db, provider, row, ct);
        }
    }

    public async Task EnrichAllAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
        var pluginIds = registry.GetAllMetadataProviders().Select(p => p.PluginId).ToList();
        foreach (var id in pluginIds)
        {
            ct.ThrowIfCancellationRequested();
            await EnrichPendingAsync(id, ct);
        }
    }

    public async Task ResetAsync(string pluginId, ResetScope scope, int? mediaItemId = null, CancellationToken ct = default)
    {
        await using var svc = scopeFactory.CreateAsyncScope();
        var db = svc.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var query = db.EnrichmentStatuses.Where(x => x.PluginId == pluginId);
        query = scope switch
        {
            ResetScope.Single       => query.Where(x => x.MediaItemId == mediaItemId),
            ResetScope.AllExhausted => query.Where(x => x.Status == EnrichmentStatus.Exhausted),
            ResetScope.AllForPlugin => query.Where(x => x.Status != EnrichmentStatus.Skipped),
            _                       => query
        };

        await query.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, EnrichmentStatus.Pending)
            .SetProperty(x => x.RetryCount, 0)
            .SetProperty(x => x.ErrorMessage, (string?)null)
            .SetProperty(x => x.LastAttemptedAt, (DateTime?)null), ct);
    }

    public async Task SkipAsync(int mediaItemId, string pluginId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        await db.EnrichmentStatuses
            .Where(x => x.MediaItemId == mediaItemId && x.PluginId == pluginId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, EnrichmentStatus.Skipped), ct);
    }

    public async Task<IReadOnlyList<EnrichmentStats>> GetStatsAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        return await db.EnrichmentStatuses
            .GroupBy(x => x.PluginId)
            .Select(g => new EnrichmentStats(
                g.Key,
                g.Count(x => x.Status == EnrichmentStatus.Pending),
                g.Count(x => x.Status == EnrichmentStatus.Completed),
                g.Count(x => x.Status == EnrichmentStatus.Failed),
                g.Count(x => x.Status == EnrichmentStatus.Exhausted),
                g.Count(x => x.Status == EnrichmentStatus.NotFound),
                g.Count(x => x.Status == EnrichmentStatus.Skipped)
            ))
            .ToListAsync(ct);
    }

    private async Task EnrichOneAsync(ChronicleDbContext db, IMetadataProvider provider,
        MediaItemEnrichmentStatus row, CancellationToken ct)
    {
        row.LastAttemptedAt = DateTime.UtcNow;
        try
        {
            MediaMetadata? result = null;
            if (!string.IsNullOrEmpty(row.ExternalId))
            {
                result = await provider.GetByIdAsync(row.ExternalId, ct);
            }
            else if (row.MediaItem is not null)
            {
                var supportedTypes = provider.GetSupportedMediaTypes().Select(t => t.MediaTypeName).ToList();
                var mediaTypeName = await db.MediaTypes
                    .Where(t => t.Id == row.MediaItem.MediaTypeId)
                    .Select(t => t.Name)
                    .FirstOrDefaultAsync(ct);
                if (mediaTypeName is not null && supportedTypes.Contains(mediaTypeName))
                    result = await provider.SearchAsync(row.MediaItem.Title, mediaTypeName, ct);
            }

            if (result is null || string.IsNullOrEmpty(result.ExternalId))
            {
                row.Status = EnrichmentStatus.NotFound;
            }
            else
            {
                row.ExternalId = result.ExternalId;
                row.Status = EnrichmentStatus.Completed;
                row.LastCompletedAt = DateTime.UtcNow;
                row.ErrorMessage = null;
                await MergeMetadataAsync(db, row.MediaItem!, provider.PluginId, result, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Enrichment failed for item {ItemId} plugin {PluginId}",
                row.MediaItemId, row.PluginId);
            row.RetryCount++;
            row.ErrorMessage = ex.Message;
            row.Status = row.RetryCount >= row.MaxRetries
                ? EnrichmentStatus.Exhausted
                : EnrichmentStatus.Failed;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task MergeMetadataAsync(ChronicleDbContext db, MediaItem item,
        string pluginId, MediaMetadata result, CancellationToken ct)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            item.MetadataJson ?? "{}") ?? [];
        existing[pluginId] = JsonSerializer.SerializeToElement(result);
        item.MetadataJson = JsonSerializer.Serialize(existing);

        if (!string.IsNullOrEmpty(result.PosterUrl) && string.IsNullOrEmpty(item.PosterUrl))
            item.PosterUrl = result.PosterUrl;

        await db.SaveChangesAsync(ct);
    }
}
```

**Step 4: Run tests**

```bash
cd tests/Chronicle.Tests.Unit
dotnet test --filter "MetadataEnrichmentServiceTests" -v
```

Expected: all PASS

**Step 5: Commit**

```bash
git add src/Chronicle.Services/ tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs
git commit -m "feat(services): implement MetadataEnrichmentService with retry/exhausted logic"
```

---

## Task 5: MetadataEnrichmentScheduledTask

**Files:**
- Create: `src/Chronicle.Services/MetadataEnrichmentScheduledTask.cs`

**Step 1: Implement**

```csharp
// src/Chronicle.Services/MetadataEnrichmentScheduledTask.cs
namespace Chronicle.Services;

public sealed class MetadataEnrichmentScheduledTask(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataEnrichmentScheduledTask> logger) : IScheduledTask
{
    public string TaskId      => "metadata_enrichment";
    public string DisplayName => "Metadata Enrichment";
    public string Description => "Enriches all pending media items with metadata from installed plugins.";
    public string DefaultCron => "0 4 * * *";  // 4am daily — after the 3am file scan

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var enrichmentSvc = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
        logger.LogInformation("Starting scheduled metadata enrichment");
        await enrichmentSvc.EnrichAllAsync(ct);
        logger.LogInformation("Scheduled metadata enrichment complete");
    }
}
```

**Step 2: Commit**

```bash
git add src/Chronicle.Services/MetadataEnrichmentScheduledTask.cs
git commit -m "feat(services): add MetadataEnrichmentScheduledTask (IScheduledTask, 4am daily)"
```

---

## Task 6: API endpoints — enrichment status + reset

**Files:**
- Create: `src/Chronicle.API/Controllers/EnrichmentController.cs`
- Create: `src/Chronicle.API/DTOs/EnrichmentDTOs.cs`

**Step 1: DTOs**

```csharp
// src/Chronicle.API/DTOs/EnrichmentDTOs.cs
namespace Chronicle.API.DTOs;

public record EnrichmentStatsDto(
    string PluginId,
    int Pending,
    int Completed,
    int Failed,
    int Exhausted,
    int NotFound,
    int Skipped);

public record ResetEnrichmentDto(string Scope, int? MediaItemId);
// Scope values: "single", "exhausted", "all"
```

**Step 2: Controller**

```csharp
// src/Chronicle.API/Controllers/EnrichmentController.cs
[ApiController]
[Route("api/v1/enrichment")]
[Authorize]
public class EnrichmentController(IMetadataEnrichmentService enrichmentSvc) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var stats = await enrichmentSvc.GetStatsAsync(ct);
        var dtos = stats.Select(s => new EnrichmentStatsDto(
            s.PluginId, s.Pending, s.Completed, s.Failed, s.Exhausted, s.NotFound, s.Skipped));
        return Ok(new { success = true, data = dtos });
    }

    [HttpPost("{pluginId}/run")]
    [Authorize(Policy = "AdminOnly")]
    public IActionResult RunEnrichment(string pluginId, CancellationToken ct)
    {
        // Fire and forget — enrichment runs in background
        _ = Task.Run(() => enrichmentSvc.EnrichPendingAsync(pluginId, CancellationToken.None), ct);
        return Accepted(new { success = true, message = $"Enrichment started for {pluginId}" });
    }

    [HttpPost("{pluginId}/reset")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Reset(string pluginId, [FromBody] ResetEnrichmentDto dto, CancellationToken ct)
    {
        var scope = dto.Scope.ToLower() switch
        {
            "single"    => ResetScope.Single,
            "exhausted" => ResetScope.AllExhausted,
            "all"       => ResetScope.AllForPlugin,
            _ => throw new ArgumentException("Invalid scope")
        };
        await enrichmentSvc.ResetAsync(pluginId, scope, dto.MediaItemId, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{pluginId}/items/{mediaItemId:int}/skip")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Skip(string pluginId, int mediaItemId, CancellationToken ct)
    {
        await enrichmentSvc.SkipAsync(mediaItemId, pluginId, ct);
        return Ok(new { success = true });
    }
}
```

**Step 3: Integration test**

```csharp
// tests/Chronicle.Tests.Integration/EnrichmentTests.cs
public class EnrichmentTests(ChronicleApiFactory factory) : IClassFixture<ChronicleApiFactory>
{
    [Fact]
    public async Task GetStats_ReturnsEmptyList_WhenNoEnrichmentRows()
    {
        var client = factory.CreateClient();
        var token = await factory.GetAdminTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/api/v1/enrichment/stats");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("success").GetBoolean().Should().BeTrue();
    }
}
```

**Step 4: Run integration tests**

```bash
cd tests/Chronicle.Tests.Integration
dotnet test --filter "EnrichmentTests" -v
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/Chronicle.API/Controllers/EnrichmentController.cs src/Chronicle.API/DTOs/EnrichmentDTOs.cs tests/Chronicle.Tests.Integration/EnrichmentTests.cs
git commit -m "feat(api): add enrichment stats/reset/skip endpoints"
```

---

## Task 7: Trigger enrichment after file scan completes

**Files:**
- Modify: `src/Chronicle.Services/ScheduledScanService.cs`

**Step 1: After a successful scan import, fire enrichment async**

In `ScheduledScanService.ExecuteAsync`, after `ImportGroupsAsync` succeeds for a folder, add:

```csharp
// Fire-and-forget enrichment for newly imported items (non-blocking)
var enrichSvc = scope.ServiceProvider.GetRequiredService<IMetadataEnrichmentService>();
_ = Task.Run(() => enrichSvc.EnrichAllAsync(CancellationToken.None));
```

Add this inside the per-folder try/catch, after the `LastScannedAt` update. Use `CancellationToken.None` so enrichment isn't cancelled if the scan task is cancelled.

**Step 2: Commit**

```bash
git add src/Chronicle.Services/ScheduledScanService.cs
git commit -m "feat(services): trigger async metadata enrichment after file scan completes"
```

---

## Task 8: TMDB plugin — migrate to standalone project

**Goal:** Move `src/Chronicle.Plugins.TMDB/` → `W:\Scripts\Chronicle.Plugin.TMDB\` matching the FileScanner pattern.

**Step 1: Create standalone project directory**

```bash
mkdir "W:/Scripts/Chronicle.Plugin.TMDB"
```

**Step 2: Copy all files**

```bash
cp -r "W:/Scripts/Chronicle/src/Chronicle.Plugins.TMDB/"* "W:/Scripts/Chronicle.Plugin.TMDB/"
```

**Step 3: Replace the .csproj**

Create `W:\Scripts\Chronicle.Plugin.TMDB\Chronicle.Plugin.TMDB.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Chronicle.Plugin.TMDB</AssemblyName>
    <RootNamespace>Chronicle.Plugins.TMDB</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
    <PackageReference Include="Serilog" Version="4.3.1" />
  </ItemGroup>

  <ItemGroup>
    <None Include="manifest.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

**Step 4: Update manifest.json to use new entry_type format**

```json
{
  "plugin_id": "chronicle.plugin.tmdb",
  "name": "TMDB",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "Fetches movie and TV metadata from The Movie Database (TMDB). Requires a free TMDB API key.",
  "min_chronicle_version": "0.1.0",
  "entry_type": "Chronicle.Plugins.TMDB.TmdbMetadataProvider"
}
```

**Step 5: Build and verify**

```bash
cd "W:/Scripts/Chronicle.Plugin.TMDB"
dotnet build
```

Expected: Build succeeded, 0 errors

**Step 6: Remove from main repo solution**

```bash
cd "W:/Scripts/Chronicle"
dotnet sln src/Chronicle.sln remove src/Chronicle.Plugins.TMDB/Chronicle.Plugins.TMDB.csproj
rm -rf src/Chronicle.Plugins.TMDB/
```

**Step 7: Verify solution still builds**

```bash
cd src && dotnet build Chronicle.sln
```

Expected: Build succeeded

**Step 8: Run all tests**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: all 229+ tests pass

**Step 9: Commit both repos**

```bash
# Main repo
cd "W:/Scripts/Chronicle"
git add -A
git commit -m "feat(plugins): migrate TMDB plugin to standalone project W:/Scripts/Chronicle.Plugin.TMDB"

# TMDB plugin repo (init git)
cd "W:/Scripts/Chronicle.Plugin.TMDB"
git init
git add -A
git commit -m "feat: initial commit — TMDB plugin migrated from Chronicle main repo"
```

---

## Task 9: MusicBrainz plugin — project scaffold

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\Chronicle.Plugin.MusicBrainz.csproj`
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\manifest.json`
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs` (stub)

**Step 1: Create project file**

```xml
<!-- W:\Scripts\Chronicle.Plugin.MusicBrainz\Chronicle.Plugin.MusicBrainz.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Chronicle.Plugin.MusicBrainz</AssemblyName>
    <RootNamespace>Chronicle.Plugin.MusicBrainz</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false"
                      ExcludeAssets="runtime" />
    <PackageReference Include="Serilog" Version="4.3.1" />
  </ItemGroup>

  <ItemGroup>
    <None Include="manifest.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

**Step 2: Create manifest.json**

```json
{
  "plugin_id": "chronicle.plugin.musicbrainz",
  "name": "MusicBrainz",
  "version": "1.0.0",
  "author": "Chronicle Contributors",
  "description": "Fetches comprehensive music metadata from MusicBrainz (artist, album, track) and cover art from the Cover Art Archive. No API key required.",
  "min_chronicle_version": "0.1.0",
  "entry_type": "Chronicle.Plugin.MusicBrainz.MusicBrainzMetadataProvider"
}
```

**Step 3: Create stub provider**

```csharp
// W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs
namespace Chronicle.Plugin.MusicBrainz;

public class MusicBrainzMetadataProvider : IMetadataProvider
{
    public string PluginId => "chronicle.plugin.musicbrainz";
    public string Name     => "MusicBrainz";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new() { MediaTypeName = "music", DefaultPriority = 10 }
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new() { Key = "UserAgent",    Label = "User-Agent",         Type = SettingType.Text,     Required = true,  DefaultValue = "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)" },
            new() { Key = "Username",     Label = "MusicBrainz Username", Type = SettingType.Text,   Required = false, Description = "Optional. Enables authenticated access for higher rate limits and personal data." },
            new() { Key = "Password",     Label = "MusicBrainz Password", Type = SettingType.Password, Required = false },
            new() { Key = "MaxRetries",   Label = "Max Retries",         Type = SettingType.Number,   Required = false, DefaultValue = "3" },
        ]
    };

    public void Configure(IReadOnlyDictionary<string, string> settings) { }

    public Task<MediaMetadata> SearchAsync(string query, string mediaType, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
}
```

**Step 4: Verify it builds**

```bash
cd "W:/Scripts/Chronicle.Plugin.MusicBrainz"
dotnet build
```

Expected: Build succeeded, 0 errors

**Step 5: Init git and commit**

```bash
cd "W:/Scripts/Chronicle.Plugin.MusicBrainz"
git init
git add -A
git commit -m "feat: scaffold MusicBrainz plugin project"
```

---

## Task 10: MusicBrainzClient — HTTP + rate limiting + auth

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzClient.cs`

**Step 1: Implement the HTTP client with built-in rate limiter**

```csharp
// W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzClient.cs
namespace Chronicle.Plugin.MusicBrainz;

/// <summary>
/// Thread-safe MusicBrainz API client with built-in rate limiting.
/// Anonymous: 1 req/sec. Authenticated: 5 req/sec.
/// All methods return raw JSON strings — parsing handled by callers.
/// </summary>
internal sealed class MusicBrainzClient : IDisposable
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";
    private const string CoverArtBaseUrl = "https://coverartarchive.org";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private readonly TimeSpan _minInterval;

    public MusicBrainzClient(string userAgent, string? username, string? password)
    {
        _minInterval = string.IsNullOrEmpty(username)
            ? TimeSpan.FromMilliseconds(1100)   // anonymous: just over 1/sec
            : TimeSpan.FromMilliseconds(220);   // authenticated: just over 5/sec

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            handler.Credentials = new NetworkCredential(username, password);
            handler.PreAuthenticate = true;
        }

        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>GET the MusicBrainz API. Automatically throttled.</summary>
    public async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        var url = $"{BaseUrl}/{path.TrimStart('/')}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>GET the Cover Art Archive. Throttled via same limiter.</summary>
    public async Task<string> GetCoverArtAsync(string path, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        var url = $"{CoverArtBaseUrl}/{path.TrimStart('/')}";
        var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "{}";
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Download raw image bytes. Throttled.</summary>
    public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        await ThrottleAsync(ct);
        return await _http.GetByteArrayAsync(url, ct);
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < _minInterval)
                await Task.Delay(_minInterval - elapsed, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    public void Dispose()
    {
        _throttle.Dispose();
        _http.Dispose();
    }
}
```

**Step 2: Wire Configure() to create the client**

In `MusicBrainzMetadataProvider.cs`:

```csharp
private MusicBrainzClient? _client;

public void Configure(IReadOnlyDictionary<string, string> settings)
{
    var userAgent  = settings.GetValueOrDefault("UserAgent",
        "Chronicle/1.0 (https://github.com/thegoddamnbeckster/Chronicle)");
    var username   = settings.GetValueOrDefault("Username");
    var password   = settings.GetValueOrDefault("Password");
    _maxRetries    = int.TryParse(settings.GetValueOrDefault("MaxRetries"), out var r) ? r : 3;

    _client?.Dispose();
    _client = new MusicBrainzClient(userAgent, username, password);
}
```

**Step 3: Implement HealthCheckAsync**

```csharp
public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
{
    try
    {
        var json = await _client!.GetAsync("artist/4a4ee089-93b9-4a56-a4f0-9f234f0cb04f", ct);
        return json.Contains("Radiohead");
    }
    catch { return false; }
}
```

**Step 4: Build**

```bash
cd "W:/Scripts/Chronicle.Plugin.MusicBrainz"
dotnet build
```

Expected: Build succeeded

**Step 5: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): HTTP client with rate limiting (1/s anon, 5/s auth) + health check"
```

---

## Task 11: MusicBrainz API models (deserialisation)

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\Models\MbModels.cs`

Create all deserialisation models. Use `System.Text.Json` with `JsonPropertyName` attributes.

```csharp
// W:\Scripts\Chronicle.Plugin.MusicBrainz\Models\MbModels.cs
// Only key models shown — implement ALL fields listed in the design doc
namespace Chronicle.Plugin.MusicBrainz.Models;

public class MbArtist
{
    [JsonPropertyName("id")]           public string? Id { get; set; }
    [JsonPropertyName("name")]         public string? Name { get; set; }
    [JsonPropertyName("sort-name")]    public string? SortName { get; set; }
    [JsonPropertyName("type")]         public string? Type { get; set; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
    [JsonPropertyName("life-span")]    public MbLifeSpan? LifeSpan { get; set; }
    [JsonPropertyName("area")]         public MbArea? Area { get; set; }
    [JsonPropertyName("begin-area")]   public MbArea? BeginArea { get; set; }
    [JsonPropertyName("end-area")]     public MbArea? EndArea { get; set; }
    [JsonPropertyName("aliases")]      public List<MbAlias>? Aliases { get; set; }
    [JsonPropertyName("tags")]         public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]       public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("rating")]       public MbRating? Rating { get; set; }
    [JsonPropertyName("relations")]    public List<MbRelation>? Relations { get; set; }
    [JsonPropertyName("release-groups")] public List<MbReleaseGroup>? ReleaseGroups { get; set; }
}

public class MbReleaseGroup
{
    [JsonPropertyName("id")]             public string? Id { get; set; }
    [JsonPropertyName("title")]          public string? Title { get; set; }
    [JsonPropertyName("primary-type")]   public string? PrimaryType { get; set; }
    [JsonPropertyName("secondary-types")] public List<string>? SecondaryTypes { get; set; }
    [JsonPropertyName("first-release-date")] public string? FirstReleaseDate { get; set; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
    [JsonPropertyName("artist-credit")]  public List<MbArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("releases")]       public List<MbRelease>? Releases { get; set; }
    [JsonPropertyName("tags")]           public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]         public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("rating")]         public MbRating? Rating { get; set; }
    [JsonPropertyName("relations")]      public List<MbRelation>? Relations { get; set; }
}

public class MbRelease
{
    [JsonPropertyName("id")]             public string? Id { get; set; }
    [JsonPropertyName("title")]          public string? Title { get; set; }
    [JsonPropertyName("date")]           public string? Date { get; set; }
    [JsonPropertyName("country")]        public string? Country { get; set; }
    [JsonPropertyName("status")]         public string? Status { get; set; }
    [JsonPropertyName("barcode")]        public string? Barcode { get; set; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
    [JsonPropertyName("label-info")]     public List<MbLabelInfo>? LabelInfo { get; set; }
    [JsonPropertyName("media")]          public List<MbMedium>? Media { get; set; }
    [JsonPropertyName("artist-credit")]  public List<MbArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("release-group")]  public MbReleaseGroup? ReleaseGroup { get; set; }
    [JsonPropertyName("text-representation")] public MbTextRepresentation? TextRepresentation { get; set; }
    [JsonPropertyName("quality")]        public string? Quality { get; set; }
    [JsonPropertyName("packaging")]      public string? Packaging { get; set; }
    [JsonPropertyName("tags")]           public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]         public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("relations")]      public List<MbRelation>? Relations { get; set; }
}

public class MbRecording
{
    [JsonPropertyName("id")]             public string? Id { get; set; }
    [JsonPropertyName("title")]          public string? Title { get; set; }
    [JsonPropertyName("length")]         public int? Length { get; set; }  // milliseconds
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
    [JsonPropertyName("first-release-date")] public string? FirstReleaseDate { get; set; }
    [JsonPropertyName("video")]          public bool? Video { get; set; }
    [JsonPropertyName("isrcs")]          public List<string>? Isrcs { get; set; }
    [JsonPropertyName("artist-credit")]  public List<MbArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("releases")]       public List<MbRelease>? Releases { get; set; }
    [JsonPropertyName("tags")]           public List<MbTag>? Tags { get; set; }
    [JsonPropertyName("genres")]         public List<MbTag>? Genres { get; set; }
    [JsonPropertyName("rating")]         public MbRating? Rating { get; set; }
    [JsonPropertyName("relations")]      public List<MbRelation>? Relations { get; set; }
}

public class MbWork
{
    [JsonPropertyName("id")]             public string? Id { get; set; }
    [JsonPropertyName("title")]          public string? Title { get; set; }
    [JsonPropertyName("type")]           public string? Type { get; set; }
    [JsonPropertyName("iswcs")]          public List<string>? Iswcs { get; set; }
    [JsonPropertyName("language")]       public string? Language { get; set; }
    [JsonPropertyName("relations")]      public List<MbRelation>? Relations { get; set; }
}

// Supporting types
public class MbLifeSpan
{
    [JsonPropertyName("begin")] public string? Begin { get; set; }
    [JsonPropertyName("end")]   public string? End { get; set; }
    [JsonPropertyName("ended")] public bool? Ended { get; set; }
}

public class MbArea
{
    [JsonPropertyName("id")]   public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public class MbAlias
{
    [JsonPropertyName("name")]   public string? Name { get; set; }
    [JsonPropertyName("type")]   public string? Type { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("primary")] public bool? Primary { get; set; }
}

public class MbTag
{
    [JsonPropertyName("name")]  public string? Name { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public class MbRating
{
    [JsonPropertyName("value")]       public double? Value { get; set; }
    [JsonPropertyName("votes-count")] public int VotesCount { get; set; }
}

public class MbRelation
{
    [JsonPropertyName("type")]          public string? Type { get; set; }
    [JsonPropertyName("type-id")]       public string? TypeId { get; set; }
    [JsonPropertyName("direction")]     public string? Direction { get; set; }
    [JsonPropertyName("artist")]        public MbArtist? Artist { get; set; }
    [JsonPropertyName("work")]          public MbWork? Work { get; set; }
    [JsonPropertyName("url")]           public MbUrl? Url { get; set; }
    [JsonPropertyName("attributes")]    public List<string>? Attributes { get; set; }
    [JsonPropertyName("begin")]         public string? Begin { get; set; }
    [JsonPropertyName("end")]           public string? End { get; set; }
}

public class MbUrl
{
    [JsonPropertyName("id")]       public string? Id { get; set; }
    [JsonPropertyName("resource")] public string? Resource { get; set; }
}

public class MbArtistCredit
{
    [JsonPropertyName("name")]         public string? Name { get; set; }
    [JsonPropertyName("joinphrase")]   public string? JoinPhrase { get; set; }
    [JsonPropertyName("artist")]       public MbArtist? Artist { get; set; }
}

public class MbMedium
{
    [JsonPropertyName("position")]    public int Position { get; set; }
    [JsonPropertyName("format")]      public string? Format { get; set; }
    [JsonPropertyName("title")]       public string? Title { get; set; }
    [JsonPropertyName("track-count")] public int TrackCount { get; set; }
    [JsonPropertyName("tracks")]      public List<MbTrack>? Tracks { get; set; }
}

public class MbTrack
{
    [JsonPropertyName("id")]        public string? Id { get; set; }
    [JsonPropertyName("number")]    public string? Number { get; set; }
    [JsonPropertyName("title")]     public string? Title { get; set; }
    [JsonPropertyName("length")]    public int? Length { get; set; }
    [JsonPropertyName("position")]  public int Position { get; set; }
    [JsonPropertyName("recording")] public MbRecording? Recording { get; set; }
}

public class MbLabelInfo
{
    [JsonPropertyName("catalog-number")] public string? CatalogNumber { get; set; }
    [JsonPropertyName("label")]          public MbLabel? Label { get; set; }
}

public class MbLabel
{
    [JsonPropertyName("id")]   public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("sort-name")] public string? SortName { get; set; }
}

public class MbTextRepresentation
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("script")]   public string? Script { get; set; }
}

// Search result wrappers
public class MbSearchResult<T>
{
    [JsonPropertyName("count")]   public int Count { get; set; }
    [JsonPropertyName("offset")]  public int Offset { get; set; }
    [JsonPropertyName("artists")]    public List<T>? Artists { get; set; }
    [JsonPropertyName("release-groups")] public List<T>? ReleaseGroups { get; set; }
    [JsonPropertyName("releases")]   public List<T>? Releases { get; set; }
    [JsonPropertyName("recordings")] public List<T>? Recordings { get; set; }
}

// Cover Art Archive
public class CaaResponse
{
    [JsonPropertyName("images")]    public List<CaaImage>? Images { get; set; }
    [JsonPropertyName("release")]   public string? Release { get; set; }
}

public class CaaImage
{
    [JsonPropertyName("id")]          public long Id { get; set; }
    [JsonPropertyName("types")]       public List<string>? Types { get; set; }
    [JsonPropertyName("front")]       public bool Front { get; set; }
    [JsonPropertyName("back")]        public bool Back { get; set; }
    [JsonPropertyName("comment")]     public string? Comment { get; set; }
    [JsonPropertyName("image")]       public string? Image { get; set; }
    [JsonPropertyName("thumbnails")]  public CaaThumbnails? Thumbnails { get; set; }
    [JsonPropertyName("approved")]    public bool Approved { get; set; }
}

public class CaaThumbnails
{
    [JsonPropertyName("250")]  public string? Small { get; set; }
    [JsonPropertyName("500")]  public string? Medium { get; set; }
    [JsonPropertyName("1200")] public string? Large { get; set; }
    [JsonPropertyName("large")] public string? LargeAlt { get; set; }
    [JsonPropertyName("small")] public string? SmallAlt { get; set; }
}
```

**Step 2: Commit**

```bash
cd "W:/Scripts/Chronicle.Plugin.MusicBrainz"
git add -A
git commit -m "feat(musicbrainz): add full MusicBrainz + Cover Art Archive deserialisation models"
```

---

## Task 12: CoverArtArchiveClient

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\CoverArtArchiveClient.cs`

```csharp
// W:\Scripts\Chronicle.Plugin.MusicBrainz\CoverArtArchiveClient.cs
namespace Chronicle.Plugin.MusicBrainz;

internal static class CoverArtArchiveClient
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<List<CaaImage>> GetImagesAsync(
        MusicBrainzClient client, string entityType, string mbid, CancellationToken ct)
    {
        // entityType: "release" or "release-group"
        var json = await client.GetCoverArtAsync($"{entityType}/{mbid}", ct);
        if (json == "{}") return [];
        var response = JsonSerializer.Deserialize<CaaResponse>(json, Opts);
        return response?.Images ?? [];
    }

    public static List<object> ToStorageFormat(List<CaaImage> images) =>
        images.Select(img => (object)new
        {
            id = img.Id,
            types = img.Types ?? [],
            front = img.Front,
            back = img.Back,
            comment = img.Comment,
            url = img.Image,
            thumbnails = new
            {
                small  = img.Thumbnails?.Small ?? img.Thumbnails?.SmallAlt,
                medium = img.Thumbnails?.Medium,
                large  = img.Thumbnails?.Large ?? img.Thumbnails?.LargeAlt
            },
            approved = img.Approved
        }).ToList();
}
```

**Step 2: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): add CoverArtArchiveClient"
```

---

## Task 13: SearchAsync implementation (artist, release-group, recording)

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzSearcher.cs`
- Modify: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzMetadataProvider.cs`

**Step 1: Implement searcher**

```csharp
// W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzSearcher.cs
namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzSearcher
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Search artists. Returns list of results in MediaMetadata.Results.</summary>
    public static async Task<MediaMetadata> SearchArtistsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"artist?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbArtist>>(json, Opts);
        var items = (result?.Artists ?? []).Select(a => new MediaMetadata
        {
            ExternalId  = $"artist:{a.Id}",
            Source      = "musicbrainz",
            Title       = a.Name ?? string.Empty,
            Overview    = BuildArtistDescription(a),
            Year        = ParseYear(a.LifeSpan?.Begin)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    /// <summary>Search release groups (albums/EPs/singles).</summary>
    public static async Task<MediaMetadata> SearchReleaseGroupsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"release-group?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbReleaseGroup>>(json, Opts);
        var items = (result?.ReleaseGroups ?? []).Select(rg => new MediaMetadata
        {
            ExternalId = $"release-group:{rg.Id}",
            Source     = "musicbrainz",
            Title      = rg.Title ?? string.Empty,
            Overview   = $"{rg.PrimaryType}{(rg.SecondaryTypes?.Count > 0 ? " (" + string.Join(", ", rg.SecondaryTypes) + ")" : "")}",
            Year       = ParseYear(rg.FirstReleaseDate)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    /// <summary>Search recordings (tracks).</summary>
    public static async Task<MediaMetadata> SearchRecordingsAsync(
        MusicBrainzClient client, string query, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var json = await client.GetAsync($"recording?query={encoded}&limit=10&fmt=json", ct);
        var result = JsonSerializer.Deserialize<MbSearchResult<MbRecording>>(json, Opts);
        var items = (result?.Recordings ?? []).Select(r => new MediaMetadata
        {
            ExternalId     = $"recording:{r.Id}",
            Source         = "musicbrainz",
            Title          = r.Title ?? string.Empty,
            RuntimeMinutes = r.Length.HasValue ? r.Length.Value / 60000 : null,
            Year           = ParseYear(r.FirstReleaseDate)
        }).ToList();
        return new MediaMetadata { Results = items, TotalResults = result?.Count ?? 0 };
    }

    private static string BuildArtistDescription(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type)) parts.Add(a.Type);
        if (a.Area?.Name is { } area) parts.Add(area);
        if (!string.IsNullOrEmpty(a.Disambiguation)) parts.Add($"({a.Disambiguation})");
        return string.Join(" · ", parts);
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        return int.TryParse(date[..Math.Min(4, date.Length)], out var y) ? y : null;
    }
}
```

**Step 2: Hook into SearchAsync on the provider**

```csharp
public async Task<MediaMetadata> SearchAsync(string query, string mediaType, CancellationToken ct = default)
{
    EnsureConfigured();
    // Determine which level to search based on hierarchy context
    // Convention: mediaType "music" + caller passes level hint in query prefix
    // "artist:Radiohead" → search artists
    // "album:OK Computer" → search release groups
    // "track:Creep" → search recordings
    // Default (no prefix) → search all three and merge
    if (query.StartsWith("artist:", StringComparison.OrdinalIgnoreCase))
        return await MusicBrainzSearcher.SearchArtistsAsync(_client!, query[7..].Trim(), ct);
    if (query.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
        return await MusicBrainzSearcher.SearchReleaseGroupsAsync(_client!, query[6..].Trim(), ct);
    if (query.StartsWith("track:", StringComparison.OrdinalIgnoreCase))
        return await MusicBrainzSearcher.SearchRecordingsAsync(_client!, query[6..].Trim(), ct);

    // No prefix — try artist search first as default for music root level
    return await MusicBrainzSearcher.SearchArtistsAsync(_client!, query, ct);
}

private void EnsureConfigured()
{
    if (_client is null) throw new InvalidOperationException("Plugin not configured. Call Configure() first.");
}
```

**Step 3: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): implement SearchAsync for artist/release-group/recording"
```

---

## Task 14: GetByIdAsync — Artist

**Files:**
- Create: `W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzEntityFetcher.cs`

**Step 1: Implement artist fetch with ALL includes**

```csharp
// W:\Scripts\Chronicle.Plugin.MusicBrainz\MusicBrainzEntityFetcher.cs
namespace Chronicle.Plugin.MusicBrainz;

internal static class MusicBrainzEntityFetcher
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    private const string ArtistIncludes =
        "recordings+releases+release-groups+works+aliases+tags+genres+ratings+url-rels+artist-rels";

    private const string ReleaseGroupIncludes =
        "artists+releases+tags+genres+ratings+url-rels";

    private const string ReleaseIncludes =
        "artists+recordings+release-groups+labels+media+tags+genres+url-rels+artist-credits+isrcs";

    private const string RecordingIncludes =
        "artists+releases+tags+genres+isrcs+url-rels+artist-rels+work-rels";

    private const string WorkIncludes =
        "artist-rels+url-rels";

    public static async Task<MediaMetadata> FetchArtistAsync(
        MusicBrainzClient client, string mbid, CancellationToken ct)
    {
        var json = await client.GetAsync($"artist/{mbid}?inc={ArtistIncludes}&fmt=json", ct);
        var artist = JsonSerializer.Deserialize<MbArtist>(json, Opts)
            ?? throw new InvalidOperationException($"Empty response for artist {mbid}");

        // Fetch cover art — artist images come from Wikimedia URLs in relations
        var artistImageUrl = ExtractWikimediaImageUrl(artist.Relations);

        // Fetch release groups with cover art (first 5 to avoid hammering the API)
        var releaseGroupImages = new List<object>();
        foreach (var rg in (artist.ReleaseGroups ?? []).Take(5))
        {
            if (rg.Id is null) continue;
            var images = await CoverArtArchiveClient.GetImagesAsync(client, "release-group", rg.Id, ct);
            releaseGroupImages.AddRange(CoverArtArchiveClient.ToStorageFormat(images));
            if (images.Count > 0 && rg.Id is not null)
            {
                // Tag first front image as poster for this release group
            }
        }

        var mbData = new
        {
            mbid = artist.Id,
            name = artist.Name,
            sort_name = artist.SortName,
            type = artist.Type,
            disambiguation = artist.Disambiguation,
            life_span = artist.LifeSpan,
            area = artist.Area?.Name,
            begin_area = artist.BeginArea?.Name,
            end_area = artist.EndArea?.Name,
            aliases = artist.Aliases?.Select(a => new { a.Name, a.Type, a.Locale, a.Primary }),
            tags = artist.Tags?.Select(t => new { t.Name, t.Count }),
            genres = artist.Genres?.Select(g => g.Name),
            rating = artist.Rating,
            urls = ExtractUrls(artist.Relations),
            members = ExtractMembers(artist.Relations),
            member_of = ExtractMemberOf(artist.Relations),
            release_groups = artist.ReleaseGroups?.Select(rg => new
            {
                rg.Id,
                rg.Title,
                rg.PrimaryType,
                rg.SecondaryTypes,
                rg.FirstReleaseDate
            }),
            images = releaseGroupImages,
            artist_image_url = artistImageUrl
        };

        var posterUrl = artistImageUrl
            ?? artist.ReleaseGroups?.FirstOrDefault()?.Id.Let(id =>
                $"https://coverartarchive.org/release-group/{id}/front-250");

        return new MediaMetadata
        {
            ExternalId   = $"artist:{artist.Id}",
            Source       = "musicbrainz",
            Title        = artist.Name ?? string.Empty,
            Overview     = BuildBio(artist),
            Year         = ParseYear(artist.LifeSpan?.Begin),
            PosterUrl    = posterUrl,
            Genres       = artist.Genres?.Select(g => g.Name ?? "").Where(g => g != "").ToList() ?? [],
            Rating       = artist.Rating?.Value,
            MetadataJson = $"{{\"musicbrainz\": {JsonSerializer.Serialize(mbData)}}}"
        };
    }

    private static string? ExtractWikimediaImageUrl(List<MbRelation>? relations)
    {
        // MusicBrainz links to Wikimedia Commons for artist images
        var wikimediaUrl = relations?
            .Where(r => r.Type == "image" && r.Url?.Resource?.Contains("wikimedia") == true)
            .Select(r => r.Url!.Resource)
            .FirstOrDefault();
        // Convert Commons page URL to direct image URL if needed
        return wikimediaUrl;
    }

    private static object ExtractUrls(List<MbRelation>? relations)
    {
        if (relations is null) return new { };
        var urlRels = relations.Where(r => r.Url is not null).ToList();
        return new
        {
            official      = urlRels.FirstOrDefault(r => r.Type == "official homepage")?.Url?.Resource,
            discogs       = urlRels.FirstOrDefault(r => r.Type == "discogs")?.Url?.Resource,
            wikidata      = urlRels.FirstOrDefault(r => r.Type == "wikidata")?.Url?.Resource,
            wikipedia     = urlRels.FirstOrDefault(r => r.Type == "wikipedia")?.Url?.Resource,
            youtube       = urlRels.FirstOrDefault(r => r.Type == "youtube")?.Url?.Resource,
            bandcamp      = urlRels.FirstOrDefault(r => r.Type == "bandcamp")?.Url?.Resource,
            soundcloud    = urlRels.FirstOrDefault(r => r.Type == "soundcloud")?.Url?.Resource,
            allmusic      = urlRels.FirstOrDefault(r => r.Type == "allmusic")?.Url?.Resource,
            imdb          = urlRels.FirstOrDefault(r => r.Type == "IMDb")?.Url?.Resource,
            spotify       = urlRels.FirstOrDefault(r => r.Type?.Contains("streaming") == true &&
                                r.Url?.Resource?.Contains("spotify") == true)?.Url?.Resource,
            image         = urlRels.FirstOrDefault(r => r.Type == "image")?.Url?.Resource,
            social_media  = urlRels.Where(r => r.Type == "social network").Select(r => r.Url?.Resource).ToList()
        };
    }

    private static List<object> ExtractMembers(List<MbRelation>? relations) =>
        (relations ?? [])
            .Where(r => r.Type == "member of band" && r.Direction == "backward" && r.Artist is not null)
            .Select(r => (object)new
            {
                mbid = r.Artist!.Id,
                name = r.Artist.Name,
                begin = r.Begin,
                end = r.End,
                attributes = r.Attributes
            }).ToList();

    private static List<object> ExtractMemberOf(List<MbRelation>? relations) =>
        (relations ?? [])
            .Where(r => r.Type == "member of band" && r.Direction == "forward" && r.Artist is not null)
            .Select(r => (object)new
            {
                mbid = r.Artist!.Id,
                name = r.Artist.Name
            }).ToList();

    private static string BuildBio(MbArtist a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(a.Type)) parts.Add(a.Type);
        if (a.Area?.Name is { } area) parts.Add(area);
        if (a.LifeSpan?.Begin is { } begin) parts.Add($"Active from {begin}");
        if (a.LifeSpan?.Ended == true && a.LifeSpan.End is { } end) parts.Add($"Ended {end}");
        return string.Join(" · ", parts);
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        return int.TryParse(date[..Math.Min(4, date.Length)], out var y) ? y : null;
    }
}

// Extension helper
internal static class ObjectExtensions
{
    public static TResult? Let<T, TResult>(this T? value, Func<T, TResult> func)
        where T : class => value is null ? default : func(value);
}
```

**Step 2: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): FetchArtistAsync with all includes, urls, members, CAA images"
```

---

## Task 15: GetByIdAsync — Release Group + Release

Add to `MusicBrainzEntityFetcher.cs`:

```csharp
public static async Task<MediaMetadata> FetchReleaseGroupAsync(
    MusicBrainzClient client, string mbid, CancellationToken ct)
{
    var json = await client.GetAsync($"release-group/{mbid}?inc={ReleaseGroupIncludes}&fmt=json", ct);
    var rg = JsonSerializer.Deserialize<MbReleaseGroup>(json, Opts)!;

    // Fetch all releases in the group for comprehensive detail
    var releases = new List<object>();
    foreach (var release in (rg.Releases ?? []).Take(20))
    {
        if (release.Id is null) continue;
        var releaseJson = await client.GetAsync($"release/{release.Id}?inc={ReleaseIncludes}&fmt=json", ct);
        var fullRelease = JsonSerializer.Deserialize<MbRelease>(releaseJson, Opts);
        if (fullRelease is not null) releases.Add(MapRelease(fullRelease));
    }

    // Cover art for the release group
    var images = await CoverArtArchiveClient.GetImagesAsync(client, "release-group", mbid, ct);
    var frontImage = images.FirstOrDefault(i => i.Front)?.Image
        ?? images.FirstOrDefault()?.Image;

    var mbData = new
    {
        mbid = rg.Id,
        title = rg.Title,
        primary_type = rg.PrimaryType,
        secondary_types = rg.SecondaryTypes,
        first_release_date = rg.FirstReleaseDate,
        disambiguation = rg.Disambiguation,
        artist_credit = rg.ArtistCredit?.Select(ac => new { ac.Name, ac.JoinPhrase, artist_mbid = ac.Artist?.Id }),
        tags = rg.Tags?.Select(t => new { t.Name, t.Count }),
        genres = rg.Genres?.Select(g => g.Name),
        rating = rg.Rating,
        releases,
        images = CoverArtArchiveClient.ToStorageFormat(images)
    };

    var creditedArtist = rg.ArtistCredit?.FirstOrDefault()?.Artist?.Name ?? string.Empty;

    return new MediaMetadata
    {
        ExternalId   = $"release-group:{rg.Id}",
        Source       = "musicbrainz",
        Title        = rg.Title ?? string.Empty,
        Overview     = $"{rg.PrimaryType} by {creditedArtist}",
        Year         = ParseYear(rg.FirstReleaseDate),
        PosterUrl    = frontImage,
        Genres       = rg.Genres?.Select(g => g.Name ?? "").Where(g => g != "").ToList() ?? [],
        Rating       = rg.Rating?.Value,
        MetadataJson = $"{{\"musicbrainz\": {JsonSerializer.Serialize(mbData)}}}"
    };
}

private static object MapRelease(MbRelease r) => new
{
    mbid           = r.Id,
    title          = r.Title,
    date           = r.Date,
    country        = r.Country,
    status         = r.Status,
    barcode        = r.Barcode,
    disambiguation = r.Disambiguation,
    packaging      = r.Packaging,
    quality        = r.Quality,
    language       = r.TextRepresentation?.Language,
    script         = r.TextRepresentation?.Script,
    label_info     = r.LabelInfo?.Select(li => new
    {
        catalog_number = li.CatalogNumber,
        label_name = li.Label?.Name,
        label_mbid = li.Label?.Id
    }),
    media = r.Media?.Select(m => new
    {
        position    = m.Position,
        format      = m.Format,
        title       = m.Title,
        track_count = m.TrackCount,
        tracks      = m.Tracks?.Select(t => new
        {
            position  = t.Position,
            number    = t.Number,
            title     = t.Title,
            length_ms = t.Length,
            recording_mbid = t.Recording?.Id,
            isrcs     = t.Recording?.Isrcs
        })
    }),
    artist_credit = r.ArtistCredit?.Select(ac => new { ac.Name, ac.JoinPhrase, artist_mbid = ac.Artist?.Id }),
    tags  = r.Tags?.Select(t => new { t.Name, t.Count }),
    genres = r.Genres?.Select(g => g.Name)
};
```

**Step 2: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): FetchReleaseGroupAsync with all releases, tracklists, CAA images"
```

---

## Task 16: GetByIdAsync — Recording (track) + Work

Add to `MusicBrainzEntityFetcher.cs`:

```csharp
public static async Task<MediaMetadata> FetchRecordingAsync(
    MusicBrainzClient client, string mbid, CancellationToken ct)
{
    var json = await client.GetAsync($"recording/{mbid}?inc={RecordingIncludes}&fmt=json", ct);
    var rec = JsonSerializer.Deserialize<MbRecording>(json, Opts)!;

    // Fetch linked works (compositions) for composer/lyricist info
    var works = new List<object>();
    foreach (var rel in (rec.Relations ?? []).Where(r => r.Work is not null).Take(3))
    {
        if (rel.Work?.Id is null) continue;
        var workJson = await client.GetAsync($"work/{rel.Work.Id}?inc={WorkIncludes}&fmt=json", ct);
        var work = JsonSerializer.Deserialize<MbWork>(workJson, Opts);
        if (work is not null) works.Add(MapWork(work));
    }

    // Get cover art from first release that has it
    string? coverUrl = null;
    foreach (var release in (rec.Releases ?? []).Take(5))
    {
        if (release.Id is null) continue;
        var images = await CoverArtArchiveClient.GetImagesAsync(client, "release", release.Id, ct);
        var front = images.FirstOrDefault(i => i.Front);
        if (front?.Image is not null) { coverUrl = front.Image; break; }
    }

    var artistRoles = (rec.Relations ?? [])
        .Where(r => r.Artist is not null)
        .Select(r => new { role = r.Type, mbid = r.Artist!.Id, name = r.Artist.Name, attributes = r.Attributes })
        .ToList();

    var mbData = new
    {
        mbid              = rec.Id,
        title             = rec.Title,
        length_ms         = rec.Length,
        disambiguation    = rec.Disambiguation,
        first_release_date = rec.FirstReleaseDate,
        video             = rec.Video,
        isrcs             = rec.Isrcs,
        artist_credit     = rec.ArtistCredit?.Select(ac => new { ac.Name, ac.JoinPhrase, artist_mbid = ac.Artist?.Id }),
        artist_roles      = artistRoles,
        releases          = rec.Releases?.Select(r => new
        {
            mbid    = r.Id,
            title   = r.Title,
            date    = r.Date,
            country = r.Country
        }),
        tags              = rec.Tags?.Select(t => new { t.Name, t.Count }),
        genres            = rec.Genres?.Select(g => g.Name),
        rating            = rec.Rating,
        works
    };

    return new MediaMetadata
    {
        ExternalId     = $"recording:{rec.Id}",
        Source         = "musicbrainz",
        Title          = rec.Title ?? string.Empty,
        Year           = ParseYear(rec.FirstReleaseDate),
        PosterUrl      = coverUrl,
        RuntimeMinutes = rec.Length.HasValue ? rec.Length.Value / 60000 : null,
        Genres         = rec.Genres?.Select(g => g.Name ?? "").Where(g => g != "").ToList() ?? [],
        Rating         = rec.Rating?.Value,
        MetadataJson   = $"{{\"musicbrainz\": {JsonSerializer.Serialize(mbData)}}}"
    };
}

private static object MapWork(MbWork w) => new
{
    mbid       = w.Id,
    title      = w.Title,
    type       = w.Type,
    iswcs      = w.Iswcs,
    language   = w.Language,
    composers  = (w.Relations ?? []).Where(r => r.Type == "composer" && r.Artist is not null)
                     .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name }).ToList(),
    lyricists  = (w.Relations ?? []).Where(r => r.Type == "lyricist" && r.Artist is not null)
                     .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name }).ToList(),
    arrangers  = (w.Relations ?? []).Where(r => r.Type == "arranger" && r.Artist is not null)
                     .Select(r => new { mbid = r.Artist!.Id, name = r.Artist.Name }).ToList(),
    urls       = (w.Relations ?? []).Where(r => r.Url is not null)
                     .Select(r => new { type = r.Type, url = r.Url!.Resource }).ToList()
};
```

**Step 2: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): FetchRecordingAsync with ISRCs, artist roles, works, cover art"
```

---

## Task 17: Wire GetByIdAsync dispatcher + GetImageAsync

**Step 1: Implement GetByIdAsync on the provider**

```csharp
public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
{
    EnsureConfigured();
    // externalId format: "artist:{mbid}", "release-group:{mbid}", "recording:{mbid}"
    var sep = externalId.IndexOf(':');
    if (sep < 0) throw new ArgumentException($"Invalid MusicBrainz ID format: {externalId}");

    var type = externalId[..sep];
    var mbid = externalId[(sep + 1)..];

    return type switch
    {
        "artist"        => await MusicBrainzEntityFetcher.FetchArtistAsync(_client!, mbid, ct),
        "release-group" => await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(_client!, mbid, ct),
        "release"       => await MusicBrainzEntityFetcher.FetchReleaseGroupAsync(_client!, mbid, ct), // fallback
        "recording"     => await MusicBrainzEntityFetcher.FetchRecordingAsync(_client!, mbid, ct),
        _ => throw new ArgumentException($"Unknown MusicBrainz entity type: {type}")
    };
}
```

**Step 2: Implement GetImageAsync**

```csharp
public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
{
    EnsureConfigured();
    return await _client!.GetBytesAsync(url, ct);
}
```

**Step 3: Build and verify**

```bash
cd "W:/Scripts/Chronicle.Plugin.MusicBrainz"
dotnet build
```

Expected: 0 errors

**Step 4: Commit**

```bash
git add -A
git commit -m "feat(musicbrainz): wire GetByIdAsync dispatcher and GetImageAsync"
```

---

## Task 18: Plugin integration — enrich status rows on plugin install

**Files:**
- Modify: `src/Chronicle.Services/PluginHostService.cs` (or wherever plugin install is handled)

**Step 1: Find the plugin install handler**

Search for `InstallPluginAsync` or similar in `Chronicle.Services`. When a plugin is installed, insert `pending` enrichment rows for all existing items of the plugin's supported media types:

```csharp
// After successful plugin install + Configure():
if (plugin is IMetadataProvider metadataProvider)
{
    var supportedTypes = metadataProvider.GetSupportedMediaTypes()
        .Select(t => t.MediaTypeName).ToList();

    var maxRetries = 3; // or read from plugin settings

    var itemIds = await db.MediaItems
        .Where(i => db.MediaTypes
            .Where(mt => supportedTypes.Contains(mt.Name))
            .Select(mt => mt.Id)
            .Contains(i.MediaTypeId))
        .Select(i => i.Id)
        .ToListAsync();

    var rows = itemIds.Select(id => new MediaItemEnrichmentStatus
    {
        MediaItemId = id,
        PluginId    = metadataProvider.PluginId,
        Status      = EnrichmentStatus.Pending,
        MaxRetries  = maxRetries
    });

    // Ignore conflicts (item might already have a row if plugin was reinstalled)
    foreach (var row in rows)
    {
        if (!await db.EnrichmentStatuses.AnyAsync(x =>
            x.MediaItemId == row.MediaItemId && x.PluginId == row.PluginId))
            db.EnrichmentStatuses.Add(row);
    }
    await db.SaveChangesAsync();
}
```

**Step 2: Also insert rows when new items are imported**

In `FileScanService.ImportGroupsAsync` (or wherever new `MediaItem` records are created), after saving items, insert pending rows for all installed metadata providers that support the item's media type. This can be a fire-and-forget background task.

**Step 3: Commit**

```bash
git add src/Chronicle.Services/
git commit -m "feat(services): auto-insert enrichment pending rows on plugin install + item import"
```

---

## Task 19: Frontend — enrichment status panel on Background Tasks page

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.tsx`
- Modify: `src/Chronicle.Web/src/api/enrichment.ts` (new)
- Modify: `src/Chronicle.Web/src/pages/settings/BackgroundTasksPage.module.css`

**Step 1: Create API client functions**

```typescript
// src/Chronicle.Web/src/api/enrichment.ts
import client from './client'

export interface EnrichmentStats {
  pluginId: string
  pending: number
  completed: number
  failed: number
  exhausted: number
  notFound: number
  skipped: number
}

export const getEnrichmentStats = async (): Promise<EnrichmentStats[]> => {
  const { data } = await client.get('/api/v1/enrichment/stats')
  return data.data
}

export const runEnrichment = async (pluginId: string): Promise<void> => {
  await client.post(`/api/v1/enrichment/${encodeURIComponent(pluginId)}/run`)
}

export const resetEnrichment = async (
  pluginId: string,
  scope: 'single' | 'exhausted' | 'all',
  mediaItemId?: number
): Promise<void> => {
  await client.post(`/api/v1/enrichment/${encodeURIComponent(pluginId)}/reset`, {
    scope,
    mediaItemId
  })
}
```

**Step 2: Add enrichment panel to BackgroundTasksPage**

Add a new section below the existing scheduled tasks section. The panel should show a table with one row per plugin:

| Plugin | Pending | Completed | Failed | Exhausted | Skipped | Actions |
|--------|---------|-----------|--------|-----------|---------|---------|
| chronicle.plugin.musicbrainz | 4821 | 12043 | 23 | 7 | 2 | [Run Now] [Reset Exhausted ▾] |

Reset dropdown: "Reset Exhausted" / "Reset All"

The Run Now button calls `runEnrichment(pluginId)` and shows a toast/confirmation.

**Step 3: Add CSS for the enrichment table**

Add styles to `BackgroundTasksPage.module.css` for the enrichment stats table — match the existing card/table aesthetic.

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/
git commit -m "feat(web): add enrichment status panel to Background Tasks page with reset controls"
```

---

## Task 20: Build + publish scripts update

**Files:**
- Modify: `scripts/publish-windows.ps1`

**Step 1: Add MusicBrainz (and TMDB) plugin build + copy steps**

After the main Chronicle.API publish, add:

```powershell
# ── Build external plugins ─────────────────────────────────────────────────
$PluginsOutDir = Join-Path $OutputDir "plugins"
New-Item -ItemType Directory -Force -Path $PluginsOutDir | Out-Null

foreach ($pluginDir in @(
    "W:\Scripts\Chronicle.Plugin.TMDB",
    "W:\Scripts\Chronicle.Plugin.MusicBrainz"
)) {
    if (Test-Path $pluginDir) {
        Write-Host "Building plugin: $pluginDir" -ForegroundColor Cyan
        $pluginPublish = Join-Path $pluginDir "publish"
        dotnet publish $pluginDir -c Release -o $pluginPublish --no-self-contained
        $pluginId = (Get-Content (Join-Path $pluginDir "manifest.json") | ConvertFrom-Json).plugin_id
        $dest = Join-Path $PluginsOutDir $pluginId
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -Path (Join-Path $pluginPublish "*") -Destination $dest -Recurse -Force
        Write-Host "  Deployed $pluginId to $dest" -ForegroundColor Green
    }
}
```

**Step 2: Run full test suite to verify everything still works**

```bash
cd "W:/Scripts/Chronicle/tests"
dotnet test --verbosity normal
```

Expected: all tests pass

**Step 3: Commit**

```bash
cd "W:/Scripts/Chronicle"
git add scripts/publish-windows.ps1
git commit -m "feat(build): include MusicBrainz and TMDB external plugin builds in publish script"
```

---

## Task 21: End-to-end smoke test

**Step 1: Start the dev environment**

```powershell
# Run from non-elevated PowerShell
W:\Scripts\Chronicle\scripts\RunTestEnvironment.ps1
```

**Step 2: Install the MusicBrainz plugin via the UI**

1. Navigate to http://localhost:8888/plugins
2. Click "+ Install Plugin"
3. Enter the path to `W:\Scripts\Chronicle.Plugin.MusicBrainz\bin\Debug\net9.0\`
4. Verify it appears in Installed Plugins as "MusicBrainz v1.0.0 ENABLED"

**Step 3: Configure the plugin**

1. Click "Configure" on the MusicBrainz plugin
2. Verify the User-Agent field has a default value
3. Optionally enter MusicBrainz credentials
4. Click "Save Settings"

**Step 4: Verify health check**

Click "Test" — should show "✓ Healthy" (confirms API reachable and rate limiter working)

**Step 5: Check Background Tasks**

Navigate to Settings → Background Tasks:
- Verify "Metadata Enrichment" task appears with 4am cron
- Verify enrichment stats panel shows the MusicBrainz plugin with counts
- Click "Run Now" on the enrichment task
- Wait and refresh — counts should move from Pending to Completed (or Failed if no music in library)

**Step 6: Commit final**

```bash
cd "W:/Scripts/Chronicle"
git push origin main
```

---

## Summary of New Files

### Chronicle main repo
- `src/Chronicle.Core/Models/EnrichmentStatus.cs`
- `src/Chronicle.Core/Models/MediaItemEnrichmentStatus.cs`
- `src/Chronicle.Data/Migrations/XXXXXXXX_AddEnrichmentStatus.cs` (generated)
- `src/Chronicle.Services/IMetadataEnrichmentService.cs`
- `src/Chronicle.Services/MetadataEnrichmentService.cs`
- `src/Chronicle.Services/MetadataEnrichmentScheduledTask.cs`
- `src/Chronicle.API/Controllers/EnrichmentController.cs`
- `src/Chronicle.API/DTOs/EnrichmentDTOs.cs`
- `src/Chronicle.Web/src/api/enrichment.ts`
- `tests/Chronicle.Tests.Unit/Data/EnrichmentStatusDbTests.cs`
- `tests/Chronicle.Tests.Unit/Services/MetadataEnrichmentServiceTests.cs`
- `tests/Chronicle.Tests.Integration/EnrichmentTests.cs`

### W:\Scripts\Chronicle.Plugin.TMDB (migrated)
- All files from `src/Chronicle.Plugins.TMDB/` with updated .csproj

### W:\Scripts\Chronicle.Plugin.MusicBrainz (new)
- `Chronicle.Plugin.MusicBrainz.csproj`
- `manifest.json`
- `MusicBrainzMetadataProvider.cs`
- `MusicBrainzClient.cs`
- `MusicBrainzSearcher.cs`
- `MusicBrainzEntityFetcher.cs`
- `CoverArtArchiveClient.cs`
- `Models/MbModels.cs`
