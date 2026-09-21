using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class DuplicateCleanupServiceTests : IDisposable
{
    private readonly ChronicleDbContext _context;
    private readonly DuplicateCleanupService _service;
    private readonly MediaType _moviesType;
    private readonly MediaType _faneditsType;
    private readonly MediaType _tvType;

    public DuplicateCleanupServiceTests()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new ChronicleDbContext(options);

        // Seed media types
        _moviesType  = new MediaType { Name = "movies",   DisplayName = "Movies",    HierarchyLevels = 1 };
        _faneditsType = new MediaType { Name = "fanedits", DisplayName = "Fan Edits", HierarchyLevels = 1 };
        _tvType      = new MediaType { Name = "tv",       DisplayName = "TV",        HierarchyLevels = 3 };
        _context.MediaTypes.AddRange(_moviesType, _faneditsType, _tvType);
        _context.SaveChanges();

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        var provider = services.BuildServiceProvider();

        var scopeFactory = new DirectScopeFactory(_context);
        _service = new DuplicateCleanupService(scopeFactory);
    }

    public void Dispose() => _context.Dispose();

    // ── BUG-009: folderPath must NOT be used as the duplicate key ─────────────

    [Fact]
    public async Task RunAsync_DoesNotGroupItemsBySharedFolderPath()
    {
        // Items in the same folder (e.g. TV episodes) share a folderPath but each
        // has a distinct filePaths entry — they must NOT be treated as duplicates.
        var sharedFolder = "/media/TV/Breaking Bad/Season 1";
        _context.MediaItems.AddRange(
            MakeItem("s01e01.mkv", sharedFolder, "Pilot"),
            MakeItem("s01e02.mkv", sharedFolder, "Cat's in the Bag"),
            MakeItem("s01e03.mkv", sharedFolder, "...And the Bag's in the River")
        );
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "items sharing a folder but with distinct filePaths are not duplicates");
        _context.MediaItems.Count().Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_DetectsRealDuplicate_SameFilePath()
    {
        // Two items pointing at the exact same file are genuine duplicates.
        // The OLDER item (lower Id) survives regardless of metadata richness.
        const string folder = "/media/Movies/Inception (2010)";

        // Older item added first (will get a lower Id) — no metadata.
        var older = MakeItem("Inception.mkv", folder, "Inception");
        _context.MediaItems.Add(older);
        await _context.SaveChangesAsync();

        // Newer item added after (higher Id) — richer metadata, but should still lose.
        var newer = MakeItem("Inception.mkv", folder, "Inception", posterUrl: "/img/poster.jpg", overview: "A thief...");
        _context.MediaItems.Add(newer);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1);
        _context.MediaItems.Count().Should().Be(1);
        // The OLDER item (lower Id) survives — age beats metadata richness.
        _context.MediaItems.Single().Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task RunAsync_NeverMergesAcrossMediaTypes_EvenWithSameFilePathKey()
    {
        // A fan edit and its source movie (or any two differently-typed items) must NEVER be
        // silently collapsed into one record by the automated cleanup — even if they happen to
        // resolve to the same file-path key (e.g. because of a scan-matching bug upstream, or
        // stale legacy data). Losing one item's type/identity to an unattended nightly job is
        // exactly the "fan edits keep getting stolen into the real movie" bug this guards against.
        // Cross-type collisions like this are surfaced via a warning log for manual review/unmerge
        // instead of being auto-merged.
        const string folder = "/media/FanEdits/Apocalypse Now";

        var asMovie   = MakeItem("ApocalypseNow_Redux.mkv", folder, "Apocalypse Now", typeId: _moviesType.Id,
                                 posterUrl: "/img/p.jpg", overview: "War film...");
        var asFanEdit = MakeItem("ApocalypseNow_Redux.mkv", folder, "Apocalypse Now Redux", typeId: _faneditsType.Id);
        _context.MediaItems.AddRange(asMovie, asFanEdit);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "items of different media types must never be auto-merged, regardless of file path");
        _context.MediaItems.Count().Should().Be(2);
    }

    // ── Pass 2: external-ID sentinel exclusions ─────────────────────────────

    [Fact]
    public async Task RunAsync_DoesNotMergeItems_SharingAHardcoverSeriesFallbackId()
    {
        // Regression test for a confirmed live incident (2026-08-05): Chronicle.Plugin.Hardcover
        // falls back to a series-level "hardcover:series:{id}" ExternalId whenever an individual
        // book/edition can't be individually disambiguated. Every sibling volume that hits this
        // fallback gets the IDENTICAL string, so it is not unique-per-item and must never be used
        // as a Pass 2 merge signal — confirmed live: 20+ such shared IDs existed across the DB,
        // each spanning 2-3 otherwise-unrelated items.
        var itemA = MakeItem("bookA.epub", "/media/Books/Series", "Alice in Borderland Vol A");
        var itemB = MakeItem("bookB.epub", "/media/Books/Series", "Alice in Borderland Vol B");
        _context.MediaItems.AddRange(itemA, itemB);
        await _context.SaveChangesAsync();

        _context.MediaExternalIds.Add(new MediaExternalId { MediaItemId = itemA.Id, Source = "hardcover", ExternalId = "hardcover:series:445749" });
        _context.MediaExternalIds.Add(new MediaExternalId { MediaItemId = itemB.Id, Source = "hardcover", ExternalId = "hardcover:series:445749" });
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "a shared series-level fallback ID is bookkeeping, not proof these are the same item");
        _context.MediaItems.Count().Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_DetectsRealDuplicate_SameFilePath_SameType_AmongThreeTypes()
    {
        // Two Movies sharing a file path merge; a third, differently-typed item sharing the
        // same key is left untouched instead of being folded into either survivor.
        const string folder = "/media/Movies/Dune Part Two";

        var older = MakeItem("Dune2.mkv", folder, "Dune: Part Two", typeId: _moviesType.Id);
        _context.MediaItems.Add(older);
        await _context.SaveChangesAsync();

        var newer = MakeItem("Dune2.mkv", folder, "Dune Part Two", typeId: _moviesType.Id,
                              posterUrl: "/p.jpg");
        var faneditSameFile = MakeItem("Dune2.mkv", folder, "Dune Part Two Fan Cut", typeId: _faneditsType.Id);
        _context.MediaItems.AddRange(newer, faneditSameFile);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1, "the two same-type duplicates should still merge");
        _context.MediaItems.Count().Should().Be(2);
        _context.MediaItems.Should().Contain(m => m.Id == older.Id);
        _context.MediaItems.Should().Contain(m => m.Id == faneditSameFile.Id);
    }

    [Fact]
    public async Task RunAsync_ItemsWithoutFileScannerMetadata_AreIgnored()
    {
        // Items created purely through TMDB (no file scanner data) have no filePaths
        // and must never be falsely matched as duplicates.
        var tmdbOnly = new MediaItem
        {
            Name         = "The Matrix",
            MediaTypeId  = _moviesType.Id,
            MetadataJson = JsonSerializer.Serialize(new { tmdb = new { id = 603 } }),
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
        var tmdbOnly2 = new MediaItem
        {
            Name         = "The Matrix",
            MediaTypeId  = _moviesType.Id,
            MetadataJson = JsonSerializer.Serialize(new { tmdb = new { id = 603 } }),
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
        _context.MediaItems.AddRange(tmdbOnly, tmdbOnly2);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "items without fileScanner metadata are excluded from file-path duplicate detection");
    }

    [Fact]
    public async Task RunAsync_LargeSeasonFolder_AllEpisodesPreserved()
    {
        // Regression for BUG-009: 20 episodes in the same folder must all survive.
        const string folder = "/media/TV/The Wire/Season 3";
        var episodes = Enumerable.Range(1, 20)
            .Select(i => MakeItem($"s03e{i:D2}.mkv", folder, $"Episode {i}"))
            .ToList();
        _context.MediaItems.AddRange(episodes);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0);
        _context.MediaItems.Count().Should().Be(20);
    }

    [Fact]
    public async Task RunAsync_UserLibraryReassignedToWinner_BeforeLoserDeleted()
    {
        // User data on the loser must be migrated to the winner (older item), not lost.
        const string folder = "/media/Movies/Dune";
        // winner = older item (added and saved first → lower Id)
        var winner = MakeItem("Dune.mkv", folder, "Dune");
        _context.MediaItems.Add(winner);
        await _context.SaveChangesAsync();
        // loser = newer item (higher Id)
        var loser = MakeItem("Dune.mkv", folder, "Dune", posterUrl: "/p.jpg");
        _context.MediaItems.Add(loser);
        await _context.SaveChangesAsync();

        var user = new User { Username = "alice", PasswordHash = "x", CreatedAt = DateTime.UtcNow };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _context.UserLibraries.Add(new UserLibrary
        {
            UserId      = user.Id,
            MediaItemId = loser.Id,
            Status      = LibraryStatus.Completed,
            AddedAt     = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1);
        var lib = _context.UserLibraries.Single();
        lib.MediaItemId.Should().NotBe(loser.Id, "library entry should have been re-pointed to the winner");
    }

    // ── Pass 4: same-parent, same-name duplicates (e.g. items restored via Unmerge,
    //    which never carries Year/Number forward — see MergeService.UnmergeAsync) ──────

    [Fact]
    public async Task RunAsync_MergesSameParentSameName_WhenYearDiffersOrIsNull()
    {
        var collection = new MediaItem
        {
            Name = "Terminator Collection", MediaTypeId = _moviesType.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(collection);
        await _context.SaveChangesAsync();

        // Real item — has a file and a year.
        var real = new MediaItem
        {
            Name = "Terminator 2: Judgment Day", Year = 1991, MediaTypeId = _moviesType.Id,
            ParentId = collection.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(real);
        await _context.SaveChangesAsync();

        // Restored-via-Unmerge duplicate — same name, same parent, but no Year and no file.
        var restored = new MediaItem
        {
            Name = "Terminator 2: Judgment Day", Year = null, MediaTypeId = _moviesType.Id,
            ParentId = collection.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(restored);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1, "Pass 3 misses this (Year null != 1991) but Pass 4 should catch it via shared parent+name");
        _context.MediaItems.Where(m => m.Id != collection.Id).Should().ContainSingle().Which.Id.Should().Be(real.Id);
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeSameParentSameName_WhenNumbersDiffer()
    {
        // Two genuinely distinct tracks that happen to share a generic title (e.g. "Interlude")
        // under the same album must NOT be merged just because the name/parent match.
        var album = new MediaItem
        {
            Name = "Some Album", MediaTypeId = _moviesType.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(album);
        await _context.SaveChangesAsync();

        var trackA = new MediaItem
        {
            Name = "Interlude", Number = 3, MediaTypeId = _moviesType.Id,
            ParentId = album.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var trackB = new MediaItem
        {
            Name = "Interlude", Number = 7, MediaTypeId = _moviesType.Id,
            ParentId = album.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.AddRange(trackA, trackB);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "differing non-null Number means these are genuinely different items");
        _context.MediaItems.Count(m => m.Name == "Interlude").Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeSameParentSameName_WhenYearsDifferAndBothAreSet()
    {
        // Regression test for the "Alice in Borderland" bug (2026-08-05): distinct volumes/
        // editions sharing a parent and title, each with its own genuine Year, were merged
        // because Pass 4 had no Year check at all (unlike Pass 3). Number is null on both sides
        // here (no natural "Number" field distinguishes manga volumes), so the pre-existing
        // Number guard alone could not have caught this.
        var series = new MediaItem
        {
            Name = "Alice in Borderland Series", MediaTypeId = _moviesType.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(series);
        await _context.SaveChangesAsync();

        var vol2012 = new MediaItem
        {
            Name = "Alice in Borderland", Year = 2012, MediaTypeId = _moviesType.Id,
            ParentId = series.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var vol2014 = new MediaItem
        {
            Name = "Alice in Borderland", Year = 2014, MediaTypeId = _moviesType.Id,
            ParentId = series.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.AddRange(vol2012, vol2014);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "two different, genuinely-dated editions must not be collapsed just because they share a parent and title");
        _context.MediaItems.Count(m => m.Name == "Alice in Borderland").Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeSameParentSameName_WhenYearsDifferAcrossALargeGroup()
    {
        // Reproduces the exact production shape (2026-08-05): 10 same-parent, same-normalized-
        // title items spanning three different years, each with its own distinct external ID,
        // none with a Number set. If the guard only checks pairwise (winner vs each loser) and
        // something causes it to not fire across a large group the way it does for a 2-item
        // group, this should catch it.
        var series = new MediaItem
        {
            Name = "Alice in Borderland Series", MediaTypeId = _moviesType.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(series);
        await _context.SaveChangesAsync();

        var names = new[]
        {
            ("Alice in Borderland (2012)", 2012, "427841"),
            ("Alice in Borderland (2012)", 2012, "427845"),
            ("Alice in Borderland (2012)", 2012, "427849"),
            ("Alice in Borderland (2013)", 2013, "427850"),
            ("Alice in Borderland (2013)", 2013, "427852"),
            ("Alice in Borderland (2013)", 2013, "427858"),
            ("Alice in Borderland (2013)", 2013, "427859"),
            ("Alice In Borderland (2012)", 2012, "427860"),
            ("Alice in Borderland (2014)", 2014, "427865"),
            ("Alice in Borderland (2014)", 2014, "427866"),
        };

        var items = new List<MediaItem>();
        foreach (var (name, year, extId) in names)
        {
            var item = new MediaItem
            {
                Name = name, Year = year, MediaTypeId = _moviesType.Id,
                ParentId = series.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            items.Add(item);
        }
        await _context.SaveChangesAsync();

        foreach (var (item, (_, _, extId)) in items.Zip(names))
        {
            _context.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = item.Id, Source = "hardcover", ExternalId = extId,
            });
        }
        await _context.SaveChangesAsync();

        await _service.RunAsync();

        // Whatever survives, every survivor's Year must be internally consistent — no merge
        // should ever have crossed a Year boundary between two non-null, disagreeing values.
        var survivors = await _context.MediaItems
            .Where(m => m.Id != series.Id)
            .Select(m => new { m.Id, m.Year })
            .ToListAsync();
        var distinctYears = survivors.Select(s => s.Year).Where(y => y.HasValue).Distinct().Count();
        distinctYears.Should().Be(3, "three genuinely different years must never collapse into fewer than three surviving Year values");
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeSameParentSameName_WhenExternalIdsConflict()
    {
        var series = new MediaItem
        {
            Name = "Conflicting Ids Series", MediaTypeId = _moviesType.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(series);
        await _context.SaveChangesAsync();

        var itemA = new MediaItem
        {
            Name = "Same Title", MediaTypeId = _moviesType.Id,
            ParentId = series.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var itemB = new MediaItem
        {
            Name = "Same Title", MediaTypeId = _moviesType.Id,
            ParentId = series.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.AddRange(itemA, itemB);
        await _context.SaveChangesAsync();

        _context.MediaExternalIds.Add(new MediaExternalId { MediaItemId = itemA.Id, Source = "hardcover", ExternalId = "111" });
        _context.MediaExternalIds.Add(new MediaExternalId { MediaItemId = itemB.Id, Source = "hardcover", ExternalId = "222" });
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "differing external IDs from the same source are direct evidence of distinct real items");
        _context.MediaItems.Count(m => m.Name == "Same Title").Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_SameParentSameName_PrefersLosersFileScannerWhenWinnersDoesNotMatchTitle()
    {
        // Confirmed live (2026-09-21): an episode enriched correctly via TMDB (right season/
        // episode identity, so it wins on score) had a corrupted fileScanner.filePaths pointing
        // at a completely different show's folder, AND a corrupted Number (11, its season's
        // number, instead of 1, its real episode number) -- same underlying scrape-matching
        // bug. Its file-scan-stub duplicate (same parent/name, no external ids, so it loses on
        // score) held the real file and the real episode Number. Two things had to be true for
        // this to self-heal: the Number-mismatch guard must not block the merge just because the
        // corrupted side disagrees, and the merge itself must prefer the loser's fileScanner/
        // Number once the file-match signal shows the winner's own values are the untrustworthy
        // ones -- otherwise "winner blobs take precedence" would keep the corruption forever.
        var season = MakeHierarchyItem("Season 11", _tvType.Id, hierarchyLevel: 1);

        var enriched = new MediaItem
        {
            Name = "Celebrity: Social Media Food Failures", MediaTypeId = _tvType.Id,
            HierarchyLevel = 2, ParentId = season.Id, PosterUrl = "poster.jpg", Overview = "desc",
            Number = 11,
            MetadataJson = JsonSerializer.Serialize(new
            {
                fileScanner = new { filePaths = new[] { @"J:\Videos\TV\Futurama (1999)\Season 11" } },
            }),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(enriched);
        await _context.SaveChangesAsync();
        _context.MediaExternalIds.Add(new MediaExternalId { MediaItemId = enriched.Id, Source = "tmdb", ExternalId = "tv:31783/season:11/episode:1" });

        var stub = new MediaItem
        {
            Name = "Celebrity: Social Media Food Failures", MediaTypeId = _tvType.Id,
            HierarchyLevel = 2, ParentId = season.Id,
            Number = 1,
            MetadataJson = JsonSerializer.Serialize(new
            {
                fileScanner = new { filePaths = new[] { @"D:\Video\TV\Worst Cooks in America (2010)\Season 11\Worst Cooks in America - S11E01 - Celebrity - Social Media Food Failures.mkv" } },
            }),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(stub);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1, "the Number mismatch is explained by known corruption on the winner's side, not by these being genuinely different episodes");
        var survivor = await _context.MediaItems.SingleAsync(m => m.Id == enriched.Id);
        Chronicle.Services.Scan.FileIdentityJson.GetKnownFileName(survivor.MetadataJson)
            .Should().Contain("Celebrity", "the surviving item must end up with the file that actually matches its own title");
        survivor.Number.Should().Be(1, "the surviving item must end up with the real episode number, not its season's number");
    }

    // ── Pass 5: same-parent, same-Number CONTAINER auto-merge ──────────────────

    [Fact]
    public async Task RunAsync_MergesContainerDuplicates_WhenChildrenDoNotOverlap()
    {
        // The safe case this pass exists for: two season containers share the real season
        // Number ("Season 2" / "Season 02" -- a format mismatch the name-based passes above
        // never catch) but their episodes don't collide, so merging is unambiguous.
        var show    = MakeHierarchyItem("Renovation Resort", _tvType.Id, hierarchyLevel: 0);
        var seasonA = MakeHierarchyItem("Season 2",  _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeHierarchyItem("Season 02", _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeHierarchyItem("Episode One", _tvType.Id, hierarchyLevel: 2, parentId: seasonA.Id, number: 1);
        MakeHierarchyItem("Episode Two", _tvType.Id, hierarchyLevel: 2, parentId: seasonB.Id, number: 2);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1);
        var survivingSeason = await _context.MediaItems.SingleAsync(m => m.ParentId == show.Id);
        var children = await _context.MediaItems.Where(m => m.ParentId == survivingSeason.Id).ToListAsync();
        children.Select(c => c.Number).Should().BeEquivalentTo(new[] { 1, 2 },
            "both episodes should now live under the single surviving season");
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeContainerDuplicates_WhenChildrenOverlap()
    {
        // The unsafe case: both season containers already have their own "Episode 1". A plain
        // reparent can't resolve which one is real, so this must be left alone entirely --
        // reproducing that judgment call automatically is exactly the near-miss caught live
        // 2026-09-13 (an insufficiently-careful heuristic nearly deleted 40 real Rick and Morty
        // episodes). DuplicateCandidateScanService queues this shape for a human instead.
        var show    = MakeHierarchyItem("Renovation Resort", _tvType.Id, hierarchyLevel: 0);
        var seasonA = MakeHierarchyItem("Season 2",  _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        var seasonB = MakeHierarchyItem("Season 02", _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeHierarchyItem("Episode One (real)", _tvType.Id, hierarchyLevel: 2, parentId: seasonA.Id, number: 1);
        MakeHierarchyItem("Episode One (dup)",  _tvType.Id, hierarchyLevel: 2, parentId: seasonB.Id, number: 1);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "overlapping children make this ambiguous -- must not auto-merge");
        (await _context.MediaItems.CountAsync(m => m.ParentId == show.Id)).Should().Be(2, "both seasons must survive untouched");
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeContainerDuplicates_WhenBothSidesAreChildlessAndNeitherIsAStub()
    {
        // Caught in review before release: absence of overlap is not real safety evidence when
        // there's nothing on either side to compare in the first place. Two containers sharing
        // just (ParentId, MediaTypeId, Number) with zero children and no stub marker on either
        // side isn't proof of duplication -- it must be left for a human, not auto-merged on
        // bare Number agreement alone.
        var show    = MakeHierarchyItem("Renovation Resort", _tvType.Id, hierarchyLevel: 0);
        MakeHierarchyItem("Season 2",  _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeHierarchyItem("Season 02", _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "neither side has children or a stub marker -- not enough evidence to auto-merge");
        (await _context.MediaItems.CountAsync(m => m.ParentId == show.Id)).Should().Be(2, "both seasons must survive untouched");
    }

    [Fact]
    public async Task RunAsync_MergesContainerDuplicates_WhenChildlessSideIsAConfirmedStub()
    {
        // The genuinely safe childless case: an empty stub container (never actually
        // populated) colliding with a real, populated season is exactly the spurious-duplicate
        // shape this pass exists to clean up automatically.
        var show      = MakeHierarchyItem("Renovation Resort", _tvType.Id, hierarchyLevel: 0);
        var stub      = MakeHierarchyItem("Season 2",  _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2, isStub: true);
        var realSeason = MakeHierarchyItem("Season 02", _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 2);
        MakeHierarchyItem("Episode One", _tvType.Id, hierarchyLevel: 2, parentId: realSeason.Id, number: 1);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1, "a childless stub colliding with a populated season is the safe auto-merge case");
        (await _context.MediaItems.CountAsync(m => m.ParentId == show.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_DoesNotMergeContainers_UnderDifferentParents()
    {
        var showA = MakeHierarchyItem("Show A", _tvType.Id, hierarchyLevel: 0);
        var showB = MakeHierarchyItem("Show B", _tvType.Id, hierarchyLevel: 0);
        MakeHierarchyItem("Season 1", _tvType.Id, hierarchyLevel: 1, parentId: showA.Id, number: 1);
        MakeHierarchyItem("Season 1", _tvType.Id, hierarchyLevel: 1, parentId: showB.Id, number: 1);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "season 1 of two different shows must never be merged");
    }

    [Fact]
    public async Task RunAsync_DoesNotAutoMergeLeafItems_SharingANumber()
    {
        // Confirmed real, legitimate case (root-caused 2026-09-12): an unreliably-parsed
        // reality show can have several genuinely different episodes that all parsed to the
        // same episode Number. Pass 5 must never touch leaf-level items.
        var show   = MakeHierarchyItem("Reality Show", _tvType.Id, hierarchyLevel: 0);
        var season = MakeHierarchyItem("Season 1", _tvType.Id, hierarchyLevel: 1, parentId: show.Id, number: 1);
        MakeHierarchyItem("Episode Five A", _tvType.Id, hierarchyLevel: 2, parentId: season.Id, number: 5);
        MakeHierarchyItem("Episode Five B", _tvType.Id, hierarchyLevel: 2, parentId: season.Id, number: 5);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "leaf-level Number collisions are a known-legitimate case, not a duplicate signal");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A bare hierarchy node (show/season/episode container) with no fileScanner metadata --
    /// distinct from <see cref="MakeItem"/> below, which always attaches file metadata and is
    /// meant for flat, file-backed items (Passes 1-4). Pass 5 operates on containers, which
    /// legitimately have no file of their own.
    /// </summary>
    private MediaItem MakeHierarchyItem(
        string name, int typeId, int hierarchyLevel, int? parentId = null, int? number = null, bool isStub = false)
    {
        // Added to the context immediately (rather than returned bare) so its Id is assigned
        // right away -- a caller building a child in the same statement (e.g.
        // MakeHierarchyItem("Season 1", ..., parentId: show.Id)) needs the PARENT's real Id
        // already assigned at that point, not its default 0. Confirmed live while writing
        // these tests: batching multiple bare instances into one AddRange afterward captured
        // parentId as 0 for every child, since none of the Ids exist yet at construction time.
        var item = new MediaItem
        {
            Name           = name,
            MediaTypeId    = typeId,
            HierarchyLevel = hierarchyLevel,
            ParentId       = parentId,
            Number         = number,
            IsStub         = isStub,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        _context.MediaItems.Add(item);
        return item;
    }

    private MediaItem MakeItem(
        string fileName,
        string folder,
        string name,
        int?   typeId    = null,
        string? posterUrl = null,
        string? overview  = null)
    {
        var fullPath = $"{folder}/{fileName}";
        return new MediaItem
        {
            Name         = name,
            MediaTypeId  = typeId ?? _moviesType.Id,
            PosterUrl    = posterUrl,
            Overview     = overview,
            MetadataJson = JsonSerializer.Serialize(new
            {
                fileScanner = new
                {
                    importedAt = DateTime.UtcNow,
                    filePaths  = new[] { fullPath },
                    folderPath = folder,
                }
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}

/// <summary>
/// Minimal <see cref="IServiceScopeFactory"/> that resolves a pre-built
/// <see cref="ChronicleDbContext"/> — avoids full DI container in unit tests.
/// </summary>
file sealed class DirectScopeFactory : IServiceScopeFactory
{
    private readonly ChronicleDbContext _ctx;
    public DirectScopeFactory(ChronicleDbContext ctx) => _ctx = ctx;

    public IServiceScope CreateScope() => new DirectScope(_ctx);

    private sealed class DirectScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; }
        public DirectScope(ChronicleDbContext ctx)
            => ServiceProvider = new DirectServiceProvider(ctx);
        public void Dispose() { }
    }

    private sealed class DirectServiceProvider : IServiceProvider
    {
        private readonly ChronicleDbContext _ctx;
        private static readonly IMetadataResolutionService _noopResolution = new NoopResolutionService();
        private static readonly IMovieCollectionService _noopCollections = new NoopMovieCollectionService();
        private readonly IMergeService _mergeService;

        public DirectServiceProvider(ChronicleDbContext ctx)
        {
            _ctx = ctx;
            // A real MergeService, not a fake -- these tests exercise RunAsync's merge behavior
            // (see e.g. RunAsync_UserLibraryReassignedToWinner_BeforeLoserDeleted below), and
            // that behavior now lives in MergeService.MergeLoadedItemsAsync, the single shared
            // implementation DuplicateCleanupService calls instead of its own former copy.
            var fileScanMock = new Mock<IFileScanService>();
            fileScanMock.Setup(f => f.EnsureKnownFileNameAsync(
                    It.IsAny<MediaItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _mergeService = new MergeService(
                _ctx, _noopResolution, _noopCollections, fileScanMock.Object,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MergeService>.Instance);
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ChronicleDbContext))          return _ctx;
            if (serviceType == typeof(IMetadataResolutionService))  return _noopResolution;
            if (serviceType == typeof(IMovieCollectionService))     return _noopCollections;
            if (serviceType == typeof(IMergeService))               return _mergeService;
            return null;
        }
    }

    /// <summary>
    /// No-op collection service for unit tests — none of these tests exercise real collection
    /// containers, so IsCollectionContainerAsync always reporting "not a container" keeps
    /// MergeService's new container-mismatch guard a no-op here. Every other member is
    /// unreachable from DuplicateCleanupService.RunAsync and throws if that ever changes.
    /// </summary>
    private sealed class NoopMovieCollectionService : IMovieCollectionService
    {
        public Task<bool> IsCollectionContainerAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<HashSet<int>> GetCollectionContainerIdsAsync(
            ChronicleDbContext db, IReadOnlyCollection<int> candidateIds, CancellationToken ct = default)
            => Task.FromResult(new HashSet<int>());
        public Task<string?> GetFallbackPosterAsync(ChronicleDbContext db, int collectionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureCollectionParentAsync(ChronicleDbContext db, Chronicle.Core.Models.MediaItem movieItem,
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
        public Task<bool> EnsureCollectionStubsAsync(ChronicleDbContext db, Chronicle.Core.Models.MediaItem collection,
            Chronicle.Plugins.IMetadataProvider provider, CancellationToken ct = default,
            IReadOnlyList<(string PluginId, Chronicle.Plugins.IMetadataProvider Provider)>? allProviders = null)
            => throw new NotSupportedException();
        public Task UnparentFromCollectionAsync(ChronicleDbContext db, int itemId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ReparentIntoCollectionAsync(ChronicleDbContext db, int movieId, int collectionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// No-op resolution service for unit tests — ResolveAsync is a side effect we don't
    /// need to verify in DuplicateCleanupService tests.
    /// </summary>
    private sealed class NoopResolutionService : IMetadataResolutionService
    {
        public Task ResolveAsync(Chronicle.Core.Models.MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
            => Task.CompletedTask;
        public IReadOnlyCollection<string> GetCanonicalFields() => Array.Empty<string>();
        public Task SetOverrideAsync(Chronicle.Core.Models.MediaItem item, ChronicleDbContext db, string field, string url,
            string? sourcePluginId, string? sourceType, int? userId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearOverrideAsync(Chronicle.Core.Models.MediaItem item, ChronicleDbContext db, string field, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearItemOverridesAsync(Chronicle.Core.Models.MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> ClearOverridesForMediaTypeAsync(string mediaTypeName, Action<int, int>? onBatch = null, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> ClearAllOverridesLibraryWideAsync(Action<int, int>? onBatch = null, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> ClearOverridesForSubtreeAsync(int rootId, Action<int, int>? onBatch = null, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
