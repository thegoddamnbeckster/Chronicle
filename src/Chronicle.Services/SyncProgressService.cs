using System.Collections.Concurrent;

namespace Chronicle.Services;

/// <summary>
/// Snapshot of a plugin's current sync run (or its most-recent completed run).
/// IsRunning=false means the last run finished; the counts reflect the final totals.
/// </summary>
public record SyncProgressSnapshot(
    bool IsRunning,
    int  ItemsMatched,
    int  StubsCreated,
    int  WatchEventsAdded,
    int  Errors);

/// <summary>
/// Singleton that tracks live per-plugin sync progress so the frontend can poll
/// and show incrementing counters without waiting for the entire sync to finish.
/// </summary>
public class SyncProgressService
{
    private readonly ConcurrentDictionary<string, SyncProgressState> _states = new();

    /// <summary>Reset and start tracking a new sync run for <paramref name="pluginId"/>.</summary>
    public void Start(string pluginId)
        => _states[pluginId] = new SyncProgressState();

    public void IncrementMatched(string pluginId)
        => _states.GetValueOrDefault(pluginId)?.IncrementMatched();

    public void IncrementStub(string pluginId)
        => _states.GetValueOrDefault(pluginId)?.IncrementStub();

    public void IncrementWatchEvent(string pluginId)
        => _states.GetValueOrDefault(pluginId)?.IncrementWatchEvent();

    public void IncrementError(string pluginId)
        => _states.GetValueOrDefault(pluginId)?.IncrementError();

    /// <summary>
    /// Mark the run as finished. The final counts are preserved so the frontend
    /// can display a post-run summary until the next sync starts.
    /// </summary>
    public void Stop(string pluginId)
        => _states.GetValueOrDefault(pluginId)?.MarkDone();

    /// <summary>Returns the current snapshot, or null if this plugin has never synced.</summary>
    public SyncProgressSnapshot? GetSnapshot(string pluginId)
        => _states.TryGetValue(pluginId, out var s) ? s.ToSnapshot() : null;
}

internal class SyncProgressState
{
    private volatile bool _isRunning = true;
    private int _matched, _stubs, _watchEvents, _errors;

    public void IncrementMatched()    => Interlocked.Increment(ref _matched);
    public void IncrementStub()       => Interlocked.Increment(ref _stubs);
    public void IncrementWatchEvent() => Interlocked.Increment(ref _watchEvents);
    public void IncrementError()      => Interlocked.Increment(ref _errors);
    public void MarkDone()            => _isRunning = false;

    public SyncProgressSnapshot ToSnapshot() =>
        new(_isRunning, _matched, _stubs, _watchEvents, _errors);
}
