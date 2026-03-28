using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Data;

public class EnrichmentStatusDbTests : IDisposable
{
    private readonly ChronicleDbContext _db;

    public EnrichmentStatusDbTests()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ChronicleDbContext(opts);
    }

    [Fact]
    public async Task CanInsertAndRetrieveEnrichmentStatus()
    {
        var user = new User { Username = "u", PasswordHash = "h", Email = "e@e.com" };
        _db.Users.Add(user);
        var mediaType = new MediaType
        {
            Name = "music", DisplayName = "Music", HierarchyLevels = 3,
            HierarchyLabels = "Artist,Album,Track", InteractionVerb = "listened", ProgressUnit = "tracks"
        };
        _db.MediaTypes.Add(mediaType);
        await _db.SaveChangesAsync();

        var item = new MediaItem { Name = "Radiohead", MediaTypeId = mediaType.Id, HierarchyLevel = 0 };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();

        var enrichment = new MediaItemEnrichment
        {
            MediaItemId = item.Id,
            PluginId    = "chronicle.plugin.musicbrainz",
            Status      = EnrichmentStatus.Pending,
            MaxRetries  = 3
        };
        _db.MediaEnrichments.Add(enrichment);
        await _db.SaveChangesAsync();

        var retrieved = await _db.MediaEnrichments.FirstAsync(x => x.MediaItemId == item.Id);
        retrieved.Status.Should().Be(EnrichmentStatus.Pending);
        retrieved.PluginId.Should().Be("chronicle.plugin.musicbrainz");
    }

    public void Dispose() => _db.Dispose();
}
