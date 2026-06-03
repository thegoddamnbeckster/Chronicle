# Metadata Assignment Enforcement Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enforce the metadata assignment config during enrichment so that the highest-priority plugin's value wins for each field, and expose a `resolvedMetadata` object in the API response for use by external metadata consumers.

**Architecture:** A new `MetadataResolutionService` runs after every enrichment write to compute `metadata_json["_resolved"]` by walking each field's plugin priority list and taking the first non-empty value. The resolved data promotes five first-class columns on the `MediaItem` row and is surfaced in the API response as `resolvedMetadata`. When the assignment config changes, a background batch pass recomputes `_resolved` for all affected items using only already-stored data — no network calls. A tiny `AssignmentConfigCache` singleton avoids DB round-trips on every enrichment write.

**Tech Stack:** .NET 9 / C#, Entity Framework Core 9, SQLite, System.Text.Json

---

## Repo paths

- Main repo: `W:\Scripts\Chronicle\`
- Solution: `W:\Scripts\Chronicle\src\Chronicle.sln`
- Services: `W:\Scripts\Chronicle\src\Chronicle.Services\`
- API: `W:\Scripts\Chronicle\src\Chronicle.API\`
- Unit tests: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\`
- Integration tests: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Integration\`

---

## Task 1: `AssignmentConfigCache` singleton

This singleton holds the parsed assignment config in memory so `MetadataResolutionService` doesn't hit the DB on every enrichment write. It is invalidated whenever the settings controller saves a new config.

**Files:**
- Create: `W:\Scripts\Chronicle\src\Chronicle.Services\AssignmentConfigCache.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\Program.cs`
- Create: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\AssignmentConfigCacheTests.cs`

**Step 1: Create the cache class**

```csharp
// W:\Scripts\Chronicle\src\Chronicle.Services\AssignmentConfigCache.cs
using System.Text.Json;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Services;

/// <summary>
/// Singleton in-memory cache for the metadata_assignment.config app setting.
/// The config is a small JSON blob (a few KB) — safe to hold in RAM indefinitely.
/// Call <see cref="Invalidate"/> when the config is saved to force a reload on next access.
/// </summary>
public class AssignmentConfigCache(IServiceScopeFactory scopeFactory)
{
    // null = not loaded yet (or invalidated). Dictionary is immutable once set.
    private volatile Dictionary<string, Dictionary<string, List<string>>>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns the field→pluginList priority map for the given media type + hierarchy level.
    /// Key format: "audiobooks" (level 0) or "audiobooks.2" (level > 0).
    /// Falls back to the base type key (e.g. "audiobooks") when a level-specific key is absent.
    /// Returns an empty dictionary when no config exists for this type.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetForTypeAsync(
        string mediaTypeName, int hierarchyLevel, CancellationToken ct = default)
    {
        var config = await LoadAsync(ct);

        var levelKey = hierarchyLevel > 0
            ? $"{mediaTypeName}.{hierarchyLevel}"
            : mediaTypeName;

        if (config.TryGetValue(levelKey, out var byLevel))
            return byLevel;

        // Level-specific key absent — try the base type key
        if (hierarchyLevel > 0 && config.TryGetValue(mediaTypeName, out var byBase))
            return byBase;

        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discards the cached config. The next call to <see cref="GetForTypeAsync"/>
    /// will reload from the database.
    /// </summary>
    public void Invalidate() =>
        Interlocked.Exchange(ref _cache, null);

    private async Task<Dictionary<string, Dictionary<string, List<string>>>> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } hit) return hit;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is { } doubleCheck) return doubleCheck;

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            var row = await db.AppSettings
                .FirstOrDefaultAsync(s => s.Key == "metadata_assignment.config", ct);

            _cache = row?.Value is not null
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(
                    row.Value,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
                : [];

            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

**Step 2: Register as singleton in `Program.cs`**

Find the block of `AddSingleton` / `AddScoped` registrations (around line 117) and add:

```csharp
builder.Services.AddSingleton<AssignmentConfigCache>();
```

**Step 3: Write unit tests**

```csharp
// W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\AssignmentConfigCacheTests.cs
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class AssignmentConfigCacheTests
{
    [Fact]
    public async Task GetForTypeAsync_NoConfig_ReturnsEmpty()
    {
        // When no config is stored, the result is an empty dictionary.
        var cache = AssignmentConfigCacheTestHelper.BuildWithJson(null);
        var result = await cache.GetForTypeAsync("audiobooks", 0);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForTypeAsync_LevelZeroKey_ReturnsCorrectMap()
    {
        const string json = """
            {
              "audiobooks": {
                "poster_url": ["hardcover", "chronicle.plugin.musicbrainz"],
                "title":      ["hardcover", "chronicle.plugin.musicbrainz"]
              }
            }
            """;

        var cache = AssignmentConfigCacheTestHelper.BuildWithJson(json);
        var result = await cache.GetForTypeAsync("audiobooks", 0);

        result["poster_url"].Should().Equal("hardcover", "chronicle.plugin.musicbrainz");
        result["title"].Should().Equal("hardcover", "chronicle.plugin.musicbrainz");
    }

    [Fact]
    public async Task GetForTypeAsync_LevelSpecificKey_TakesPrecedenceOverBase()
    {
        const string json = """
            {
              "audiobooks":   { "title": ["chronicle.plugin.musicbrainz"] },
              "audiobooks.2": { "title": ["hardcover"] }
            }
            """;

        var cache = AssignmentConfigCacheTestHelper.BuildWithJson(json);
        var result = await cache.GetForTypeAsync("audiobooks", 2);
        result["title"].Should().Equal("hardcover");
    }

    [Fact]
    public async Task GetForTypeAsync_LevelSpecificKeyAbsent_FallsBackToBase()
    {
        const string json = """
            { "audiobooks": { "title": ["hardcover"] } }
            """;

        var cache = AssignmentConfigCacheTestHelper.BuildWithJson(json);
        var result = await cache.GetForTypeAsync("audiobooks", 2);
        result["title"].Should().Equal("hardcover");
    }

    [Fact]
    public async Task Invalidate_ForcesReloadOnNextCall()
    {
        const string json = """{ "audiobooks": { "title": ["hardcover"] } }""";
        var cache = AssignmentConfigCacheTestHelper.BuildWithJson(json);

        // Warm the cache
        await cache.GetForTypeAsync("audiobooks", 0);

        // Invalidate + update the backing data
        cache.Invalidate();
        AssignmentConfigCacheTestHelper.UpdateJson(cache,
            """{ "audiobooks": { "title": ["chronicle.plugin.musicbrainz"] } }""");

        var result = await cache.GetForTypeAsync("audiobooks", 0);
        result["title"].Should().Equal("chronicle.plugin.musicbrainz");
    }
}
```

Because `AssignmentConfigCache` uses `IServiceScopeFactory` internally, the tests need a thin helper that bypasses the DB. Add this helper class in the same test file:

```csharp
internal static class AssignmentConfigCacheTestHelper
{
    private static string? _json;

    public static AssignmentConfigCache BuildWithJson(string? json)
    {
        _json = json;
        var factory = new FakeScopeFactory(_json);
        return new AssignmentConfigCache(factory);
    }

    public static void UpdateJson(AssignmentConfigCache cache, string json)
    {
        // Swap the backing JSON; invalidation already called by the test.
        _json = json;
    }

    private class FakeScopeFactory(string? json) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FakeScope(json);
    }

    private class FakeScope(string? json) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new FakeServiceProvider(json);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class FakeServiceProvider(string? json) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ChronicleDbContext))
                return new FakeDbContext(json);
            return null;
        }
    }
}
```

Note: `FakeDbContext` needs a bit more scaffolding. Given the project already has `ChronicleApiFactory` for integration tests, the simplest approach for unit tests is to test `GetForTypeAsync` through a thin seam. Alternatively, extract the JSON-parsing logic to a static `ParseConfig(string? json)` method that is unit-tested directly without DI, and test the DB-loading path via an integration test. **Recommended approach:** make `ParseConfig` internal static and test it directly.

Refactor `LoadAsync` to call:

```csharp
internal static Dictionary<string, Dictionary<string, List<string>>> ParseConfig(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return [];
    return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<string>>>>(
        json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
}
```

Then the unit tests call `ParseConfig` directly — no DI needed.

**Step 4: Run tests**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "AssignmentConfigCache" --verbosity normal
```

Expected: all pass.

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/AssignmentConfigCache.cs `
        src/Chronicle.API/Program.cs `
        tests/Chronicle.Tests.Unit/Services/AssignmentConfigCacheTests.cs
git commit -m "feat(services): add AssignmentConfigCache singleton"
```

---

## Task 2: `IMetadataResolutionService` + skeleton

**Files:**
- Create: `W:\Scripts\Chronicle\src\Chronicle.Services\IMetadataResolutionService.cs`
- Create: `W:\Scripts\Chronicle\src\Chronicle.Services\MetadataResolutionService.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\Program.cs`

**Step 1: Create the interface**

```csharp
// W:\Scripts\Chronicle\src\Chronicle.Services\IMetadataResolutionService.cs
using Chronicle.Core.Models;
using Chronicle.Data;

namespace Chronicle.Services;

public interface IMetadataResolutionService
{
    /// <summary>
    /// Recomputes metadata_json["_resolved"] for a single item, then promotes
    /// the 5 first-class columns (Name, Year, Overview, PosterUrl, RuntimeMinutes).
    /// Does NOT call SaveChangesAsync — the caller is responsible.
    /// </summary>
    Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default);

    /// <summary>
    /// Bulk recompute of _resolved for every item belonging to the given media type.
    /// Streams items in batches of 100 to bound memory usage.
    /// Calls SaveChangesAsync per batch.
    /// </summary>
    Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default);
}
```

**Step 2: Create the service skeleton with the field map**

```csharp
// W:\Scripts\Chronicle\src\Chronicle.Services\MetadataResolutionService.cs
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MetadataResolutionService(
    AssignmentConfigCache configCache,
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataResolutionService> logger) : IMetadataResolutionService
{
    private const int BatchSize = 100;

    // Maps assignment config snake_case field names → camelCase keys in metadata_json plugin blobs.
    private static readonly Dictionary<string, string> FieldMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["title"]           = "title",
            ["overview"]        = "overview",
            ["year"]            = "year",
            ["poster_url"]      = "posterUrl",
            ["backdrop_url"]    = "backdropUrl",
            ["runtime_minutes"] = "runtimeMinutes",
            ["rating"]          = "rating",
            ["genres"]          = "genres",
            ["cast"]            = "cast",
            ["directors"]       = "directors",
            ["tags"]            = "tags",
        };

    public Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
        => throw new NotImplementedException(); // filled in Task 3

    public Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
        => throw new NotImplementedException(); // filled in Task 4

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses metadata_json into a mutable dictionary keyed by plugin ID.
    /// Returns an empty dictionary if the JSON is absent or malformed.
    /// </summary>
    internal static Dictionary<string, JsonElement> ParsePluginBlobs(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = false })
                   ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Returns true when a JsonElement is considered "present" — i.e. not null,
    /// not an empty string, and not an empty array.
    /// </summary>
    internal static bool HasValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null      => false,
        JsonValueKind.Undefined => false,
        JsonValueKind.String    => !string.IsNullOrWhiteSpace(el.GetString()),
        JsonValueKind.Array     => el.GetArrayLength() > 0,
        _                       => true,
    };
}
```

**Step 3: Register as scoped in `Program.cs`**

```csharp
builder.Services.AddScoped<IMetadataResolutionService, MetadataResolutionService>();
```

**Step 4: Build to confirm no compile errors**

```powershell
cd W:\Scripts\Chronicle\src
dotnet build Chronicle.sln --verbosity minimal
```

Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/IMetadataResolutionService.cs `
        src/Chronicle.Services/MetadataResolutionService.cs `
        src/Chronicle.API/Program.cs
git commit -m "feat(services): add IMetadataResolutionService skeleton and AssignmentConfigCache"
```

---

## Task 3: Implement `ResolveAsync` + unit tests

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\MetadataResolutionService.cs`
- Create: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\MetadataResolutionServiceTests.cs`

**Step 1: Write failing unit tests first**

```csharp
// W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\MetadataResolutionServiceTests.cs
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class MetadataResolutionServiceTests
{
    // ── ParsePluginBlobs ──────────────────────────────────────────────────────

    [Fact]
    public void ParsePluginBlobs_NullInput_ReturnsEmpty()
    {
        var result = MetadataResolutionService.ParsePluginBlobs(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParsePluginBlobs_ValidJson_ReturnsBlobsKeyedByPluginId()
    {
        const string json = """
            {
              "hardcover":                    { "title": "Dune" },
              "chronicle.plugin.musicbrainz": { "title": "Dune (Audiobook)" }
            }
            """;
        var result = MetadataResolutionService.ParsePluginBlobs(json);
        result.Should().ContainKey("hardcover");
        result.Should().ContainKey("chronicle.plugin.musicbrainz");
    }

    // ── HasValue ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("null",   false)]
    [InlineData("\"\"",   false)]
    [InlineData("\"  \"", false)]
    [InlineData("\"ok\"", true)]
    [InlineData("42",     true)]
    [InlineData("[]",     false)]
    [InlineData("[1]",    true)]
    public void HasValue_VariousInputs_CorrectResult(string rawJson, bool expected)
    {
        var el = JsonDocument.Parse(rawJson).RootElement;
        MetadataResolutionService.HasValue(el).Should().Be(expected);
    }

    // ── ResolveAsync — priority waterfall ────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_HigherPriorityPluginWins()
    {
        // Hardcover has a poster; MusicBrainz also has one.
        // Assignment: hardcover first → Hardcover's poster should win.
        var item = BuildItem(
            mediaTypeName: "audiobooks",
            hierarchyLevel: 0,
            metadataJson: """
            {
              "hardcover":                    { "posterUrl": "https://hardcover.app/cover.jpg", "title": "Dune" },
              "chronicle.plugin.musicbrainz": { "posterUrl": "https://musicbrainz.org/cover.jpg", "title": "Dune" }
            }
            """);

        var config = new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["audiobooks"] = new()
            {
                ["poster_url"] = ["hardcover", "chronicle.plugin.musicbrainz"],
                ["title"]      = ["hardcover", "chronicle.plugin.musicbrainz"],
            }
        };

        await ResolveWithConfig(item, config);

        item.PosterUrl.Should().Be("https://hardcover.app/cover.jpg");
        item.Name.Should().Be("Dune");
        GetResolved(item).Should().ContainKey("posterUrl")
            .WhoseValue.GetString().Should().Be("https://hardcover.app/cover.jpg");
    }

    [Fact]
    public async Task ResolveAsync_PrimaryPluginMissingField_FallsBackToSecondary()
    {
        // Hardcover has no overview; MusicBrainz does.
        var item = BuildItem(
            mediaTypeName: "audiobooks",
            hierarchyLevel: 0,
            metadataJson: """
            {
              "hardcover":                    { "overview": "" },
              "chronicle.plugin.musicbrainz": { "overview": "A science fiction epic." }
            }
            """);

        var config = new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["audiobooks"] = new()
            {
                ["overview"] = ["hardcover", "chronicle.plugin.musicbrainz"],
            }
        };

        await ResolveWithConfig(item, config);

        item.Overview.Should().Be("A science fiction epic.");
        GetResolved(item)["overview"].GetString().Should().Be("A science fiction epic.");
    }

    [Fact]
    public async Task ResolveAsync_NoPluginHasValue_FieldAbsentFromResolved()
    {
        var item = BuildItem(
            mediaTypeName: "audiobooks",
            hierarchyLevel: 0,
            metadataJson: """
            {
              "hardcover":                    { "rating": null },
              "chronicle.plugin.musicbrainz": { "rating": null }
            }
            """);

        var config = new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["audiobooks"] = new() { ["rating"] = ["hardcover", "chronicle.plugin.musicbrainz"] }
        };

        await ResolveWithConfig(item, config);

        GetResolved(item).Should().NotContainKey("rating");
    }

    [Fact]
    public async Task ResolveAsync_NoConfigForType_DoesNotCrash()
    {
        // When there is no assignment config for this media type, _resolved should
        // be written as an empty object and first-class columns left unchanged.
        var item = BuildItem("music", 0, """{ "chronicle.plugin.musicbrainz": { "title": "Abbey Road" } }""");
        item.Name = "Abbey Road";

        await ResolveWithConfig(item, new Dictionary<string, Dictionary<string, List<string>>>());

        // Name unchanged (no config = no assignment = nothing to promote)
        item.Name.Should().Be("Abbey Road");
    }

    [Fact]
    public async Task ResolveAsync_TitleAndYearOnlyPromotedAtLevelZero()
    {
        // Level 1 (e.g. Series under Author): title in _resolved but Name NOT updated
        var item = BuildItem(
            mediaTypeName: "audiobooks",
            hierarchyLevel: 1,
            metadataJson: """{ "hardcover": { "title": "Stormlight Archive", "year": 2010 } }""");

        item.Name = "Stormlight Archive (original)";
        item.Year = null;

        var config = new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["audiobooks.1"] = new()
            {
                ["title"] = ["hardcover"],
                ["year"]  = ["hardcover"],
            }
        };

        await ResolveWithConfig(item, config);

        // _resolved populated
        GetResolved(item)["title"].GetString().Should().Be("Stormlight Archive");
        // BUT first-class Name/Year NOT changed at level > 0
        item.Name.Should().Be("Stormlight Archive (original)");
        item.Year.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MediaItem BuildItem(string mediaTypeName, int hierarchyLevel, string metadataJson) =>
        new()
        {
            Id             = 1,
            Name           = "Test",
            HierarchyLevel = hierarchyLevel,
            MetadataJson   = metadataJson,
            MediaType      = new MediaType { Id = 1, Name = mediaTypeName, DisplayName = mediaTypeName },
            MediaTypeId    = 1,
        };

    private static Dictionary<string, JsonElement> GetResolved(MediaItem item)
    {
        var blobs = MetadataResolutionService.ParsePluginBlobs(item.MetadataJson);
        blobs.TryGetValue("_resolved", out var resolvedEl);
        return resolvedEl.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resolvedEl.GetRawText()) ?? []
            : [];
    }

    private static async Task ResolveWithConfig(
        MediaItem item,
        Dictionary<string, Dictionary<string, List<string>>> config)
    {
        // Build a service backed by a pre-populated cache (no DB needed)
        var cache = new AssignmentConfigCache(null!); // ctor with null scopeFactory
        AssignmentConfigCache.InjectForTest(cache, config);   // see note below

        var svc = new MetadataResolutionService(cache, null!, NullLogger.Instance);
        await svc.ResolveAsync(item, null!, CancellationToken.None);
    }
}
```

**Note on testability:** `AssignmentConfigCache` takes `IServiceScopeFactory` to load from DB. For unit tests, add an `internal static void InjectForTest(AssignmentConfigCache cache, Dictionary<...> config)` method (guarded by `[assembly: InternalsVisibleTo("Chronicle.Tests.Unit")]`) that directly sets `_cache`. `ResolveAsync` also takes `ChronicleDbContext db` — since `SaveChangesAsync` is called by the caller, not by `ResolveAsync`, pass `null!` safely in unit tests (it is never used by `ResolveAsync` itself).

**Step 2: Run tests — confirm they FAIL**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MetadataResolutionService" --verbosity normal
```

Expected: compile error (NotImplementedException) or failing tests.

**Step 3: Implement `ResolveAsync`**

Replace the `throw new NotImplementedException()` stub in `MetadataResolutionService.cs`:

```csharp
public async Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
{
    var mediaTypeName = (item.MediaType?.Name
                      ?? db.MediaTypes.Local.FirstOrDefault(t => t.Id == item.MediaTypeId)?.Name
                      ?? string.Empty).ToLowerInvariant();

    var priorityMap = await configCache.GetForTypeAsync(mediaTypeName, item.HierarchyLevel, ct);

    var blobs = ParsePluginBlobs(item.MetadataJson);
    // Remove any stale _resolved key before recomputing
    blobs.Remove("_resolved");

    var resolved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    foreach (var (assignmentField, jsonKey) in FieldMap)
    {
        if (!priorityMap.TryGetValue(assignmentField, out var plugins) || plugins.Count == 0)
            continue; // no assignment for this field — skip

        foreach (var pluginId in plugins)
        {
            if (!blobs.TryGetValue(pluginId, out var blob)) continue;
            if (blob.ValueKind != JsonValueKind.Object) continue;
            if (!blob.TryGetProperty(jsonKey, out var val)) continue;
            if (!HasValue(val)) continue;

            resolved[jsonKey] = val;
            break; // first non-empty value wins
        }
    }

    // Write _resolved back into metadata_json
    blobs["_resolved"] = JsonSerializer.SerializeToElement(resolved);
    item.MetadataJson  = JsonSerializer.Serialize(blobs);

    // Promote first-class columns from _resolved
    if (resolved.TryGetValue("posterUrl", out var poster) && HasValue(poster))
        item.PosterUrl = poster.GetString();

    if (resolved.TryGetValue("overview", out var overview) && HasValue(overview))
        item.Overview = overview.GetString();

    if (resolved.TryGetValue("runtimeMinutes", out var rt) && rt.ValueKind == JsonValueKind.Number)
        item.RuntimeMinutes = rt.GetInt32();

    // title and year are only promoted at the root level (level 0)
    if (item.HierarchyLevel == 0)
    {
        if (resolved.TryGetValue("title", out var title) && HasValue(title))
            item.Name = title.GetString()!;

        if (resolved.TryGetValue("year", out var yr) && yr.ValueKind == JsonValueKind.Number)
            item.Year = yr.GetInt32();
    }

    logger.LogDebug(
        "Resolved metadata for item {ItemId} ({Name}): {FieldCount} fields resolved",
        item.Id, item.Name, resolved.Count);
}
```

Also add `InjectForTest` to `AssignmentConfigCache` (guarded by `InternalsVisibleTo`):

```csharp
// In AssignmentConfigCache.cs — inside the class body
#if DEBUG
internal static void InjectForTest(
    AssignmentConfigCache cache,
    Dictionary<string, Dictionary<string, List<string>>> config) =>
    cache._cache = config;
#endif
```

Add to `Chronicle.Services.csproj` if not already present:
```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
    <_Parameter1>Chronicle.Tests.Unit</_Parameter1>
  </AssemblyAttribute>
</ItemGroup>
```

**Step 4: Run tests — confirm they PASS**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MetadataResolutionService" --verbosity normal
```

Expected: all pass.

**Step 5: Run full suite to check for regressions**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal
```

Expected: all pass.

**Step 6: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/MetadataResolutionService.cs `
        src/Chronicle.Services/AssignmentConfigCache.cs `
        src/Chronicle.Services/Chronicle.Services.csproj `
        tests/Chronicle.Tests.Unit/Services/MetadataResolutionServiceTests.cs
git commit -m "feat(services): implement MetadataResolutionService.ResolveAsync with priority waterfall"
```

---

## Task 4: Implement `ResolveAllForMediaTypeAsync` (bulk batch pass)

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\MetadataResolutionService.cs`
- Modify: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\MetadataResolutionServiceTests.cs`

**Step 1: Write failing test**

Add to `MetadataResolutionServiceTests.cs`:

```csharp
[Fact]
public async Task ResolveAllForMediaTypeAsync_ProcessesAllItemsInBatches()
{
    // This verifies the batching logic doesn't skip items or crash on empty sets.
    // Full DB-wired behaviour is covered by integration tests (Task 8).
    // Here we just confirm the method exists and returns without error on an empty set.
    var cache  = BuildCacheWithConfig("audiobooks", "poster_url", ["hardcover"]);
    var svc    = new MetadataResolutionService(cache, BuildScopeFactoryWithNoItems(), NullLogger.Instance);

    var act = async () => await svc.ResolveAllForMediaTypeAsync("audiobooks");
    await act.Should().NotThrowAsync();
}
```

**Step 2: Implement `ResolveAllForMediaTypeAsync`**

```csharp
public async Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
{
    logger.LogInformation(
        "Starting bulk _resolved recompute for media type '{Type}'", mediaTypeName);

    int lastId    = 0;
    int totalDone = 0;

    while (true)
    {
        ct.ThrowIfCancellationRequested();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Keyset pagination — load one batch at a time, ordered by Id.
        var batch = await db.MediaItems
            .Include(m => m.MediaType)
            .Where(m => m.MediaType!.Name == mediaTypeName && m.Id > lastId)
            .OrderBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) break;

        foreach (var item in batch)
        {
            try   { await ResolveAsync(item, db, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "ResolveAllForMediaTypeAsync: failed to resolve item {ItemId}", item.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        lastId     = batch[^1].Id;
        totalDone += batch.Count;

        logger.LogDebug(
            "Bulk resolve progress: {Done} items processed for type '{Type}'",
            totalDone, mediaTypeName);
    }

    logger.LogInformation(
        "Bulk _resolved recompute complete: {Total} items for type '{Type}'",
        totalDone, mediaTypeName);
}
```

**Step 3: Run tests**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MetadataResolution" --verbosity normal
```

Expected: all pass.

**Step 4: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/MetadataResolutionService.cs `
        tests/Chronicle.Tests.Unit/Services/MetadataResolutionServiceTests.cs
git commit -m "feat(services): implement ResolveAllForMediaTypeAsync with keyset-paged batch processing"
```

---

## Task 5: Wire `ResolveAsync` into `MetadataEnrichmentService`

`ResolveAsync` must be called after every successful enrichment write — at both call sites where plugin data is merged into an item.

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\MetadataEnrichmentService.cs`

**Step 1: Inject `IMetadataResolutionService` into the constructor**

`MetadataEnrichmentService` currently takes `IServiceScopeFactory` and `ILogger`. Add the new service:

```csharp
public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    IMetadataResolutionService resolutionService,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
```

**Step 2: Call `ResolveAsync` after `MergeMetadata` (line ~1117)**

Find:
```csharp
MergeMetadata(row.MediaItem!, row.PluginId, result);
// Keep media_external_ids in sync...
await UpsertExternalIdForEnrichmentAsync(db, row.MediaItemId, result.ExternalId, ct, row.PluginId);
```

Replace with:
```csharp
MergeMetadata(row.MediaItem!, row.PluginId, result);
await resolutionService.ResolveAsync(row.MediaItem!, db, ct);
// Keep media_external_ids in sync...
await UpsertExternalIdForEnrichmentAsync(db, row.MediaItemId, result.ExternalId, ct, row.PluginId);
```

**Step 3: Call `ResolveAsync` after `MergeProviderResult` (line ~1819)**

Find:
```csharp
// 6. Merge losslessly
MergeProviderResult(item, pluginId, result);

// 7. Update row
row.ExternalId      = resolvedId;
```

Replace with:
```csharp
// 6. Merge losslessly
MergeProviderResult(item, pluginId, result);
await resolutionService.ResolveAsync(item, db, ct);

// 7. Update row
row.ExternalId      = resolvedId;
```

**Step 4: Build**

```powershell
cd W:\Scripts\Chronicle\src
dotnet build Chronicle.sln --verbosity minimal
```

Expected: 0 errors.

**Step 5: Run full test suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal
```

Expected: all pass (the new `ResolveAsync` calls are safe even when the assignment config is empty — they just write an empty `_resolved`).

**Step 6: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/MetadataEnrichmentService.cs
git commit -m "feat(enrichment): call ResolveAsync after every successful plugin enrichment write"
```

---

## Task 6: Wire `ResolveAllForMediaTypeAsync` into `SettingsController`

When the user saves a new metadata assignment config, automatically invalidate the cache and trigger a background bulk recompute for every media type whose assignment changed.

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\Controllers\SettingsController.cs`

**Step 1: Inject dependencies**

Find the `SettingsController` constructor and add `AssignmentConfigCache` and `IMetadataResolutionService`:

```csharp
public SettingsController(
    ChronicleDbContext db,
    IPluginRegistry registry,
    AssignmentConfigCache assignmentCache,
    IMetadataResolutionService resolutionService,
    /* existing params */)
```

**Step 2: Find the `PUT metadata-assignment` action**

Locate the method decorated with `[HttpPut("metadata-assignment")]` (around line 231 in `SettingsController.cs`). After saving the new config to the DB, add:

```csharp
// Invalidate the in-memory cache so the next enrichment picks up the new config.
assignmentCache.Invalidate();

// Determine which media types changed and trigger a background bulk recompute.
// We recompute all types present in the new config to be safe.
var changedTypes = request.Assignments.Keys
    .Select(k => k.Contains('.') ? k[..k.LastIndexOf('.')] : k)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

_ = Task.Run(async () =>
{
    foreach (var mediaType in changedTypes)
    {
        try   { await resolutionService.ResolveAllForMediaTypeAsync(mediaType); }
        catch (Exception ex)
        {
            // Log but do not surface to the user — this runs in the background.
            logger.LogWarning(ex,
                "Background _resolved recompute failed for media type '{Type}'", mediaType);
        }
    }
}, CancellationToken.None);
```

Add `ILogger<SettingsController> logger` to the constructor if not already present.

**Step 3: Build**

```powershell
cd W:\Scripts\Chronicle\src
dotnet build Chronicle.sln --verbosity minimal
```

Expected: 0 errors.

**Step 4: Run full test suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal
```

Expected: all pass.

**Step 5: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.API/Controllers/SettingsController.cs
git commit -m "feat(api): invalidate assignment cache and trigger bulk resolve on config save"
```

---

## Task 7: `ResolvedMetadataDto` + update `MediaItemDto` and API response

**Files:**
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\DTOs\MediaDTOs.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\Controllers\MediaController.cs`

**Step 1: Add `ResolvedMetadataDto` to `MediaDTOs.cs`**

Append at the bottom of the file:

```csharp
/// <summary>
/// The authoritative merged metadata for a media item, computed from the
/// assignment config by walking each field's plugin priority list.
/// All field values come from the highest-priority plugin that has a non-empty value.
/// Null fields mean no plugin provided a value for that field.
/// </summary>
public record ResolvedMetadataDto(
    string?        Title,
    string?        Overview,
    int?           Year,
    string?        PosterUrl,
    string?        BackdropUrl,
    int?           RuntimeMinutes,
    double?        Rating,
    List<string>?  Genres,
    List<string>?  Cast,
    List<string>?  Directors,
    List<string>?  Tags
);
```

**Step 2: Add `ResolvedMetadata` to `MediaItemDto`**

Add as an optional parameter at the end of the `MediaItemDto` record (after `HasMetadataOnly`):

```csharp
/// <summary>
/// Authoritative merged metadata resolved from the assignment config.
/// Null when no enrichment has run for this item.
/// Intended for use by external applications consuming Chronicle as a metadata source.
/// </summary>
ResolvedMetadataDto? ResolvedMetadata = null
```

**Step 3: Exclude `_resolved` from `pluginMeta` in `MediaController`**

Find `_firstClassKeys`:

```csharp
private static readonly HashSet<string> _firstClassKeys =
    new(StringComparer.OrdinalIgnoreCase) { "fileScanner" };
```

Add `_resolved` so it never appears as a PluginMetadataBox in the UI:

```csharp
private static readonly HashSet<string> _firstClassKeys =
    new(StringComparer.OrdinalIgnoreCase) { "fileScanner", "_resolved" };
```

**Step 4: Populate `ResolvedMetadata` in `MapToMediaItemDto`**

In `MediaController.cs`, find the private static helper that builds `MediaItemDto` (the method containing `return new MediaItemDto(...)` around line 477). Before the `return` statement, add:

```csharp
ResolvedMetadataDto? resolvedMetadata = null;
if (!string.IsNullOrEmpty(m.MetadataJson))
{
    try
    {
        using var doc = JsonDocument.Parse(m.MetadataJson);
        if (doc.RootElement.TryGetProperty("_resolved", out var r) &&
            r.ValueKind == JsonValueKind.Object)
        {
            resolvedMetadata = new ResolvedMetadataDto(
                Title:          TryGetString(r,  "title"),
                Overview:       TryGetString(r,  "overview"),
                Year:           TryGetInt(r,     "year"),
                PosterUrl:      TryGetString(r,  "posterUrl"),
                BackdropUrl:    TryGetString(r,  "backdropUrl"),
                RuntimeMinutes: TryGetInt(r,     "runtimeMinutes"),
                Rating:         TryGetDouble(r,  "rating"),
                Genres:         TryGetStringList(r, "genres"),
                Cast:           TryGetStringList(r, "cast"),
                Directors:      TryGetStringList(r, "directors"),
                Tags:           TryGetStringList(r, "tags")
            );
        }
    }
    catch { /* malformed JSON — leave resolvedMetadata null */ }
}
```

Then add `ResolvedMetadata: resolvedMetadata` to the `new MediaItemDto(...)` call.

Add the following small private static helpers to the same controller class (or the same inner static class that contains `ParseMetaJson`):

```csharp
private static string? TryGetString(JsonElement el, string key) =>
    el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() : null;

private static int? TryGetInt(JsonElement el, string key) =>
    el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetInt32() : null;

private static double? TryGetDouble(JsonElement el, string key) =>
    el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetDouble() : null;

private static List<string>? TryGetStringList(JsonElement el, string key)
{
    if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
        return null;
    var list = v.EnumerateArray()
        .Where(x => x.ValueKind == JsonValueKind.String)
        .Select(x => x.GetString()!)
        .ToList();
    return list.Count > 0 ? list : null;
}
```

**Step 5: Apply the same `ResolvedMetadata` population to the other two `new MediaItemDto(...)` call sites**

There are two other sites in `FileScanController.cs` (line ~224) and `LibraryController.cs` (line ~256). In both cases, `resolvedMetadata` will be `null` — simply add `ResolvedMetadata: null` to those call sites so they compile.

**Step 6: Build**

```powershell
cd W:\Scripts\Chronicle\src
dotnet build Chronicle.sln --verbosity minimal
```

Expected: 0 errors.

**Step 7: Frontend type-check**

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web
npm run type-check
```

Expected: 0 errors. (`resolvedMetadata` is an optional field added to the DTO — no existing TypeScript needs to change unless you want to consume it.)

**Step 8: Commit**

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.API/DTOs/MediaDTOs.cs `
        src/Chronicle.API/Controllers/MediaController.cs `
        src/Chronicle.API/Controllers/FileScanController.cs `
        src/Chronicle.API/Controllers/LibraryController.cs
git commit -m "feat(api): add ResolvedMetadataDto and resolvedMetadata field to MediaItemDto response"
```

---

## Task 8: Integration tests

**Files:**
- Create: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Integration\MetadataResolutionTests.cs`

**Step 1: Write the tests**

```csharp
// W:\Scripts\Chronicle\tests\Chronicle.Tests.Integration\MetadataResolutionTests.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration;

public class MetadataResolutionTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public MetadataResolutionTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    [Fact]
    public async Task ResolvedMetadata_AfterEnrichment_ReflectsAssignmentPriority()
    {
        // 1. Register + login
        var client = await AuthenticatedClientAsync();

        // 2. Create a media type and media item via the API
        // (seed the assignment config so hardcover wins for poster_url)
        // ... seed the DB directly via ChronicleApiFactory.DbContext
        // This test seeds:
        //   - media_type: audiobooks
        //   - media_item with metadata_json containing hardcover + musicbrainz blobs
        //   - app_settings: metadata_assignment.config with hardcover first for poster_url
        // Then calls POST /enrichment/chronicle.plugin.musicbrainz/run to trigger resolve
        // and verifies GET /media/{id} returns resolvedMetadata.posterUrl from hardcover.

        // Seed config
        await client.PutAsJsonAsync("/api/v1/settings/metadata-assignment", new
        {
            assignments = new Dictionary<string, object>
            {
                ["audiobooks"] = new Dictionary<string, object>
                {
                    ["poster_url"] = new[] { "hardcover", "chronicle.plugin.musicbrainz" },
                    ["title"]      = new[] { "hardcover", "chronicle.plugin.musicbrainz" },
                    ["overview"]   = new[] { "hardcover", "chronicle.plugin.musicbrainz" },
                }
            }
        });

        // Seed a media item directly (using the factory's DB context)
        var itemId = await _factory.SeedAudiobookWithPluginDataAsync(
            hardcoverPoster: "https://hardcover.app/cover.jpg",
            musicbrainzPoster: "https://mb.org/cover.jpg");

        // Force resolution by hitting the resolve endpoint
        // (or trigger via enrichment if a resolve-only endpoint exists)
        // For now: patch metadata directly through the factory and call ResolveAsync
        await _factory.TriggerResolutionAsync(itemId);

        // Verify
        var resp = await client.GetAsync($"/api/v1/media/{itemId}");
        resp.EnsureSuccessStatusCode();
        var body   = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var data   = body.RootElement.GetProperty("data");
        var resolved = data.GetProperty("resolvedMetadata");

        resolved.GetProperty("posterUrl").GetString()
            .Should().Be("https://hardcover.app/cover.jpg",
                because: "hardcover is assigned higher priority than musicbrainz for poster_url");
    }
}
```

**Note:** `SeedAudiobookWithPluginDataAsync` and `TriggerResolutionAsync` are helpers to add to `ChronicleApiFactory`. They directly manipulate the DB context to seed test data and call the service layer. Add them as `internal` methods on `ChronicleApiFactory`.

**Step 2: Run tests**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --filter "MetadataResolution" --verbosity normal
```

Expected: all pass.

**Step 3: Run full suite**

```powershell
cd W:\Scripts\Chronicle
dotnet test src/Chronicle.sln --verbosity minimal
```

Expected: all pass.

**Step 4: Commit**

```powershell
cd W:\Scripts\Chronicle
git add tests/Chronicle.Tests.Integration/MetadataResolutionTests.cs
git commit -m "test(integration): add MetadataResolutionTests verifying assignment priority enforcement"
```

---

## Task 9: Push and smoke test

**Step 1: Push to GitHub**

```powershell
cd W:\Scripts\Chronicle
git push
```

**Step 2: Start the API and verify `resolvedMetadata` appears**

```powershell
# Start the API (Development environment)
cd W:\Scripts\Chronicle\src\Chronicle.API
dotnet run
```

In the browser or via curl, hit any enriched audiobook item:

```
GET http://localhost:7979/api/v1/media/{id}
Authorization: Bearer {token}
```

Verify the response includes:
```json
{
  "data": {
    "resolvedMetadata": {
      "title": "...",
      "posterUrl": "...",
      "overview": "...",
      ...
    }
  }
}
```

**Step 3: Verify `_resolved` not visible as a plugin box**

Confirm `resolvedMetadata` does not appear as an entry in `pluginMetadata` in the response (it should be absent from that dictionary).

**Step 4: Verify config change triggers bulk recompute**

1. Open Settings → Metadata Assignment
2. Swap the priority order for one field
3. Save
4. Wait ~5 seconds
5. Re-fetch an enriched item — confirm `resolvedMetadata` reflects the new priority

---

## Smoke test checklist

- [ ] `GET /api/v1/media/{audiobook-id}` — `resolvedMetadata` present and non-null
- [ ] `resolvedMetadata.posterUrl` comes from the highest-priority plugin that has a poster
- [ ] `resolvedMetadata.rating`, `resolvedMetadata.genres`, `resolvedMetadata.cast` populated from correct plugin
- [ ] Field absent from `resolvedMetadata` when no plugin has a value (not null, truly absent)
- [ ] Changing assignment config → bulk recompute fires automatically → `resolvedMetadata` updates
- [ ] `_resolved` key does not appear as a plugin metadata box on the media detail page
- [ ] All 348 tests still passing
