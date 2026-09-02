using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class FileScanServiceHierarchyTests
{
    // ── CollapseAudiobooksToFolders ───────────────────────────────────────────

    [Fact]
    public void CollapseAudiobooksToFolders_SumsDurationsAcrossGroup()
    {
        // Path.Combine (not a hardcoded "/") throughout this file: FileScanService derives
        // AuthorFolderPath via Path.GetDirectoryName, which normalizes to the OS-native
        // separator -- a hardcoded "/" fixture only round-trips unchanged on Linux (where CI
        // runs) and fails on Windows, where the real, correct output uses "\".
        var root       = Path.Combine("C:", "Books", "Brandon Sanderson");
        var bookFolder = Path.Combine(root, "Stormlight - 1 - (2010) - The Way of Kings");
        var files = new List<ScannedFile>
        {
            new() { FilePath = Path.Combine(bookFolder, "01.mp3"), DurationSeconds = 1800,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
            new() { FilePath = Path.Combine(bookFolder, "02.mp3"), DurationSeconds = 2100,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
            new() { FilePath = Path.Combine(bookFolder, "03.mp3"), DurationSeconds = 900,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, root);

        Assert.Single(result);
        Assert.Equal(4800, result[0].TotalDurationSeconds); // 1800+2100+900
    }

    [Fact]
    public void CollapseAudiobooksToFolders_SingleRootLevelFile_SetsTotal()
    {
        var root = Path.Combine("C:", "Books");
        var files = new List<ScannedFile>
        {
            new() { FilePath = Path.Combine(root, "Elantris.mp3"), DurationSeconds = 3600 },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, root);

        Assert.Single(result);
        Assert.Equal(3600, result[0].TotalDurationSeconds);
    }

    [Fact]
    public void CollapseAudiobooksToFolders_BookFolderUnderAuthor_SetsAuthorFolderPath()
    {
        var libraryRoot   = Path.Combine("C:", "Books");
        var authorFolder  = Path.Combine(libraryRoot, "Brandon Sanderson");
        var bookFolder    = Path.Combine(authorFolder, "Stormlight - 1 - (2010) - The Way of Kings");
        var files = new List<ScannedFile>
        {
            new() { FilePath = Path.Combine(bookFolder, "01.mp3"), DurationSeconds = 1800,
                    AudioAlbum = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson" },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, libraryRoot);

        Assert.Single(result);
        Assert.Equal(authorFolder, result[0].AuthorFolderPath);
    }

    [Fact]
    public void CollapseAudiobooksToFolders_BookFolderAtScanRoot_LeavesAuthorFolderPathNull()
    {
        var root       = Path.Combine("C:", "Books", "Brandon Sanderson");
        var bookFolder = root; // book folder IS the scan root — no author level above it
        var files = new List<ScannedFile>
        {
            new() { FilePath = Path.Combine(bookFolder, "01.mp3"), DurationSeconds = 1800 },
        };

        var result = FileScanService.CollapseAudiobooksToFoldersForTest(files, root);

        Assert.Single(result);
        Assert.Null(result[0].AuthorFolderPath);
    }

    // ── GroupAudiobooksByAuthorAndSeries ──────────────────────────────────────

    [Fact]
    public void GroupAudiobooksByAuthorAndSeries_CreatesAuthorSeriesBookTree()
    {
        var files = new List<ScannedFile>
        {
            new() { FilePath = @"C:/Books/B Sanderson/SA-1-(2010)-Way",
                    ParsedTitle = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = "Stormlight Archive", ParsedYear = 2010, TotalDurationSeconds = 3600 },
            new() { FilePath = @"C:/Books/B Sanderson/SA-2-(2014)-Words",
                    ParsedTitle = "Words of Radiance", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = "Stormlight Archive", ParsedYear = 2014, TotalDurationSeconds = 4200 },
            new() { FilePath = @"C:/Books/B Sanderson/(2005)-Elantris",
                    ParsedTitle = "Elantris", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = null, ParsedYear = 2005, TotalDurationSeconds = 1800 },
        };

        var groups = FileScanService.GroupAudiobooksByAuthorAndSeriesForTest(files);

        // One author group
        Assert.Single(groups);
        var author = groups[0];
        Assert.Equal("Brandon Sanderson", author.Name);
        Assert.Equal(0, author.HierarchyLevel);

        // Two children: one series, one standalone book
        Assert.Equal(2, author.Children.Count);

        var series = author.Children.First(c => c.Name == "Stormlight Archive");
        Assert.Equal(1, series.HierarchyLevel);
        Assert.Equal(2, series.Children.Count);

        var standalone = author.Children.First(c => c.Name == "Elantris");
        Assert.Equal(1, standalone.HierarchyLevel);
        Assert.Empty(standalone.Children);
        Assert.Single(standalone.Files);
    }

    [Fact]
    public void GroupAudiobooksByAuthorAndSeries_PropagatesAuthorFolderPathOntoAuthorGroup()
    {
        var files = new List<ScannedFile>
        {
            new() { FilePath = @"C:/Books/B Sanderson/SA-1-(2010)-Way",
                    ParsedTitle = "The Way of Kings", AudioAlbumArtist = "Brandon Sanderson",
                    AudioGrouping = "Stormlight Archive", ParsedYear = 2010,
                    AuthorFolderPath = @"C:/Books/B Sanderson" },
        };

        var groups = FileScanService.GroupAudiobooksByAuthorAndSeriesForTest(files);

        Assert.Single(groups);
        Assert.Equal(@"C:/Books/B Sanderson", groups[0].FolderPath);
    }

    // ── FindOrCreateParentAsync merge-alias resolution ────────────────────────

    private static ChronicleDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(options);
    }

    [Fact]
    public async Task ScanAudiobooksHierarchically_TagNameWasMergedAway_ResolvesToAliasWinner_NotADuplicate()
    {
        await using var context = NewInMemoryContext();

        var mediaType = new MediaType
        {
            Id = 1, Name = "audiobooks", DisplayName = "Audiobooks",
            HierarchyLevels = 3, CreatedAt = DateTime.UtcNow,
        };
        context.MediaTypes.Add(mediaType);

        // The survivor of a previous merge -- e.g. "James Hunter, eden Hudson" was merged
        // into this item, which recorded the loser's exact name as a merge alias.
        var winner = new MediaItem
        {
            Id = 100, MediaTypeId = 1, Name = "James Hunter", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        context.MediaItems.Add(winner);
        context.MediaItemAliases.Add(new MediaItemAlias
        {
            MediaItemId = 100, Alias = "James Hunter, eden Hudson", Source = "merge",
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = new FileScanService(context, null!, null!, null!, null!, null!);

        // A fresh scan re-derives the exact merged-away tag string from the audiobook's
        // own ID3 AudioAlbumArtist tag -- no MediaItem has that literal Name any more.
        var collapsed = new List<ScannedFile>
        {
            new()
            {
                FilePath = @"C:/Books/James Hunter/Rebel Bounty Hunter - 1 - (2020) - Fringe World",
                ParsedTitle = "Fringe World", AudioAlbumArtist = "James Hunter, eden Hudson",
                ParsedYear = 2020, TotalDurationSeconds = 3600,
            },
        };

        await service.ScanAudiobooksHierarchicallyForTest(collapsed, mediaType, userId: 1, threshold: 0);

        // No new author-level duplicate was created -- exactly the one seeded winner remains.
        var authorItems = await context.MediaItems.Where(m => m.HierarchyLevel == 0).ToListAsync();
        Assert.Single(authorItems);
        Assert.Equal(100, authorItems[0].Id);

        // The new book was parented under the existing winner, not a fresh stub.
        var book = await context.MediaItems.SingleAsync(m => m.HierarchyLevel == 1);
        Assert.Equal(100, book.ParentId);
    }

    [Fact]
    public async Task ScanAudiobooksHierarchically_NoAliasMatch_StillCreatesNewAuthor()
    {
        await using var context = NewInMemoryContext();

        var mediaType = new MediaType
        {
            Id = 1, Name = "audiobooks", DisplayName = "Audiobooks",
            HierarchyLevels = 3, CreatedAt = DateTime.UtcNow,
        };
        context.MediaTypes.Add(mediaType);
        await context.SaveChangesAsync();

        var service = new FileScanService(context, null!, null!, null!, null!, null!);

        var collapsed = new List<ScannedFile>
        {
            new()
            {
                FilePath = @"C:/Books/Brand New Author/Some Book",
                ParsedTitle = "Some Book", AudioAlbumArtist = "Brand New Author",
                ParsedYear = 2022, TotalDurationSeconds = 1800,
            },
        };

        await service.ScanAudiobooksHierarchicallyForTest(collapsed, mediaType, userId: 1, threshold: 0);

        var authorItems = await context.MediaItems.Where(m => m.HierarchyLevel == 0).ToListAsync();
        Assert.Single(authorItems);
        Assert.Equal("Brand New Author", authorItems[0].Name);
    }

    // ── UpsertGroupItemAsync merge-alias resolution (ImportGroupsAsync) ───────
    //
    // Separate code path from FindOrCreateParentAsync above -- UpsertGroupItemAsync is what
    // ScheduledScanService's nightly scan (via ImportGroupsAsync) actually calls for
    // audiobooks in production. Confirmed live (2026-08-28): merging "Domagoj Kurmaić" into
    // an existing winner correctly recorded a MediaItemAlias, but the very next scheduled
    // scan still recreated it as a fresh duplicate stub, because this method never checked
    // MediaItemAliases at all -- FindOrCreateParentAsync's 2026-08-27 fix never touched it.

    [Fact]
    public async Task ImportGroupsAsync_RootNameWasMergedAway_ResolvesToAliasWinner_NotADuplicate()
    {
        await using var context = NewInMemoryContext();

        var mediaType = new MediaType
        {
            Id = 1, Name = "audiobooks", DisplayName = "Audiobooks",
            HierarchyLevels = 3, CreatedAt = DateTime.UtcNow,
        };
        context.MediaTypes.Add(mediaType);

        var winner = new MediaItem
        {
            Id = 100, MediaTypeId = 1, Name = "Domagoj Kurmaic", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        context.MediaItems.Add(winner);
        context.MediaItemAliases.Add(new MediaItemAlias
        {
            MediaItemId = 100, Alias = "Domagoj Kurmaić", Source = "merge",
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = new FileScanService(
            context, null!, null!, null!, new ImportProgressService(), null!);

        var request = new ImportGroupsRequest(
            [
                new ScanGroupImport(
                    Name: "Domagoj Kurmaić", Year: null, PosterPath: null,
                    Children: [], Files: [], FolderPath: @"E:/Audio Books/Domagoj Kurmaić"),
            ],
            MediaTypeId: 1);

        await service.ImportGroupsAsync(request, userIds: [1], manageProgress: false);

        // No new author-level duplicate was created -- exactly the one seeded winner remains.
        var authorItems = await context.MediaItems.Where(m => m.HierarchyLevel == 0).ToListAsync();
        Assert.Single(authorItems);
        Assert.Equal(100, authorItems[0].Id);
    }

    [Fact]
    public async Task ImportGroupsAsync_NoAliasMatch_StillCreatesNewRoot()
    {
        await using var context = NewInMemoryContext();

        var mediaType = new MediaType
        {
            Id = 1, Name = "audiobooks", DisplayName = "Audiobooks",
            HierarchyLevels = 3, CreatedAt = DateTime.UtcNow,
        };
        context.MediaTypes.Add(mediaType);
        await context.SaveChangesAsync();

        // Unlike the alias-match test above, this one DOES create a new item, which
        // triggers enrichment-row seeding -- needs a registry that returns no providers,
        // not null.
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);

        var service = new FileScanService(
            context, registry.Object, null!, null!, new ImportProgressService(), null!);

        var request = new ImportGroupsRequest(
            [
                new ScanGroupImport(
                    Name: "Brand New Author", Year: null, PosterPath: null,
                    Children: [], Files: [], FolderPath: @"E:/Audio Books/Brand New Author"),
            ],
            MediaTypeId: 1);

        await service.ImportGroupsAsync(request, userIds: [1], manageProgress: false);

        var authorItems = await context.MediaItems.Where(m => m.HierarchyLevel == 0).ToListAsync();
        Assert.Single(authorItems);
        Assert.Equal("Brand New Author", authorItems[0].Name);
    }

    [Fact]
    public async Task ImportGroupsAsync_FolderPathMatchesItemOfDifferentMediaType_CreatesSeparateItemNotCrossType()
    {
        // Regression test: confirmed live (2026-09-02) -- a music library folder literally
        // named "Dogma" (E:\Music\Dogma\) matched the Secondary (folder-path) tier against an
        // existing MOVIE named "Dogma" that happened to share that exact fileScanner.folderPath
        // string, silently attaching a music album as the movie's child instead of creating (or
        // finding) a "Dogma" music artist. The folder-path tier is only reached for container
        // groups with no files of their own (Files: [] here) -- exactly a music artist folder.
        await using var context = NewInMemoryContext();

        var movieType = new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow };
        var musicType = new MediaType { Id = 2, Name = "music", DisplayName = "Music", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        context.MediaTypes.AddRange(movieType, musicType);

        var movie = new MediaItem
        {
            Id = 500, MediaTypeId = 1, Name = "Dogma", HierarchyLevel = 0,
            MetadataJson = """{"fileScanner":{"folderPath":"E:\\Music\\Dogma","filePaths":["E:\\Movies\\Dogma\\Dogma.mkv"]}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        context.MediaItems.Add(movie);
        await context.SaveChangesAsync();

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProviderEntries()).Returns([]);
        var service = new FileScanService(context, registry.Object, null!, null!, new ImportProgressService(), null!);

        var request = new ImportGroupsRequest(
            [
                new ScanGroupImport(
                    Name: "Dogma", Year: null, PosterPath: null,
                    Children: [], Files: [], FolderPath: @"E:\Music\Dogma"),
            ],
            MediaTypeId: 2);

        await service.ImportGroupsAsync(request, userIds: [1], manageProgress: false);

        // A NEW music-type root was created -- the movie was never touched or reused as a parent.
        var musicRoots = await context.MediaItems.Where(m => m.MediaTypeId == 2 && m.HierarchyLevel == 0).ToListAsync();
        Assert.Single(musicRoots);
        Assert.NotEqual(movie.Id, musicRoots[0].Id);

        var untouchedMovie = await context.MediaItems.FindAsync(movie.Id);
        Assert.Equal(1, untouchedMovie!.MediaTypeId);
    }

    [Fact]
    public void GroupAudiobooksByAuthorAndSeries_UnknownAuthor_GroupsUnderUnknown()
    {
        var files = new List<ScannedFile>
        {
            new() { FilePath = @"C:/Books/Mystery/(2020)-Unknown Book",
                    ParsedTitle = "Unknown Book", AudioAlbumArtist = null,
                    AudioGrouping = null, ParsedYear = 2020 },
        };

        var groups = FileScanService.GroupAudiobooksByAuthorAndSeriesForTest(files);

        Assert.Single(groups);
        Assert.Equal("Unknown", groups[0].Name);
        Assert.Single(groups[0].Children);
    }


    [Fact]
    public async Task GroupFilesForHierarchyImport_TvFiles_GroupsByShow()
    {
        // Arrange — three episodes of the same show, two different seasons
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S01E01.mkv", ParsedTitle = "Ep1",
                ShowTitle = "My Show", SeasonNumber = 1, EpisodeNumber = 1
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S01E02.mkv", ParsedTitle = "Ep2",
                ShowTitle = "My Show", SeasonNumber = 1, EpisodeNumber = 2
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S02E01.mkv", ParsedTitle = "S2Ep1",
                ShowTitle = "My Show", SeasonNumber = 2, EpisodeNumber = 1
            },
        };

        // Act — group by show then season
        var groups = FileScanService.GroupByShowForTest(files);

        // Assert
        Assert.Single(groups);                                    // one show
        Assert.Equal("My Show", groups[0].ShowTitle);
        Assert.Equal(2, groups[0].Seasons.Count);                // two seasons
        Assert.Equal(2, groups[0].Seasons[1].Episodes.Count);    // S1 has 2 eps
        Assert.Single(groups[0].Seasons[2].Episodes);            // S2 has 1 ep

        await Task.CompletedTask; // suppress async-without-await warning
    }

    [Fact]
    public void GroupFilesForHierarchyImport_CaseInsensitive_MergesShowNames()
    {
        // Titles differing only in case should be merged into one show group
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "A.mkv", ParsedTitle = "Ep1",
                ShowTitle = "my show", SeasonNumber = 1, EpisodeNumber = 1
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "B.mkv", ParsedTitle = "Ep2",
                ShowTitle = "MY SHOW", SeasonNumber = 1, EpisodeNumber = 2
            },
        };

        var groups = FileScanService.GroupByShowForTest(files);

        Assert.Single(groups); // both should be in the same show
        Assert.Equal(2, groups[0].Seasons[1].Episodes.Count);
    }

    [Fact]
    public void GroupFilesForHierarchyImport_NullSeasonNumber_GroupsIntoSeasonOne()
    {
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "C.mkv", ParsedTitle = "Special",
                ShowTitle = "My Show", SeasonNumber = null, EpisodeNumber = 1
            },
        };

        var groups = FileScanService.GroupByShowForTest(files);

        Assert.Single(groups);
        Assert.True(groups[0].Seasons.ContainsKey(1)); // null season defaults to Season 1
    }

    [Fact]
    public void GroupFilesForHierarchyImport_EmptyShowTitle_IsSkipped()
    {
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "D.mkv", ParsedTitle = "", // empty ParsedTitle, no ShowTitle
                ShowTitle = null, SeasonNumber = 1, EpisodeNumber = 1
            },
        };

        var groups = FileScanService.GroupByShowForTest(files);

        Assert.Empty(groups); // file with no title should be skipped
    }

    [Fact]
    public void GroupFilesForHierarchyImport_EmptyInput_ReturnsEmptyList()
    {
        var groups = FileScanService.GroupByShowForTest([]);

        Assert.Empty(groups);
    }
}
