using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Singleton service that manages fire-and-forget sync jobs.
/// Each job runs on a background thread; callers poll GetSnapshot() until done.
/// Old completed jobs are retained until the next Enqueue call to keep memory bounded.
/// </summary>
public sealed class SyncJobTracker : ISyncJobTracker
{
    private readonly ConcurrentDictionary<string, SyncJobSnapshot> _jobs = new();
    private readonly ILogger<SyncJobTracker> _log;

    public SyncJobTracker(ILogger<SyncJobTracker> log) => _log = log;

    public string Enqueue(Func<Task<SyncSummary>> work)
    {
        // Prune completed / failed jobs older than 1 hour to keep the dictionary small.
        var stale = _jobs.Where(kv => kv.Value.Status != "running")
                         .Select(kv => kv.Key)
                         .ToList();
        foreach (var key in stale) _jobs.TryRemove(key, out _);

        var jobId = Guid.NewGuid().ToString("N")[..10];
        _jobs[jobId] = new SyncJobSnapshot("running", null, null);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work();
                _jobs[jobId] = new SyncJobSnapshot("complete", result, null);
                _log.LogInformation("Sync job {JobId} completed: {Matched} matched, {Created} created",
                    jobId, result.ItemsMatched, result.StubsCreated);
            }
            catch (Exception ex)
            {
                _jobs[jobId] = new SyncJobSnapshot("failed", null, ex.Message);
                _log.LogError(ex, "Sync job {JobId} failed", jobId);
            }
        });

        return jobId;
    }

    public SyncJobSnapshot? GetSnapshot(string jobId) =>
        _jobs.TryGetValue(jobId, out var snap) ? snap : null;
}
