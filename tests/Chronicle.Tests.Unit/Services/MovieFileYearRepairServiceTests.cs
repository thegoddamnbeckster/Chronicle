using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Confirmed live (2026-09-26): "Total Recall (2012).mkv" sat on the provider-confirmed 1990 item, so
/// Kodi showed the 1990 film's plot, cast and poster for the 2012 file. A file name's year that
/// contradicts a provider-confirmed item year detaches the file so the next scan imports it properly.
/// </summary>
public class MovieFileYearRepairServiceTests
{
    private static string Json(params string[] paths) => new JsonObject
    {
        ["fileScanner"] = new JsonObject { ["filePaths"] = new JsonArray(paths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) },
        ["chronicle.plugin.tmdb"] = new JsonObject { ["year"] = "1990", ["title"] = "Total Recall" },
        ["scraperResolvedFile"] = new JsonObject { ["fileName"] = "Total Recall (1990).mkv" },
    }.ToJsonString();

    [Theory]
    [InlineData(@"N:\Videos\Movies\Total Recall (2012)\Total Recall (2012).mkv", 2012)]
    [InlineData(@"N:\Videos\Movies\Total Recall (1990)\Total Recall [1990].mkv", 1990)]
    [InlineData(@"N:\Videos\Movies\Total Recall (2012)\movie.mkv", 2012)] // year only in the folder
    [InlineData(@"N:\Videos\Movies\Total Recall\Total Recall.mkv", null)]
    public void YearOfFilePath_ReadsTheNameThenTheFolder(string path, int? expected) =>
        Assert.Equal(expected, MovieFileYearRepairService.YearOfFilePath(path));

    [Fact]
    public void ContradictedFile_OnAProviderConfirmedItem_IsFlagged_TheMatchingFileIsNot()
    {
        var json = Json(@"N:\Movies\Total Recall (1990)\Total Recall (1990).mkv", @"N:\Movies\Total Recall (2012)\Total Recall (2012).mkv");

        var bad = MovieFileYearRepairService.FindContradictedPaths(json, 1990);

        Assert.Single(bad);
        Assert.Contains("(2012)", bad[0]);
    }

    [Fact]
    public void OneYearOfNoise_IsNeverAContradiction() =>
        Assert.Empty(MovieFileYearRepairService.FindContradictedPaths(Json(@"N:\Movies\Total Recall (1991)\Total Recall (1991).mkv"), 1990));

    [Fact]
    public void ItemYearNoProviderConfirms_IsLeftAlone()
    {
        // No provider partition states 1990 -- the item's own year may be the wrong side, so nothing is guessed.
        var json = new JsonObject
        {
            ["fileScanner"] = new JsonObject { ["filePaths"] = new JsonArray(JsonValue.Create(@"N:\Movies\X (2012)\X (2012).mkv")) },
        }.ToJsonString();
        Assert.Empty(MovieFileYearRepairService.FindContradictedPaths(json, 1990));
    }

    [Fact]
    public void NonVideoPaths_AreIgnored() =>
        Assert.Empty(MovieFileYearRepairService.FindContradictedPaths(Json(@"N:\Books\Odds On (1966).epub"), 2013));

    [Fact]
    public async Task Execute_DetachesOnlyTheContradictedFile_AndItsKnownFilenameRow()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        var item = new MediaItem
        {
            MediaTypeId = 1, Name = "Total Recall", Year = 1990, HierarchyLevel = 0,
            MetadataJson = Json(@"N:\Movies\Total Recall (1990)\Total Recall (1990).mkv", @"N:\Movies\Total Recall (2012)\Total Recall (2012).mkv"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        db.MediaItemKnownFileNames.AddRange(
            new MediaItemKnownFileName { MediaItemId = item.Id, FileName = "Total Recall (1990).mkv" },
            new MediaItemKnownFileName { MediaItemId = item.Id, FileName = "Total Recall (2012).mkv" });
        await db.SaveChangesAsync();

        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var svc = new MovieFileYearRepairService(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MovieFileYearRepairService>.Instance);

        await svc.ExecuteAsync(default);

        var reloaded = await db.MediaItems.SingleAsync();
        var remaining = Chronicle.Services.Scan.FileIdentityJson.ExtractFilePaths(reloaded.MetadataJson);
        Assert.Equal([@"N:\Movies\Total Recall (1990)\Total Recall (1990).mkv"], remaining);
        Assert.Equal(["Total Recall (1990).mkv"], await db.MediaItemKnownFileNames.Select(k => k.FileName).ToListAsync());
        // The item itself is untouched: still the provider-confirmed 1990 film.
        Assert.Equal(1990, reloaded.Year);
        Assert.Contains("chronicle.plugin.tmdb", reloaded.MetadataJson);
    }
}
