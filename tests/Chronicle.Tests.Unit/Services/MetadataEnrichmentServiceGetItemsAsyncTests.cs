using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Root-caused live (2026-09-15): the "Enrichment — SIMKL" drill-down page showed "Pending (3)"
/// but only rendered 2 items. GetItemsAsync used to build its query with
/// .Include(x => x.MediaItem).ThenInclude(m => m.MediaType) -- EF Core silently upgrades a
/// ThenInclude onto a REQUIRED reference navigation (MediaType, a non-nullable FK on MediaItem)
/// into an INNER JOIN, since it "knows" MediaType can never be null once MediaItem itself is
/// present. When MediaItem itself was null (an orphaned enrichment row whose MediaItem had been
/// deleted without this row cascading away), that inner join silently dropped the entire
/// enrichment row from the result -- even though `total` (a plain COUNT(*), never touched by
/// any join) correctly still counted it.
///
/// This is a real-SQL-translation bug: EF Core's InMemory provider (used by every other test in
/// this project) never generates actual JOINs at all, so it can never reproduce this -- these
/// tests use a real SQLite connection specifically so the same JOIN translation that broke in
/// production runs here too.
/// </summary>
public class MetadataEnrichmentServiceGetItemsAsyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ChronicleDbContext _db;
    private readonly MetadataEnrichmentService _svc;
    private readonly MediaType _movieType;

    public MetadataEnrichmentServiceGetItemsAsyncTests()
    {
        // A real, open SQLite connection -- ":memory:" only persists for as long as SOME
        // connection to it stays open, so this one is kept alive for the test's lifetime
        // rather than letting EF Core open/close a fresh one per operation.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new ChronicleDbContext(options);
        _db.Database.EnsureCreated();

        // ChronicleDbContext seeds built-in MediaTypes (including "movies", Id=2) via HasData,
        // so EnsureCreated() already inserts it -- reuse that row instead of colliding with it
        // on the unique Name index.
        _movieType = _db.MediaTypes.Single(t => t.Name == "movies");

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _svc = new MetadataEnrichmentService(
            scopeFactory,
            Mock.Of<IMetadataResolutionService>(),
            Mock.Of<IMovieCollectionService>(),
            Mock.Of<IMetadataUrlValidator>(),
            Mock.Of<IPersonResolutionService>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<MetadataEnrichmentService>>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MediaItem AddMovie(string name, int year)
    {
        var item = new MediaItem
        {
            Name = name, Year = year, MediaTypeId = _movieType.Id,
            HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(item);
        return item;
    }

    [Fact]
    public async Task GetItemsAsync_OrphanedEnrichmentRow_StillReturnedNotSilentlyDropped()
    {
        var real1 = AddMovie("Terrestrial", 2024);
        var real2 = AddMovie("Zodiac", 2007);
        var toBeDeleted = AddMovie("Deleted Movie", 2010);
        _db.SaveChanges();

        _db.MediaEnrichments.AddRange(
            new MediaItemEnrichment { MediaItemId = real1.Id, PluginId = "chronicle.plugin.simkl", Status = EnrichmentStatus.Pending },
            new MediaItemEnrichment { MediaItemId = real2.Id, PluginId = "chronicle.plugin.simkl", Status = EnrichmentStatus.Pending },
            new MediaItemEnrichment { MediaItemId = toBeDeleted.Id, PluginId = "chronicle.plugin.simkl", Status = EnrichmentStatus.Pending });
        _db.SaveChanges();
        var orphanId = toBeDeleted.Id;

        // Microsoft.Data.Sqlite enforces "PRAGMA foreign_keys=ON" by default on connections it
        // opens, so an enrichment row can never be INSERTed pointing at a nonexistent MediaItem
        // -- confirming the orphan can only arise from deleting the parent while a child
        // enrichment row still references it (matching the production orphans found live, which
        // is exactly what this reproduces: disable the pragma for one raw DELETE, exactly as a
        // cascade-less delete path in production would leave things, then restore it).
        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        _db.Database.ExecuteSqlRaw("DELETE FROM media_items WHERE Id = {0}", orphanId);
        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        _db.ChangeTracker.Clear();

        var result = await _svc.GetItemsAsync("chronicle.plugin.simkl", "Pending", page: 1, pageSize: 50, search: null, CancellationToken.None);

        result.Total.Should().Be(3, "the count query is unaffected by joins either way");
        result.Items.Should().HaveCount(3,
            "the orphaned row must render too (as \"(unknown)\"), not silently vanish while still being counted");
        result.Items.Should().Contain(i => i.Name == "Terrestrial");
        result.Items.Should().Contain(i => i.Name == "Zodiac");
        result.Items.Should().Contain(i => i.MediaItemId == orphanId && i.Name == "(unknown)" && i.MediaType == "Unknown");
    }

    [Fact]
    public async Task GetItemsAsync_DanglingMediaType_StillReturnedWithUnknownMediaType()
    {
        // MediaItem.MediaTypeId is a non-nullable int too, exactly like MediaItemEnrichment's own
        // MediaItemId -- so the m -> mt hop is a second REQUIRED navigation in this same query,
        // and needs the same explicit-LEFT-JOIN treatment as the x -> m hop. Proves that hop
        // wasn't left on the old Include-style translation by mistake.
        var movie = AddMovie("Alien", 1979);
        _db.SaveChanges();
        _db.MediaEnrichments.Add(new MediaItemEnrichment
        { MediaItemId = movie.Id, PluginId = "chronicle.plugin.simkl", Status = EnrichmentStatus.Pending });
        _db.SaveChanges();

        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        _db.Database.ExecuteSqlRaw("DELETE FROM media_types WHERE Id = {0}", _movieType.Id);
        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        _db.ChangeTracker.Clear();

        var result = await _svc.GetItemsAsync("chronicle.plugin.simkl", "Pending", page: 1, pageSize: 50, search: null, CancellationToken.None);

        result.Total.Should().Be(1);
        var item = result.Items.Single();
        item.Name.Should().Be("Alien", "the MediaItem itself is intact -- only its MediaType row vanished");
        item.MediaType.Should().Be("Unknown");
    }

    [Fact]
    public async Task GetItemsAsync_DanglingParent_StillReturnedWithNullParentName()
    {
        // MediaItem.ParentId is nullable, so m -> p -> gp are OPTIONAL navigations -- this
        // exercises those two hops surviving a parent deleted out from under a surviving child,
        // confirming the join chain doesn't silently regress to INNER further down either.
        var parent = AddMovie("Parent Container", 2000);
        _db.SaveChanges();
        var child = AddMovie("Child Item", 2001);
        child.ParentId = parent.Id;
        _db.SaveChanges();
        _db.MediaEnrichments.Add(new MediaItemEnrichment
        { MediaItemId = child.Id, PluginId = "chronicle.plugin.simkl", Status = EnrichmentStatus.Pending });
        _db.SaveChanges();
        var parentId = parent.Id;

        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        _db.Database.ExecuteSqlRaw("DELETE FROM media_items WHERE Id = {0}", parentId);
        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        _db.ChangeTracker.Clear();

        var result = await _svc.GetItemsAsync("chronicle.plugin.simkl", "Pending", page: 1, pageSize: 50, search: null, CancellationToken.None);

        result.Total.Should().Be(1);
        var item = result.Items.Single();
        item.Name.Should().Be("Child Item", "the child MediaItem itself is intact -- only its parent row vanished");
        item.ParentName.Should().BeNull();
    }

    [Fact]
    public async Task GetItemsAsync_NoOrphans_ReturnsAllRealItemsWithCorrectFields()
    {
        var movie = AddMovie("Fight Club", 1999);
        movie.PosterUrl = "https://example.com/poster.jpg";
        _db.SaveChanges();

        _db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = movie.Id, PluginId = "chronicle.plugin.simkl", Status = EnrichmentStatus.Completed,
            ExternalId = "simkl:movie:123",
        });
        _db.SaveChanges();

        var result = await _svc.GetItemsAsync("chronicle.plugin.simkl", null, page: 1, pageSize: 50, search: null, CancellationToken.None);

        result.Total.Should().Be(1);
        var item = result.Items.Single();
        item.Name.Should().Be("Fight Club");
        item.Year.Should().Be(1999);
        item.MediaType.Should().Be("Movies");
        item.PosterUrl.Should().Be("https://example.com/poster.jpg");
        item.ExternalId.Should().Be("simkl:movie:123");
    }
}
