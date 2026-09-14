using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class PluginServiceTests
{
    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    // Per-user request (2026-08-30, bug report -- Trakt's metadata box lingering after
    // removal): "if it's uninstalled, it means there are no holdovers allowed." Confirms
    // UninstallPluginAsync now purges the enrichment row, the external id, the plugin's own
    // MetadataJson partition, credits, and headshots -- not just the Plugins row itself.
    //
    // MediaCredit/PersonHeadshot were added to this purge 2026-09-14 after a real gap: TheTVDB
    // had been uninstalled well before this method's holdover-purge logic even existed, so its
    // MediaEnrichment rows were only ever cleaned up by hand -- had it also left MediaCredit or
    // PersonHeadshot rows behind, those would still be dangling today, the same way the
    // enrichment rows were found to be.
    [Fact]
    public async Task UninstallPluginAsync_PurgesEnrichmentExternalIdCreditsHeadshotsAndMetadataJson()
    {
        await using var db = MakeDb();
        const string pluginId = "chronicle.plugin.trakt";

        var plugin = new Plugin
        {
            PluginId = pluginId, Name = "Trakt", Version = "1.0", IsEnabled = false,
            InstalledAt = DateTime.UtcNow, DllPath = "trakt.dll",
        };
        db.Plugins.Add(plugin);

        var mediaType = new MediaType
        {
            Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mediaType);
        await db.SaveChangesAsync();

        var item = new MediaItem
        {
            MediaTypeId = mediaType.Id, Name = "Any Given Sunday", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            MetadataJson = "{\"" + pluginId + "\":{\"matched\":true},\"fileScanner\":{\"filePath\":\"x\"}}",
        };
        db.MediaItems.Add(item);

        var person = new MediaItem
        {
            MediaTypeId = mediaType.Id, Name = "Al Pacino", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(person);
        await db.SaveChangesAsync();

        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = item.Id, PluginId = pluginId, Status = EnrichmentStatus.Completed,
        });
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = item.Id, Source = "trakt", ExternalId = "12345",
        });
        db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId = item.Id, PersonName = "Al Pacino", Role = "Actor",
            Source = "trakt", PersonMediaItemId = person.Id,
        });
        db.PersonHeadshots.Add(new PersonHeadshot
        {
            PersonMediaItemId = person.Id, Url = "https://trakt.example/al-pacino.jpg",
            Source = "trakt",
        });
        // A different plugin's data on the same items must survive untouched.
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:1832",
        });
        db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId = item.Id, PersonName = "Al Pacino", Role = "Actor",
            Source = "tmdb", PersonMediaItemId = person.Id,
        });
        db.PersonHeadshots.Add(new PersonHeadshot
        {
            PersonMediaItemId = person.Id, Url = "https://tmdb.example/al-pacino.jpg",
            Source = "tmdb",
        });
        await db.SaveChangesAsync();

        var registry = new Mock<Chronicle.Services.Plugins.IPluginRegistry>();
        var protector = new Mock<Chronicle.Services.Plugins.IPluginSettingsProtector>();
        var service = new PluginService(db, registry.Object, protector.Object);

        await service.UninstallPluginAsync(plugin.Id);

        Assert.Null(await db.Plugins.FindAsync(plugin.Id));
        Assert.False(await db.MediaEnrichments.AnyAsync(e => e.PluginId == pluginId));
        Assert.False(await db.MediaExternalIds.AnyAsync(e => e.Source == "trakt"));
        Assert.False(await db.MediaCredits.AnyAsync(c => c.Source == "trakt"));
        Assert.False(await db.PersonHeadshots.AnyAsync(h => h.Source == "trakt"));
        // The surviving tmdb rows prove the purge is scoped to this plugin only.
        Assert.True(await db.MediaExternalIds.AnyAsync(e => e.Source == "tmdb"));
        Assert.True(await db.MediaCredits.AnyAsync(c => c.Source == "tmdb"));
        Assert.True(await db.PersonHeadshots.AnyAsync(h => h.Source == "tmdb"));

        var reloaded = await db.MediaItems.FindAsync(item.Id);
        Assert.DoesNotContain(pluginId, reloaded!.MetadataJson);
        Assert.Contains("fileScanner", reloaded.MetadataJson);
        registry.Verify(r => r.UnloadPlugin(plugin.Id), Times.Once);
    }

    // A movie-collection CONTAINER's MetadataJson partition is written directly by
    // MovieCollectionService.PersistCollectionMetadataAsync with no MediaEnrichment row of its
    // own at all -- collections can't go through the normal enrichment pipeline (see that
    // method's own doc). Found 2026-09-14 alongside the MediaCredit/PersonHeadshot gap: scoping
    // the MetadataJson strip to only items with an enrichment row for this plugin would leave a
    // stale metadata box on every collection container forever, since none of them ever get one.
    [Fact]
    public async Task UninstallPluginAsync_StripsMetadataJson_OnCollectionContainerWithNoEnrichmentRow()
    {
        await using var db = MakeDb();
        const string pluginId = "chronicle.plugin.tmdb";

        var plugin = new Plugin
        {
            PluginId = pluginId, Name = "TMDB", Version = "1.0", IsEnabled = false,
            InstalledAt = DateTime.UtcNow, DllPath = "tmdb.dll",
        };
        db.Plugins.Add(plugin);

        var mediaType = new MediaType
        {
            Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mediaType);
        await db.SaveChangesAsync();

        // No MediaEnrichment row for this item at all -- exactly how a collection container's
        // MetadataJson gets set in production.
        var collection = new MediaItem
        {
            MediaTypeId = mediaType.Id, Name = "Ghostbusters Collection", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            MetadataJson = "{\"" + pluginId + "\":{\"posterUrl\":\"https://tmdb.example/x.jpg\"}," +
                           "\"chronicle.plugin.fanarttv\":{\"posterUrl\":\"https://fanart.example/y.jpg\"}}",
        };
        db.MediaItems.Add(collection);
        await db.SaveChangesAsync();

        var registry = new Mock<Chronicle.Services.Plugins.IPluginRegistry>();
        var protector = new Mock<Chronicle.Services.Plugins.IPluginSettingsProtector>();
        var service = new PluginService(db, registry.Object, protector.Object);

        await service.UninstallPluginAsync(plugin.Id);

        var reloaded = await db.MediaItems.FindAsync(collection.Id);
        Assert.DoesNotContain(pluginId, reloaded!.MetadataJson);
        // A different, still-installed plugin's partition on the same container must survive.
        Assert.Contains("chronicle.plugin.fanarttv", reloaded.MetadataJson);
    }

    [Fact]
    public async Task SeedPluginTasksAsync_SetsSchedulable_False_WhenManifestSpecifies()
    {
        await using var db = MakeDb();
        var tasks = new List<PluginTaskManifest>
        {
            new() { TaskId = "fetch-missing-metadata", DisplayName = "Fetch",
                    DefaultCron = null, DefaultEnabled = false,
                    Schedulable = false }
        };

        await Chronicle.Services.Plugins.PluginService.SeedPluginTasksAsync(db, "chronicle.plugin.fanedit", tasks);

        var row = await db.BackgroundTasks.FindAsync("chronicle.plugin.fanedit:fetch-missing-metadata");
        Assert.NotNull(row);
        Assert.False(row.Schedulable);
    }

    [Fact]
    public async Task SeedPluginTasksAsync_SetsRunConfirmation_WhenManifestSpecifies()
    {
        await using var db = MakeDb();
        var tasks = new List<PluginTaskManifest>
        {
            new() { TaskId = "fetch-missing-metadata", DisplayName = "Fetch",
                    DefaultCron = null, DefaultEnabled = false,
                    RunConfirmationTitle   = "Are you sure?",
                    RunConfirmationMessage = "This scrapes a community site." }
        };

        await Chronicle.Services.Plugins.PluginService.SeedPluginTasksAsync(db, "chronicle.plugin.fanedit", tasks);

        var row = await db.BackgroundTasks.FindAsync("chronicle.plugin.fanedit:fetch-missing-metadata");
        Assert.Equal("Are you sure?",              row!.RunConfirmationTitle);
        Assert.Equal("This scrapes a community site.", row.RunConfirmationMessage);
    }

    [Fact]
    public async Task SeedPluginTasksAsync_DefaultsSchedulable_True_WhenNotSpecified()
    {
        await using var db = MakeDb();
        var tasks = new List<PluginTaskManifest>
        {
            new() { TaskId = "fetch-missing-metadata", DisplayName = "Fetch", DefaultCron = "0 4 * * *" }
        };

        await Chronicle.Services.Plugins.PluginService.SeedPluginTasksAsync(db, "chronicle.plugin.tmdb", tasks);

        var row = await db.BackgroundTasks.FindAsync("chronicle.plugin.tmdb:fetch-missing-metadata");
        Assert.True(row!.Schedulable);
        Assert.Null(row.RunConfirmationTitle);
    }
}
