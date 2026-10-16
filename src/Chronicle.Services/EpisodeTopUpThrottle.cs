using System.Collections.Concurrent;

namespace Chronicle.Services;

/// <summary>
/// Remembers when a show's episode list was last checked against its metadata provider, so Kodi asking for the same show over
/// and over (every library update, every episode) costs one provider round trip per season per hour, not per request. The
/// check itself walks every season in turn (~0.8 s per show, up to 8 s) and is only there to learn about episodes that aired
/// since the last look, which an hour's delay does not hurt. Singleton.
/// </summary>
public sealed class EpisodeTopUpThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastChecked = new();

    /// <summary>True when the show was checked successfully within <see cref="Interval"/>.</summary>
    public bool IsFresh(int showId) =>
        _lastChecked.TryGetValue(showId, out var at) && clock.GetUtcNow() - at < Interval;

    /// <summary>Record a check that ran to the end. A failed check is not recorded, so the next request retries.</summary>
    public void MarkChecked(int showId) => _lastChecked[showId] = clock.GetUtcNow();
}
