using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        int hierarchyLevel = 0, int? parentId = null, int? number = null, string? filePath = null)
    {
        var metadataJson = filePath is null
            ? null
            : JsonSerializer.Serialize(new { fileScanner = new { filePaths = new[] { filePath } } });
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
            Number         = number,
            MetadataJson   = metadataJson,
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

    // ── Same-parent, same-Number CONTAINER pass (added 2026-09-13 for the 2026-08-03 incident) ──
    // Per-user request (2026-09-13): minimize how often a duplicate needs a human to look at it
    // at all. So this pass only queues a container pair when their CHILDREN actually collide by
    // Number (genuinely ambiguous -- see DuplicateCleanupService's own Pass 5 for the safe,
    // non-overlapping case, which is auto-merged and never reaches this queue).

    [Fact]
    public async Task Container_SameParentSameNumber_OverlappingChildren_Flagged()
    {
        // The actual gap this pass closes: two season containers share the real season
        // Number but are named differently enough ("Season 2" vs "Season 02") that the
        // NormalizedName-based pass above never groups them together. Both sides also each
        // have an "Episode 1" -- a real content conflict a plain reparent can't safely
        // resolve, so this is exactly the shape that still needs a human.
        var show = MakeItem("Renovation Resort", _tvType);
        var seasonA = MakeItem("Season 2", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeItem("Season 02", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeItem("Episode One", _tvType, hierarchyLevel: 2, parentId: seasonA.Id, number: 1);
        MakeItem("Episode One (dup)", _tvType, hierarchyLevel: 2, parentId: seasonB.Id, number: 1);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(seasonA.Id, seasonB.Id), Math.Max(seasonA.Id, seasonB.Id)));
    }

    [Fact]
    public async Task Container_SameParentSameNumber_NonOverlappingChildren_NotFlagged()
    {
        // The safe case -- one season holds episodes 1-2, its duplicate holds only episode 3,
        // nothing to conflict over. DuplicateCleanupService's Pass 5 resolves this
        // automatically, so it must never reach the human-review queue here.
        var show = MakeItem("Renovation Resort", _tvType);
        var seasonA = MakeItem("Season 2", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeItem("Season 02", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeItem("Episode One", _tvType, hierarchyLevel: 2, parentId: seasonA.Id, number: 1);
        MakeItem("Episode Two", _tvType, hierarchyLevel: 2, parentId: seasonA.Id, number: 2);
        MakeItem("Episode Three", _tvType, hierarchyLevel: 2, parentId: seasonB.Id, number: 3);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().NotContain((Math.Min(seasonA.Id, seasonB.Id), Math.Max(seasonA.Id, seasonB.Id)),
            "non-overlapping containers are unambiguous and handled by Pass 5's auto-merge instead");
    }

    [Fact]
    public async Task Container_SameParentSameNumber_DifferentParents_NotFlagged()
    {
        var showA = MakeItem("Show A", _tvType);
        var showB = MakeItem("Show B", _tvType);
        MakeItem("Season 1", _tvType, hierarchyLevel: 1, parentId: showA.Id, number: 1);
        MakeItem("Season 1", _tvType, hierarchyLevel: 1, parentId: showB.Id, number: 1);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("season 1 of two different shows is not a duplicate pair");
    }

    [Fact]
    public async Task Leaf_SameParentSameNumber_NeverFlaggedByContainerPass()
    {
        // Confirmed real, legitimate case (root-caused live 2026-09-12): an unreliably-parsed
        // reality show can have several genuinely different episodes that all parsed to the
        // same episode Number. The container-Number pass must never touch leaf-level items,
        // no matter how many share a Number under the same season.
        var show = MakeItem("Reality Show", _tvType);
        var season = MakeItem("Season 1", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 1);
        var epA = MakeItem("Episode Five A", _tvType, hierarchyLevel: 2, parentId: season.Id, number: 5);
        var epB = MakeItem("Episode Five B", _tvType, hierarchyLevel: 2, parentId: season.Id, number: 5);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Where(c => c.Item1 == epA.Id || c.Item2 == epA.Id || c.Item1 == epB.Id || c.Item2 == epB.Id)
            .Should().BeEmpty("leaf-level Number collisions are a known-legitimate case, not a duplicate signal");
    }

    [Fact]
    public async Task Container_SameParentSameNumber_OverlapOnlyVisibleViaBlankNameChild_Flagged()
    {
        // Caught in review before release: the overlap check must see every child, including
        // one with a blank Name (the exact shape of a corrupted/fabricated record) -- if it
        // only sees the name-filtered `items` list, this pair looks like a false "no overlap"
        // and never reaches the review queue, even though DuplicateCleanupService's own
        // unfiltered check would correctly detect the real overlap and refuse to auto-merge it,
        // leaving the pair invisible to BOTH the automatic and manual cleanup paths.
        var show = MakeItem("Renovation Resort", _tvType);
        var seasonA = MakeItem("Season 2", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeItem("Season 02", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeItem("", _tvType, hierarchyLevel: 2, parentId: seasonA.Id, number: 1);
        MakeItem("Episode One (dup)", _tvType, hierarchyLevel: 2, parentId: seasonB.Id, number: 1);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(seasonA.Id, seasonB.Id), Math.Max(seasonA.Id, seasonB.Id)),
            "the blank-Name child must still be visible to the overlap check");
    }

    [Fact]
    public async Task Container_SameParentSameNumber_BothChildless_Flagged()
    {
        // Mirrors DuplicateCleanupService's own Pass 5 fix: zero children on both sides isn't
        // evidence of safety, it's an absence of evidence -- must be queued for a human, not
        // silently skipped as "safe, Cleanup handles it" when Cleanup would actually now defer
        // to manual review too.
        var show = MakeItem("Renovation Resort", _tvType);
        var seasonA = MakeItem("Season 2", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeItem("Season 02", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().Contain((Math.Min(seasonA.Id, seasonB.Id), Math.Max(seasonA.Id, seasonB.Id)),
            "neither side has children or a stub marker -- not enough evidence to call this safe");
    }

    [Fact]
    public async Task Container_SameParentSameNumber_ChildlessStubSide_NotFlagged()
    {
        // The genuinely safe childless case must still be left for Pass 5's auto-merge, not
        // queued -- a confirmed stub colliding with a real, populated season.
        var show = MakeItem("Renovation Resort", _tvType);
        var stub = MakeItem("Season 2", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2, isStub: true);
        var realSeason = MakeItem("Season 02", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeItem("Episode One", _tvType, hierarchyLevel: 2, parentId: realSeason.Id, number: 1);

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().NotContain((Math.Min(stub.Id, realSeason.Id), Math.Max(stub.Id, realSeason.Id)),
            "a childless stub colliding with a populated season is Pass 5's own safe auto-merge case");
    }

    [Fact]
    public async Task Container_DismissedPair_NotReSurfaced()
    {
        var show = MakeItem("Some Show", _tvType);
        var seasonA = MakeItem("Season 2", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeItem("Season 02", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 2);
        await _db.SaveChangesAsync();
        _db.MediaItemDuplicateDismissals.Add(new MediaItemDuplicateDismissal
        {
            ItemAId = Math.Min(seasonA.Id, seasonB.Id), ItemBId = Math.Max(seasonA.Id, seasonB.Id), DismissedAt = DateTime.UtcNow,
        });

        var candidates = await RunAndGetCandidatesAsync();

        candidates.Should().BeEmpty("a dismissed container pair must not reappear either");
    }

    // ── Show/path mismatch diagnostic logging (added 2026-09-13) ──────────────────────────

    [Fact]
    public async Task PathMismatch_EpisodeFileUnderWrongShowFolder_LogsWarning()
    {
        var show = MakeItem("Dark Matter", _tvType);
        var season = MakeItem("Season 1", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 1);
        MakeItem("Episode One", _tvType, hierarchyLevel: 2, parentId: season.Id, number: 1,
            filePath: @"H:\TV\Top Chef - Last Chance Kitchen\S01\file.mkv");
        await _db.SaveChangesAsync();

        var capturingLogger = new CapturingLogger<DuplicateCandidateScanService>();
        var svc = new DuplicateCandidateScanService(new DirectScopeFactory(_db), capturingLogger);
        await svc.ExecuteAsync(CancellationToken.None);

        capturingLogger.Warnings.Should().Contain(w => w.Contains("Dark Matter"),
            "the episode's own recorded file path doesn't contain its show's name at all");
    }

    [Fact]
    public async Task PathMismatch_EpisodeFileUnderCorrectShowFolder_NoWarning()
    {
        var show = MakeItem("Dark Matter", _tvType);
        var season = MakeItem("Season 1", _tvType, hierarchyLevel: 1, parentId: show.Id, number: 1);
        MakeItem("Episode One", _tvType, hierarchyLevel: 2, parentId: season.Id, number: 1,
            filePath: @"H:\TV\Dark Matter\Season 01\file.mkv");
        await _db.SaveChangesAsync();

        var capturingLogger = new CapturingLogger<DuplicateCandidateScanService>();
        var svc = new DuplicateCandidateScanService(new DirectScopeFactory(_db), capturingLogger);
        await svc.ExecuteAsync(CancellationToken.None);

        capturingLogger.Warnings.Should().BeEmpty("the file path correctly names its own show");
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

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that records formatted Warning-level messages so tests can
/// assert on the show/path-mismatch diagnostic pass, which -- unlike the candidate passes --
/// has no database table to inspect and only ever surfaces via a log line.
/// </summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}
