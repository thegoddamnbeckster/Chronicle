using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Data;

public class MediaItemEnrichmentModelTests
{
    [Fact]
    public async Task CanSaveAndRetrieveEnrichmentRow()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ChronicleDbContext(opts);

        var mediaType = new MediaType { Name = "movies", DisplayName = "Movies" };
        db.MediaTypes.Add(mediaType);
        var item = new MediaItem { Name = "Blade Runner", Year = 1982,
            MediaTypeId = mediaType.Id, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        var row = new MediaItemEnrichment
        {
            MediaItemId     = item.Id,
            PluginId        = "chronicle.plugin.tmdb",
            ExternalId      = "movie:78",
            Status          = EnrichmentStatus.Completed,
            RetryCount      = 0,
            MaxRetries      = 3,
            LastCompletedAt = DateTime.UtcNow,
        };
        db.MediaEnrichments.Add(row);
        await db.SaveChangesAsync();

        var saved = await db.MediaEnrichments
            .FirstAsync(e => e.PluginId == "chronicle.plugin.tmdb");
        saved.ExternalId.Should().Be("movie:78");
        saved.Status.Should().Be(EnrichmentStatus.Completed);
    }
}
