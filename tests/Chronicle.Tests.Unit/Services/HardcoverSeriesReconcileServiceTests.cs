using System.Net;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The sweep that runs every series Chronicle has matched to a Hardcover id through
/// <see cref="IBookSeriesService.EnsureSeriesStubsAsync"/> -- chained after Hardcover's own
/// fetch-missing-metadata task (see TaskSchedulerService's own call site).
/// </summary>
public class HardcoverSeriesReconcileServiceTests
{
    private static (ChronicleDbContext db, HardcoverSeriesReconcileService svc, Mock<IMetadataProvider> provider)
        Setup(bool pluginInstalled = true)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.MediaTypes.Add(new MediaType { Id = 1, Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetSupportedMediaTypes()).Returns([new MediaTypeSupport { MediaTypeName = "audiobooks" }]);

        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProvider("hardcover")).Returns(pluginInstalled ? provider.Object : null);
        registry.Setup(r => r.GetMetadataProviderEntries())
            .Returns([("hardcover", provider.Object, (string?)null)]);

        var services = new ServiceCollection().AddSingleton(db)
            .AddScoped<IPluginRegistry>(_ => registry.Object)
            .AddScoped<IBookSeriesService, BookSeriesService>()
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<BookSeriesService>>(NullLogger<BookSeriesService>.Instance)
            .BuildServiceProvider();
        return (db, new HardcoverSeriesReconcileService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HardcoverSeriesReconcileService>.Instance), provider);
    }

    private static MediaItem Item(string name, int level, int? parentId, int? number = null) => new()
    {
        MediaTypeId = 1, Name = name, HierarchyLevel = level, ParentId = parentId, Number = number,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task SyncsEverySeriesWithAHardcoverId_AcrossMultipleAuthors()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author1 = Item("Author One", 0, null);
        var author2 = Item("Author Two", 0, null);
        db.MediaItems.AddRange(author1, author2);
        await db.SaveChangesAsync();
        var series1 = Item("Series One", 1, author1.Id);
        var series2 = Item("Series Two", 1, author2.Id);
        var notASeries = Item("Standalone, No Hardcover Id", 1, author1.Id);
        db.MediaItems.AddRange(series1, series2, notASeries);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = series1.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = series2.Id, Source = "hardcover", ExternalId = "hardcover:series:2" });
        await db.SaveChangesAsync();

        provider.Setup(p => p.GetByIdAsync("hardcover:series:1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Series One", Results = [new MediaMetadata { ExternalId = "hardcover:1", Title = "Book A" }] });
        provider.Setup(p => p.GetByIdAsync("hardcover:series:2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Series Two", Results = [new MediaMetadata { ExternalId = "hardcover:2", Title = "Book B" }] });

        await svc.ExecuteAsync(default);

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == series1.Id));
        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == series2.Id));
        Assert.Equal(0, await db.MediaItems.CountAsync(m => m.ParentId == notASeries.Id)); // never touched -- has no Hardcover id
    }

    [Fact]
    public async Task WithHardcoverNotInstalled_DoesNothing_AndDoesNotThrow()
    {
        var (db, svc, _) = Setup(pluginInstalled: false);
        await using var _ = db;

        await svc.ExecuteAsync(default); // must not throw
    }

    [Fact]
    public async Task AFailureOnOneSeries_DoesNotStopTheOthersFromSyncing()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var broken = Item("Broken Series", 1, author.Id);
        var fine = Item("Fine Series", 1, author.Id);
        db.MediaItems.AddRange(broken, fine);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = broken.Id, Source = "hardcover", ExternalId = "hardcover:series:1" },
            new MediaExternalId { MediaItemId = fine.Id, Source = "hardcover", ExternalId = "hardcover:series:2" });
        await db.SaveChangesAsync();

        provider.Setup(p => p.GetByIdAsync("hardcover:series:1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network blip"));
        provider.Setup(p => p.GetByIdAsync("hardcover:series:2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaMetadata { Title = "Fine Series", Results = [new MediaMetadata { ExternalId = "hardcover:2", Title = "Book" }] });

        await svc.ExecuteAsync(default); // must not throw, and must still process "fine"

        Assert.Equal(1, await db.MediaItems.CountAsync(m => m.ParentId == fine.Id));
    }

    private static async Task<MediaItem> AddSeriesAsync(ChronicleDbContext db, MediaItem author, string name, int hcId)
    {
        var series = Item(name, 1, author.Id);
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = series.Id, Source = "hardcover", ExternalId = $"hardcover:series:{hcId}" });
        await db.SaveChangesAsync();
        return series;
    }

    [Fact]
    public async Task ASeriesSyncedRecently_IsNotRefetched_ButAStaleOrNeverSyncedOneIs()
    {
        // Root-caused live (2026-09-28): the sweep re-fetched every matched series after every
        // fetch-missing-metadata run (~3,300 Hardcover requests a day against a 5,000/day cap).
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var fresh = await AddSeriesAsync(db, author, "Fresh", 1);
        var stale = await AddSeriesAsync(db, author, "Stale", 2);
        var never = await AddSeriesAsync(db, author, "Never", 3);
        db.AppSettings.Add(new AppSetting
        {
            Key = HardcoverSeriesReconcileService.LastSyncedSettingKey,
            Value = JsonSerializer.Serialize(new Dictionary<int, DateTime>
            {
                [fresh.Id] = DateTime.UtcNow.AddDays(-1),
                [stale.Id] = DateTime.UtcNow.AddDays(-8),
            }),
        });
        await db.SaveChangesAsync();
        foreach (var n in new[] { 1, 2, 3 })
            provider.Setup(p => p.GetByIdAsync($"hardcover:series:{n}", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaMetadata { Title = n == 1 ? "Fresh" : n == 2 ? "Stale" : "Never", Results = [] });

        await svc.ExecuteAsync(default);

        provider.Verify(p => p.GetByIdAsync("hardcover:series:1", It.IsAny<CancellationToken>()), Times.Never);
        provider.Verify(p => p.GetByIdAsync("hardcover:series:2", It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.GetByIdAsync("hardcover:series:3", It.IsAny<CancellationToken>()), Times.Once);
        var saved = JsonSerializer.Deserialize<Dictionary<int, DateTime>>(
            (await db.AppSettings.SingleAsync(s => s.Key == HardcoverSeriesReconcileService.LastSyncedSettingKey)).Value)!;
        Assert.True(saved[stale.Id] > DateTime.UtcNow.AddMinutes(-1)); // stamped now
        Assert.True(saved[never.Id] > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task WhenHardcoverIsRateLimited_TheSweepStopsAtOnce_KeepsEveryExternalId_AndDoesNotMarkTheSeriesSynced()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var first = await AddSeriesAsync(db, author, "First", 1);
        var second = await AddSeriesAsync(db, author, "Second", 2);
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("quota", null, HttpStatusCode.TooManyRequests));

        await svc.ExecuteAsync(default); // must not throw

        // Stopped after the first failure, not one doomed call per series...
        provider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // ...and a throttled provider is not evidence the stored ids are bad: both must survive.
        Assert.Equal(2, await db.MediaExternalIds.CountAsync(e => e.ExternalId.StartsWith("hardcover:series:")));
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == HardcoverSeriesReconcileService.LastSyncedSettingKey);
        var saved = setting is null ? [] : JsonSerializer.Deserialize<Dictionary<int, DateTime>>(setting.Value)!;
        Assert.Empty(saved);
    }

    [Fact]
    public async Task ASinglePass_NeverChecksMoreThanTheCap()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        for (var i = 1; i <= HardcoverSeriesReconcileService.MaxPerRun + 10; i++)
            await AddSeriesAsync(db, author, $"S{i}", i);
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new MediaMetadata
                { Title = db.MediaExternalIds.Where(e => e.ExternalId == id).Select(e => e.MediaItem!.Name).First(), Results = [] });

        await svc.ExecuteAsync(default);

        provider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(HardcoverSeriesReconcileService.MaxPerRun));
    }

    [Fact]
    public async Task ASeriesThatFails_IsNotRetriedNextPass_AndThreeFailuresInARowStopThePass()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        for (var i = 1; i <= 5; i++) await AddSeriesAsync(db, author, $"S{i}", i);
        provider.Setup(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Hardcover API returned 403 after 3 attempts"));

        await svc.ExecuteAsync(default);

        provider.Verify(p => p.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(HardcoverSeriesReconcileService.MaxConsecutiveFailures));
        // Their ids all survive (a failed fetch is not proof the id is bad)...
        Assert.Equal(5, await db.MediaExternalIds.CountAsync(e => e.ExternalId.StartsWith("hardcover:series:")));
        // ...and the failures are stamped so the next pass moves on to the untried ones first.
        var saved = JsonSerializer.Deserialize<Dictionary<int, DateTime>>(
            (await db.AppSettings.SingleAsync(s => s.Key == HardcoverSeriesReconcileService.LastSyncedSettingKey)).Value)!;
        Assert.Equal(HardcoverSeriesReconcileService.MaxConsecutiveFailures, saved.Count);
        Assert.All(saved.Values, at => Assert.True(DateTime.UtcNow - at < HardcoverSeriesReconcileService.StaleAfter));
    }

    [Fact]
    public async Task StampsForSeriesThatNoLongerHaveAHardcoverId_ArePruned()
    {
        var (db, svc, provider) = Setup();
        await using var _ = db;
        var author = Item("Author", 0, null);
        db.MediaItems.Add(author);
        await db.SaveChangesAsync();
        var live = await AddSeriesAsync(db, author, "Live", 1);
        db.AppSettings.Add(new AppSetting
        {
            Key = HardcoverSeriesReconcileService.LastSyncedSettingKey,
            Value = JsonSerializer.Serialize(new Dictionary<int, DateTime> { [live.Id] = DateTime.UtcNow.AddDays(-1), [999999] = DateTime.UtcNow }),
        });
        await db.SaveChangesAsync();

        await svc.ExecuteAsync(default);

        var saved = JsonSerializer.Deserialize<Dictionary<int, DateTime>>(
            (await db.AppSettings.SingleAsync(s => s.Key == HardcoverSeriesReconcileService.LastSyncedSettingKey)).Value)!;
        Assert.Equal([live.Id], saved.Keys);
    }
}
