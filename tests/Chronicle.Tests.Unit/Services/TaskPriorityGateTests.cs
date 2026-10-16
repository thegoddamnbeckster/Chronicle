using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Of two jobs that cannot share the database the higher-ranked one runs and the other pauses until it is done; jobs that
/// do not conflict run side by side. Every job has its own priority, kept in one reorderable list.
/// </summary>
public class TaskPriorityGateTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(250);

    private sealed record World(ChronicleDbContext Db, TaskPriorityGate Gate, IServiceScopeFactory Scopes);

    private static async Task<World> BuildAsync(params string[] taskIds)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var id in taskIds)
            db.BackgroundTasks.Add(new BackgroundTask
            {
                TaskId = id, DisplayName = id, CronExpression = "0 2 * * *",
                PluginId = id.Contains(':') ? id[..id.IndexOf(':')] : null,
            });
        await db.SaveChangesAsync();
        var scopes = new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new World(db, new TaskPriorityGate(scopes, NullLogger<TaskPriorityGate>.Instance), scopes);
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan time) => await Task.WhenAny(task, Task.Delay(time)) == task;

    // ── who may run ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ALowerRankedJobWaitsForAHigherRankedExclusiveOne_ThenRuns()
    {
        var w = await BuildAsync("database_backup", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        using var backup = await w.Gate.EnterAsync("database_backup", exclusive: true);

        var enrichment = w.Gate.EnterAsync("chronicle.plugin.omdb:fetch-missing-metadata", exclusive: false);
        Assert.False(await CompletesWithin(enrichment, Brief));                                 // paused behind the backup
        Assert.Equal("database_backup", w.Gate.WaitingFor()["chronicle.plugin.omdb:fetch-missing-metadata"]);

        backup.Dispose();
        using var ticket = await enrichment.WaitAsync(TimeSpan.FromSeconds(5));                 // the moment the backup is done
        Assert.Empty(w.Gate.WaitingFor());
    }

    [Fact]
    public async Task TheHigherRankedJobNeverWaitsForALowerRankedOne()
    {
        var w = await BuildAsync("database_backup", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        using var enrichment = await w.Gate.EnterAsync("chronicle.plugin.omdb:fetch-missing-metadata", exclusive: false);

        using var backup = await w.Gate.EnterAsync("database_backup", exclusive: true).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(backup);   // it runs at once; the running lower job is what pauses (next test)
    }

    [Fact]
    public async Task JobsThatDoNotConflictRunTogether_WhateverTheirRanks()
    {
        var w = await BuildAsync("chronicle.plugin.hardcover:fetch-missing-metadata", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        using var first = await w.Gate.EnterAsync("chronicle.plugin.hardcover:fetch-missing-metadata", exclusive: false);

        using var second = await w.Gate.EnterAsync("chronicle.plugin.omdb:fetch-missing-metadata", exclusive: false).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(second);
    }

    [Fact]
    public async Task TwoExclusiveJobsRunOneAfterTheOther_InRankOrder()
    {
        var w = await BuildAsync("database_backup", "duplicate_maintenance");
        await using var _ = w.Db;
        var lowFirst = w.Gate.EnterAsync("duplicate_maintenance", exclusive: true);
        using var low = await lowFirst;                                  // alone, so it runs

        var high = w.Gate.EnterAsync("database_backup", exclusive: true);
        using var backup = await high.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(backup);                                          // the higher job is never held up

        // and a lower one arriving while the backup runs has to wait for it
        var again = w.Gate.EnterAsync("duplicate_maintenance", exclusive: true);
        Assert.False(await CompletesWithin(again, Brief));
        backup.Dispose();
        using var resumed = await again.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(resumed);
    }

    // ── pausing a job that is already running ────────────────────────────────

    [Fact]
    public async Task ARunningLowerJobPausesAtItsNextSafePoint_WhenAHigherExclusiveOneArrives_AndResumesAfter()
    {
        var w = await BuildAsync("database_backup", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        using var enrichment = await w.Gate.EnterAsync("chronicle.plugin.omdb:fetch-missing-metadata", exclusive: false);
        await enrichment.CheckpointAsync(default);                       // nothing outranks it yet: returns at once

        var backup = await w.Gate.EnterAsync("database_backup", exclusive: true);
        var paused = enrichment.CheckpointAsync(default);
        Assert.False(await CompletesWithin(paused, Brief));              // held at the safe point
        Assert.Equal("database_backup", w.Gate.WaitingFor()["chronicle.plugin.omdb:fetch-missing-metadata"]);

        backup.Dispose();
        await paused.WaitAsync(TimeSpan.FromSeconds(5));                 // resumes the moment the backup is done
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheQueue()
    {
        var w = await BuildAsync("database_backup", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        using var backup = await w.Gate.EnterAsync("database_backup", exclusive: true);
        using var cts = new CancellationTokenSource();

        var waiting = w.Gate.EnterAsync("chronicle.plugin.omdb:fetch-missing-metadata", exclusive: false, cts.Token);
        await Task.Delay(Brief);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Empty(w.Gate.WaitingFor());
    }

    [Fact]
    public void OutsideAScheduledJob_ASafePointDoesNothing() =>
        Assert.True(TaskGate.CheckpointAsync(default).IsCompletedSuccessfully);

    // ── the order ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryTaskGetsItsOwnPriority_HeavyJobsFirst_AndTheOrderIsSaved()
    {
        var w = await BuildAsync(
            "chronicle.plugin.zeta:fetch-missing-metadata", "chronicle.plugin.alpha:fetch-missing-metadata",
            "chronicle.plugin.alpha:delta-sync", "chronicle.plugin.alpha:import-all", "plugin_update_check",
            "library_integrity_repair", "database_backup", "duplicate_maintenance");
        await using var _ = w.Db;

        var order = await w.Gate.GetOrderAsync();

        Assert.Equal(
        [
            "database_backup", "library_integrity_repair", "duplicate_maintenance",   // the heavy jobs, in the order they win the database
            "plugin_update_check",                                                    // other system jobs
            "chronicle.plugin.alpha:delta-sync",                                      // syncs
            "chronicle.plugin.alpha:fetch-missing-metadata", "chronicle.plugin.zeta:fetch-missing-metadata",
            "chronicle.plugin.alpha:import-all",                                      // anything else a plugin offers
        ], order);
        Assert.Equal(order.Count, order.Distinct().Count());                          // unique
        Assert.Equal(order, await w.Gate.GetOrderAsync());                            // stable on the next read (it was saved)
        Assert.True(await w.Db.AppSettings.AnyAsync(a => a.Key == TaskPriorityGate.OrderSettingKey));
    }

    [Fact]
    public async Task ANewTaskIsPlacedByDefaultWithoutDisturbingTheSavedOrder()
    {
        var w = await BuildAsync("database_backup", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        Assert.True(await w.Gate.MoveAsync("chronicle.plugin.omdb:fetch-missing-metadata", -1));   // the user puts omdb first

        w.Db.BackgroundTasks.Add(new BackgroundTask { TaskId = "chronicle.plugin.tmdb:fetch-missing-metadata", DisplayName = "t", CronExpression = "", PluginId = "chronicle.plugin.tmdb" });
        await w.Db.SaveChangesAsync();

        var order = await w.Gate.GetOrderAsync();

        Assert.Equal(["chronicle.plugin.omdb:fetch-missing-metadata", "database_backup", "chronicle.plugin.tmdb:fetch-missing-metadata"], order);
    }

    [Fact]
    public async Task MovingATaskSwapsItWithItsNeighbour_AndChangesWhoWins()
    {
        var w = await BuildAsync("database_backup", "duplicate_maintenance");
        await using var _ = w.Db;
        Assert.Equal(["database_backup", "duplicate_maintenance"], await w.Gate.GetOrderAsync());

        Assert.True(await w.Gate.MoveAsync("duplicate_maintenance", -1));
        Assert.Equal(["duplicate_maintenance", "database_backup"], await w.Gate.GetOrderAsync());
        Assert.False(await w.Gate.MoveAsync("duplicate_maintenance", -1));            // already first
        Assert.False(await w.Gate.MoveAsync("database_backup", 1));                   // already last

        using var duplicates = await w.Gate.EnterAsync("duplicate_maintenance", exclusive: true);
        var backup = w.Gate.EnterAsync("database_backup", exclusive: true);
        Assert.False(await CompletesWithin(backup, Brief));                           // the backup now waits for it
    }

    // ── spreading shared start times ──────────────────────────────────────────

    [Fact]
    public async Task TasksSharingAStartTimeAreSpreadOut_TheHighestPriorityKeepingTheEarliest_AndOnlyOnce()
    {
        var w = await BuildAsync("database_backup", "duplicate_maintenance", "hardcover:delta-sync", "chronicle.plugin.omdb:fetch-missing-metadata");
        await using var _ = w.Db;
        var other = w.Db.BackgroundTasks.Single(t => t.TaskId == "chronicle.plugin.omdb:fetch-missing-metadata");
        other.CronExpression = "10 6 * * *";                                          // already alone: untouched
        await w.Db.SaveChangesAsync();
        var scheduler = new TaskSchedulerService([], w.Scopes, w.Gate);

        await scheduler.StaggerSchedulesAsync(default);

        string Cron(string id) => w.Db.BackgroundTasks.Single(t => t.TaskId == id).CronExpression;
        Assert.Equal("0 2 * * *", Cron("database_backup"));                           // highest priority keeps the minute
        Assert.Equal("7 2 * * *", Cron("duplicate_maintenance"));
        Assert.Equal("14 2 * * *", Cron("hardcover:delta-sync"));
        Assert.Equal("10 6 * * *", Cron("chronicle.plugin.omdb:fetch-missing-metadata"));

        // a schedule set by hand afterwards is never touched again
        w.Db.BackgroundTasks.Single(t => t.TaskId == "duplicate_maintenance").CronExpression = "0 2 * * *";
        await w.Db.SaveChangesAsync();
        await scheduler.StaggerSchedulesAsync(default);
        Assert.Equal("0 2 * * *", Cron("duplicate_maintenance"));
    }
}
