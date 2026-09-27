using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Live (2026-09-27): "- - (Unknown) - Title" folders were read as a series named "(Unknown)".</summary>
public class UnknownSeriesRepairServiceTests
{
    private static (ChronicleDbContext db, UnknownSeriesRepairService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return (db, new UnknownSeriesRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UnknownSeriesRepairService>.Instance));
    }

    private static MediaItem Item(string name, int level, int? parentId, string? folder = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        MetadataJson = folder is null ? null
            : "{\"fileScanner\":{\"folderPath\":\"" + folder.Replace("\\", "\\\\") + "\"}}",
    };

    [Fact]
    public async Task BooksLeaveTheUnknownSeries_ForTheAuthorWhoseFolderHoldsThem_AndTheSeriesIsRemoved()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var pirateaba = Item("pirateaba", 0, null, @"E:\Audio Books\Pirateaba");
        var stross = Item("Charles Stross", 0, null, @"E:\Audio Books\Charles Stross");
        db.MediaItems.AddRange(pirateaba, stross);
        await db.SaveChangesAsync();
        var unknown = Item("(Unknown)", 1, pirateaba.Id);
        db.MediaItems.Add(unknown);
        await db.SaveChangesAsync();
        var hidden = Item("The Hidden Family", 2, unknown.Id, @"E:\Audio Books\Charles Stross\- - (Unknown) - The Hidden Family");
        var noFolder = Item("Orphan", 2, unknown.Id);
        db.MediaItems.AddRange(hidden, noFolder);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var movedHidden = (await db.MediaItems.FindAsync(hidden.Id))!;
        Assert.Equal((stross.Id, 1), (movedHidden.ParentId, movedHidden.HierarchyLevel));
        var movedOrphan = (await db.MediaItems.FindAsync(noFolder.Id))!;
        Assert.Equal((pirateaba.Id, 1), (movedOrphan.ParentId, movedOrphan.HierarchyLevel)); // falls back to the series' author
        Assert.Null(await db.MediaItems.FindAsync(unknown.Id));
    }

    [Fact]
    public async Task OrdinarySeries_AreNeverTouched()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null, @"E:\Audio Books\Author");
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var series = Item("Real Series", 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        var book = Item("Book", 2, series.Id, @"E:\Audio Books\Author\Real Series - 1 - Book");
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal((series.Id, 2), ((await db.MediaItems.FindAsync(book.Id))!.ParentId, (await db.MediaItems.FindAsync(book.Id))!.HierarchyLevel));
    }
}
