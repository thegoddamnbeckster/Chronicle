using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Kodi stores an unknown year as -1 and reports it as 65535; a device search carrying it minted a movie
/// with Year 65535 (2026-09-26), which the device then showed as year 65535.
/// </summary>
public class ReleaseYearTests
{
    [Theory]
    [InlineData(1888, true)]
    [InlineData(1999, true)]
    [InlineData(2026, true)]
    [InlineData(65535, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1500, false)]
    public void IsPlausible(int year, bool expected) => Assert.Equal(expected, ReleaseYear.IsPlausible(year));

    [Fact]
    public void OrNull_NeverInventsAYear()
    {
        Assert.Null(ReleaseYear.OrNull(65535));
        Assert.Null(ReleaseYear.OrNull(null));
        Assert.Equal(2014, ReleaseYear.OrNull(2014));
    }

    [Fact]
    public async Task Repair_ClearsOnlyTheImpossibleYears()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var _ = db;
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        MediaItem Item(string name, int? year) => new()
        {
            MediaTypeId = 1, Name = name, Year = year, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.AddRange(Item("Bad", 65535), Item("Zero", 0), Item("Good", 2014), Item("None", null));
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var svc = new ImplausibleYearRepairService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ImplausibleYearRepairService>.Instance);

        await svc.ExecuteAsync(default);

        var years = await db.MediaItems.OrderBy(m => m.Name).Select(m => new { m.Name, m.Year }).ToListAsync();
        Assert.Equal(null, years.Single(x => x.Name == "Bad").Year);
        Assert.Equal(null, years.Single(x => x.Name == "Zero").Year);
        Assert.Equal(2014, years.Single(x => x.Name == "Good").Year);
    }

    [Theory]
    [InlineData("1990-05-01", true)]
    [InlineData("0200-01-01", false)]
    [InlineData("20222-06-21", false)]
    public void LifeDates_BirthMustBeReal(string date, bool expected)
    {
        if (!DateTime.TryParse(date, out var d)) { Assert.False(expected); return; }
        Assert.Equal(expected, ReleaseYear.IsPlausibleBirthDate(d));
    }

    [Fact]
    public void LifeDates_NoOneIsBornOrDiesInTheFuture()
    {
        Assert.False(ReleaseYear.IsPlausibleBirthDate(DateTime.UtcNow.AddYears(1)));
        Assert.False(ReleaseYear.IsPlausibleDeathDate(DateTime.UtcNow.AddYears(1)));
        Assert.True(ReleaseYear.IsPlausibleDeathDate(DateTime.UtcNow));
    }

    [Fact]
    public void ScrubMetadataJson_NullsOnlyTheImpossibleYears_InPluginAndResolvedBlobs()
    {
        // live (2026-10-19): Hardcover reported a book as released in 20222
        var json = "{\"fileScanner\":{\"importedAt\":\"2026-10-17\"},\"hardcover\":{\"title\":\"Biomancer 2\",\"year\":20222},\"tmdb\":{\"year\":2022},\"_resolved\":{\"year\":20222,\"title\":\"Biomancer 2\"}}";

        var clean = ReleaseYear.ScrubMetadataJson(json)!;
        var doc = System.Text.Json.JsonDocument.Parse(clean).RootElement;

        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.GetProperty("hardcover").GetProperty("year").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.GetProperty("_resolved").GetProperty("year").ValueKind);
        Assert.Equal(2022, doc.GetProperty("tmdb").GetProperty("year").GetInt32());
        Assert.Equal("Biomancer 2", doc.GetProperty("hardcover").GetProperty("title").GetString());
    }

    [Fact]
    public void ScrubMetadataJson_LeavesCleanDataAlone()
    {
        Assert.Null(ReleaseYear.ScrubMetadataJson("{\"tmdb\":{\"year\":2022}}"));
        Assert.Null(ReleaseYear.ScrubMetadataJson(null));
        Assert.Null(ReleaseYear.ScrubMetadataJson("{\"tmdb\":{\"title\":\"x\"}}"));
    }
}
