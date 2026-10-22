using Chronicle.Plugins;

namespace Chronicle.Services.Export;

/// <summary>One item as Chronicle knows it, ready to be compared with a service's records.</summary>
/// <param name="PlaybacksUtc">Real playbacks seen on a device (<see cref="DerivedWatches.Playbacks"/>); the only
/// evidence that can be a rewatch.</param>
/// <param name="FirstWatchEvidenceUtc">One time the item was watched from a source that cannot tell watches apart
/// (<see cref="DerivedWatches.FirstWatchEvidence"/>); sent only to a service that holds no watch of the item.</param>
/// <param name="Rating">The user's rating (1-10) or null.</param>
/// <param name="RatedAtUtc">When it was set; null when unknown.</param>
/// <param name="WatchedLocally">Chronicle considers the item watched (a rating is only exported for watched items:
/// SIMKL files a rated, unlisted movie as completed).</param>
/// <param name="ListStatus">SIMKL-vocabulary status (plantowatch, watching, hold, dropped) or null.</param>
public record LocalItem(
    ExportItemRef Item,
    IReadOnlyList<DateTime> PlaybacksUtc,
    DateTime? FirstWatchEvidenceUtc,
    int? Rating,
    DateTime? RatedAtUtc,
    bool WatchedLocally,
    string? ListStatus);

/// <summary>
/// Works out what Chronicle holds that a service does not. Pure: no I/O, so every rule is unit-tested.
///
/// Rules (the user's, 2026-10-18): act only where the two sides are obviously different; the most recent record
/// wins; a watch within a minute of one the service has is the same watch. The service's own minimum gap between
/// separate watches (SIMKL: two days) is also treated as "the same watch" - the service would fold it into the
/// existing one anyway, and the same window absorbs the old six-hour timezone skew in imported times.
/// Nothing here ever removes anything from the service.
/// </summary>
public static class ExportPlanner
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>What the service holds for one item, gathered under every key the item is known by.</summary>
    private sealed class RemoteEntry
    {
        public readonly List<DateTimeOffset> Watches = [];     // watches with a known time
        public int UnknownTimeWatches;                          // watched, but the service does not know when
        public RemoteRating? Rating;
        public RemoteListStatus? Status;
    }

    public static ExportBatch Plan(
        IReadOnlyList<LocalItem> local,
        RemoteState remote,
        ExportCapabilities caps,
        ExportOptions options)
    {
        var index = IndexRemote(remote);
        var window = caps.RewatchMinimumGap > Grace ? caps.RewatchMinimumGap : Grace;

        var watches = new List<ExportWatch>();
        var ratings = new List<ExportRating>();
        var statuses = new List<ExportListStatus>();

        foreach (var item in local)
        {
            if (item.Item.Keys.Count == 0) continue;     // no usable id: cannot be identified to the service
            index.TryGetValue(FirstKnownKey(item.Item, index), out var entry);

            if (caps.SupportsWatches)
                PlanWatches(item, entry, caps, window, watches);

            if (caps.SupportsRatings && item.WatchedLocally && item.Rating is { } rating && rating is >= 1 and <= 10)
                PlanRating(item, entry, rating, ratings);

            if (options.SyncListStatus && caps.SupportsListStatus && item.ListStatus is { } status && entry?.Status is null)
                statuses.Add(new ExportListStatus(item.Item, status));
        }

        return new ExportBatch(watches, ratings, statuses);
    }

    private static void PlanWatches(
        LocalItem item, RemoteEntry? entry, ExportCapabilities caps, TimeSpan window, List<ExportWatch> into)
    {
        var remoteTimes = entry?.Watches ?? [];
        var haveRemote = remoteTimes.Count > 0 || (entry?.UnknownTimeWatches ?? 0) > 0;
        var accepted = new List<DateTime>();

        // Real playbacks can always be a watch the service lacks. A source that cannot tell watches apart can only
        // supply the FIRST watch, and only to a service that holds none: its time for the same watch differs from
        // the service's own, so against an existing record it would look like a rewatch that never happened.
        var candidates = item.PlaybacksUtc.ToList();
        if (!haveRemote && item.FirstWatchEvidenceUtc is { } evidence) candidates.Add(evidence);

        foreach (var local in candidates.OrderBy(t => t))
        {
            var t = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc));
            if (remoteTimes.Any(r => (r - t).Duration() <= window)) continue;                       // the service has it
            if (accepted.Any(a => (new DateTimeOffset(DateTime.SpecifyKind(a, DateTimeKind.Utc)) - t).Duration() <= window))
                continue;                                                                           // folded into one
            accepted.Add(local);
        }

        for (var i = 0; i < accepted.Count; i++)
        {
            var isRewatch = haveRemote || i > 0;
            if (isRewatch && !caps.SupportsRewatches) continue;
            into.Add(new ExportWatch(item.Item,
                new DateTimeOffset(DateTime.SpecifyKind(accepted[i], DateTimeKind.Utc)), isRewatch));
        }
    }

    private static void PlanRating(LocalItem item, RemoteEntry? entry, int rating, List<ExportRating> into)
    {
        var remote = entry?.Rating;
        if (remote is null)
        {
            into.Add(new ExportRating(item.Item, rating, ToOffset(item.RatedAtUtc)));
            return;
        }
        if (remote.Rating == rating) return;

        // They disagree: the most recent wins. A local rating of unknown age cannot be shown to be newer, so it
        // does not overwrite the service's.
        if (item.RatedAtUtc is not { } localAt) return;
        var localWhen = ToOffset(localAt)!.Value;
        if (remote.RatedAt is null || localWhen - remote.RatedAt.Value > Grace)
            into.Add(new ExportRating(item.Item, rating, localWhen));
    }

    private static DateTimeOffset? ToOffset(DateTime? utc) =>
        utc is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;

    // ── remote indexing ───────────────────────────────────────────────────────

    private static Dictionary<string, RemoteEntry> IndexRemote(RemoteState remote)
    {
        var index = new Dictionary<string, RemoteEntry>();

        RemoteEntry EntryFor(ExportItemRef item)
        {
            var keys = item.Keys;
            foreach (var key in keys)
                if (index.TryGetValue(key, out var existing))
                {
                    foreach (var other in keys) index.TryAdd(other, existing);
                    return existing;
                }
            var created = new RemoteEntry();
            foreach (var key in keys) index[key] = created;
            return created;
        }

        foreach (var w in remote.Watches)
        {
            var entry = EntryFor(w.Item);
            if (w.WatchedAt == DateTimeOffset.MinValue) entry.UnknownTimeWatches++;
            else entry.Watches.Add(w.WatchedAt);
        }
        foreach (var r in remote.Ratings) EntryFor(r.Item).Rating = r;
        foreach (var s in remote.ListStatuses) EntryFor(s.Item).Status = s;
        return index;
    }

    /// <summary>The first of the item's keys the service knows, or its first key (which will not be found).</summary>
    private static string FirstKnownKey(ExportItemRef item, Dictionary<string, RemoteEntry> index)
    {
        var keys = item.Keys;
        foreach (var key in keys)
            if (index.ContainsKey(key)) return key;
        return keys.FirstOrDefault() ?? string.Empty;
    }
}
