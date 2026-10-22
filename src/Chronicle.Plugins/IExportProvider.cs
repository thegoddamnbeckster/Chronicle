namespace Chronicle.Plugins;

// ── Export: pushing Chronicle's own data out to a tracking service ───────────
//
// IImportProvider only ever reads from a service (Trakt, Simkl, ...). A plugin that can also WRITE to the same
// service implements IExportProvider on the same class. Chronicle's export service then reconciles Chronicle's
// records against the service's and pushes what the service is missing -- automatically, after each import, with
// no step that waits on the user (a preview-only mode exists as an optional setting, off by default).

/// <summary>What a service can accept from Chronicle.</summary>
/// <param name="SupportsWatches">First watches (movies; TV episodes).</param>
/// <param name="SupportsRewatches">Repeat watches of something already watched.</param>
/// <param name="SupportsRatings">User ratings.</param>
/// <param name="SupportsListStatus">Plan to watch / dropped / on hold.</param>
/// <param name="RewatchMinimumGap">
/// How far apart two watches of the same item must be for the service to keep them as separate watches (SIMKL
/// folds anything closer than two days into one). A local watch within this gap of a remote one is treated as
/// the same watch and never pushed.
/// </param>
public record ExportCapabilities(
    bool SupportsWatches,
    bool SupportsRewatches,
    bool SupportsRatings,
    bool SupportsListStatus,
    TimeSpan RewatchMinimumGap);

/// <summary>User-configurable behaviour, read by the plugin from its own settings.</summary>
/// <param name="Enabled">Master switch for exporting to this service.</param>
/// <param name="PreviewOnly">Work out and log what would be pushed, push nothing. Off by default.</param>
/// <param name="SyncListStatus">Also push plan-to-watch / dropped / on-hold. Off by default.</param>
public record ExportOptions(bool Enabled = true, bool PreviewOnly = false, bool SyncListStatus = false);

/// <summary>
/// Identifies one movie or TV episode to a service.
/// For an episode, <see cref="Ids"/> are the SHOW's ids (a service identifies an episode by its show plus numbers).
/// </summary>
public record ExportItemRef(
    /// <summary>"movie" | "tv_episode"</summary>
    string MediaType,
    string Title,
    int? Year,
    /// <summary>Source name -> id, e.g. imdb -> tt22084616, tmdb -> movie:969681, simkl -> simkl:movie:1902343.</summary>
    IReadOnlyDictionary<string, string> Ids,
    string? ShowTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null)
{
    /// <summary>Normalised keys (see <see cref="ExportKeys"/>); two refs are the same item when any key is shared.</summary>
    public IReadOnlyList<string> Keys => ExportKeys.For(this);
}

/// <summary>One real watch to send to the service.</summary>
public record ExportWatch(ExportItemRef Item, DateTimeOffset WatchedAt, bool IsRewatch);

public record ExportRating(ExportItemRef Item, int Rating, DateTimeOffset? RatedAt);

public record ExportListStatus(ExportItemRef Item, string Status);

/// <summary>Everything Chronicle wants the service to have.</summary>
public record ExportBatch(
    IReadOnlyList<ExportWatch> Watches,
    IReadOnlyList<ExportRating> Ratings,
    IReadOnlyList<ExportListStatus> ListStatuses)
{
    public bool IsEmpty => Watches.Count == 0 && Ratings.Count == 0 && ListStatuses.Count == 0;
}

/// <summary>
/// One watch the service already holds. <c>WatchedAt == DateTimeOffset.MinValue</c> means the service knows the
/// item was watched but not when (SIMKL keeps a 1970 placeholder for those): such a watch accounts for one local
/// watch, whatever its time, and never causes a rewatch to be sent.
/// </summary>
public record RemoteWatch(ExportItemRef Item, DateTimeOffset WatchedAt);

/// <summary>A rating the service already holds. <c>RatedAt</c> is null when the service did not say when.</summary>
public record RemoteRating(ExportItemRef Item, int Rating, DateTimeOffset? RatedAt);

/// <summary>A list status the service already holds (SIMKL vocabulary: plantowatch, completed, watching, hold, dropped).</summary>
public record RemoteListStatus(ExportItemRef Item, string Status);

/// <summary>The service's current records, as read for one reconciliation run.</summary>
public record RemoteState(
    IReadOnlyList<RemoteWatch> Watches,
    IReadOnlyList<RemoteRating> Ratings,
    IReadOnlyList<RemoteListStatus> ListStatuses);

/// <summary>
/// What a push actually did. <c>NotFound</c> counts items the service could not match. <c>Deferred</c> counts items
/// left unsent on purpose to stay inside the service's request quota; they are picked up by the next run.
/// </summary>
public record ExportPushResult(
    int WatchesPushed,
    int RatingsPushed,
    int ListStatusesPushed,
    int NotFound,
    IReadOnlyList<string> Errors,
    int Deferred = 0);

public interface IExportProvider
{
    ExportCapabilities GetExportCapabilities();

    /// <summary>Current export settings (read from the plugin's own saved settings).</summary>
    ExportOptions GetExportOptions();

    /// <summary>
    /// Reads everything the service currently holds for the user, including repeat watches. Must be complete:
    /// the export service never pushes on a partial read, so throw rather than return a partial result.
    /// </summary>
    Task<RemoteState> GetRemoteStateAsync(CancellationToken ct = default);

    /// <summary>
    /// A cheap value that changes whenever anything on the service changes (SIMKL: /sync/activities' "all"), or
    /// null when the service has no such thing. The export service skips a run - and the expensive full read -
    /// when this and Chronicle's own data are both unchanged since the last complete run. Services ask clients
    /// to gate their full reads this way.
    /// </summary>
    Task<string?> GetRemoteFingerprintAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>
    /// Sends the batch. Never deletes anything on the service. Must be safe to repeat: sending an item the
    /// service already has must not create a second copy.
    /// </summary>
    Task<ExportPushResult> PushAsync(ExportBatch batch, CancellationToken ct = default);
}

/// <summary>
/// Turns an item's ids into normalised keys so Chronicle's items and a service's items can be matched without
/// either side knowing the other's id formats ("movie:969681", "simkl:movie:1902343", "tt22084616" ...).
/// </summary>
public static class ExportKeys
{
    /// <summary>The id sources <see cref="Normalize"/> understands; an item's ids from any other source are not sent to a service.</summary>
    public static readonly IReadOnlyList<string> Sources = new List<string> { "imdb", "tmdb", "tvdb", "simkl" }.AsReadOnly();

    /// <summary>The keys for an item: imdb, tmdb, tvdb and simkl ids, namespaced by kind where ids can collide.</summary>
    public static IReadOnlyList<string> For(ExportItemRef item)
    {
        var kind = item.MediaType == "movie" ? "movie" : "tv";
        var keys = new List<string>();
        foreach (var (source, value) in item.Ids)
        {
            var key = Normalize(source, value, kind);
            if (key is not null) keys.Add(key);
        }

        if (item.MediaType == "tv_episode" && item.SeasonNumber.HasValue && item.EpisodeNumber.HasValue)
        {
            var suffix = $":s{item.SeasonNumber}e{item.EpisodeNumber}";
            return keys.Select(k => k + suffix).ToList();
        }
        return keys;
    }

    /// <summary>One id as a normalised key, or null when it carries nothing usable.</summary>
    public static string? Normalize(string source, string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = source.Trim().ToLowerInvariant();

        if (s == "imdb")
        {
            var v = value.Trim().ToLowerInvariant();
            if (v.StartsWith("tt") && v.Length > 2 && v[2..].All(char.IsDigit)) return $"imdb:{v}";
            return v.All(char.IsDigit) ? $"imdb:tt{v}" : null;
        }

        if (s is "tmdb" or "tvdb" or "simkl")
        {
            // "movie:969681", "tv:92611", "simkl:movie:1902343", or just "969681". Anything else is not an id of
            // THIS kind of item and yields no key: an episode-level id ("tv:92611/season:8/episode:3"), a movie
            // collection ("collection:726871" - the container, not a movie), a TV id on a movie, and so on.
            var v = value.Trim();
            if (v.Contains('/')) return null;
            var parts = v.Split(':');
            var number = parts[^1];
            if (number.Length == 0 || !number.All(char.IsDigit)) return null;
            var prefix = parts[..^1].Where(p => !p.Equals(s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefix.Count > 1) return null;
            if (prefix.Count == 1 && !prefix[0].Equals(kind, StringComparison.OrdinalIgnoreCase)) return null;
            return $"{s}:{kind}:{number}";
        }

        return null;
    }
}
