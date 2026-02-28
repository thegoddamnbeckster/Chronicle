using Chronicle.Plugin.Trakt.Models;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// IImportProvider implementation for Trakt.tv.
///
/// Auth flow (OAuth 2.0 Device Authorization Grant):
///   1. Chronicle calls StartAuthAsync() — user is shown a code + URL.
///   2. User visits the URL and enters the code in their browser.
///   3. Chronicle polls PollAuthAsync() every few seconds.
///   4. On success, PollAuthAsync returns NewSettings containing the tokens.
///   5. Chronicle persists those settings and calls Configure() again.
///
/// Settings keys (all stored by Chronicle):
///   client_id        — Trakt app Client ID        (user-configured)
///   client_secret    — Trakt app Client Secret     (user-configured)
///   access_token     — OAuth2 access token         (stored after auth)
///   refresh_token    — OAuth2 refresh token        (stored after auth)
///   token_expires_at — Token expiry as Unix seconds (stored after auth)
/// </summary>
public sealed class TraktPlugin : IImportProvider, IDisposable
{
    private TraktClient? _client;

    // ── IImportProvider identity ──────────────────────────────────────────────

    public string PluginId    => "chronicle.plugin.trakt";
    public string Name        => "Trakt";
    public string Version     => "1.0.0";
    public string Author      => "thegoddamnbeckster";
    public string Description => "Import watch history, ratings, and watchlist from Trakt.tv.";

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = "client_id",
                Label       = "Trakt Client ID",
                Description = "Your Trakt application's Client ID. " +
                              "Create a free app at https://trakt.tv/oauth/applications/new",
                Type     = SettingType.Password,
                Required = true
            },
            new SettingDefinition
            {
                Key         = "client_secret",
                Label       = "Trakt Client Secret",
                Description = "Your Trakt application's Client Secret.",
                Type     = SettingType.Password,
                Required = true
            }
        ]
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by Chronicle on startup and after each settings update.
    /// Rebuilds the HTTP client with current credentials and restores any
    /// previously persisted OAuth tokens.
    /// </summary>
    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _client?.Dispose();

        var clientId     = settings.GetValueOrDefault("client_id",     "");
        var clientSecret = settings.GetValueOrDefault("client_secret", "");

        _client = new TraktClient(clientId, clientSecret);

        // Restore persisted OAuth tokens if present.
        if (settings.TryGetValue("access_token",     out var at)  &&
            settings.TryGetValue("refresh_token",    out var rt)  &&
            settings.TryGetValue("token_expires_at", out var exStr) &&
            long.TryParse(exStr, out var expiresAt))
        {
            _client.SetTokens(at, rt, expiresAt);
        }
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default)
    {
        EnsureConfigured();

        var dc = await _client!.InitiateDeviceAuthAsync(ct);

        return new DeviceAuthStart(
            UserCode:               dc.UserCode,
            VerificationUrl:        dc.VerificationUrl,
            ExpiresInSeconds:       dc.ExpiresIn,
            PollingIntervalSeconds: dc.Interval,
            PollCode:               dc.DeviceCode);
    }

    public async Task<DeviceAuthPollResult> PollAuthAsync(
        string pollCode, CancellationToken ct = default)
    {
        EnsureConfigured();

        var (token, status) = await _client!.PollForTokenAsync(pollCode, ct);

        if (status == "authorized" && token is not null)
        {
            var expiresAt = token.CreatedAt + token.ExpiresIn;

            // Update in-memory state immediately so subsequent calls work.
            _client.SetTokens(token.AccessToken, token.RefreshToken, expiresAt);

            // Return NewSettings so Chronicle can persist the tokens.
            return new DeviceAuthPollResult(
                DeviceAuthStatus.Authorized,
                NewSettings: new Dictionary<string, string>
                {
                    ["access_token"]     = token.AccessToken,
                    ["refresh_token"]    = token.RefreshToken,
                    ["token_expires_at"] = expiresAt.ToString()
                });
        }

        return status switch
        {
            "expired"   => new DeviceAuthPollResult(DeviceAuthStatus.Expired),
            "denied"    => new DeviceAuthPollResult(DeviceAuthStatus.Denied),
            "slow_down" => new DeviceAuthPollResult(DeviceAuthStatus.Pending,
                               ErrorMessage: "Polling too frequently — slow down."),
            _           => new DeviceAuthPollResult(DeviceAuthStatus.Pending)
        };
    }

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        Task.FromResult(_client?.IsAuthenticated ?? false);

    // ── Import capabilities ───────────────────────────────────────────────────

    public ImportCapabilities GetCapabilities() => new(
        SupportsHistory:    true,
        SupportsRatings:    true,
        SupportsWatchlist:  true,
        RequiresDeviceAuth: true);

    // ── Import methods ────────────────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        var items = await _client!.GetWatchHistoryAsync(since, ct);
        return items.Select(ToWatchEvent).OfType<ImportedWatchEvent>().ToList();
    }

    public async Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        var items = await _client!.GetRatingsAsync(ct);
        return items.Select(ToRating).OfType<ImportedRating>().ToList();
    }

    public async Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(
        CancellationToken ct = default)
    {
        EnsureConfigured();
        var items = await _client!.GetWatchlistAsync(ct);
        return items.Select(ToWatchlistEntry).OfType<ImportedWatchlistEntry>().ToList();
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return Task.FromResult(false);
        return _client.HealthCheckAsync(ct);
    }

    // ── Mapping: Trakt models → Chronicle plugin models ───────────────────────

    private static ImportedWatchEvent? ToWatchEvent(TraktHistoryItem item)
    {
        if (item.Type == "movie" && item.Movie is not null)
        {
            return new ImportedWatchEvent(
                ExternalId:      $"trakt:movie:{item.Movie.Ids.Trakt}",
                AdditionalIds:   BuildIds(item.Movie.Ids),
                MediaType:       "movie",
                Title:           item.Movie.Title,
                Year:            item.Movie.Year,
                WatchedAt:       item.WatchedAt,
                ProgressPercent: 100.0);
        }

        if (item.Type == "episode" && item.Show is not null && item.Episode is not null)
        {
            var ep = item.Episode;
            return new ImportedWatchEvent(
                ExternalId:      $"trakt:episode:{ep.Ids.Trakt}",
                AdditionalIds:   BuildIds(ep.Ids, item.Show.Ids),
                MediaType:       "tv_episode",
                Title:           FormatEpisodeTitle(item.Show.Title, ep),
                Year:            item.Show.Year,
                WatchedAt:       item.WatchedAt,
                ProgressPercent: 100.0);
        }

        return null;   // Unknown type — skip silently.
    }

    private static ImportedRating? ToRating(TraktRatingItem item)
    {
        if (item.Type == "movie" && item.Movie is not null)
        {
            return new ImportedRating(
                ExternalId:    $"trakt:movie:{item.Movie.Ids.Trakt}",
                AdditionalIds: BuildIds(item.Movie.Ids),
                MediaType:     "movie",
                Title:         item.Movie.Title,
                Year:          item.Movie.Year,
                Rating:        item.Rating,
                RatedAt:       item.RatedAt);
        }

        if (item.Type == "show" && item.Show is not null)
        {
            return new ImportedRating(
                ExternalId:    $"trakt:show:{item.Show.Ids.Trakt}",
                AdditionalIds: BuildIds(item.Show.Ids),
                MediaType:     "tv",
                Title:         item.Show.Title,
                Year:          item.Show.Year,
                Rating:        item.Rating,
                RatedAt:       item.RatedAt);
        }

        if (item.Type == "episode" && item.Show is not null && item.Episode is not null)
        {
            var ep = item.Episode;
            return new ImportedRating(
                ExternalId:    $"trakt:episode:{ep.Ids.Trakt}",
                AdditionalIds: BuildIds(ep.Ids, item.Show.Ids),
                MediaType:     "tv_episode",
                Title:         FormatEpisodeTitle(item.Show.Title, ep),
                Year:          item.Show.Year,
                Rating:        item.Rating,
                RatedAt:       item.RatedAt);
        }

        return null;
    }

    private static ImportedWatchlistEntry? ToWatchlistEntry(TraktWatchlistItem item)
    {
        if (item.Type == "movie" && item.Movie is not null)
        {
            return new ImportedWatchlistEntry(
                ExternalId:    $"trakt:movie:{item.Movie.Ids.Trakt}",
                AdditionalIds: BuildIds(item.Movie.Ids),
                MediaType:     "movie",
                Title:         item.Movie.Title,
                Year:          item.Movie.Year,
                AddedAt:       item.ListedAt);
        }

        if (item.Type == "show" && item.Show is not null)
        {
            return new ImportedWatchlistEntry(
                ExternalId:    $"trakt:show:{item.Show.Ids.Trakt}",
                AdditionalIds: BuildIds(item.Show.Ids),
                MediaType:     "tv",
                Title:         item.Show.Title,
                Year:          item.Show.Year,
                AddedAt:       item.ListedAt);
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatEpisodeTitle(string showTitle, TraktEpisode ep)
    {
        var epCode = $"S{ep.Season:D2}E{ep.Number:D2}";
        return ep.Title is not null
            ? $"{showTitle} {epCode} - {ep.Title}"
            : $"{showTitle} {epCode}";
    }

    /// <summary>
    /// Builds the AdditionalIds dictionary from a primary (episode/movie) ID set,
    /// with an optional secondary (show) ID set prefixed with "show_".
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildIds(
        TraktIds primary, TraktIds? showIds = null)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIds(d, primary, prefix: "");
        if (showIds is not null)
            AddIds(d, showIds, prefix: "show_");
        return d;
    }

    private static void AddIds(Dictionary<string, string> d, TraktIds ids, string prefix)
    {
        if (ids.Trakt.HasValue)   d[$"{prefix}trakt"] = ids.Trakt.Value.ToString();
        if (ids.Slug  is not null) d[$"{prefix}slug"]  = ids.Slug;
        if (ids.Imdb  is not null) d[$"{prefix}imdb"]  = ids.Imdb;
        if (ids.Tmdb.HasValue)    d[$"{prefix}tmdb"]   = ids.Tmdb.Value.ToString();
        if (ids.Tvdb.HasValue)    d[$"{prefix}tvdb"]   = ids.Tvdb.Value.ToString();
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "TraktPlugin.Configure() must be called before use.");
    }

    public void Dispose() => _client?.Dispose();
}
