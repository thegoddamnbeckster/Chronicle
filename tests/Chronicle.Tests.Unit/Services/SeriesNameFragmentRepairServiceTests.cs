using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Live (2026-09-27): before "(Unknown)" was recognised as a missing year, a folder like
/// "Undying Mercenaries - 13 - (Unknown) - Glass World" produced a series item literally named
/// "Undying Mercenaries - 13 - (Unknown)" beside the real, 13-book "Undying Mercenaries" series.
/// </summary>
public class SeriesNameFragmentRepairServiceTests
{
    [Theory]
    [InlineData("Undying Mercenaries - 13 - (Unknown)", "Undying Mercenaries", 13.0)]
    [InlineData("Star Force - 11 - (Unknown)", "Star Force", 11.0)]
    [InlineData("Star Force - 4.5 - (Unknown)", "Star Force", 4.5)]   // kept whole: SeriesPosition needs the .5
    [InlineData("Star Force - 3 - (2011)", "Star Force", 3.0)]
    [InlineData("Star Force Universe", null, null)]
    [InlineData("Star Force - Starship Pandora", null, null)]
    public void Split_OnlyTheBaseDashNumberDashParenShape_Matches(string name, string? baseName, double? position)
    {
        var result = SeriesNameFragmentRepairService.Split(name);
        if (baseName is null) Assert.Null(result);
        else Assert.Equal((baseName, position!.Value), result);
    }

    private static (ChronicleDbContext db, SeriesNameFragmentRepairService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var services = new ServiceCollection().AddSingleton(db)
            .AddScoped<IMergeService>(_ => new MergeService(
                db, Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(),
                Mock.Of<IFileScanService>(), NullLogger<MergeService>.Instance))
            .BuildServiceProvider();
        return (db, new SeriesNameFragmentRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SeriesNameFragmentRepairService>.Instance));
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null, double? position = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number, SeriesPosition = position,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task AFragmentSeriesWithANumberedBook_FoldsIntoTheRealSeries()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("B.V. Larson", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var real = Item("Undying Mercenaries", 1, author.Id);
        var fragment = Item("Undying Mercenaries - 13 - (Unknown)", 1, author.Id);
        db.MediaItems.AddRange(real, fragment);
        await db.SaveChangesAsync();
        var book12 = Item("Clone World", 2, real.Id, number: 12);
        db.MediaItems.Add(book12);
        var glassWorld = Item("Glass World", 2, fragment.Id, number: 13);
        db.MediaItems.Add(glassWorld);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var moved = (await db.MediaItems.FindAsync(glassWorld.Id))!;
        Assert.Equal((real.Id, 13), (moved.ParentId, moved.Number));
        Assert.Null(await db.MediaItems.FindAsync(fragment.Id));
    }

    [Fact]
    public async Task ABookAlreadyAtThatNumberInTheRealSeries_MergesInstead()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var real = Item("Star Force", 1, author.Id);
        var fragment = Item("Star Force - 11 - (Unknown)", 1, author.Id);
        db.MediaItems.AddRange(real, fragment);
        await db.SaveChangesAsync();
        var already = Item("Exile", 2, real.Id, number: 11);
        var dup = Item("Exile", 2, fragment.Id, number: 11);
        db.MediaItems.AddRange(already, dup);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Null(await db.MediaItems.FindAsync(dup.Id));   // merged away
        Assert.NotNull(await db.MediaItems.FindAsync(already.Id));
        Assert.Null(await db.MediaItems.FindAsync(fragment.Id));
    }

    [Fact]
    public async Task WithNoRealSiblingSeriesOfThatBaseName_TheFragmentIsLeftAlone()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var fragment = Item("Some Series - 2 - (Unknown)", 1, author.Id);
        db.MediaItems.Add(fragment);
        await db.SaveChangesAsync();
        var book = Item("Book", 2, fragment.Id, number: 2);
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(fragment.Id));
        Assert.Equal(fragment.Id, (await db.MediaItems.FindAsync(book.Id))!.ParentId);
    }

    [Fact]
    public async Task AmbiguouslyNamedSiblingSeries_AreNeverTouched()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var universe = Item("Star Force Universe", 1, author.Id); // no "- N - (...)" suffix
        db.MediaItems.Add(universe);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.NotNull(await db.MediaItems.FindAsync(universe.Id));
    }

    [Fact]
    public async Task ABookWithNoNumber_TakesTheFragmentNamesPosition_IntoBothNumberAndSeriesPosition()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var real = Item("Star Force", 1, author.Id);
        var fragment = Item("Star Force - 4.5 - (Unknown)", 1, author.Id);
        db.MediaItems.AddRange(real, fragment);
        await db.SaveChangesAsync();
        var novella = Item("Novella", 2, fragment.Id);
        db.MediaItems.Add(novella);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var moved = (await db.MediaItems.FindAsync(novella.Id))!;
        Assert.Equal((real.Id, 4, 4.5), (moved.ParentId, moved.Number, moved.SeriesPosition));
    }

    [Fact]
    public async Task ABookThatAlreadyHasItsOwnPosition_KeepsItUntouchedWhenMoved()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var real = Item("The Expanse", 1, author.Id);
        var fragment = Item("The Expanse - 1 - (Unknown)", 1, author.Id);
        db.MediaItems.AddRange(real, fragment);
        await db.SaveChangesAsync();
        var butcher = Item("The Butcher of Anderson Station", 2, fragment.Id, number: 1, position: 1.1);
        db.MediaItems.Add(butcher);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var moved = (await db.MediaItems.FindAsync(butcher.Id))!;
        Assert.Equal((real.Id, 1, 1.1), (moved.ParentId, moved.Number, moved.SeriesPosition));
    }

    [Fact]
    public async Task AFractionalBookIsNeverMergedIntoTheWholeNumberedBookThatSharesItsFlooredNumber()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var real = Item("The Expanse", 1, author.Id);
        var fragment = Item("The Expanse - 1 - (Unknown)", 1, author.Id);
        db.MediaItems.AddRange(real, fragment);
        await db.SaveChangesAsync();
        var leviathan = Item("Leviathan Wakes", 2, real.Id, number: 1, position: 1);
        var butcher = Item("The Butcher of Anderson Station", 2, fragment.Id, number: 1, position: 1.1);
        db.MediaItems.AddRange(leviathan, butcher);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        // Both survive as distinct books in the one real series.
        Assert.NotNull(await db.MediaItems.FindAsync(leviathan.Id));
        var moved = (await db.MediaItems.FindAsync(butcher.Id))!;
        Assert.Equal((real.Id, 1.1), (moved.ParentId, moved.SeriesPosition));
    }
}
