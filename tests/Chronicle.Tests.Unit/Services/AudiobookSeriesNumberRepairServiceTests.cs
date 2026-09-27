using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>A series lists in reading order: the position comes from "Series - N - (Year) - Title".</summary>
public class AudiobookSeriesNumberRepairServiceTests
{
    private static (ChronicleDbContext db, AudiobookSeriesNumberRepairService svc) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        return (db, new AudiobookSeriesNumberRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AudiobookSeriesNumberRepairService>.Instance));
    }

    private static MediaItem Book(string name, string folder, int? number = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = 2, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        MetadataJson = "{\"fileScanner\":{\"folderPath\":\"" + folder.Replace("\\", "\\\\") + "\"}}",
    };

    [Fact]
    public async Task SetsTheSeriesPositionFromTheFolderName_OnlyWhereNoneIsSet()
    {
        var (db, svc) = Setup();
        await using var _ = db;
        var two = Book("A.I. Apocalypse", @"E:\Audio Books\William Hertling\Singularity - 2 - (2012) - A.I. Apocalypse");
        var one = Book("Avogadro Corp", @"E:\Audio Books\William Hertling\Singularity - 1 - (2011) - Avogadro Corp");
        var alreadySet = Book("Set By Hand", @"E:\Audio Books\X\Series - 3 - (2020) - Set By Hand", number: 9);
        var noSeries = Book("Standalone", @"E:\Audio Books\X\- - (2015) - Standalone");
        db.MediaItems.AddRange(two, one, alreadySet, noSeries);
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        Assert.Equal(2, (await db.MediaItems.FindAsync(two.Id))!.Number);
        Assert.Equal(1, (await db.MediaItems.FindAsync(one.Id))!.Number);
        Assert.Equal(9, (await db.MediaItems.FindAsync(alreadySet.Id))!.Number);   // never overwritten
        Assert.Null((await db.MediaItems.FindAsync(noSeries.Id))!.Number);
    }
}
