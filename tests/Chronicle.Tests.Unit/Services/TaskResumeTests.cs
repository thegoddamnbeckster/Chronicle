using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A restart cuts off whatever plugin fetches and syncs were running, and the missed-run skip then moves each to its next
/// cron time (a daily task would not run again for up to a day). The ones that were running are started again after startup.
/// </summary>
public class TaskResumeTests
{
    private static (ChronicleDbContext Db, TaskSchedulerService Svc) Build(params (string Id, string? Plugin, bool Enabled)[] rows)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var (id, plugin, enabled) in rows)
            db.BackgroundTasks.Add(new BackgroundTask { TaskId = id, DisplayName = id, CronExpression = "0 2 * * *", PluginId = plugin, IsEnabled = enabled });
        db.SaveChanges();
        var scopes = new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (db, new TaskSchedulerService([], scopes));
    }

    [Theory]
    [InlineData("chronicle.plugin.theaudiodb:fetch-missing-metadata", "chronicle.plugin.theaudiodb", true)]
    [InlineData("hardcover:delta-sync", "hardcover", true)]
    [InlineData("chronicle.plugin.tmdb:resync-all-metadata", "chronicle.plugin.tmdb", false)]   // a full re-fetch is not resumed
    [InlineData("chronicle.plugin.imdb:sync-imdb-datasets", "chronicle.plugin.imdb", false)]
    [InlineData("database_backup", null, false)]                                                  // heavy system jobs are never resumed
    public void OnlyFetchesAndSyncsAreResumable(string taskId, string? plugin, bool expected) =>
        Assert.Equal(expected, TaskSchedulerService.IsResumable(taskId, plugin));

    [Fact]
    public async Task ATaskIsListedWhileItRuns_AndNotAfterItFinishes()
    {
        var (db, svc) = Build(("chronicle.plugin.theaudiodb:fetch-missing-metadata", "chronicle.plugin.theaudiodb", true));
        await using var _ = db;

        await svc.RecordRunningAsync("chronicle.plugin.theaudiodb:fetch-missing-metadata", running: true);
        Assert.Contains("theaudiodb", (await db.AppSettings.SingleAsync(a => a.Key == TaskSchedulerService.RunningTasksSettingKey)).Value);

        await svc.RecordRunningAsync("chronicle.plugin.theaudiodb:fetch-missing-metadata", running: false);
        Assert.DoesNotContain("theaudiodb", (await db.AppSettings.SingleAsync(a => a.Key == TaskSchedulerService.RunningTasksSettingKey)).Value);
    }

    [Fact]
    public async Task AfterARestart_TheTasksThatWereRunningComeBack_ButNotDisabledOrHeavyOnes_AndTheListIsCleared()
    {
        var (db, svc) = Build(
            ("chronicle.plugin.theaudiodb:fetch-missing-metadata", "chronicle.plugin.theaudiodb", true),
            ("chronicle.plugin.musicbrainz:fetch-missing-metadata", "chronicle.plugin.musicbrainz", true),
            ("chronicle.plugin.deezer:fetch-missing-metadata", "chronicle.plugin.deezer", false),      // turned off since
            ("database_backup", null, true));
        await using var _ = db;
        foreach (var id in new[] { "chronicle.plugin.theaudiodb:fetch-missing-metadata", "chronicle.plugin.musicbrainz:fetch-missing-metadata",
                                   "chronicle.plugin.deezer:fetch-missing-metadata", "database_backup", "a-task-that-no-longer-exists" })
            await svc.RecordRunningAsync(id, running: true);

        var resumed = await svc.InterruptedTasksAsync(default);

        Assert.Equal(["chronicle.plugin.musicbrainz:fetch-missing-metadata", "chronicle.plugin.theaudiodb:fetch-missing-metadata"], resumed);
        Assert.Empty(await svc.InterruptedTasksAsync(default));                                       // listed once only
    }

    [Fact]
    public async Task NothingIsResumedWhenNothingWasRunning()
    {
        var (db, svc) = Build(("chronicle.plugin.theaudiodb:fetch-missing-metadata", "chronicle.plugin.theaudiodb", true));
        await using var _ = db;

        Assert.Empty(await svc.InterruptedTasksAsync(default));
    }
}
