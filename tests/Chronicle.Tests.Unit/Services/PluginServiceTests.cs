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
    // UninstallPluginAsync now purges the enrichment row, the external id, and the plugin's
    // own MetadataJson partition -- not just the Plugins row itself.
    [Fact]
    public async Task UninstallPluginAsync_PurgesEnrichmentExternalIdAndMetadataJson()
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
        await db.SaveChangesAsync();

        db.MediaEnrichments.Add(new MediaItemEnrichment
        {
            MediaItemId = item.Id, PluginId = pluginId, Status = EnrichmentStatus.Completed,
        });
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = item.Id, Source = "trakt", ExternalId = "12345",
        });
        // A different plugin's data on the same item must survive untouched.
        db.MediaExternalIds.Add(new MediaExternalId
        {
            MediaItemId = item.Id, Source = "tmdb", ExternalId = "movie:1832",
        });
        await db.SaveChangesAsync();

        var registry = new Mock<Chronicle.Services.Plugins.IPluginRegistry>();
        var protector = new Mock<Chronicle.Services.Plugins.IPluginSettingsProtector>();
        var service = new PluginService(db, registry.Object, protector.Object);

        await service.UninstallPluginAsync(plugin.Id);

        Assert.Null(await db.Plugins.FindAsync(plugin.Id));
        Assert.False(await db.MediaEnrichments.AnyAsync(e => e.PluginId == pluginId));
        Assert.False(await db.MediaExternalIds.AnyAsync(e => e.Source == "trakt"));
        // The surviving tmdb external id proves the purge is scoped to this plugin only.
        Assert.True(await db.MediaExternalIds.AnyAsync(e => e.Source == "tmdb"));

        var reloaded = await db.MediaItems.FindAsync(item.Id);
        Assert.DoesNotContain(pluginId, reloaded!.MetadataJson);
        Assert.Contains("fileScanner", reloaded.MetadataJson);
        registry.Verify(r => r.UnloadPlugin(plugin.Id), Times.Once);
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
