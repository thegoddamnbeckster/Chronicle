using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The user's own book ("Heroes", #12, a file, no provider id) and a provider's placeholder for the same book
/// ("Hell Divers XII: Heroes", #12) used to sit side by side: neither the id nor the title matched them. The
/// position, when unambiguous, now settles it -- during a series sync and in the nightly repair pass.
/// </summary>
public class SeriesSlotTwinTests
{
    private static ChronicleDbContext NewDb()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null, bool stub = false) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number,
        SeriesPosition = stub ? number : null, IsStub = stub,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private sealed record Library(ChronicleDbContext Db, MediaItem Series, MediaItem Real, MediaItem Placeholder);

    /// <summary>A series with one book of the user's own at #12 (no ids) and Hardcover's placeholder at #12.</summary>
    private static async Task<Library> BuildAsync(string realName = "Heroes", string stubName = "Hell Divers XII: Heroes")
    {
        var db = NewDb();
        var author = Item("Nicholas Sansbury Smith", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Hell Divers", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var real = Item(realName, 2, series.Id, number: 12);
        var placeholder = Item(stubName, 2, series.Id, number: 12, stub: true);
        db.MediaItems.AddRange(real, placeholder);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = placeholder.Id, Source = "hardcover", ExternalId = "hardcover:1486584" },
            new MediaExternalId { MediaItemId = placeholder.Id, Source = "openlibrary", ExternalId = "OL37877667W" },
            new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = "hardcover:series:9" });
        db.MediaEnrichments.AddRange(
            new MediaItemEnrichment { MediaItemId = real.Id, PluginId = "hardcover", Status = EnrichmentStatus.NotFound, MaxRetries = 3 },
            new MediaItemEnrichment { MediaItemId = placeholder.Id, PluginId = "hardcover", Status = EnrichmentStatus.Completed, MaxRetries = 3 });
        await db.SaveChangesAsync();
        return new Library(db, series, real, placeholder);
    }

    private static MediaMetadata HardcoverBook(string id, string title, double position) => new()
    {
        ExternalId = id, Source = "hardcover", Title = title,
        ExtendedData = JsonSerializer.SerializeToElement(new { seriesPosition = position }),
    };

    private static Mock<IMetadataProvider> Provider(params MediaMetadata[] books)
    {
        var mock = new Mock<IMetadataProvider>();
        mock.Setup(p => p.Name).Returns("hardcover");
        mock.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "audiobooks" }]);
        mock.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Hell Divers", Results = books.ToList() });
        return mock;
    }

    private static SeriesSlotTwinRepairService NewRepair(ChronicleDbContext db) =>
        new(new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SeriesSlotTwinRepairService>.Instance);

    // ── nightly repair pass (no provider call) ────────────────────────────────

    [Fact]
    public async Task Repair_JoinsThePlaceholderToTheUsersBook_AtTheSamePosition()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;

        await NewRepair(db).ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        var real = (await db.MediaItems.FindAsync(lib.Real.Id))!;
        Assert.Equal("Heroes", real.Name);   // the user's book keeps its own name and its file
        var ids = (await db.MediaExternalIds.Where(e => e.MediaItemId == real.Id).ToListAsync()).Select(e => e.ExternalId);
        Assert.Contains("hardcover:1486584", ids);
        Assert.Contains("OL37877667W", ids);
        // its enrichment is queued again so the providers fetch it now that they have an id
        Assert.All(await db.MediaEnrichments.Where(e => e.MediaItemId == real.Id).ToListAsync(),
            e => Assert.Equal(EnrichmentStatus.Pending, e.Status));
        Assert.Empty(await db.MediaEnrichments.Where(e => e.MediaItemId == lib.Placeholder.Id).ToListAsync());
    }

    [Fact]
    public async Task Repair_LeavesBothWhenTheUsersBookHasItsOwnProviderIdAndAnUnrelatedTitle()
    {
        var lib = await BuildAsync(realName: "A Completely Different Book");
        await using var db = lib.Db;
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "hardcover", ExternalId = "hardcover:999" });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        Assert.NotNull(await db.MediaItems.FindAsync(lib.Real.Id));
    }

    [Fact]
    public async Task Repair_JoinsABookThatAlreadyHasADifferentEditionId_WhenTheTitlesAreRelated()
    {
        // "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure" (the user's file, matched to one edition) against the
        // provider's placeholder "Dungeon Crawler Carl" (another edition's id), both #1.
        var lib = await BuildAsync(realName: "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure", stubName: "Dungeon Crawler Carl");
        await using var db = lib.Db;
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "hardcover", ExternalId = "hardcover:2333832" });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        var ids = (await db.MediaExternalIds.Where(e => e.MediaItemId == lib.Real.Id).ToListAsync()).Select(e => e.ExternalId).ToList();
        Assert.Contains("hardcover:2333832", ids);          // keeps its own Hardcover id
        Assert.DoesNotContain("hardcover:1486584", ids);    // one id per source: the placeholder's is not added
        Assert.Contains("OL37877667W", ids);                // an id from a source it lacked is
    }

    [Fact]
    public async Task Repair_DoesNotRequeueEnrichment_WhenTheTwinGainedNoId()
    {
        var lib = await BuildAsync(realName: "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure", stubName: "Dungeon Crawler Carl");
        await using var db = lib.Db;
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "hardcover", ExternalId = "hardcover:2333832" });
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "openlibrary", ExternalId = "OL1W" });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        Assert.All(await db.MediaEnrichments.Where(e => e.MediaItemId == lib.Real.Id).ToListAsync(),
            e => Assert.Equal(EnrichmentStatus.NotFound, e.Status));   // untouched
    }

    private const string FileJson = """{"fileScanner":{"filePaths":["E:/Audio Books/Matt Dinniman/Dungeon Crawler Carl - 1 - (2021) - Dungeon Crawler Carl"]}}""";

    [Fact]
    public async Task Repair_NeverRemovesAFlaggedPlaceholderThatHoldsAFile()
    {
        // The scanner attached the user's folder to the provider's item and never cleared its stub flag, so the
        // "placeholder" is in fact the item that owns the file (Dungeon Crawler Carl #1, 2026-10-15).
        var lib = await BuildAsync(realName: "Dungeon Crawler Carl: A LitRPG/Gamelit Adventure", stubName: "Dungeon Crawler Carl");
        await using var db = lib.Db;
        lib.Placeholder.MetadataJson = FileJson;
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "hardcover", ExternalId = "hardcover:2333832" });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        Assert.NotNull(await db.MediaItems.FindAsync(lib.Real.Id));
        Assert.Contains(await db.MediaExternalIds.Where(e => e.MediaItemId == lib.Placeholder.Id).ToListAsync(),
            e => e.ExternalId == "hardcover:1486584");   // its ids stay with it
    }

    [Fact]
    public async Task Sync_NeverRemovesAFlaggedPlaceholderThatHoldsAFile()
    {
        var lib = await BuildAsync(realName: "Heroes", stubName: "Hell Divers XII: Heroes");
        await using var db = lib.Db;
        lib.Placeholder.MetadataJson = FileJson;
        await db.SaveChangesAsync();

        var provider = Provider(HardcoverBook("hardcover:1486584", "Hell Divers XII: Heroes", 12));
        await new BookSeriesService(null!, NullLogger<BookSeriesService>.Instance)
            .EnsureSeriesStubsAsync(db, lib.Series, provider.Object, default);

        Assert.NotNull(await db.MediaItems.FindAsync(lib.Placeholder.Id));
    }

    [Fact]
    public async Task StubFlagRepair_ClearsTheFlagOnlyWhereAFileIsHeld()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;
        lib.Placeholder.MetadataJson = FileJson;      // flagged, holds a file
        var bare = Item("Still A Placeholder", 2, lib.Series.Id, number: 20, stub: true);
        db.MediaItems.Add(bare);                      // flagged, no file
        await db.SaveChangesAsync();

        var svc = new StubFlagRepairService(
            new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StubFlagRepairService>.Instance);
        await svc.ExecuteAsync(default);

        Assert.False((await db.MediaItems.FindAsync(lib.Placeholder.Id))!.IsStub);
        Assert.True((await db.MediaItems.FindAsync(bare.Id))!.IsStub);
    }

    [Fact]
    public async Task Repair_JoinsOnTheFolderName_AndReplacesTheWronglyMatchedId()
    {
        // The file's folder says book 5, "The Butcher's Masquerade"; its title and Hardcover id came from a wrong
        // match to "The Dungeon Anarchist's Cookbook, Book 3". The provider's placeholder at #5 is that same book.
        var lib = await BuildAsync(realName: "The Dungeon Anarchist's Cookbook: Dungeon Crawler Carl, Book 3", stubName: "The Butcher's Masquerade");
        await using var db = lib.Db;
        lib.Real.MetadataJson = """{"fileScanner":{"folderPath":"E:/Audio Books/Matt Dinniman/Dungeon Crawler Carl - 5 - (2022) - The Butcher's Masquerade","filePaths":["E:/x/y"]}}""";
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "hardcover", ExternalId = "hardcover:2333834" });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        var ids = (await db.MediaExternalIds.Where(e => e.MediaItemId == lib.Real.Id).ToListAsync()).Select(e => e.ExternalId).ToList();
        Assert.DoesNotContain("hardcover:2333834", ids);   // the wrong match is gone
        Assert.Contains("hardcover:1486584", ids);          // the placeholder's id replaces it
        Assert.All(await db.MediaEnrichments.Where(e => e.MediaItemId == lib.Real.Id).ToListAsync(),
            e => Assert.Equal(EnrichmentStatus.Pending, e.Status));   // so the providers look again
    }

    [Fact]
    public async Task Repair_LeavesBothWhenMoreThanOneRealBookClaimsThePosition()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;
        db.MediaItems.Add(Item("Heroes (second copy)", 2, lib.Series.Id, number: 12));
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(lib.Placeholder.Id));
    }

    [Fact]
    public async Task Repair_KeepsAPlaceholderTheUserHasDataOn()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;
        db.UserLibraries.Add(new UserLibrary
        {
            UserId = 1, MediaItemId = lib.Placeholder.Id, Status = LibraryStatus.Completed,
            AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(lib.Placeholder.Id));
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == lib.Real.Id).ToListAsync());
    }

    [Fact]
    public async Task Repair_IgnoresABookTheUserPlacedInTheSeriesByHand()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = lib.Real.Id, Source = "chronicle", ExternalId = "manual-series-member" });
        await db.SaveChangesAsync();

        await NewRepair(db).ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(lib.Placeholder.Id));
    }

    // ── series sync (a provider's list in hand) ───────────────────────────────

    [Fact]
    public async Task Sync_MatchesTheUsersBookByPosition_WhenNoIdOrTitleMatches_InsteadOfCreatingAPlaceholder()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;
        db.MediaItems.Remove(lib.Placeholder);                // sync-time: no placeholder exists yet
        await db.SaveChangesAsync();

        var provider = Provider(HardcoverBook("hardcover:1486584", "Hell Divers XII: Heroes", 12));
        await new BookSeriesService(null!, NullLogger<BookSeriesService>.Instance)
            .EnsureSeriesStubsAsync(db, lib.Series, provider.Object, default);

        var books = await db.MediaItems.Where(m => m.ParentId == lib.Series.Id).ToListAsync();
        var only = Assert.Single(books);
        Assert.Equal((lib.Real.Id, "Heroes", false), (only.Id, only.Name, only.IsStub));
        Assert.Contains(await db.MediaExternalIds.Where(e => e.MediaItemId == only.Id).ToListAsync(),
            e => e.ExternalId == "hardcover:1486584");
        Assert.Equal(12.0, only.SeriesPosition);
    }

    [Fact]
    public async Task Sync_JoinsAnExistingPlaceholderToTheUsersBook()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;

        var provider = Provider(HardcoverBook("hardcover:1486584", "Hell Divers XII: Heroes", 12));
        await new BookSeriesService(null!, NullLogger<BookSeriesService>.Instance)
            .EnsureSeriesStubsAsync(db, lib.Series, provider.Object, default);

        var only = Assert.Single(await db.MediaItems.Where(m => m.ParentId == lib.Series.Id).ToListAsync());
        Assert.Equal(lib.Real.Id, only.Id);
    }

    [Fact]
    public async Task Sync_DoesNotGuess_WhenTheProviderListsTwoBooksAtTheSamePosition()
    {
        var lib = await BuildAsync();
        await using var db = lib.Db;
        db.MediaItems.Remove(lib.Placeholder);
        await db.SaveChangesAsync();

        var provider = Provider(
            HardcoverBook("hardcover:1", "Heroes (print)", 12),
            HardcoverBook("hardcover:2", "Heroes (omnibus)", 12));
        await new BookSeriesService(null!, NullLogger<BookSeriesService>.Instance)
            .EnsureSeriesStubsAsync(db, lib.Series, provider.Object, default);

        // the user's book was not claimed by either listing
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == lib.Real.Id).ToListAsync());
    }
}
