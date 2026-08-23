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

    // ── UninstallPluginAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UninstallPluginAsync_DeletesTheDeployedPluginDirectory()
    {
        // Without this, PluginHostService's own AutoRegisterBundledPluginsAsync rediscovers
        // the still-present manifest.json + DLL on the very next API restart and silently
        // reinstalls the plugin right back -- confirmed live with Trakt.
        await using var db = MakeDb();
        var tempDir = Path.Combine(Path.GetTempPath(), "chronicle-plugin-uninstall-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var dllPath = Path.Combine(tempDir, "Chronicle.Plugin.Fake.dll");
        await File.WriteAllTextAsync(dllPath, "not a real dll, just needs to exist");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "manifest.json"), "{}");

        var plugin = new Plugin
        {
            PluginId = "chronicle.plugin.faketest", Name = "Fake", Version = "1.0.0",
            Author = "Test", DllPath = dllPath, IsEnabled = true,
            InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Plugins.Add(plugin);
        await db.SaveChangesAsync();

        var registry = new Mock<IPluginRegistry>();
        var protector = new Mock<IPluginSettingsProtector>();
        var service = new PluginService(db, registry.Object, protector.Object);

        try
        {
            await service.UninstallPluginAsync(plugin.Id);

            Assert.False(Directory.Exists(tempDir));
            registry.Verify(r => r.UnloadPlugin(plugin.Id), Times.Once);
            Assert.Null(await db.Plugins.FindAsync(plugin.Id));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UninstallPluginAsync_StillSucceeds_WhenPluginDirectoryAlreadyGone()
    {
        // The directory delete is best-effort -- a plugin whose files were already removed by
        // some other means (or never actually deployed) must not block the uninstall itself.
        await using var db = MakeDb();
        var missingDllPath = Path.Combine(Path.GetTempPath(),
            "chronicle-plugin-never-existed-" + Guid.NewGuid(), "Chronicle.Plugin.Fake.dll");

        var plugin = new Plugin
        {
            PluginId = "chronicle.plugin.faketest2", Name = "Fake", Version = "1.0.0",
            Author = "Test", DllPath = missingDllPath, IsEnabled = true,
            InstalledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Plugins.Add(plugin);
        await db.SaveChangesAsync();

        var registry = new Mock<IPluginRegistry>();
        var protector = new Mock<IPluginSettingsProtector>();
        var service = new PluginService(db, registry.Object, protector.Object);

        await service.UninstallPluginAsync(plugin.Id);

        Assert.Null(await db.Plugins.FindAsync(plugin.Id));
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
