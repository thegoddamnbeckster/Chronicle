using System.Text.Json;
using Chronicle.Data;
using Chronicle.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Decides which background job runs when two of them cannot run together.
///
/// SQLite has one writer at a time. A heavy job (the nightly backup, duplicate cleanup, the integrity repair, a library
/// scan) holds the database long enough that every light job writing at the same moment waits, times out and fails --
/// MusicBrainz and TheAudioDB failed that way nightly. So every job has a unique priority, and two jobs <i>conflict</i>
/// when either is exclusive (a heavy writer). Of two conflicting jobs the higher-ranked one runs; the lower-ranked one
/// pauses, at its next safe point, until the higher-ranked one is done. Jobs that do not conflict (two plugins fetching
/// from their own web services) run side by side whatever their ranks.
///
/// The ranks are one ordered list in app_settings (<see cref="OrderSettingKey"/>), top first, so they can be reordered
/// without a deploy. A job missing from the list is placed by a sensible default and the list is saved.
/// </summary>
public interface ITaskPriorityGate
{
    /// <summary>Queues the job and returns once no conflicting higher-ranked job is queued or running.</summary>
    Task<TaskTicket> EnterAsync(string taskId, bool exclusive, CancellationToken ct = default);

    /// <summary>All task ids, highest priority first (rank 1 is the first).</summary>
    Task<IReadOnlyList<string>> GetOrderAsync(CancellationToken ct = default);

    /// <summary>Moves a task one place up (-1) or down (+1) in the order. False when it cannot move that way.</summary>
    Task<bool> MoveAsync(string taskId, int direction, CancellationToken ct = default);

    /// <summary>For each job currently paused or queued: the job it is waiting for.</summary>
    IReadOnlyDictionary<string, string> WaitingFor();
}

/// <summary>A job's place in the gate. Dispose when the job ends; call <see cref="CheckpointAsync"/> at safe points.</summary>
public sealed class TaskTicket : IDisposable
{
    private readonly TaskPriorityGate _gate;
    internal TaskPriorityGate.Entry Entry { get; }
    internal TaskTicket(TaskPriorityGate gate, TaskPriorityGate.Entry entry) { _gate = gate; Entry = entry; }

    public string TaskId => Entry.TaskId;

    /// <summary>Returns at once unless a conflicting higher-ranked job has arrived, in which case waits until it is done.</summary>
    public Task CheckpointAsync(CancellationToken ct) => _gate.WaitTurnAsync(Entry, "paused", ct);

    public void Dispose() => _gate.Release(Entry);
}

/// <summary>
/// The ambient "which job am I running inside" for code deep in a job, so a long loop can reach a safe point without every
/// method between it and the scheduler passing a ticket along.
/// </summary>
public static class TaskGate
{
    private static readonly AsyncLocal<TaskTicket?> Current = new();

    /// <summary>Id of the scheduled job this code is running inside, or null (a request, a scan end-of-run, a test).</summary>
    public static string? CurrentTaskId => Current.Value?.TaskId;

    internal static void Use(TaskTicket? ticket) => Current.Value = ticket;

    /// <summary>A safe point: waits here while a higher-ranked conflicting job runs. Does nothing outside a scheduled job.</summary>
    public static Task CheckpointAsync(CancellationToken ct) =>
        Current.Value is { } ticket ? ticket.CheckpointAsync(ct) : Task.CompletedTask;
}

public sealed class TaskPriorityGate(IServiceScopeFactory scopeFactory, ILogger<TaskPriorityGate> logger) : ITaskPriorityGate
{
    /// <summary>app_settings key: JSON array of task ids, highest priority first.</summary>
    public const string OrderSettingKey = "scheduler.task_priority_order";

    /// <summary>Plugin task kinds that write in bulk and so conflict with everything else.</summary>
    private static readonly HashSet<string> ExclusivePluginTasks = new(StringComparer.OrdinalIgnoreCase)
        { "import-all", "sync-imdb-datasets", "sync-search-index" };

    /// <summary>The system tasks that hold the database longest, in the order they should win it. They head the default order.</summary>
    private static readonly string[] DefaultSystemOrder =
    [
        "database_backup", "scheduled_scan", "library_integrity_repair", "duplicate_maintenance",
        "database_maintenance_full", "rebuild_movie_collections",
    ];

    internal sealed class Entry(string taskId, bool exclusive)
    {
        public string TaskId { get; } = taskId;
        public bool Exclusive { get; } = exclusive;
        public int Rank { get; set; }
        public string? WaitingFor { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private TaskCompletionSource _changed = NewSignal();
    private Dictionary<string, int>? _ranks;
    private DateTime _ranksLoadedAt;
    private static readonly TimeSpan RankCacheAge = TimeSpan.FromSeconds(30);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static bool IsExclusivePluginTask(string bareTaskId) => ExclusivePluginTasks.Contains(bareTaskId);

    // ── entering, pausing, leaving ───────────────────────────────────────────

    public async Task<TaskTicket> EnterAsync(string taskId, bool exclusive, CancellationToken ct = default)
    {
        var ranks = await GetRanksAsync(ct);
        var entry = new Entry(taskId, exclusive) { Rank = ranks.GetValueOrDefault(taskId, int.MaxValue) };
        lock (_lock) _entries[taskId] = entry;
        Notify();
        try
        {
            await WaitTurnAsync(entry, "queued", ct);
        }
        catch
        {
            Release(entry);
            throw;
        }
        return new TaskTicket(this, entry);
    }

    internal async Task WaitTurnAsync(Entry entry, string verb, CancellationToken ct)
    {
        DateTime? since = null;
        while (true)
        {
            Task changed;
            Entry? blocker;
            lock (_lock)
            {
                blocker = FirstBlocker(entry);
                changed = _changed.Task;
                entry.WaitingFor = blocker?.TaskId;
            }
            if (blocker is null) break;

            if (since is null)
            {
                since = DateTime.UtcNow;
                logger.LogInformation(
                    "TaskPriority: '{Task}' (priority {Rank}) {Verb} at {At:HH:mm:ss} UTC -- waiting for '{Blocker}' (priority {BlockerRank}), which outranks it and cannot share the database",
                    entry.TaskId, entry.Rank, verb, since, blocker.TaskId, blocker.Rank);
            }
            await changed.WaitAsync(ct);
        }
        if (since is not null)
            logger.LogInformation(
                "TaskPriority: '{Task}' resumed at {At:HH:mm:ss} UTC after waiting {Seconds:F0}s",
                entry.TaskId, DateTime.UtcNow, (DateTime.UtcNow - since.Value).TotalSeconds);
    }

    /// <summary>The highest-ranked queued or running job that outranks this one and conflicts with it, or null.</summary>
    private Entry? FirstBlocker(Entry entry) =>
        _entries.Values
            .Where(o => !ReferenceEquals(o, entry) && o.Rank < entry.Rank && (o.Exclusive || entry.Exclusive))
            .OrderBy(o => o.Rank)
            .FirstOrDefault();

    internal void Release(Entry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(entry.TaskId, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.TaskId);
        }
        Notify();
    }

    private void Notify()
    {
        TaskCompletionSource old;
        lock (_lock)
        {
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }

    public IReadOnlyDictionary<string, string> WaitingFor()
    {
        lock (_lock)
            return _entries.Values.Where(e => e.WaitingFor is not null).ToDictionary(e => e.TaskId, e => e.WaitingFor!);
    }

    // ── the order ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetOrderAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        return await LoadOrderAsync(db, ct);
    }

    private async Task<Dictionary<string, int>> GetRanksAsync(CancellationToken ct)
    {
        lock (_lock)
            if (_ranks is not null && DateTime.UtcNow - _ranksLoadedAt < RankCacheAge) return _ranks;
        var order = await GetOrderAsync(ct);
        var ranks = order.Select((id, i) => (id, rank: i + 1)).ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);
        lock (_lock)
        {
            _ranks = ranks;
            _ranksLoadedAt = DateTime.UtcNow;
        }
        return ranks;
    }

    /// <summary>The saved order, with tasks that have gone dropped and tasks it does not list yet placed by default and saved.</summary>
    private async Task<IReadOnlyList<string>> LoadOrderAsync(ChronicleDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.Where(a => a.Key == OrderSettingKey).Select(a => a.Value).FirstOrDefaultAsync(ct);
        var saved = new List<string>();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { saved = JsonSerializer.Deserialize<List<string>>(raw) ?? []; }
            catch (JsonException) { /* unreadable: start from the defaults */ }
        }

        var existing = await db.BackgroundTasks.Select(t => t.TaskId).ToListAsync(ct);
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);
        var order = saved.Where(existingSet.Contains).Distinct(StringComparer.Ordinal).ToList();
        var missing = existing.Where(id => !order.Contains(id, StringComparer.Ordinal))
            .OrderBy(DefaultKey).ThenBy(id => id, StringComparer.Ordinal).ToList();

        if (missing.Count > 0 || order.Count != saved.Count)
        {
            order.AddRange(missing);
            await SaveOrderAsync(db, order, ct);
            logger.LogInformation("TaskPriority: saved the priority order ({Count} tasks, {New} newly placed)", order.Count, missing.Count);
        }
        return order;
    }

    private static async Task SaveOrderAsync(ChronicleDbContext db, List<string> order, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(order);
        var row = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == OrderSettingKey, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = OrderSettingKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Where a task not yet in the saved order goes: the heavy system jobs first, then the other system jobs, then the plugins'
    /// syncs, their fetch-missing-metadata runs, and last everything else a plugin offers.
    /// </summary>
    internal static (int Group, int Position) DefaultKey(string taskId)
    {
        var heavy = Array.IndexOf(DefaultSystemOrder, taskId);
        if (heavy >= 0) return (0, heavy);
        if (!taskId.Contains(':')) return (1, 0);
        var bare = taskId[(taskId.IndexOf(':') + 1)..];
        return bare switch
        {
            "delta-sync" or "refresh-imdb-ratings" => (2, 0),
            "fetch-missing-metadata" => (3, 0),
            _ => (4, 0),
        };
    }

    public async Task<bool> MoveAsync(string taskId, int direction, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var order = (await LoadOrderAsync(db, ct)).ToList();
        var index = order.IndexOf(taskId);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= order.Count) return false;

        (order[index], order[target]) = (order[target], order[index]);
        await SaveOrderAsync(db, order, ct);

        var ranks = order.Select((id, i) => (id, rank: i + 1)).ToDictionary(x => x.id, x => x.rank, StringComparer.Ordinal);
        lock (_lock)
        {
            _ranks = ranks;
            _ranksLoadedAt = DateTime.UtcNow;
            foreach (var entry in _entries.Values) entry.Rank = ranks.GetValueOrDefault(entry.TaskId, int.MaxValue);
        }
        Notify();
        logger.LogInformation("TaskPriority: '{Task}' moved to priority {Rank}", taskId, target + 1);
        return true;
    }
}
