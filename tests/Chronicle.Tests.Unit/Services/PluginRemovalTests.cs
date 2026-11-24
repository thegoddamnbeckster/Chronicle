using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Removing a plugin is one process, always followed, and it removes the plugin's data, not just the plugin: enrichment rows, ids,
/// credits, person credits, tasks, notifications, its data on every item (with the resolved values recomputed), its entries in the
/// settings that name plugins, and its own folder. It is recorded first and the plugin's record deleted last, so a removal that
/// is cut short is visible and is finished later; it is never half-removed in a way nobody can tell.
/// </summary>
public class PluginRemovalTests
{
    private const string Id = "chronicle.plugin.lastfm";

    private static ChronicleDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(int PluginDbId, int ItemId)> SeedAsync(ChronicleDbContext db, string dllPath = "lastfm.dll")
    {
        var plugin = new Plugin { PluginId = Id, Name = "Last.fm", Version = "1.0", IsEnabled = true, InstalledAt = DateTime.UtcNow, DllPath = dllPath };
        var type = new MediaType { Name = "music", DisplayName = "Music", HierarchyLevels = 3, InteractionVerb = "listened", ProgressUnit = "tracks", IsBuiltIn = true, IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Plugins.Add(plugin);
        db.MediaTypes.Add(type);
        await db.SaveChangesAsync();

        var item = new MediaItem
        {
            MediaTypeId = type.Id, Name = "Black Light Burns", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            MetadataJson = "{\"" + Id + "\":{\"title\":\"x\"},\"chronicle.plugin.musicbrainz\":{\"title\":\"y\"}}",
        };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();

        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = Id, Status = EnrichmentStatus.Completed });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = "chronicle.plugin.musicbrainz", Status = EnrichmentStatus.Completed });
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "lastfm", ExternalId = "a" });
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = "musicbrainz", ExternalId = "artist:b" });
        db.PersonProviderCredits.Add(new PersonProviderCredit { PersonMediaItemId = item.Id, Source = "lastfm", ExternalId = "c", MediaType = "music", Title = "t", Role = "r" });
        db.PersonProviderCredits.Add(new PersonProviderCredit { PersonMediaItemId = item.Id, Source = "tmdb", ExternalId = "d", MediaType = "music", Title = "t", Role = "r" });
        db.Notifications.Add(new Notification { UserId = 1, Kind = NotificationKinds.PluginUpdate, Title = "update", DedupeKey = Id + ":1.2.0", CreatedAt = DateTime.UtcNow });
        db.Notifications.Add(new Notification { UserId = 1, Kind = NotificationKinds.TaskFailed, Title = "other", DedupeKey = "task:x", CreatedAt = DateTime.UtcNow });
        db.AppSettings.Add(new AppSetting { Key = "metadata_assignment.config", Value = "{\"music\":{\"overview\":[\"chronicle.plugin.lastfm\",\"chronicle.plugin.musicbrainz\"]}}" });
        db.AppSettings.Add(new AppSetting { Key = "plugin_display_order.config", Value = "{\"music\":[\"chronicle.plugin.musicbrainz\",\"chronicle.plugin.lastfm\"]}" });
        db.AppSettings.Add(new AppSetting { Key = "scheduler.task_priority_order", Value = "[\"database_backup\",\"chronicle.plugin.lastfm:fetch-missing-metadata\"]" });
        db.AppSettings.Add(new AppSetting { Key = "sync_state." + Id + ".since", Value = "2026-01-01" });
        await db.SaveChangesAsync();
        return (plugin.Id, item.Id);
    }

    private static PluginService Service(ChronicleDbContext db, IMetadataResolutionService? resolver = null, IPluginIntegrity? integrity = null) =>
        new(db, new Mock<IPluginRegistry>().Object, new Mock<IPluginSettingsProtector>().Object, integrity, resolver);

    [Fact]
    public async Task RemovingAPlugin_RemovesItsDataEverywhere_NotJustTheRecord()
    {
        await using var db = MakeDb();
        var (pluginDbId, itemId) = await SeedAsync(db);
        var resolver = new Mock<IMetadataResolutionService>();

        await Service(db, resolver.Object).UninstallPluginAsync(pluginDbId);

        Assert.Null(await db.Plugins.FirstOrDefaultAsync(p => p.PluginId == Id));
        Assert.DoesNotContain(db.MediaEnrichments, e => e.PluginId == Id);
        Assert.DoesNotContain(db.MediaExternalIds, e => e.Source == "lastfm");
        Assert.DoesNotContain(db.PersonProviderCredits, c => c.Source == "lastfm");
        Assert.DoesNotContain(db.Notifications, n => n.DedupeKey != null && n.DedupeKey.Contains(Id));
        Assert.DoesNotContain(db.AppSettings, s => s.Key.Contains(Id));
        Assert.DoesNotContain("lastfm", (await db.AppSettings.FindAsync("metadata_assignment.config"))!.Value);
        Assert.DoesNotContain("lastfm", (await db.AppSettings.FindAsync("plugin_display_order.config"))!.Value);
        Assert.DoesNotContain("lastfm", (await db.AppSettings.FindAsync("scheduler.task_priority_order"))!.Value);

        var item = await db.MediaItems.FindAsync(itemId);
        Assert.DoesNotContain(Id, item!.MetadataJson);
        resolver.Verify(r => r.ResolveAsync(It.Is<MediaItem>(m => m.Id == itemId), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemovingAPlugin_LeavesEveryOtherPluginsDataAlone()
    {
        await using var db = MakeDb();
        var (pluginDbId, itemId) = await SeedAsync(db);

        await Service(db).UninstallPluginAsync(pluginDbId);

        Assert.Contains(db.MediaEnrichments, e => e.PluginId == "chronicle.plugin.musicbrainz");
        Assert.Contains(db.MediaExternalIds, e => e.Source == "musicbrainz");
        Assert.Contains(db.PersonProviderCredits, c => c.Source == "tmdb");
        Assert.Contains(db.Notifications, n => n.DedupeKey == "task:x");
        Assert.Contains("chronicle.plugin.musicbrainz", (await db.AppSettings.FindAsync("metadata_assignment.config"))!.Value);
        Assert.Contains("database_backup", (await db.AppSettings.FindAsync("scheduler.task_priority_order"))!.Value);
        Assert.Contains("chronicle.plugin.musicbrainz", (await db.MediaItems.FindAsync(itemId))!.MetadataJson);
    }

    [Fact]
    public async Task ARemovalThatFailsPartWay_LeavesThePluginVisiblyBeingRemoved_AndIsFinishedLater()
    {
        await using var db = MakeDb();
        var (pluginDbId, _) = await SeedAsync(db);
        var failing = new Mock<IMetadataResolutionService>();
        failing.Setup(r => r.ResolveAsync(It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database was busy"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db, failing.Object).UninstallPluginAsync(pluginDbId));

        // Not left in an unknown state: still listed, switched off, with the unfinished removal recorded.
        var plugin = await db.Plugins.SingleAsync(p => p.PluginId == Id);
        Assert.False(plugin.IsEnabled);
        Assert.NotNull(await db.AppSettings.FindAsync(PluginService.RemovalMarkerKey(Id)));

        // The next start finishes it.
        await Service(db, new Mock<IMetadataResolutionService>().Object).CompleteRemovalAsync(Id);

        Assert.Null(await db.Plugins.FirstOrDefaultAsync(p => p.PluginId == Id));
        Assert.Null(await db.AppSettings.FindAsync(PluginService.RemovalMarkerKey(Id)));
        Assert.DoesNotContain(db.MediaItems, m => m.MetadataJson != null && m.MetadataJson.Contains(Id));
        Assert.DoesNotContain(db.MediaExternalIds, e => e.Source == "lastfm");
    }

    [Fact]
    public async Task RunningARemovalAgain_IsHarmless()
    {
        await using var db = MakeDb();
        var (pluginDbId, _) = await SeedAsync(db);
        var service = Service(db);

        await service.UninstallPluginAsync(pluginDbId);
        await service.CompleteRemovalAsync(Id);

        Assert.Null(await db.Plugins.FirstOrDefaultAsync(p => p.PluginId == Id));
        Assert.Contains(db.MediaExternalIds, e => e.Source == "musicbrainz");
    }

    [Fact]
    public async Task RemovingAPlugin_DeletesItsOwnFolder_ButNeverOneOutsideThePluginsFolder()
    {
        var plugins = Path.Combine(Path.GetTempPath(), "chronicle-removal-" + Guid.NewGuid().ToString("N"));
        var own = Path.Combine(plugins, Id);
        var outside = Path.Combine(Path.GetTempPath(), "chronicle-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(own); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(own, "Chronicle.Plugin.LastFM.dll"), "x");
        File.WriteAllText(Path.Combine(outside, "Chronicle.Plugin.LastFM.dll"), "x");
        try
        {
            var integrity = new Mock<IPluginIntegrity>();
            integrity.SetupGet(i => i.PluginsDirectory).Returns(plugins);
            integrity.Setup(i => i.IsInsidePluginsDirectory(It.IsAny<string>()))
                .Returns((string p) => Path.GetFullPath(p).StartsWith(Path.GetFullPath(plugins), StringComparison.OrdinalIgnoreCase));

            await using var db = MakeDb();
            var (idInside, _) = await SeedAsync(db, Path.Combine(own, "Chronicle.Plugin.LastFM.dll"));
            await Service(db, integrity: integrity.Object).UninstallPluginAsync(idInside);
            Assert.False(Directory.Exists(own));

            await using var db2 = MakeDb();
            var (idOutside, _) = await SeedAsync(db2, Path.Combine(outside, "Chronicle.Plugin.LastFM.dll"));
            await Service(db2, integrity: integrity.Object).UninstallPluginAsync(idOutside);
            Assert.True(Directory.Exists(outside), "a folder outside the plugins folder is never deleted");
        }
        finally
        {
            foreach (var d in new[] { plugins, outside }) if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    [Fact]
    public void RemovePluginReferences_TakesThePluginOutOfEveryListAndEntryThatNamesIt()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"tv\":{\"title\":[\"a\",\"" + Id + "\"]},\"" + Id + "\":1,\"order\":[\"" + Id + ":task\",\"keep\"]}")!;

        Assert.True(PluginService.RemovePluginReferences(node, Id));

        var json = node.ToJsonString();
        Assert.DoesNotContain("lastfm", json);
        Assert.Contains("\"a\"", json);
        Assert.Contains("keep", json);
        Assert.False(PluginService.RemovePluginReferences(node, Id), "nothing left to remove");
    }
}
