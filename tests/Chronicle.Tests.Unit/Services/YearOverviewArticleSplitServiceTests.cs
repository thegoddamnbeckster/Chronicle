using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Live (2026-09-27): ten movies (e.g. "Hounded", TMDB-confirmed) were each renamed to Wikipedia's own
/// "YYYY in film" year-overview article, which also welded that article's ids onto the movie -- so when
/// the file scanner later created its own correctly-titled "Hounded" item, TMDB/FanartTV/SIMKL enrichment
/// found their ids already claimed by the year-overview record and refused to steal them.
/// </summary>
public class YearOverviewArticleSplitServiceTests
{
    private static (ChronicleDbContext db, YearOverviewArticleSplitService svc, Mock<IMetadataResolutionService> resolution) Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var svc = new YearOverviewArticleSplitService(null!, NullLogger<YearOverviewArticleSplitService>.Instance);
        return (db, svc, new Mock<IMetadataResolutionService>());
    }

    private static string HoundedJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["chronicle.plugin.tmdb"] = new Dictionary<string, object?>
        {
            ["externalId"] = "movie:935999", ["source"] = "tmdb", ["title"] = "Hounded", ["year"] = 2022,
        },
        ["chronicle.plugin.wikipedia"] = new Dictionary<string, object?>
        {
            ["externalId"] = "wikipedia:en:2022_in_film", ["source"] = "wikipedia", ["title"] = "2022 in film",
        },
    });

    [Theory]
    [InlineData("2022 in film", true)]
    [InlineData("2022 in television", true)]
    [InlineData("2022  in  Film", true)]
    [InlineData("Hounded", false)]
    [InlineData("Films of 2022", false)]
    public void TryDetectConflict_OnlyTheYearOverviewTitleShape_IsAConflict(string wikiTitle, bool expected)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["chronicle.plugin.tmdb"] = new Dictionary<string, object?> { ["title"] = "Hounded" },
            ["chronicle.plugin.wikipedia"] = new Dictionary<string, object?> { ["title"] = wikiTitle },
        });

        Assert.Equal(expected, YearOverviewArticleSplitService.TryDetectConflict(json, out _));
    }

    [Fact]
    public void TryDetectConflict_NoOtherProviderPartition_IsNotAConflict()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["chronicle.plugin.wikipedia"] = new Dictionary<string, object?> { ["title"] = "2022 in film" },
        });

        Assert.False(YearOverviewArticleSplitService.TryDetectConflict(json, out _));
    }

    [Fact]
    public async Task SplitAsync_MovesTheWikipediaArticleAndItsIdOntoANewStub_LeavesTheTmdbSideOnTheOriginal()
    {
        var (db, svc, resolution) = Setup();
        await using var _ = db;
        var item = new MediaItem
        {
            MediaTypeId = 1, Name = "2022 in film", Year = 2022, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, MetadataJson = HoundedJson(),
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:935999" },
            new MediaExternalId { MediaItemId = item.Id, Source = "wikipedia", ExternalId = "wikipedia:en:2022_in_film" });
        await db.SaveChangesAsync();

        var created = await svc.SplitAsync(db, resolution.Object, item.Id, "2022 in film", default);

        Assert.True(created);
        var original = await db.MediaItems.FindAsync(item.Id);
        Assert.DoesNotContain("wikipedia", original!.MetadataJson);
        Assert.Contains("chronicle.plugin.tmdb", original.MetadataJson);
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == item.Id && e.Source == "wikipedia").ToListAsync());
        Assert.Contains(await db.MediaExternalIds.Where(e => e.MediaItemId == item.Id).ToListAsync(), e => e.Source == "tmdb");

        var stub = await db.MediaItems.Where(m => m.Id != item.Id).SingleAsync();
        Assert.Equal("2022 in film", stub.Name);
        Assert.True(stub.IsStub);
        Assert.Contains("wikipedia", stub.MetadataJson);
        Assert.Equal("wikipedia:en:2022_in_film", (await db.MediaExternalIds.Where(e => e.MediaItemId == stub.Id).SingleAsync()).ExternalId);
        resolution.Verify(r => r.ResolveAsync(It.IsAny<MediaItem>(), db, default), Times.Exactly(2)); // original + stub
    }

    [Fact]
    public async Task SplitAsync_WhenTheArticleAlreadyHasAHome_OnlyDetaches_NoSecondStub()
    {
        var (db, svc, resolution) = Setup();
        await using var _ = db;
        var owner = new MediaItem
        {
            MediaTypeId = 1, Name = "2022 in film", HierarchyLevel = 0, IsStub = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(owner);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = owner.Id, Source = "wikipedia", ExternalId = "wikipedia:en:2022_in_film" });

        var item = new MediaItem
        {
            MediaTypeId = 1, Name = "2022 in film", Year = 2022, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, MetadataJson = HoundedJson(),
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:935999" },
            new MediaExternalId { MediaItemId = item.Id, Source = "wikipedia", ExternalId = "wikipedia:en:2022_in_film" });
        await db.SaveChangesAsync();

        var created = await svc.SplitAsync(db, resolution.Object, item.Id, "2022 in film", default);

        Assert.False(created);
        Assert.Equal(2, await db.MediaItems.CountAsync()); // no new stub
        Assert.Empty(await db.MediaExternalIds.Where(e => e.MediaItemId == item.Id && e.Source == "wikipedia").ToListAsync());
    }
}
