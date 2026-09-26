using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Confirmed live (2026-09-26): none of 3,851 episodes on a Kodi device had an air date, because the
/// TMDB/TVmaze plugins kept only the year. Episodes enriched before the fix are re-queued once, inside
/// the plugin's own Fetch Missing Metadata run.
/// </summary>
public class StaleEpisodeEnrichmentTests
{
    private static async Task<ChronicleDbContext> SeedAsync(
        string metadataJson, string pluginId, DateTime completedAt, int level = 2)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        var ep = new MediaItem
        {
            MediaTypeId = 1, Name = "Ep", HierarchyLevel = level, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(ep);
        await db.SaveChangesAsync();
        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = ep.Id, PluginId = pluginId, Status = EnrichmentStatus.Completed, LastCompletedAt = completedAt,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static readonly DateTime Before = StaleEpisodeEnrichment.FixedAt.AddDays(-3);

    [Theory]
    [InlineData("chronicle.plugin.tmdb")]
    [InlineData("chronicle.plugin.tvmaze")]
    public async Task EpisodeWithoutAnAirDate_EnrichedBeforeTheFix_IsRequeuedForThatPlugin(string pluginId)
    {
        await using var db = await SeedAsync("""{"chronicle.plugin.tmdb":{"year":2023}}""", pluginId, Before);

        var queued = await StaleEpisodeEnrichment.RequeueAsync(db, pluginId, default);

        Assert.Equal(1, queued);
        Assert.Equal(EnrichmentStatus.Pending, (await db.MediaEnrichments.SingleAsync()).Status);
    }

    [Fact]
    public async Task ARunForAnotherPlugin_NeverTouchesTheRow()
    {
        await using var db = await SeedAsync("""{"x":1}""", "chronicle.plugin.tmdb", Before);

        Assert.Equal(0, await StaleEpisodeEnrichment.RequeueAsync(db, "chronicle.plugin.tvmaze", default));
        Assert.Equal(0, await StaleEpisodeEnrichment.RequeueAsync(db, "chronicle.plugin.simkl", default));
    }

    [Fact]
    public async Task EpisodeThatAlreadyHasAnAirDate_IsLeftAlone()
    {
        await using var db = await SeedAsync(
            """{"chronicle.plugin.tmdb":{"extendedData":{"air_date":"2023-03-09"}}}""", "chronicle.plugin.tmdb", Before);

        Assert.Equal(0, await StaleEpisodeEnrichment.RequeueAsync(db, "chronicle.plugin.tmdb", default));
    }

    [Fact]
    public async Task EnrichmentCompletedAfterTheFix_IsNeverRequeuedAgain_EvenWithoutADate()
    {
        // A provider with no date for an episode is fetched once after the fix, then left alone.
        await using var db = await SeedAsync("""{"chronicle.plugin.tmdb":{"year":2023}}""", "chronicle.plugin.tmdb",
            StaleEpisodeEnrichment.FixedAt.AddHours(1));

        Assert.Equal(0, await StaleEpisodeEnrichment.RequeueAsync(db, "chronicle.plugin.tmdb", default));
    }

    [Fact]
    public async Task NonEpisodes_AreLeftAlone()
    {
        await using var db = await SeedAsync("""{"x":1}""", "chronicle.plugin.tmdb", Before, level: 0);

        Assert.Equal(0, await StaleEpisodeEnrichment.RequeueAsync(db, "chronicle.plugin.tmdb", default));
    }
}
