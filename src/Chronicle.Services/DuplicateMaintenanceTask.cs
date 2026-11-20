using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// The one nightly task for duplicates. The three passes depend on each other, so they run together in order
/// rather than as three jobs whose start times had to be staggered by hand:
///   1. Duplicate Candidate Scan    -- finds probable duplicate items and caches them for the Duplicates page.
///   2. Duplicate Person Auto-Resolve -- merges or dismisses the people candidates step 1 just produced when the
///                                     evidence is strong; the rest stay for manual review.
///   3. Duplicate Cleanup           -- removes items that share the same file path, keeping the best copy.
/// A failing pass is logged and never stops the ones after it; the task still fails at the end (see PassFailures).
/// </summary>
public sealed class DuplicateMaintenanceTask(
    DuplicateCandidateScanService candidateScan,
    DuplicatePersonAutoResolveService personAutoResolve,
    DuplicateCleanupService cleanup,
    ILogger<DuplicateMaintenanceTask> logger) : IScheduledTask
{
    public string TaskId      => "duplicate_maintenance";
    public string DisplayName => "Duplicate Maintenance";
    public string Description => "Nightly: scans for probable duplicate media items and people (cached for the Duplicates page), automatically merges or dismisses the person duplicates with strong evidence, and removes items sharing the same file path, keeping the best copy.";
    public string DefaultCron => "20 2 * * *";   // staggered after the backup
    public bool Exclusive => true; // a heavy database writer: conflicts with every other task (see TaskPriorityGate)
    public IReadOnlyList<string> ReplacesTaskIds => ["duplicate_candidate_scan", "duplicate_person_auto_resolve", "duplicate_cleanup"];

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var passes = new PassFailures(logger, "Duplicate maintenance");
        await passes.RunAsync("duplicate candidate scan", candidateScan.ExecuteAsync, ct);
        await passes.RunAsync("duplicate person auto-resolve", personAutoResolve.ExecuteAsync, ct);
        await passes.RunAsync("duplicate cleanup", cleanup.ExecuteAsync, ct);
        passes.ThrowIfAny();
    }
}
