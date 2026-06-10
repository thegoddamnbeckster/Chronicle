# Movie Collections Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Group movies into TMDB-sourced collections (e.g. "The Avengers Collection") in the library, with a toggleable "Group movies into collections" preference that keeps all existing flat-view behavior when off.

**Architecture:** Collections are real `MediaItem` rows at `HierarchyLevel 0` inside the `movies` media type (same approach as Audiobook Authors). Individual movies become Level 1 children when they belong to a collection; standalone movies stay at Level 0. The `belongs_to_collection` field already exists in TMDB's `/movie/{id}` response — the TMDB plugin must start capturing and forwarding it. A new `MovieCollectionService` runs after TMDB enrichment completes to create/find the collection parent item and re-parent the movie. The library API grows an `includeMoviesInCollections` param so the frontend can show individual movies even when they are logically nested under a collection. A `CollectionMetadataBox` component on the movie detail page shows all films in the collection and which ones are in the library.

**Tech Stack:** C# / .NET 9 / EF Core 9 / ASP.NET Core; React 18 + TypeScript + TanStack Query v5; SQLite; `Chronicle.Plugin.TMDB` separate repo at `W:\Scripts\Chronicle.Plugin.TMDB`

---

## Important conventions

- All C# files: 4-space indent, PascalCase public, `_camelCase` private fields, constructor injection.
- All TypeScript: strict, no `any`, functional components.
- TMDB plugin lives in a **separate repo** at `W:\Scripts\Chronicle.Plugin.TMDB`. After changing it you must rebuild and copy DLLs; instructions are in Task 2.
- Migration timestamps use `YYYYMMDDHHmmss` format. Use a timestamp later than `20260606200301` (latest existing migration).
- Run tests with `cd W:\Scripts\Chronicle\tests && dotnet test --verbosity normal`.
- Backend builds with `cd W:\Scripts\Chronicle\src && dotnet build Chronicle.sln`.

---

## Task 1 — Capture `belongs_to_collection` in TMDB plugin

### Files
- Modify: `W:\Scripts\Chronicle.Plugin.TMDB\TmdbModels.cs`
- Modify: `W:\Scripts\Chronicle.Plugin.TMDB\TmdbMetadataProvider.cs` (MapMovie method)

### Context
`TmdbMovie` currently lacks the `belongs_to_collection` field. TMDB's `/movie/{id}` always returns it (null when movie has no collection). We need it in `ExtendedData` so `MovieCollectionService` can read it after enrichment.

### Step 1: Add `TmdbBelongsToCollection` record and update `TmdbMovie`

In `TmdbModels.cs`, after the `TmdbMovie` record (line 28), add:

```csharp
internal record TmdbBelongsToCollection(
    [property: JsonPropertyName("id")]            int     Id,
    [property: JsonPropertyName("name")]          string  Name,
    [property: JsonPropertyName("poster_path")]   string? PosterPath,
    [property: JsonPropertyName("backdrop_path")] string? BackdropPath
);
```

Then add the `belongs_to_collection` property to the `TmdbMovie` record (after the `credits` field):

```csharp
internal record TmdbMovie(
    [property: JsonPropertyName("id")]                   int       Id,
    [property: JsonPropertyName("title")]                string    Title,
    [property: JsonPropertyName("overview")]             string?   Overview,
    [property: JsonPropertyName("release_date")]         string?   ReleaseDate,
    [property: JsonPropertyName("poster_path")]          string?   PosterPath,
    [property: JsonPropertyName("backdrop_path")]        string?   BackdropPath,
    [property: JsonPropertyName("runtime")]              int?      Runtime,
    [property: JsonPropertyName("vote_average")]         double?   VoteAverage,
    [property: JsonPropertyName("popularity")]           double?   Popularity,
    [property: JsonPropertyName("genres")]               List<TmdbGenre>? Genres,
    [property: JsonPropertyName("credits")]              TmdbCredits?     Credits,
    [property: JsonPropertyName("belongs_to_collection")] TmdbBelongsToCollection? BelongsToCollection
);
```

### Step 2: Expose collection data in `MapMovie`

In `TmdbMetadataProvider.cs`, find the `MapMovie` method (~line 506). Update `ExtendedData` to include collection info:

```csharp
private MediaMetadata MapMovie(TmdbMovie m) => new()
{
    ExternalId      = $"movie:{m.Id}",
    Source          = "tmdb",
    Title           = m.Title,
    Overview        = m.Overview,
    Year            = ParseYear(m.ReleaseDate),
    PosterUrl       = m.PosterPath   is not null ? _client!.BuildImageUrl(m.PosterPath,   _posterSize)   : null,
    BackdropUrl     = m.BackdropPath is not null ? _client!.BuildImageUrl(m.BackdropPath, _backdropSize) : null,
    RuntimeMinutes  = m.Runtime,
    Rating          = m.VoteAverage,
    Genres          = m.Genres?.Select(g => g.Name).ToList() ?? [],
    Cast            = m.Credits?.Cast?.OrderBy(c => c.Order).Select(c => c.Name).Take(10).ToList() ?? [],
    Directors       = m.Credits?.Crew?.Where(c => c.Job == "Director").Select(c => c.Name).ToList() ?? [],
    ExtendedData    = System.Text.Json.JsonSerializer.SerializeToElement(new
    {
        popularity = m.Popularity,
        belongsToCollection = m.BelongsToCollection is null ? null : new
        {
            id           = m.BelongsToCollection.Id,
            name         = m.BelongsToCollection.Name,
            posterPath   = m.BelongsToCollection.PosterPath  is not null
                              ? _client!.BuildImageUrl(m.BelongsToCollection.PosterPath, _posterSize) : null,
            backdropPath = m.BelongsToCollection.BackdropPath is not null
                              ? _client!.BuildImageUrl(m.BelongsToCollection.BackdropPath, _backdropSize) : null,
        },
    }),
};
```

### Step 3: Add `GetCollectionAsync` to `TmdbClient` (for the detail page endpoint)

In `TmdbClient.cs`, after `GetMovieAsync`, add:

```csharp
public Task<TmdbCollection> GetCollectionAsync(int collectionId, CancellationToken ct = default)
{
    var url = $"{BaseUrl}/collection/{collectionId}?api_key={_apiKey}&language={_language}";
    return GetAsync<TmdbCollection>(url, ct);
}
```

In `TmdbModels.cs`, add the collection detail model:

```csharp
internal sealed class TmdbCollection
{
    [JsonPropertyName("id")]           public int Id { get; set; }
    [JsonPropertyName("name")]         public string Name { get; set; } = string.Empty;
    [JsonPropertyName("overview")]     public string? Overview { get; set; }
    [JsonPropertyName("poster_path")]  public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("parts")]        public List<TmdbCollectionPart>? Parts { get; set; }
}

internal sealed class TmdbCollectionPart
{
    [JsonPropertyName("id")]           public int Id { get; set; }
    [JsonPropertyName("title")]        public string? Title { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("poster_path")]  public string? PosterPath { get; set; }
}
```

### Step 4: Build and deploy the TMDB plugin

```powershell
cd W:\Scripts\Chronicle.Plugin.TMDB
dotnet build -c Release
# Copy DLLs to Chronicle plugins directory
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.TMDB.dll" "W:\Scripts\Chronicle\plugins\chronicle.plugin.tmdb\"
```

### Step 5: Verify build passes

```powershell
cd W:\Scripts\Chronicle.Plugin.TMDB
dotnet build
```
Expected: Build succeeded, 0 errors.

### Step 6: Commit (TMDB plugin repo)

```powershell
cd W:\Scripts\Chronicle.Plugin.TMDB
git add TmdbModels.cs TmdbMetadataProvider.cs TmdbClient.cs
git commit -m "feat(tmdb): capture belongs_to_collection in movie ExtendedData"
```

---

## Task 2 — Migration: update movies media type to 2 hierarchy levels

### Files
- Create: `W:\Scripts\Chronicle\src\Chronicle.Data\Migrations\20260608120000_UpdateMoviesHierarchyForCollections.cs`

### Context
The `movies` media type currently has `HierarchyLevels = 1` and `HierarchyLabels = null`. Collections are Level 0, individual movies become Level 1. Fan Edits (`fanedits`) stays at 1 level — fan edits are not grouped into collections.

### Step 1: Create the migration file

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chronicle.Data.Migrations
{
    public partial class UpdateMoviesHierarchyForCollections : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Name",
                keyValue: "movies",
                columns: new[] { "HierarchyLevels", "HierarchyLabels" },
                values: new object[] { 2, "Collection,Movie" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "media_types",
                keyColumn: "Name",
                keyValue: "movies",
                columns: new[] { "HierarchyLevels", "HierarchyLabels" },
                values: new object[] { 1, null });
        }
    }
}
```

> **Note:** EF Core's `UpdateData` by key column name is not always available depending on the migration builder version. If this fails, use raw SQL instead:
> ```csharp
> migrationBuilder.Sql(
>     "UPDATE media_types SET HierarchyLevels = 2, HierarchyLabels = 'Collection,Movie' WHERE Name = 'movies'");
> ```

### Step 2: Apply migration

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.API
dotnet ef database update
```
Expected: `Applying migration '20260608120000_UpdateMoviesHierarchyForCollections'`... Done.

### Step 3: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Data/Migrations/20260608120000_UpdateMoviesHierarchyForCollections.cs
git commit -m "feat(data): update movies media type to 2 hierarchy levels for collections"
```

---

## Task 3 — `IMovieCollectionService` and `MovieCollectionService`

### Files
- Create: `W:\Scripts\Chronicle\src\Chronicle.Services\IMovieCollectionService.cs`
- Create: `W:\Scripts\Chronicle\src\Chronicle.Services\MovieCollectionService.cs`

### Context
After TMDB enrichment saves `belongs_to_collection` into `metadata_json["chronicle.plugin.tmdb"]`, this service:
1. Reads the collection id/name/poster from that JSON blob
2. Finds or creates a `MediaItem` for the collection (same media type as the movie, HierarchyLevel 0)
3. Re-parents the movie (`ParentId = collection.Id`, `HierarchyLevel = 1`)
4. Stores `collection:{id}` in `media_external_ids` for the collection item

### Step 1: Create the interface

```csharp
// W:\Scripts\Chronicle\src\Chronicle.Services\IMovieCollectionService.cs
using Chronicle.Core.Models;
using Chronicle.Data;

namespace Chronicle.Services;

public interface IMovieCollectionService
{
    /// <summary>
    /// Inspects the movie item's TMDB metadata for belongs_to_collection data.
    /// If found, ensures a Collection parent MediaItem exists and re-parents the movie under it.
    /// No-op if the movie has no collection data or media type is not "movies".
    /// </summary>
    Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        CancellationToken ct = default);
}
```

### Step 2: Create the implementation

```csharp
// W:\Scripts\Chronicle\src\Chronicle.Services\MovieCollectionService.cs
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public class MovieCollectionService(ILogger<MovieCollectionService> logger) : IMovieCollectionService
{
    private const string TmdbPluginKey = "chronicle.plugin.tmdb";

    public async Task EnsureCollectionParentAsync(
        ChronicleDbContext db,
        MediaItem movieItem,
        CancellationToken ct = default)
    {
        // Only group "movies" type — not fanedits, not tv, not anything else
        if (movieItem.MediaType is null ||
            !string.Equals(movieItem.MediaType.Name, "movies", StringComparison.OrdinalIgnoreCase))
            return;

        var collectionData = ExtractCollectionData(movieItem.MetadataJson);
        if (collectionData is null)
        {
            // Movie has no collection — ensure it is at root level
            if (movieItem.ParentId is not null)
            {
                movieItem.ParentId = null;
                movieItem.HierarchyLevel = 0;
                movieItem.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        // Find or create the collection MediaItem
        var externalIdValue = $"collection:{collectionData.Id}";
        var collection = await FindOrCreateCollectionAsync(
            db, movieItem.MediaTypeId, collectionData, externalIdValue, ct);

        // Re-parent the movie if needed
        if (movieItem.ParentId != collection.Id)
        {
            movieItem.ParentId = collection.Id;
            movieItem.HierarchyLevel = 1;
            movieItem.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Movie {ItemId} \"{Name}\" re-parented under collection {CollectionId} \"{CollectionName}\"",
                movieItem.Id, movieItem.Name, collection.Id, collection.Name);
        }
    }

    private static CollectionData? ExtractCollectionData(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;

            // Try full plugin key first, then short "tmdb" fallback
            JsonElement tmdbEl;
            if (!root.TryGetProperty(TmdbPluginKey, out tmdbEl) &&
                !root.TryGetProperty("tmdb", out tmdbEl))
                return null;

            if (!tmdbEl.TryGetProperty("belongsToCollection", out var collEl) ||
                collEl.ValueKind == JsonValueKind.Null)
                return null;

            if (!collEl.TryGetProperty("id", out var idEl) ||
                !collEl.TryGetProperty("name", out var nameEl))
                return null;

            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) return null;

            string? posterUrl = collEl.TryGetProperty("posterPath", out var pEl)
                ? pEl.GetString() : null;

            return new CollectionData(
                idEl.GetInt32(),
                name,
                posterUrl);
        }
        catch { return null; }
    }

    private async Task<MediaItem> FindOrCreateCollectionAsync(
        ChronicleDbContext db,
        int mediaTypeId,
        CollectionData data,
        string externalIdValue,
        CancellationToken ct)
    {
        // Try to find by external ID first (most reliable)
        var existing = await db.MediaExternalIds
            .Where(e => e.ExternalId == externalIdValue && e.Source == "tmdb")
            .Select(e => e.MediaItem)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            // Update poster if it changed
            if (existing.PosterUrl != data.PosterUrl && data.PosterUrl is not null)
            {
                existing.PosterUrl = data.PosterUrl;
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return existing;
        }

        // Create new collection MediaItem
        var now = DateTime.UtcNow;
        var collection = new MediaItem
        {
            MediaTypeId    = mediaTypeId,
            Name           = data.Name,
            HierarchyLevel = 0,
            PosterUrl      = data.PosterUrl,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        db.MediaItems.Add(collection);
        await db.SaveChangesAsync(ct);

        // Store external ID
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = collection.Id,
            Source      = "tmdb",
            ExternalId  = externalIdValue,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created collection MediaItem {Id} \"{Name}\" (ExternalId={ExternalId})",
            collection.Id, collection.Name, externalIdValue);

        return collection;
    }

    private record CollectionData(int Id, string Name, string? PosterUrl);
}
```

### Step 3: Register in DI

In `W:\Scripts\Chronicle\src\Chronicle.API\Program.cs`, find where other services are registered and add:

```csharp
builder.Services.AddScoped<IMovieCollectionService, MovieCollectionService>();
```

### Step 4: Build and run tests

```powershell
cd W:\Scripts\Chronicle\src && dotnet build Chronicle.sln
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity normal
```
Expected: Build succeeded. Tests pass (no MovieCollectionService unit tests yet — added in Task 8).

### Step 5: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/IMovieCollectionService.cs src/Chronicle.Services/MovieCollectionService.cs src/Chronicle.API/Program.cs
git commit -m "feat(services): add MovieCollectionService to create and parent movie collection items"
```

---

## Task 4 — Hook `MovieCollectionService` into `MetadataEnrichmentService`

### Files
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\MetadataEnrichmentService.cs`

### Context
After a successful TMDB enrichment (`MergeMetadata` writes the TMDB blob), `EnrichOneAsync` must call `movieCollectionService.EnsureCollectionParentAsync`. The service needs `db` (already in scope) and the `mediaItem` (already in `row.MediaItem`). 

There's also a hierarchy validation check at ~line 824 that must be updated: it currently rejects `"movie"` entity types for Level 1 items. A movie under a collection parent is legitimately Level 1 with a `"movie:"` ExternalId, so the check needs a bypass for collection children.

### Step 1: Inject `IMovieCollectionService` into `MetadataEnrichmentService`

Find the constructor (line 15):

```csharp
public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    IMetadataResolutionService resolutionService,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
```

Change to:

```csharp
public class MetadataEnrichmentService(
    IServiceScopeFactory scopeFactory,
    IMetadataResolutionService resolutionService,
    IMovieCollectionService movieCollectionService,
    ILogger<MetadataEnrichmentService> logger) : IMetadataEnrichmentService
```

### Step 2: Call `EnsureCollectionParentAsync` after successful enrichment

Find the block at ~line 1255 where `row.Status = EnrichmentStatus.Completed` is set:

```csharp
row.ExternalId      = result.ExternalId;
row.Status          = EnrichmentStatus.Completed;
row.LastCompletedAt = DateTime.UtcNow;
row.ErrorMessage    = null;
MergeMetadata(row.MediaItem!, row.PluginId, result);
await resolutionService.ResolveAsync(row.MediaItem!, db, ct);
await UpsertExternalIdForEnrichmentAsync(db, row.MediaItemId, result.ExternalId, ct, row.PluginId);
```

After the `UpsertExternalIdForEnrichmentAsync` line, add:

```csharp
// If this is a TMDB movie enrichment, ensure collection parent exists and re-parent if needed.
// Load MediaType navigation if not already present (needed by EnsureCollectionParentAsync).
if (row.MediaItem!.MediaType is null)
    await db.Entry(row.MediaItem).Reference(m => m.MediaType).LoadAsync(ct);
await movieCollectionService.EnsureCollectionParentAsync(db, row.MediaItem!, ct);
```

### Step 3: Fix hierarchy validation to allow `"movie"` ExternalId on Level-1 collection children

Find the hierarchy validation section at ~line 824:

```csharp
if (row.MediaItem.ParentId == null)
{
    // Root item — must be artist (MusicBrainz) or movie/show (TMDB)
    idIsValid = entityType is "artist" or "movie" or "tv";
}
else
{
    var parent = await db.MediaItems
        .AsNoTracking()
        .FirstOrDefaultAsync(m => m.Id == row.MediaItem.ParentId, ct);
    if (parent?.ParentId == null)
    {
        // Season/album level — season-specific TMDB ID must contain "/season:"
        if (entityType == "tv")
            idIsValid = row.ExternalId.Contains("/season:", ...) || ...;
        else
            idIsValid = entityType is "release-group" or "season" or "album";
    }
    ...
```

Replace the `parent?.ParentId == null` branch to also accept `"movie"` when parent is a collection:

```csharp
if (parent?.ParentId == null)
{
    // This could be: season/album level OR a movie under a collection.
    // Check if the parent is a movies-type collection (HierarchyLevel 0, same MediaType as a movie).
    // If so, a "movie" entity type is valid at this depth.
    bool parentIsMovieCollection = false;
    if (entityType == "movie" && parent is not null)
    {
        var parentMediaType = await db.MediaTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == parent.MediaTypeId, ct);
        parentIsMovieCollection = string.Equals(
            parentMediaType?.Name, "movies", StringComparison.OrdinalIgnoreCase);
    }

    if (parentIsMovieCollection)
    {
        idIsValid = true; // movie under a collection — valid
    }
    else if (entityType == "tv")
    {
        idIsValid = row.ExternalId.Contains("/season:", StringComparison.OrdinalIgnoreCase)
                 || row.ExternalId.Contains(":s", StringComparison.OrdinalIgnoreCase);
    }
    else
    {
        idIsValid = entityType is "release-group" or "season" or "album";
    }
}
```

### Step 4: Build and test

```powershell
cd W:\Scripts\Chronicle\src && dotnet build Chronicle.sln
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity normal
```
Expected: Build succeeded, all tests pass.

### Step 5: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/MetadataEnrichmentService.cs
git commit -m "feat(enrichment): call MovieCollectionService after successful movie enrichment"
```

---

## Task 5 — Library API: `includeMoviesInCollections` param

### Files
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\LibraryService.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Services\ILibraryService.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\Controllers\LibraryController.cs`

### Context
When "Group movies into collections" is OFF, the frontend needs individual movies even if they are parented under a collection item. Currently `rootOnly=true` hides any item with `ParentId != null`. The new `includeMoviesInCollections=true` param widens the query to also include Level 1 items whose parent is a `movies` collection.

### Step 1: Update `ILibraryService`

Find `GetForUserAsync` signature in `ILibraryService.cs`. Add parameter:

```csharp
Task<IEnumerable<UserLibrary>> GetForUserAsync(
    int userId,
    LibraryStatus? status = null,
    int page = 1,
    int perPage = 20,
    bool rootOnly = false,
    bool includeMoviesInCollections = false,
    CancellationToken ct = default);
```

### Step 2: Update `LibraryService.GetForUserAsync`

Find `GetForUserAsync` in `LibraryService.cs`. The `rootOnly` filter (line ~59) currently reads:

```csharp
if (rootOnly)
    itemsQuery = itemsQuery.Where(m => m.ParentId == null);
```

Replace with:

```csharp
if (rootOnly)
{
    if (includeMoviesInCollections)
    {
        // Include root items AND movies that are children of collection parents.
        // A "collection parent" is a movies-type item at HierarchyLevel 0 that exists
        // only as a grouping container (it has no tracked library entry itself by convention).
        itemsQuery = itemsQuery
            .Include(m => m.Parent)
                .ThenInclude(p => p!.MediaType)
            .Where(m => m.ParentId == null ||
                        (m.HierarchyLevel == 1 &&
                         m.Parent != null &&
                         m.Parent.MediaType != null &&
                         m.Parent.MediaType.Name == "movies"));
    }
    else
    {
        itemsQuery = itemsQuery.Where(m => m.ParentId == null);
    }
}
```

### Step 3: Update `LibraryController.GetLibrary`

Add the new query param:

```csharp
[HttpGet]
public async Task<IActionResult> GetLibrary(
    [FromQuery] string? status,
    [FromQuery] int page = 1,
    [FromQuery] int perPage = 20,
    [FromQuery] bool rootOnly = false,
    [FromQuery] bool includeMoviesInCollections = false,
    CancellationToken ct = default)
{
    ...
    var entries = await _libraryService.GetForUserAsync(
        userId, parsedStatus, page, perPage, rootOnly, includeMoviesInCollections, ct);
    ...
```

### Step 4: Build and test

```powershell
cd W:\Scripts\Chronicle\src && dotnet build Chronicle.sln
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity normal
```

### Step 5: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Services/ILibraryService.cs src/Chronicle.Services/LibraryService.cs src/Chronicle.API/Controllers/LibraryController.cs
git commit -m "feat(library): add includeMoviesInCollections param to flatten grouped movies"
```

---

## Task 6 — Collection detail endpoint

### Files
- Create: `W:\Scripts\Chronicle\src\Chronicle.API\DTOs\CollectionDto.cs`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.API\Controllers\MediaController.cs`

### Context
The movie detail page needs to show a "Collection" metadata box listing all films in the collection, indicating which are in the library. Add `GET /api/v1/media/{id}/collection` which returns the collection item (parent) with all its child movies and their library status.

### Step 1: Create the DTO

```csharp
// W:\Scripts\Chronicle\src\Chronicle.API\DTOs\CollectionDto.cs
namespace Chronicle.API.DTOs;

public class CollectionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string? Overview { get; set; }
    public List<CollectionMemberDto> Movies { get; set; } = [];
}

public class CollectionMemberDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterUrl { get; set; }
    public bool InLibrary { get; set; }
    public string? LibraryStatus { get; set; }
}
```

### Step 2: Add the endpoint to `MediaController`

Add to the controller (find a logical spot after existing `GET /media/{id}` action):

```csharp
/// <summary>Returns collection membership for a movie item.</summary>
/// <remarks>
/// Works both when id is the collection itself (Level 0) and when id is a movie
/// within a collection (Level 1). In the latter case it resolves to the parent collection.
/// Returns 404 if the item has no collection.
/// </remarks>
[HttpGet("{id:int}/collection")]
public async Task<IActionResult> GetCollection(int id, CancellationToken ct)
{
    var userId = GetUserId();

    // Load the item
    var item = await _context.MediaItems
        .Include(m => m.MediaType)
        .Include(m => m.Parent)
            .ThenInclude(p => p!.MediaType)
        .FirstOrDefaultAsync(m => m.Id == id, ct);

    if (item is null)
        return NotFound(ApiResponse<CollectionDto>.Fail("MEDIA_NOT_FOUND", "Media item not found."));

    // Resolve collection: if item IS a collection (Level 0, movies type) use it directly;
    // if item is a movie (Level 1 under movies collection) use its parent.
    MediaItem? collectionItem = null;
    bool isMoviesType = string.Equals(item.MediaType?.Name, "movies", StringComparison.OrdinalIgnoreCase);

    if (isMoviesType && item.HierarchyLevel == 0)
    {
        collectionItem = item;
    }
    else if (isMoviesType && item.HierarchyLevel == 1 && item.Parent is not null)
    {
        collectionItem = item.Parent;
    }

    if (collectionItem is null)
        return NotFound(ApiResponse<CollectionDto>.Fail("NO_COLLECTION", "Item does not belong to a collection."));

    // Load all movies in the collection
    var members = await _context.MediaItems
        .Where(m => m.ParentId == collectionItem.Id)
        .OrderBy(m => m.Year)
        .ToListAsync(ct);

    // Load library status for current user
    var memberIds = members.Select(m => m.Id).ToList();
    var libraryEntries = await _context.UserLibraries
        .Where(l => l.UserId == userId && memberIds.Contains(l.MediaItemId))
        .ToDictionaryAsync(l => l.MediaItemId, ct);

    var dto = new CollectionDto
    {
        Id       = collectionItem.Id,
        Name     = collectionItem.Name,
        PosterUrl = collectionItem.PosterUrl,
        Overview = collectionItem.Overview,
        Movies   = members.Select(m => new CollectionMemberDto
        {
            Id            = m.Id,
            Name          = m.Name,
            Year          = m.Year,
            PosterUrl     = m.PosterUrl,
            InLibrary     = libraryEntries.ContainsKey(m.Id),
            LibraryStatus = libraryEntries.TryGetValue(m.Id, out var le) ? le.Status.ToString() : null,
        }).ToList(),
    };

    return Ok(ApiResponse<CollectionDto>.Ok(dto));
}
```

### Step 3: Build and test

```powershell
cd W:\Scripts\Chronicle\src && dotnet build Chronicle.sln
cd W:\Scripts\Chronicle\tests && dotnet test --verbosity normal
```

### Step 4: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.API/DTOs/CollectionDto.cs src/Chronicle.API/Controllers/MediaController.cs
git commit -m "feat(api): add GET /media/{id}/collection endpoint for movie collection detail"
```

---

## Task 7 — Frontend: Library settings + collection grouping

### Files
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Web\src\api\library.ts`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Web\src\pages\library\LibraryPage.tsx`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Web\src\pages\library\LibraryPage.module.css`

### Context
Add `groupMoviesIntoCollections` to `LibraryPrefs` (localStorage). The Library Options panel (the filter/sort controls) needs a checkbox. When the setting is OFF, pass `includeMoviesInCollections=true` to the API so individually-trackable movies appear even if nested under collection parents. When ON, use standard `rootOnly=true` — collection Level 0 items appear as cards and their children are not duplicated.

### Step 1: Update `getLibrary` API function

In `library.ts`, find `getLibrary`. Add `includeMoviesInCollections` param:

```typescript
export const getLibrary = async (
  status?: LibraryStatus,
  page = 1,
  perPage = 0,
  rootOnly = false,
  includeMoviesInCollections = false,
): Promise<LibraryEntry[]> => {
  const params: Record<string, string | number | boolean> = { page, perPage, rootOnly }
  if (status) params.status = status
  if (includeMoviesInCollections) params.includeMoviesInCollections = true
  const { data } = await client.get('/library', { params })
  return data.data
}
```

### Step 2: Add `groupMoviesIntoCollections` to `LibraryPrefs`

In `LibraryPage.tsx`, update the `LibraryPrefs` interface:

```typescript
interface LibraryPrefs {
  sortBy: SortField
  sortDir: SortDir
  statusFilter?: LibraryStatus
  pageSizePreset: PageSizePreset
  groupMoviesIntoCollections: boolean   // NEW
}
```

Update `DEFAULT_PREFS`:

```typescript
const DEFAULT_PREFS: LibraryPrefs = {
  sortBy: 'name',
  sortDir: 'asc',
  statusFilter: undefined,
  pageSizePreset: 'medium',
  groupMoviesIntoCollections: false,    // NEW
}
```

### Step 3: Update the query to pass `includeMoviesInCollections`

Find the `useQuery` block (~line 254):

```typescript
const { data: allEntries = [], isLoading, isFetching } = useQuery({
  queryKey: ['library', 'all', { rootOnly: true }],
  queryFn: () => getLibrary(undefined, 1, 0, true),
  ...
})
```

Change to:

```typescript
const { data: allEntries = [], isLoading, isFetching } = useQuery({
  queryKey: ['library', 'all', {
    rootOnly: true,
    includeMoviesInCollections: !prefs.groupMoviesIntoCollections
  }],
  queryFn: () => getLibrary(undefined, 1, 0, true, !prefs.groupMoviesIntoCollections),
  staleTime: 5 * 60 * 1000,
  placeholderData: (prev) => prev,
})
```

> Note: `includeMoviesInCollections` is `true` when grouping is OFF (we want flat movies) and `false` when grouping is ON (collections show as root cards).

### Step 4: Add the checkbox to Library Options panel

Find where the sort and filter controls are rendered (look for the `pageSizePreset` dropdown or the statusFilter area in the JSX). Add:

```tsx
{/* Group movies into collections */}
<label className={styles.collectionGroupingLabel}>
  <input
    type="checkbox"
    checked={prefs.groupMoviesIntoCollections}
    onChange={e => setPrefs({ groupMoviesIntoCollections: e.target.checked })}
  />
  {' '}Group movies into collections
</label>
```

### Step 5: Add CSS for the label

In `LibraryPage.module.css`, add:

```css
.collectionGroupingLabel {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 0.88rem;
  color: var(--text);
  cursor: pointer;
  user-select: none;
}

.collectionGroupingLabel input[type='checkbox'] {
  cursor: pointer;
  accent-color: var(--accent);
}
```

### Step 6: Build frontend

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web && npm run type-check && npm run lint
```
Expected: No errors.

### Step 7: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Web/src/api/library.ts src/Chronicle.Web/src/pages/library/LibraryPage.tsx src/Chronicle.Web/src/pages/library/LibraryPage.module.css
git commit -m "feat(ui): add Group movies into collections library preference"
```

---

## Task 8 — Frontend: `CollectionMetadataBox` component

### Files
- Create: `W:\Scripts\Chronicle\src\Chronicle.Web\src\api\collections.ts`
- Create: `W:\Scripts\Chronicle\src\Chronicle.Web\src\components\CollectionMetadataBox.tsx`
- Create: `W:\Scripts\Chronicle\src\Chronicle.Web\src\components\CollectionMetadataBox.module.css`
- Modify: `W:\Scripts\Chronicle\src\Chronicle.Web\src\pages\media\MediaDetailPage.tsx`

### Context
On the movie detail page, when the movie belongs to a collection (either it IS the collection item, or it has a `collectionId` in its metadata), show a "Part of Collection" box listing all movies. The box renders similarly to `PluginMetadataBox` — a collapsible section with the collection name, poster, and a grid of the other films.

### Step 1: Create the API module

```typescript
// W:\Scripts\Chronicle\src\Chronicle.Web\src\api\collections.ts
import client from './client'

export interface CollectionMember {
  id: number
  name: string
  year: number | null
  posterUrl: string | null
  inLibrary: boolean
  libraryStatus: string | null
}

export interface CollectionInfo {
  id: number
  name: string
  posterUrl: string | null
  overview: string | null
  movies: CollectionMember[]
}

export const getCollection = async (mediaItemId: number): Promise<CollectionInfo> => {
  const { data } = await client.get(`/media/${mediaItemId}/collection`)
  return data.data
}
```

### Step 2: Create the component

```tsx
// W:\Scripts\Chronicle\src\Chronicle.Web\src\components\CollectionMetadataBox.tsx
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getCollection } from '@/api/collections'
import styles from './CollectionMetadataBox.module.css'

interface Props {
  mediaItemId: number
}

export default function CollectionMetadataBox({ mediaItemId }: Props) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['collection', mediaItemId],
    queryFn: () => getCollection(mediaItemId),
    retry: false,  // 404 = no collection; don't spam retries
  })

  if (isLoading) return null
  if (isError || !data) return null

  return (
    <section className={styles.box}>
      <h3 className={styles.heading}>
        {data.posterUrl && (
          <img src={data.posterUrl} alt="" className={styles.collectionPoster} />
        )}
        <span>Part of <em>{data.name}</em></span>
      </h3>
      {data.overview && <p className={styles.overview}>{data.overview}</p>}
      <div className={styles.grid}>
        {data.movies.map(movie => (
          <Link
            key={movie.id}
            to={`/media/${movie.id}`}
            className={`${styles.card} ${movie.inLibrary ? styles.inLibrary : styles.notInLibrary}`}
            title={`${movie.name}${movie.year ? ` (${movie.year})` : ''}`}
          >
            {movie.posterUrl
              ? <img src={movie.posterUrl} alt={movie.name} className={styles.poster} />
              : <div className={styles.posterPlaceholder}>{movie.name[0]}</div>
            }
            <div className={styles.cardName}>{movie.name}</div>
            {movie.year && <div className={styles.cardYear}>{movie.year}</div>}
            {!movie.inLibrary && <div className={styles.missingBadge}>Not in library</div>}
          </Link>
        ))}
      </div>
    </section>
  )
}
```

### Step 3: Create the CSS

```css
/* W:\Scripts\Chronicle\src\Chronicle.Web\src\components\CollectionMetadataBox.module.css */
.box {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 16px 20px;
  margin-bottom: 16px;
}

.heading {
  display: flex;
  align-items: center;
  gap: 12px;
  margin: 0 0 12px;
  font-size: 1rem;
  font-weight: 600;
  color: var(--text);
}

.collectionPoster {
  width: 40px;
  height: 60px;
  object-fit: cover;
  border-radius: 3px;
  flex-shrink: 0;
}

.overview {
  font-size: 0.88rem;
  color: var(--text-muted);
  margin: 0 0 14px;
  line-height: 1.5;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(90px, 1fr));
  gap: 10px;
}

.card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  text-decoration: none;
  color: var(--text);
  border-radius: 5px;
  padding: 6px;
  transition: background 0.15s;
  position: relative;
}
.card:hover { background: var(--bg-hover); }

.inLibrary { opacity: 1; }
.notInLibrary { opacity: 0.5; }

.poster {
  width: 72px;
  height: 108px;
  object-fit: cover;
  border-radius: 3px;
}

.posterPlaceholder {
  width: 72px;
  height: 108px;
  background: var(--bg-secondary);
  border-radius: 3px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 1.5rem;
  color: var(--text-muted);
}

.cardName {
  font-size: 0.75rem;
  text-align: center;
  line-height: 1.2;
  max-width: 80px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cardYear {
  font-size: 0.7rem;
  color: var(--text-muted);
}

.missingBadge {
  position: absolute;
  top: 4px;
  right: 4px;
  background: rgba(0,0,0,0.6);
  color: #aaa;
  font-size: 0.6rem;
  padding: 1px 4px;
  border-radius: 2px;
}
```

### Step 4: Mount `CollectionMetadataBox` in `MediaDetailPage`

In `MediaDetailPage.tsx`, import the component:

```typescript
import CollectionMetadataBox from '@/components/CollectionMetadataBox'
```

Then find the JSX where the page renders metadata boxes (the area with `PluginMetadataBox` calls). Immediately above those boxes (or after the main info section), add:

```tsx
{/* Collection membership — only for movies type */}
{media?.mediaTypeName === 'Movies' && (
  <CollectionMetadataBox mediaItemId={mediaId} />
)}
```

> `media.mediaTypeName` is the `DisplayName` returned by the API; check what value your media detail response uses. If it returns the raw name `"movies"`, compare against that instead.

### Step 5: Build

```powershell
cd W:\Scripts\Chronicle\src\Chronicle.Web && npm run type-check
```
Expected: No TypeScript errors.

### Step 6: Commit

```powershell
cd W:\Scripts\Chronicle
git add src/Chronicle.Web/src/api/collections.ts src/Chronicle.Web/src/components/CollectionMetadataBox.tsx src/Chronicle.Web/src/components/CollectionMetadataBox.module.css src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx
git commit -m "feat(ui): add CollectionMetadataBox component for movie collection membership"
```

---

## Task 9 — Unit and integration tests

### Files
- Create: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\MovieCollectionServiceTests.cs`
- Modify: `W:\Scripts\Chronicle\tests\Chronicle.Tests.Integration\LibraryControllerTests.cs` (or create if needed)

### Context
`MovieCollectionService` has two non-trivial code paths: creating a new collection (and storing ExternalId), and updating the poster when it changes. The library `includeMoviesInCollections` param has an important edge: Level 1 movie items inside collections must appear, while Level 1 TV episodes must NOT.

### Step 1: Unit tests for `MovieCollectionService`

```csharp
// W:\Scripts\Chronicle\tests\Chronicle.Tests.Unit\Services\MovieCollectionServiceTests.cs
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class MovieCollectionServiceTests
{
    private static ChronicleDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(options);
    }

    private static MediaType MoviesType() => new()
    {
        Id = 1, Name = "movies", DisplayName = "Movies",
        HierarchyLevels = 2, HierarchyLabels = "Collection,Movie",
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task EnsureCollectionParentAsync_NoCollectionData_LeavesItemAtRoot()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = "{}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        movie.ParentId.Should().BeNull();
        movie.HierarchyLevel.Should().Be(0);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_WithCollectionData_CreatesCollectionAndReparents()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        var metadataJson = """
        {
          "chronicle.plugin.tmdb": {
            "title": "Inception",
            "belongsToCollection": {
              "id": 748,
              "name": "Inception Collection",
              "posterPath": "https://image.tmdb.org/t/p/w500/poster.jpg"
            }
          }
        }
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        // Collection item should exist
        var collection = await db.MediaItems.FirstOrDefaultAsync(m => m.Name == "Inception Collection");
        collection.Should().NotBeNull();
        collection!.HierarchyLevel.Should().Be(0);
        collection.MediaTypeId.Should().Be(mt.Id);

        // External ID should be stored
        var extId = await db.MediaExternalIds.FirstOrDefaultAsync(e => e.MediaItemId == collection.Id);
        extId.Should().NotBeNull();
        extId!.ExternalId.Should().Be("collection:748");
        extId.Source.Should().Be("tmdb");

        // Movie should be re-parented
        movie.ParentId.Should().Be(collection.Id);
        movie.HierarchyLevel.Should().Be(1);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_CollectionAlreadyExists_DoesNotDuplicate()
    {
        await using var db = CreateInMemoryDb();
        var mt = MoviesType();
        db.MediaTypes.Add(mt);
        var collection = new MediaItem
        {
            Id = 10, Name = "Inception Collection", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(collection);
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = 10, Source = "tmdb", ExternalId = "collection:748"
        });
        var metadataJson = """
        {"chronicle.plugin.tmdb":{"belongsToCollection":{"id":748,"name":"Inception Collection","posterPath":null}}}
        """;
        var movie = new MediaItem
        {
            Id = 1, Name = "Inception", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0, MetadataJson = metadataJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(movie);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, movie);

        // No duplicate collection items
        var collections = await db.MediaItems.Where(m => m.Name == "Inception Collection").ToListAsync();
        collections.Should().HaveCount(1);

        // Movie parented to the existing collection
        movie.ParentId.Should().Be(10);
    }

    [Fact]
    public async Task EnsureCollectionParentAsync_NonMoviesType_IsNoOp()
    {
        await using var db = CreateInMemoryDb();
        var mt = new MediaType
        {
            Id = 2, Name = "tv", DisplayName = "TV",
            HierarchyLevels = 3, CreatedAt = DateTime.UtcNow
        };
        db.MediaTypes.Add(mt);
        var item = new MediaItem
        {
            Id = 1, Name = "Some Show", MediaTypeId = mt.Id, MediaType = mt,
            HierarchyLevel = 0,
            MetadataJson = """{"chronicle.plugin.tmdb":{"belongsToCollection":{"id":1,"name":"Some Collection","posterPath":null}}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var svc = new MovieCollectionService(NullLogger<MovieCollectionService>.Instance);
        await svc.EnsureCollectionParentAsync(db, item);

        // Should not create any collection items
        db.MediaItems.Count().Should().Be(1);
        item.ParentId.Should().BeNull();
    }
}
```

### Step 2: Run tests

```powershell
cd W:\Scripts\Chronicle\tests && dotnet test --filter "MovieCollectionServiceTests" --verbosity normal
```
Expected: 4 tests, all pass.

### Step 3: Commit

```powershell
cd W:\Scripts\Chronicle
git add tests/Chronicle.Tests.Unit/Services/MovieCollectionServiceTests.cs
git commit -m "test(services): unit tests for MovieCollectionService"
```

---

## Task 10 — End-to-end verification

### Manual test steps

1. **Rebuild API and restart**: `cd src/Chronicle.API && dotnet run`

2. **Re-enrich a known movie with a collection** (e.g. Avengers: Endgame):
   - Open Settings → Enrichment
   - Reset the TMDB enrichment row for the movie to Pending
   - Run enrichment for TMDB
   - Check that a "Collection" parent item was created: hit `GET /api/v1/media?search=Avengers Collection` or browse the API
   - Check that the movie's detail page shows the `CollectionMetadataBox`

3. **Test grouping OFF (default)**:
   - Open Library → Movies section
   - All individual movies should be visible including ones in collections
   - "Group movies into collections" checkbox is unchecked

4. **Test grouping ON**:
   - Check "Group movies into collections"
   - Movies that belong to collections should now show as collection-level cards in the library
   - Standalone movies (no collection) still appear individually
   - Clicking a collection card goes to the collection's MediaDetailPage which shows its children

5. **Test CollectionMetadataBox**:
   - Navigate to a movie that has a collection (e.g. Endgame)
   - Verify the "Part of The Avengers Collection" box appears
   - Movies NOT in the library should be greyed out
   - Clicking a movie in the box navigates to that movie's detail page

6. **Test fan edits are unaffected**:
   - Navigate to Library → Fan Edits
   - No "Group by collections" effect — fan edits remain flat (the service bails on non-movies type)

7. **Test preference persists**:
   - Set grouping ON, refresh the page — setting should still be ON

### Commit final

```powershell
cd W:\Scripts\Chronicle
git add .
git commit -m "feat: movie collections grouping complete — migration, service, API, UI"
```

---

## Known edge cases to watch

- **Movies with null collection**: `belongs_to_collection` is null in TMDB response — `ExtractCollectionData` returns null, movie stays at root. ✓ handled.
- **Fan edits sharing TMDB IDs with movies**: Fan edit items have `MediaType.Name == "fanedits"`, not `"movies"` — `EnsureCollectionParentAsync` returns immediately. ✓ handled.
- **Already-enriched movies** won't get collections until their enrichment row is reset to Pending and re-run, or until "Refresh All" is triggered from Settings → Enrichment.
- **ExternalId validation regression**: The hierarchy validator at ~line 824 of `MetadataEnrichmentService` is updated in Task 4 to allow `"movie"` on Level 1 items whose parent is a movies collection.
- **Library flat-view doesn't include collection items themselves**: The `includeMoviesInCollections` query returns Level 1 child movies AND root items (which includes standalone movies + collection parent items). The collection parent items will appear in the movies section since they are `movies` type. They'll look like normal movie cards (showing collection name, poster). This is acceptable in flat view — users can click them to see the collection detail. If this is unwanted, add a frontend filter to skip items that are a `HierarchyLevel 0` parent with children (i.e., pure collection containers). This is a refinement left for a follow-up.
