namespace Chronicle.Services.Export;

/// <summary>One interaction event, reduced to what watch derivation needs. Times are UTC.</summary>
public record LocalEvent(
    DateTime TimestampUtc,
    double ProgressPercent,
    bool MarkedAsWatched,
    string? DeviceName,
    bool IsApproximate);

/// <summary>What Chronicle can honestly say about an item's watches.</summary>
/// <param name="Playbacks">Real watches seen on a playing device. The only evidence that can be a rewatch.</param>
/// <param name="FirstWatchEvidence">
/// One time at which the item was watched, from a source that cannot tell watches apart (a service's own record, a
/// device's play count, the library's completion date); null when there is none. Worth sending only to a service
/// that holds NO watch of the item - never as a rewatch, because those sources' times differ for the very same
/// watch (a service stamps import time; a device stamps "now").
/// </param>
public record DerivedWatches(IReadOnlyList<DateTime> Playbacks, DateTime? FirstWatchEvidence);

/// <summary>
/// Turns an item's raw interaction events into the watches Chronicle is willing to export.
///
/// Chronicle has no table of "watches": an item carries a progress event every ~30 seconds per playback, plus
/// events reconciled from a Kodi device's own play count (whose times can be wrong - a SIMKL import once stamped
/// "now" on items and those were pulled in as plays), plus events imported from other services. Run against a
/// real library, a first version that treated imported and reconciled events as watches proposed 11,116 false
/// "rewatches": the same watch carries a different time in every system that records it. So:
///
/// 1. A <b>playback</b> is a run of consecutive marked-as-watched progress events from a playing device. Finishing a
///    film in two sittings (stop at 85%, resume next day) is one watch, not two: a further watch only counts after
///    the item was played again from near the start (progress at or below <see cref="RestartPercent"/>).
/// 2. Everything else - another service's event, a device-reconciled completion, the library's completion date - is
///    first-watch evidence only (see <see cref="DerivedWatches"/>).
/// 3. An event whose time is only approximate is never used.
/// </summary>
public static class WatchDeriver
{
    /// <summary>Progress at or below this, after a completed watch, means the item was started over.</summary>
    public const double RestartPercent = 25;

    /// <summary>Two completion events further apart than this are not part of the same playback.</summary>
    public static readonly TimeSpan RunGap = TimeSpan.FromMinutes(30);

    public static DerivedWatches Derive(
        IEnumerable<LocalEvent> events,
        ISet<string> importSourceNames,
        bool libraryCompleted,
        DateTime? libraryCompletedAtUtc)
    {
        // A completion stamped before 1970 or in the future is a bad clock or bad data, never a watch to push to a service.
        var usable = events.Where(e => !e.IsApproximate && ReleaseYear.IsPlausibleEventTime(e.TimestampUtc)).OrderBy(e => e.TimestampUtc).ToList();

        var playbacks = new List<DateTime>();
        var armed = true;
        DateTime? runLast = null;
        foreach (var e in usable.Where(e => IsDevicePlayback(e, importSourceNames)))
        {
            if (e.MarkedAsWatched)
            {
                if (runLast is { } last && e.TimestampUtc - last <= RunGap)
                {
                    runLast = e.TimestampUtc;                        // the same playback, still going
                    continue;
                }
                if (runLast is { } finished)
                {
                    playbacks.Add(finished);                         // an earlier run ended before this one began
                    armed = false;
                    runLast = null;
                }
                if (armed)
                    runLast = e.TimestampUtc;                        // a new watch begins
                else if (playbacks.Count > 0)
                    playbacks[^1] = e.TimestampUtc;                  // the same watch finished in another sitting
            }
            else
            {
                if (runLast is { } finished)
                {
                    playbacks.Add(finished);
                    armed = false;
                    runLast = null;
                }
                if (e.ProgressPercent <= RestartPercent) armed = true;
            }
        }
        if (runLast is { } tail) playbacks.Add(tail);
        playbacks.Sort();

        DateTime? evidence = usable
            .Where(e => e.MarkedAsWatched && !IsDevicePlayback(e, importSourceNames))
            .Select(e => (DateTime?)e.TimestampUtc)
            .OrderBy(t => t)
            .FirstOrDefault();
        evidence ??= libraryCompleted && libraryCompletedAtUtc is { } done && ReleaseYear.IsPlausibleEventTime(done) ? libraryCompletedAtUtc : null;

        return new DerivedWatches(playbacks, evidence);
    }

    /// <summary>
    /// A real playing device's own progress events. Not: an event reconciled from a device's stored play count, one
    /// imported from another service (its name is one of the registered import providers'), or one of unknown origin.
    /// </summary>
    private static bool IsDevicePlayback(LocalEvent e, ISet<string> importSourceNames) =>
        !string.IsNullOrWhiteSpace(e.DeviceName)
        && !e.DeviceName.Contains("(reconciled from local", StringComparison.OrdinalIgnoreCase)
        && !importSourceNames.Contains(e.DeviceName);
}
