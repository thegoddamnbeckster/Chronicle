using Chronicle.Plugins.Models;

namespace Chronicle.Plugins;

// ── Auth flow types ───────────────────────────────────────────────────────────

/// <summary>
/// Returned when the device/PIN auth flow is started.
/// Show <see cref="UserCode"/> and <see cref="VerificationUrl"/> to the user.
/// Then poll <see cref="IImportProvider.PollAuthAsync"/> every
/// <see cref="PollingIntervalSeconds"/> until status changes.
/// </summary>
public record DeviceAuthStart(
    string UserCode,
    string VerificationUrl,
    int    ExpiresInSeconds,
    int    PollingIntervalSeconds,
    /// <summary>
    /// Internal code supplied to PollAuthAsync — may differ from UserCode
    /// (Trakt uses a separate device_code; Simkl reuses the UserCode/PIN).
    /// </summary>
    string PollCode
);

public enum DeviceAuthStatus { Pending, Authorized, Expired, Denied }

/// <summary>Result of a single polling attempt.</summary>
public record DeviceAuthPollResult(
    DeviceAuthStatus Status,
    /// <summary>
    /// Populated when Status == Authorized.
    /// Chronicle will call PluginService.UpdateSettingsAsync with these key-value pairs
    /// so that access_token / refresh_token etc. are persisted and the live plugin
    /// instance is reconfigured via Configure().
    /// </summary>
    IReadOnlyDictionary<string, string>? NewSettings = null,
    string? ErrorMessage = null
);

// ── Import result types ───────────────────────────────────────────────────────

public record ImportCapabilities(
    bool SupportsHistory,
    bool SupportsRatings,
    bool SupportsWatchlist,
    /// <summary>
    /// True if the provider requires the device/PIN OAuth flow (Trakt, Simkl).
    /// False for providers that authenticate via a username or API key only (Letterboxd).
    /// </summary>
    bool RequiresDeviceAuth = true
);

/// <summary>A single watch event imported from the tracking service.</summary>
public record ImportedWatchEvent(
    /// <summary>
    /// Source-namespaced ID, e.g. "trakt:12345", "tmdb:67890".
    /// For tv_episode events use a synthetic key like "simkl:{show_id}:s{S}e{E}".
    /// </summary>
    string ExternalId,
    /// <summary>Additional IDs the service provides (tmdb, imdb, tvdb …).</summary>
    IReadOnlyDictionary<string, string> AdditionalIds,
    /// <summary>"movie" | "tv_episode" | "tv"</summary>
    string MediaType,
    /// <summary>Episode title (or "S01E05") for tv_episode; full title otherwise.</summary>
    string Title,
    int? Year,
    DateTimeOffset WatchedAt,
    double? ProgressPercent,
    // ── TV episode hierarchy fields (null for non-episode events) ─────────────
    /// <summary>Source-namespaced ID of the parent show, e.g. "simkl:12345".</summary>
    string? ShowExternalId = null,
    /// <summary>Show title — used to find/create the parent show stub.</summary>
    string? ShowTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    // ── Book/audiobook parent context ────────────────────────────────────────
    /// <summary>Author name — used to find/create the Level-0 Author parent stub.</summary>
    string? AuthorName     = null,
    /// <summary>Series name — used to find/create the Level-1 Series parent stub. Null = standalone book.</summary>
    string? SeriesName     = null,
    /// <summary>Numeric position within the series (e.g. 1.0, 2.5).</summary>
    double? SeriesPosition = null
);

public record ImportedRating(
    string ExternalId,
    IReadOnlyDictionary<string, string> AdditionalIds,
    string MediaType,
    string Title,
    int? Year,
    /// <summary>Service rating on a 1–10 scale.</summary>
    int Rating,
    DateTimeOffset RatedAt,
    string? AuthorName     = null,
    string? SeriesName     = null,
    double? SeriesPosition = null
);

public record ImportedWatchlistEntry(
    string ExternalId,
    IReadOnlyDictionary<string, string> AdditionalIds,
    string MediaType,
    string Title,
    int? Year,
    DateTimeOffset AddedAt,
    string? AuthorName     = null,
    string? SeriesName     = null,
    double? SeriesPosition = null
);

// ── Optional enrichment types ────────────────────────────────────────────────

public record ImportedCredit(
    string  PersonName,
    string  Role,              // "Director" | "Writer" | "Actor" | "Composer" | "Producer" …
    string? CharacterName,     // actors only
    int?    BillingOrder,      // 1 = top-billed
    string? ExternalPersonId   // source-specific person ID for future dedup
);

public record ImportedItemMetadata(
    string  Title,
    int?    Year,
    string? Overview,
    string? PosterUrl,
    int?    RuntimeMinutes,
    IReadOnlyDictionary<string, string> AdditionalIds,
    string? FanartUrl = null
);

// ── The interface ─────────────────────────────────────────────────────────────

/// <summary>
/// A plugin that authenticates with an external tracking service (Trakt, Simkl, …)
/// and imports the user's watch history, ratings, and watchlist into Chronicle.
/// </summary>
public interface IImportProvider
{
    string PluginId    { get; }
    string Name        { get; }
    string Version     { get; }
    string Author      { get; }
    string Description { get; }

    PluginSettingsSchema GetSettingsSchema();

    /// <summary>
    /// Applies stored settings (including any OAuth tokens) to this provider.
    /// Called on startup and again after each UpdateSettingsAsync call.
    /// </summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the device/PIN authorization flow.
    /// Returns the code the user must enter at the provider's website.
    /// </summary>
    Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether the user has completed authorization yet.
    /// Chronicle polls this until <see cref="DeviceAuthStatus"/> is no longer Pending.
    /// On Authorized, Chronicle persists <see cref="DeviceAuthPollResult.NewSettings"/>
    /// and calls Configure() again with the merged settings.
    /// </summary>
    Task<DeviceAuthPollResult> PollAuthAsync(string pollCode, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a valid, non-expired access token is currently configured.
    /// </summary>
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);

    // ── Import ────────────────────────────────────────────────────────────────

    ImportCapabilities GetCapabilities();

    /// <summary>
    /// Returns the user's watch history.
    /// <paramref name="since"/> limits results to events after that timestamp.
    /// Implementations must respect the service's rate limits.
    /// </summary>
    Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null,
        CancellationToken ct = default);

    /// <summary>Returns all ratings the user has submitted to the service.</summary>
    Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default);

    /// <summary>Returns the user's watchlist (items they plan to watch).</summary>
    Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default);

    Task<bool> HealthCheckAsync(CancellationToken ct = default);

    // ── Optional enrichment hooks ─────────────────────────────────────────────

    /// <summary>
    /// Returns cast and crew for a specific item the provider knows about.
    /// Called after stub creation to populate media_credits.
    /// Default: empty list (no credits data available from this provider).
    /// </summary>
    Task<List<ImportedCredit>> GetCreditsAsync(
        string externalId,
        string mediaType,
        CancellationToken ct = default)
        => Task.FromResult(new List<ImportedCredit>());

    /// <summary>
    /// Returns full item metadata used to create a stub MediaItem when Chronicle
    /// doesn't already know about this item.
    /// Default: null — stub will be created with title/year from the watch event only.
    /// </summary>
    Task<ImportedItemMetadata?> GetItemMetadataAsync(
        string externalId,
        string mediaType,
        CancellationToken ct = default)
        => Task.FromResult<ImportedItemMetadata?>(null);

    /// <summary>
    /// Returns updated OAuth settings (e.g. refreshed tokens) if the provider silently
    /// refreshed its access token since the last Configure() call.
    /// Chronicle calls this after each sync/import operation and persists any returned
    /// settings via MergeSettingsAsync so refreshed tokens survive the next restart.
    /// Default: null — no refresh occurred.
    /// </summary>
    IReadOnlyDictionary<string, string>? GetRefreshedSettings() => null;
}
