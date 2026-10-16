using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A plugin run that holds an item the duplicate cleanup merges away must skip it, not fail. Needs a real database: the in-memory
/// provider reports a vanished row differently, so this runs on a SQLite file with the real schema.
/// </summary>
public sealed class EnrichmentVanishedItemTests : IDisposable
{
    // Unique per test instance: the service's per-plugin and per-item locks are static, and every test database numbers its items from 1, so a shared
    // plugin id would make this test and any other enrichment test running in parallel wait on each other's locks.
    private readonly string PluginId = "chronicle.plugin.vanish-" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronicle-vanish-" + Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<ChronicleDbContext> _options;
    private readonly Mock<IPluginRegistry> _registry = new();

    public EnrichmentVanishedItemTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "t.db")};Pooling=False").Options;
        using var db = new ChronicleDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch */ }
    }

    private MetadataEnrichmentService NewService(ChronicleDbContext shared)
    {
        var services = new ServiceCollection();
        services.AddSingleton(shared);
        services.AddSingleton(_registry.Object);
        return new MetadataEnrichmentService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IMetadataResolutionService>(), Mock.Of<IMovieCollectionService>(), Mock.Of<IMetadataUrlValidator>(),
            Mock.Of<IPersonResolutionService>(), Mock.Of<ILogger<MetadataEnrichmentService>>());
    }

    private async Task<(int Item, int Row)> SeedAsync(ChronicleDbContext db, int typeId, string name, string externalId)
    {
        var item = new MediaItem { Name = name, MediaTypeId = typeId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        var row = new MediaItemEnrichment { MediaItemId = item.Id, PluginId = PluginId, Status = EnrichmentStatus.Pending, MaxRetries = 3, ExternalId = externalId };
        db.MediaEnrichments.Add(row);
        await db.SaveChangesAsync();
        return (item.Id, row.Id);
    }

    [Fact]
    public async Task AnItemMergedAwayWhileTheRunHeldIt_IsSkipped_AndTheRunCarriesOn()
    {
        await using var db = new ChronicleDbContext(_options);
        var type = await db.MediaTypes.FirstAsync(t => t.Name == "music");   // seeded by the real schema
        var (firstItem, firstRow) = await SeedAsync(db, type.Id, "First", "artist:first");
        var (secondItem, _) = await SeedAsync(db, type.Id, "Second", "artist:second");
        var (thirdItem, thirdRow) = await SeedAsync(db, type.Id, "Third", "artist:third");

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.PluginId).Returns(PluginId);
        provider.Setup(p => p.GetByIdAsync("artist:first", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken _) =>
            {
                // Behind the run's back, as the cleanup does: the second item (and its queue row, by cascade) is gone.
                await using var other = new ChronicleDbContext(_options);
                await other.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON; DELETE FROM media_items WHERE Id = {0}", secondItem);
                return new MediaMetadata { Title = "First", ExternalId = "artist:first" };
            });
        provider.Setup(p => p.GetByIdAsync("artist:second", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Second", ExternalId = "artist:second" });
        provider.Setup(p => p.GetByIdAsync("artist:third", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Third", ExternalId = "artist:third" });
        _registry.Setup(r => r.GetMetadataProvider(PluginId)).Returns(provider.Object);

        var run = () => NewService(db).EnrichPendingAsync(PluginId);

        await run.Should().NotThrowAsync();
        await using var check = new ChronicleDbContext(_options);
        (await check.MediaEnrichments.AsNoTracking().SingleAsync(e => e.Id == firstRow)).Status.Should().Be(EnrichmentStatus.Completed);
        (await check.MediaEnrichments.AsNoTracking().SingleAsync(e => e.Id == thirdRow)).Status.Should().Be(EnrichmentStatus.Completed, "the run went on past the vanished item");
        (await check.MediaItems.AnyAsync(m => m.Id == secondItem)).Should().BeFalse();
        (await check.MediaItems.AnyAsync(m => m.Id == firstItem && m.Id != thirdItem)).Should().BeTrue();
    }
}
