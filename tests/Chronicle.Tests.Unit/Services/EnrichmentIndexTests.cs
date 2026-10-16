using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// "Rows of this plugin in this status" is asked by every enrichment pass, every "anything still pending?" check and the
/// Background Tasks page. With 1.5M rows and only the (item, plugin) index, each was a full table scan: 2-4 s under load.
/// The composite index is what keeps them at milliseconds, so removing it by accident must fail a test.
/// </summary>
public class EnrichmentIndexTests
{
    [Fact]
    public void TheEnrichmentTableIsIndexedByPluginAndStatus_WithTheItemLastSoPendingItemIdsComeFromTheIndexAlone()
    {
        using var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var entity = db.Model.FindEntityType(typeof(MediaItemEnrichment))!;
        var index = entity.GetIndexes().SingleOrDefault(i => i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(MediaItemEnrichment.PluginId), nameof(MediaItemEnrichment.Status), nameof(MediaItemEnrichment.MediaItemId)]));

        Assert.NotNull(index);
        Assert.False(index!.IsUnique);
    }

    [Fact]
    public void TheUniqueItemPluginIndexIsStillThere()
    {
        using var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var entity = db.Model.FindEntityType(typeof(MediaItemEnrichment))!;
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(MediaItemEnrichment.MediaItemId), nameof(MediaItemEnrichment.PluginId)]));
    }
}
