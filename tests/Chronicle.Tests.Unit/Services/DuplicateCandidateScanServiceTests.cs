using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class DuplicateCandidateScanServiceTests : IDisposable
{
    private readonly ChronicleDbContext _db;
    private readonly DuplicateCandidateScanService _svc;
    private readonly MediaType _moviesType;
    private readonly MediaType _tvType;
    private readonly MediaType _musicType;

    public DuplicateCandidateScanServiceTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ChronicleDbContext(opts);

        _moviesType = new MediaType { Name = "movies", DisplayName = "Movies", HierarchyLevels = 1 };
        _tvType     = new MediaType { Name = "tv",     DisplayName = "TV",     HierarchyLevels = 3 };
        _musicType  = new MediaType { Name = "music",  DisplayName = "Music",  HierarchyLevels = 3 };
        _db.MediaTypes.AddRange(_moviesType, _tvType, _musicType);
        _db.SaveChanges();

        _svc = new DuplicateCandidateScanService(new DirectScopeFactory(_db), NullLogger<DuplicateCandidateScanService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private MediaItem MakeItem(
        string name, MediaType type, int? year = null, bool isStub = false,
        int hierarchyLevel = 0, int? parentId = null)
    {
        var item = new MediaItem
        {
            Name           = name,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            NormalizedNameLoose = MediaItemNormalizer.NormalizeNameLoose(name),
            MediaTypeId    = type.Id,
            Year           = year,
            IsStub         = isStub,
            HierarchyLevel = hierarchyLevel,
            ParentId       = parentId,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        _db.MediaItems.Add(item);
        return item;
    }

    private async Task<HashSet<(int, int)>> RunAndGetCandidatesAsync()
    {
        await _db.SaveChangesAsync();
        await _svc.ExecuteAsync(CancellationToken.None);
        var pairs = await _db.MediaItemDuplicateCandidates
            .Select(c => new { c.ItemAId, c.ItemBId })
            .ToListAsync();
        return pairs.Select(p => (p.ItemAId, p.ItemBId)).ToHashSet();
    }

    // ── Same-type pass (pre-existing behavior, now under test for the first time) ──────────

    [Fact]
    public async Task SameType_SameNormalizedName_FlagsAsDuplicate()
    {
        var a = MakeItem("Fight Club", _moviesType, 1999);
        var b = MakeItem("fight   club", _moviesType, 1999); // whitespace/case differ, normalizes the same

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id)));
    }

    [Fact]
    public async Task SameType_DifferentYears_NotFlagged()
    {
        MakeItem("Aladdin", _moviesType, 1992);
        MakeItem("Aladdin", _moviesType, 2019);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("different release years mean genuinely different works");
    }

    [Fact]
    public async Task SameType_OneSideMissingYear_StillFlagged()
    {
        // Pre-existing behavior: a missing year on either side is treated as "not enough
        // information to rule it out", not as a mismatch -- unlike the cross-type pass below,
        // which requires a year on BOTH sides.
        var a = MakeItem("Fight Club", _moviesType, 1999);
        var b = MakeItem("Fight Club", _moviesType, null);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(a.Id, b.Id), Math.Max(a.Id, b.Id)));
    }

    [Fact]
    public async Task SameType_DifferentParent_NotFlagged()
    {
        var show = MakeItem("Some Show", _tvType);
        var otherShow = MakeItem("Other Show", _tvType);
        MakeItem("Pilot", _tvType, hierarchyLevel: 1, parentId: show.Id);
        MakeItem("Pilot", _tvType, hierarchyLevel: 1, parentId: otherShow.Id);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("episodes named the same under different shows are not duplicates of each other");
    }

    [Fact]
    public async Task DismissedPair_NotReSurfaced()
    {
        var a = MakeItem("Fight Club", _moviesType, 1999);
        var b = MakeItem("Fight Club", _moviesType, 1999);
        await _db.SaveChangesAsync();
        _db.MediaItemDuplicateDismissals.Add(new MediaItemDuplicateDismissal
        {
            ItemAId = Math.Min(a.Id, b.Id), ItemBId = Math.Max(a.Id, b.Id), DismissedAt = DateTime.UtcNow,
        });

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("a pair the user already dismissed must not reappear");
    }

    // ── Cross-type pass (new: catches a phantom scrape duplicate of a different type) ──────

    [Fact]
    public async Task CrossType_UnenrichedDuplicateOfVerifiedShow_Flagged()
    {
        // The actual bug this pass exists for (2026-09-04): a bad Kodi movie-library scrape
        // created a flat, never-enriched "movies" item for a title that already exists,
        // correctly, as a real "tv" item.
        var tvShow = MakeItem("Rick and Morty", _tvType, 2013, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = tvShow.Id, Source = "tmdb", ExternalId = "tv:60625" });
        var phantomMovie = MakeItem("Rick and Morty", _moviesType, 2013, isStub: false);
        // No external id recorded for phantomMovie -- never successfully enriched.

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(tvShow.Id, phantomMovie.Id), Math.Max(tvShow.Id, phantomMovie.Id)));
    }

    [Fact]
    public async Task CrossType_StubDuplicateOfVerifiedShow_Flagged()
    {
        // IsStub alone (no external id needed) is also sufficient to mark a side unverified.
        var tvShow = MakeItem("Foundation", _tvType, 2021, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = tvShow.Id, Source = "tmdb", ExternalId = "tv:1073115" });
        var phantomMovie = MakeItem("Foundation", _moviesType, 2021, isStub: true);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(tvShow.Id, phantomMovie.Id), Math.Max(tvShow.Id, phantomMovie.Id)));
    }

    [Fact]
    public async Task CrossType_BothSidesIndependentlyVerified_NotFlagged()
    {
        // Per-user request (2026-09-04): don't flag two items that are both independently,
        // successfully matched against real metadata just because they coincidentally share a
        // name and year -- getting a cross-type call wrong is riskier than same-type, so this
        // case is deliberately left alone rather than surfaced.
        var movie = MakeItem("Coincidence", _moviesType, 2020, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = "tmdb", ExternalId = "movie:1" });
        var show = MakeItem("Coincidence", _tvType, 2020, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = show.Id, Source = "tmdb", ExternalId = "tv:1" });

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("both sides are independently verified real entries, not a phantom duplicate");
    }

    [Fact]
    public async Task CrossType_MissingYearOnEitherSide_NotFlagged()
    {
        // Stricter than the same-type pass: a cross-type pair needs a year on BOTH sides,
        // not just "not proven different".
        var tvShow = MakeItem("Unclear", _tvType, 2013, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = tvShow.Id, Source = "tmdb", ExternalId = "tv:1" });
        var phantomMovie = MakeItem("Unclear", _moviesType, null, isStub: false);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("a missing year on either side is not enough corroboration for a cross-type match");
    }

    [Fact]
    public async Task CrossType_DifferentYears_NotFlagged()
    {
        var tvShow = MakeItem("Homonym", _tvType, 2010, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = tvShow.Id, Source = "tmdb", ExternalId = "tv:1" });
        var unrelatedMovie = MakeItem("Homonym", _moviesType, 1985, isStub: true);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("different years mean genuinely different works, even unenriched ones");
    }

    [Fact]
    public async Task CrossType_UnrelatedMediaTypeSharingTitle_StillEvaluatedTheSameWay()
    {
        // A soundtrack album named after its movie is a real, common, NOT-a-duplicate case --
        // covered here by the same year+verification guard as movies/tv, not a special case.
        var movie = MakeItem("Dune", _moviesType, 2021, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = "tmdb", ExternalId = "movie:1" });
        var album = MakeItem("Dune", _musicType, 2021, isStub: false);
        _db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = album.Id, Source = "musicbrainz", ExternalId = "release-group:1" });

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("both are independently verified real entries of unrelated media, not a phantom duplicate");
    }

    [Fact]
    public async Task CrossType_ChildLevelItems_NotCompared()
    {
        // Scoped to root (HierarchyLevel 0) items only -- comparing episodes/tracks across
        // unrelated parents by name alone would be meaningless.
        var show = MakeItem("Some Show", _tvType);
        var movie = MakeItem("Some Show", _moviesType); // a root-level movie, not a child
        MakeItem("Pilot", _tvType, 2020, isStub: false, hierarchyLevel: 1, parentId: show.Id);
        var moviePhantomChild = MakeItem("Pilot", _moviesType, 2020, isStub: true, hierarchyLevel: 1, parentId: movie.Id);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Where(c => c.Item1 == moviePhantomChild.Id || c.Item2 == moviePhantomChild.Id)
            .Should().BeEmpty("cross-type matching only applies to root-level items");
    }
}

/// <summary>
/// Minimal <see cref="IServiceScopeFactory"/> that resolves a pre-built
/// <see cref="ChronicleDbContext"/> — avoids standing up a full DI container in unit tests.
/// Safe to reuse the same instance across "scopes" here: DuplicateCandidateScanService is
/// called directly and awaited synchronously in these tests, never from a background thread
/// the way TaskSchedulerService's fire-and-forget dispatch is (see that service's own tests
/// for why a shared instance is NOT safe there).
/// </summary>
file sealed class DirectScopeFactory(ChronicleDbContext ctx) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new DirectScope(ctx);

    private sealed class DirectScope(ChronicleDbContext ctx) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new DirectServiceProvider(ctx);
        public void Dispose() { }
    }

    private sealed class DirectServiceProvider(ChronicleDbContext ctx) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ChronicleDbContext) ? ctx : null;
    }
}
