using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Microsoft.EntityFrameworkCore;
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
