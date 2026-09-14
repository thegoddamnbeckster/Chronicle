using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Confirmed live (2026-09-14): Wikipedia's own person search matched the identical wrong
/// article ("Anthony Edwards (actor)") onto both the actor's stub and an unrelated NBA
/// player's stub, and this service then merged them because their two "wikipedia" external
/// ids agreed -- they agreed because the same upstream search bug produced both, not because
/// they're actually the same person. These tests pin the fix: a name-search-derived source
/// (currently just "wikipedia") must never be sufficient corroboration on its own, while an
/// authoritative, credit-derived source (tmdb) still works exactly as before.
/// </summary>
public class DuplicatePersonAutoResolveServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly DuplicatePersonAutoResolveService _svc;
    private readonly MediaType _peopleType;

    public DuplicatePersonAutoResolveServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ChronicleDbContext(opts);

        _peopleType = new MediaType { Name = "people", DisplayName = "People", HierarchyLevels = 1, IsTrackable = false };
        _db.MediaTypes.Add(_peopleType);
        _db.SaveChanges();

        _svc = new DuplicatePersonAutoResolveService(new DirectScopeFactory(_db), NullLogger<DuplicatePersonAutoResolveService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private MediaItem MakePerson(string name)
    {
        var item = new MediaItem
        {
            Name           = name,
            NormalizedName = name.ToLowerInvariant(),
            MediaTypeId    = _peopleType.Id,
            HierarchyLevel = 0,
            IsStub         = true,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        _db.MediaItems.Add(item);
        return item;
    }

    private async Task RunAsync()
    {
        await _db.SaveChangesAsync();
        await _svc.ExecuteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_SharedWikipediaIdOnly_DoesNotMerge()
    {
        var a = MakePerson("Anthony Edwards");
        var b = MakePerson("Anthony Edwards");
        _db.SaveChanges();

        _db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = a.Id, Source = "wikipedia", ExternalId = "wikipedia:en:Anthony_Edwards_(actor)" },
            new MediaExternalId { MediaItemId = b.Id, Source = "wikipedia", ExternalId = "wikipedia:en:Anthony_Edwards_(actor)" });
        _db.MediaItemDuplicateCandidates.Add(new MediaItemDuplicateCandidate { ItemAId = a.Id, ItemBId = b.Id, DetectedAt = DateTime.UtcNow });

        await RunAsync();

        // Neither merged nor dismissed -- left as an open candidate for a human, same as any
        // other genuinely-unverifiable same-name pair.
        Assert.Equal(2, await _db.MediaItems.CountAsync(m => m.MediaTypeId == _peopleType.Id));
        Assert.Equal(1, await _db.MediaItemDuplicateCandidates.CountAsync());
        Assert.Equal(0, await _db.MediaItemDuplicateDismissals.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_SharedTmdbId_StillMerges()
    {
        // Regression check: excluding "wikipedia" must not weaken the authoritative,
        // credit-derived sources this auto-resolve exists to act on.
        var a = MakePerson("Jane Doe");
        var b = MakePerson("Jane Doe");
        _db.SaveChanges();

        _db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = a.Id, Source = "tmdb", ExternalId = "tmdb:99" },
            new MediaExternalId { MediaItemId = b.Id, Source = "tmdb", ExternalId = "tmdb:99" });
        _db.MediaItemDuplicateCandidates.Add(new MediaItemDuplicateCandidate { ItemAId = a.Id, ItemBId = b.Id, DetectedAt = DateTime.UtcNow });

        await RunAsync();

        Assert.Equal(1, await _db.MediaItems.CountAsync(m => m.MediaTypeId == _peopleType.Id));
        Assert.Equal(0, await _db.MediaItemDuplicateCandidates.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_SharedWikipediaHeadshotOnly_DoesNotMerge()
    {
        var a = MakePerson("Jesse James");
        var b = MakePerson("Jesse James");
        _db.SaveChanges();

        _db.PersonHeadshots.AddRange(
            new PersonHeadshot { PersonMediaItemId = a.Id, Url = "https://upload.wikimedia.org/wrong.jpg", Source = "wikipedia" },
            new PersonHeadshot { PersonMediaItemId = b.Id, Url = "https://upload.wikimedia.org/wrong.jpg", Source = "wikipedia" });
        _db.MediaItemDuplicateCandidates.Add(new MediaItemDuplicateCandidate { ItemAId = a.Id, ItemBId = b.Id, DetectedAt = DateTime.UtcNow });

        await RunAsync();

        Assert.Equal(2, await _db.MediaItems.CountAsync(m => m.MediaTypeId == _peopleType.Id));
        Assert.Equal(1, await _db.MediaItemDuplicateCandidates.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_SharedWikipediaHeadshotDifferentCasing_DoesNotMerge()
    {
        // Caught in review before release: the source exclusion must be case-insensitive
        // (NameSearchDerivedSources uses OrdinalIgnoreCase) even though a Where clause backed
        // by a real relational provider would otherwise follow the DB column's collation, not
        // the C# comparer. This InMemory-provider test can't itself prove the SQL translation
        // is safe (InMemory always evaluates client-side) -- what it pins is that the fix
        // filters in memory at all, so a real provider's case-sensitive collation can't matter.
        var a = MakePerson("Jesse James");
        var b = MakePerson("Jesse James");
        _db.SaveChanges();

        _db.PersonHeadshots.AddRange(
            new PersonHeadshot { PersonMediaItemId = a.Id, Url = "https://upload.wikimedia.org/wrong.jpg", Source = "Wikipedia" },
            new PersonHeadshot { PersonMediaItemId = b.Id, Url = "https://upload.wikimedia.org/wrong.jpg", Source = "WIKIPEDIA" });
        _db.MediaItemDuplicateCandidates.Add(new MediaItemDuplicateCandidate { ItemAId = a.Id, ItemBId = b.Id, DetectedAt = DateTime.UtcNow });

        await RunAsync();

        Assert.Equal(2, await _db.MediaItems.CountAsync(m => m.MediaTypeId == _peopleType.Id));
    }

    [Fact]
    public async Task ExecuteAsync_SharedTmdbHeadshot_StillMerges()
    {
        var a = MakePerson("Jane Doe");
        var b = MakePerson("Jane Doe");
        _db.SaveChanges();

        _db.PersonHeadshots.AddRange(
            new PersonHeadshot { PersonMediaItemId = a.Id, Url = "https://image.tmdb.org/same.jpg", Source = "tmdb" },
            new PersonHeadshot { PersonMediaItemId = b.Id, Url = "https://image.tmdb.org/same.jpg", Source = "tmdb" });
        _db.MediaItemDuplicateCandidates.Add(new MediaItemDuplicateCandidate { ItemAId = a.Id, ItemBId = b.Id, DetectedAt = DateTime.UtcNow });

        await RunAsync();

        Assert.Equal(1, await _db.MediaItems.CountAsync(m => m.MediaTypeId == _peopleType.Id));
    }

    private sealed class DirectScopeFactory(ChronicleDbContext ctx) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DirectScope(ctx);

        private sealed class DirectScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; }
            public DirectScope(ChronicleDbContext ctx) => ServiceProvider = new DirectServiceProvider(ctx);
            public void Dispose() { }
        }

        private sealed class DirectServiceProvider : IServiceProvider
        {
            private readonly ChronicleDbContext _ctx;
            private readonly IMergeService _mergeService;

            public DirectServiceProvider(ChronicleDbContext ctx)
            {
                _ctx = ctx;
                _mergeService = new MergeService(
                    _ctx, new NoopResolutionService(), new NoopMovieCollectionService(),
                    NullLogger<MergeService>.Instance);
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(ChronicleDbContext)) return _ctx;
                if (serviceType == typeof(IMergeService))      return _mergeService;
                return null;
            }
        }
    }

    /// <summary>No-op collection service — none of these tests exercise collection containers,
    /// so "not a container" keeps MergeService's container-mismatch guard a no-op here. Copied
    /// from DuplicateCleanupServiceTests' own identical helper (private there, so not reusable
    /// directly).</summary>
    private sealed class NoopMovieCollectionService : IMovieCollectionService
    {
        public Task<bool> IsCollectionContainerAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<HashSet<int>> GetCollectionContainerIdsAsync(
            ChronicleDbContext db, IReadOnlyCollection<int> candidateIds, CancellationToken ct = default)
            => Task.FromResult(new HashSet<int>());
        public Task<string?> GetFallbackPosterAsync(ChronicleDbContext db, int collectionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task EnsureCollectionParentAsync(ChronicleDbContext db, MediaItem movieItem,
            string? pluginId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ProcessAllExistingMovieCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeduplicateCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RemoveEmptyCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task RebuildSingleCollectionAsync(int collectionId,
            IReadOnlyList<(string PluginId, Chronicle.Plugins.IMetadataProvider Provider)> providers,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateStubsForAllCollectionsAsync(
            IReadOnlyList<(string PluginId, Chronicle.Plugins.IMetadataProvider Provider)> providers,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> EnsureCollectionStubsAsync(ChronicleDbContext db, MediaItem collection,
            Chronicle.Plugins.IMetadataProvider provider, CancellationToken ct = default,
            IReadOnlyList<(string PluginId, Chronicle.Plugins.IMetadataProvider Provider)>? allProviders = null)
            => throw new NotSupportedException();
        public Task UnparentFromCollectionAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ReparentIntoCollectionAsync(ChronicleDbContext db, int movieId, int collectionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>No-op resolution service — ResolveAsync is a side effect these tests don't need
    /// to verify. Copied from DuplicateCleanupServiceTests' own identical helper.</summary>
    private sealed class NoopResolutionService : IMetadataResolutionService
    {
        public Task ResolveAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
            => Task.CompletedTask;
        public IReadOnlyCollection<string> GetCanonicalFields() => Array.Empty<string>();
        public Task SetOverrideAsync(MediaItem item, ChronicleDbContext db, string field, string url,
            string? sourcePluginId, string? sourceType, int? userId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearOverrideAsync(MediaItem item, ChronicleDbContext db, string field, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearItemOverridesAsync(MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> ClearOverridesForMediaTypeAsync(string mediaTypeName, Action<int, int>? onBatch = null, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> ClearAllOverridesLibraryWideAsync(Action<int, int>? onBatch = null, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> ClearOverridesForSubtreeAsync(int rootId, Action<int, int>? onBatch = null, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
