using System.Globalization;

namespace Chronicle.Services;

/// <summary>One adjustable number: where it lives (<c>app_settings</c> key), what it means, what it is unless an administrator says otherwise.</summary>
public sealed record TunableDefinition(
    string Key, string Group, string Label, string Description, double Default, double Min, double Max, string Unit);

/// <summary>
/// The numbers that are policy rather than engineering: how much of a film counts as watched, how long a failed lookup rests,
/// how far two years must differ to be called a contradiction. They used to be constants buried in the services that used
/// them. Each is declared once here with its default, can be changed from Settings (stored in <c>app_settings</c> as
/// <c>tuning.*</c>) and takes effect within about a minute, with no rebuild. A missing, unreadable or out-of-range stored
/// value is ignored in favour of the default, so a typo can never switch a rule off.
/// </summary>
public static class Tunables
{
    public const string Prefix = "tuning.";

    public static readonly TunableDefinition WatchedPercent = new(
        "tuning.scrobble.watched_percent", "Scrobbling", "Counts as watched at",
        "A play that reaches this much of the item is marked watched; less than this is saved as a resume position.",
        80, 1, 100, "%");

    public static readonly TunableDefinition ActiveSessionSeconds = new(
        "tuning.scrobble.active_session_seconds", "Scrobbling", "Now-playing window",
        "A device that reported progress this recently is shown as still playing.", 90, 10, 3600, "seconds");

    public static readonly TunableDefinition HistorySessionGapSeconds = new(
        "tuning.scrobble.history_session_gap_seconds", "Scrobbling", "Watch session gap",
        "Plays of one item further apart than this are separate viewings in the history.", 1800, 60, 86400, "seconds");

    public static readonly TunableDefinition EnrichmentRetryHours = new(
        "tuning.enrichment.retry_hours", "Metadata", "Retry a failed lookup after",
        "How long a provider lookup that failed or found nothing rests before it is tried again.", 24, 1, 720, "hours");

    public static readonly TunableDefinition ConfidenceThreshold = new(
        "tuning.enrichment.confidence_threshold", "Metadata", "Minimum match confidence",
        "A best search candidate scoring below this is not accepted automatically.", 50, 1, 100, "points");

    public static readonly TunableDefinition FullCreditsDays = new(
        "tuning.people.full_credits_days", "Metadata", "Refresh a person's full credits after",
        "A person's complete filmography is kept this long before it is fetched again.", 7, 1, 365, "days");

    public static readonly TunableDefinition YearConflictGap = new(
        "tuning.repair.year_conflict_gap", "Repairs", "Years apart that count as a contradiction",
        "Two years for the same thing (a file name against the confirmed release year, a provider's birth year against the trusted one) differing by this many years or more are treated as a real conflict.",
        2, 1, 50, "years");

    public static readonly TunableDefinition SeriesRecheckDays = new(
        "tuning.books.series_recheck_days", "Books", "Re-check a series after",
        "A matched book series is compared with its provider again this many days after the last check.", 7, 1, 365, "days");

    public static readonly TunableDefinition SeriesChecksPerRun = new(
        "tuning.books.series_checks_per_run", "Books", "Series checked per run",
        "At most this many series are re-checked in one pass, which spreads a provider's request quota over the day.", 150, 1, 5000, "series");

    public static readonly TunableDefinition SeriesFailureRetryDays = new(
        "tuning.books.series_failure_retry_days", "Books", "Retry a failed series check after",
        "A series whose check failed rests this long before it is tried again.", 1, 1, 30, "days");

    public static readonly TunableDefinition SeriesFailuresToStop = new(
        "tuning.books.series_failures_to_stop", "Books", "Failures that end a run",
        "A series pass that fails this many times in a row stops, assuming the provider is unavailable.", 3, 1, 100, "failures");

    public static readonly TunableDefinition FetchMissingRetryMinutes = new(
        "tuning.tasks.fetch_missing_retry_minutes", "Tasks", "Rerun interrupted metadata fetch after",
        "A metadata fetch that stopped with work left over is started again after this long.", 15, 1, 1440, "minutes");

    public static readonly TunableDefinition ImageCheckValidHours = new(
        "tuning.images.valid_hours", "Images", "Trust a working image address for",
        "An image address that loaded is not checked again for this long.", 12, 1, 720, "hours");

    public static readonly TunableDefinition ImageCheckInvalidMinutes = new(
        "tuning.images.invalid_minutes", "Images", "Re-check a broken image address after",
        "An image address that failed to load is checked again after this long.", 30, 1, 1440, "minutes");

    public static readonly TunableDefinition KodiScanActiveMinutes = new(
        "tuning.kodi.scan_active_minutes", "Kodi", "Scan signal lifetime",
        "A Kodi scan the add-on reported stays marked as running this long without a further report.", 3, 1, 120, "minutes");

    public static readonly TunableDefinition TypeMismatchMajorityPercent = new(
        "tuning.scan.mismatch_majority_percent", "Scanning", "Folder looks like another type at",
        "A folder is flagged as the wrong media type when at least this share of its files look like that other type.", 60, 10, 100, "%");

    public static readonly TunableDefinition TypeMismatchMinorityPercent = new(
        "tuning.scan.mismatch_minority_percent", "Scanning", "…and looks like its own type below",
        "…and no more than this share of its files look like the type it is filed as.", 20, 0, 90, "%");

    public static readonly IReadOnlyList<TunableDefinition> All =
    [
        WatchedPercent, ActiveSessionSeconds, HistorySessionGapSeconds,
        EnrichmentRetryHours, ConfidenceThreshold, FullCreditsDays,
        YearConflictGap, SeriesRecheckDays, SeriesChecksPerRun, SeriesFailureRetryDays, SeriesFailuresToStop,
        FetchMissingRetryMinutes, ImageCheckValidHours, ImageCheckInvalidMinutes, KodiScanActiveMinutes,
        TypeMismatchMajorityPercent, TypeMismatchMinorityPercent,
    ];

    private static volatile IReadOnlyDictionary<string, string> _overrides = new Dictionary<string, string>();

    // Tests run in parallel; one can give its own flow of execution its own values without touching the others.
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> _scoped = new();

    private static IReadOnlyDictionary<string, string> Parse(IEnumerable<KeyValuePair<string, string>> rows) =>
        rows.Where(r => r.Key.StartsWith(Prefix, StringComparison.Ordinal))
            .GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    /// <summary>Replaces the stored values (every <c>tuning.*</c> row of <c>app_settings</c>).</summary>
    public static void Load(IEnumerable<KeyValuePair<string, string>> rows) => _overrides = Parse(rows);

    /// <summary>Replaces the stored values for the current flow of execution only (tests).</summary>
    public static void LoadForCurrentContext(IEnumerable<KeyValuePair<string, string>> rows) => _scoped.Value = Parse(rows);

    /// <summary>The stored value when it is a number inside the allowed range; otherwise the default.</summary>
    public static double Value(TunableDefinition d) =>
        (_scoped.Value ?? _overrides).TryGetValue(d.Key, out var text) && TryParse(d, text, out var v) ? v : d.Default;

    public static int Int(TunableDefinition d) => (int)Math.Round(Value(d));

    public static TimeSpan Seconds(TunableDefinition d) => TimeSpan.FromSeconds(Value(d));
    public static TimeSpan Minutes(TunableDefinition d) => TimeSpan.FromMinutes(Value(d));
    public static TimeSpan Hours(TunableDefinition d) => TimeSpan.FromHours(Value(d));
    public static TimeSpan Days(TunableDefinition d) => TimeSpan.FromDays(Value(d));

    /// <summary>The share as a fraction (60 -> 0.6).</summary>
    public static double Fraction(TunableDefinition d) => Value(d) / 100.0;

    public static bool TryParse(TunableDefinition d, string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !double.IsNaN(value) && value >= d.Min && value <= d.Max;

    public static TunableDefinition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
}
