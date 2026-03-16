using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;

namespace Chronicle.Plugins.TMDB;

/// <summary>
/// TMDB (The Movie Database) metadata provider.
/// Implements <see cref="IMetadataProvider"/> for movie and TV search/lookup,
/// and <see cref="ITvDetailProvider"/> for per-season and per-episode data.
/// </summary>
public sealed class TmdbMetadataProvider : IMetadataProvider, ITvDetailProvider, IDisposable
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.tmdb";
    public string Name     => "TMDB";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle";

    // ── Internal state ────────────────────────────────────────────────────────

    private static readonly ILogger _log = Log.ForContext<TmdbMetadataProvider>();
    private readonly HttpClient _http;
    private string _apiKey = string.Empty;

    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);

    private const string ImageBase = "https://image.tmdb.org/t/p/w500";
    private const string ApiBase   = "https://api.themoviedb.org/3";

    public TmdbMetadataProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ── IMetadataProvider: capability ─────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new() { MediaTypeName = "movie" },
        new() { MediaTypeName = "tv"    }
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new()
            {
                Key         = "ApiKey",
                Label       = "TMDB API Key",
                Description = "Your TMDB v3 API key from https://www.themoviedb.org/settings/api",
                Type        = Chronicle.Plugins.Models.SettingType.Password,
                Required    = true
            }
        ]
    };

    // ── IMetadataProvider: lifecycle ──────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue("ApiKey", out var key) && !string.IsNullOrWhiteSpace(key))
            _apiKey = key.Trim();
        else
            _log.Warning("TmdbMetadataProvider: no ApiKey configured");
    }

    // ── IMetadataProvider: core operations ────────────────────────────────────

    public async Task<MediaMetadata> SearchAsync(string query, string mediaType, CancellationToken ct = default)
    {
        RequireApiKey();

        var tmdbType = NormalizeMediaType(mediaType);
        var url = $"{ApiBase}/search/{tmdbType}?api_key={_apiKey}&query={Uri.EscapeDataString(query)}";

        var response = await _http.GetFromJsonAsync<TmdbSearchResponse>(url, _jsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from TMDB search");

        var results = response.Results
            .Select(r => ToMetadata(r, tmdbType))
            .ToList();

        return new MediaMetadata
        {
            Source       = "tmdb",
            Results      = results,
            TotalResults = response.TotalResults
        };
    }

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        RequireApiKey();

        // externalId format: "movie:550" or "tv:1396"
        var (tmdbType, numericId) = ParseExternalId(externalId);
        var url = $"{ApiBase}/{tmdbType}/{numericId}?api_key={_apiKey}&append_to_response=credits";

        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var meta = ParseDetailResponse(root, tmdbType, externalId);
        meta.Source = "tmdb";
        return meta;
    }

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return false;
        try
        {
            var url = $"{ApiBase}/configuration?api_key={_apiKey}";
            var response = await _http.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── ITvDetailProvider ─────────────────────────────────────────────────────

    public async Task<TvSeasonDetail?> GetTvSeasonAsync(int seriesId, int seasonNumber, CancellationToken ct = default)
    {
        RequireApiKey();

        var url = $"{ApiBase}/tv/{seriesId}/season/{seasonNumber}?api_key={_apiKey}";
        _log.Debug("TMDB: fetching season {S} for series {Id}", seasonNumber, seriesId);

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log.Debug("TMDB: season {S} not found for series {Id}", seasonNumber, seriesId);
                return null;
            }
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var episodeCount = root.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array
                ? eps.GetArrayLength()
                : (int?)null;

            return new TvSeasonDetail
            {
                SeasonId     = root.TryGetProperty("id", out var idEl)             ? idEl.GetInt32()               : null,
                SeasonNumber = root.TryGetProperty("season_number", out var snEl)  ? snEl.GetInt32()               : seasonNumber,
                Name         = root.TryGetProperty("name", out var nameEl)         ? nameEl.GetString()            : null,
                Overview     = root.TryGetProperty("overview", out var ovEl)       ? ovEl.GetString()              : null,
                AirDate      = root.TryGetProperty("air_date", out var adEl)       ? adEl.GetString()              : null,
                PosterPath   = root.TryGetProperty("poster_path", out var ppEl)    ? ppEl.GetString()              : null,
                VoteAverage  = root.TryGetProperty("vote_average", out var vaEl)   ? vaEl.GetDouble()              : null,
                EpisodeCount = episodeCount,
                RawJson      = json
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "TMDB: failed fetching season {S} for series {Id}", seasonNumber, seriesId);
            return null;
        }
    }

    public async Task<TvEpisodeDetail?> GetTvEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken ct = default)
    {
        RequireApiKey();

        var url = $"{ApiBase}/tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}?api_key={_apiKey}&append_to_response=credits";
        _log.Debug("TMDB: fetching s{S}e{E} for series {Id}", seasonNumber, episodeNumber, seriesId);

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log.Debug("TMDB: s{S}e{E} not found for series {Id}", seasonNumber, episodeNumber, seriesId);
                return null;
            }
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract guest stars
            var guestStars = new List<string>();
            if (root.TryGetProperty("guest_stars", out var gsArr) && gsArr.ValueKind == JsonValueKind.Array)
                foreach (var gs in gsArr.EnumerateArray())
                    if (gs.TryGetProperty("name", out var gsName) && gsName.GetString() is { } n)
                        guestStars.Add(n);

            // Extract crew (directors + writers)
            var crew = new List<string>();
            if (root.TryGetProperty("crew", out var crewArr) && crewArr.ValueKind == JsonValueKind.Array)
                foreach (var member in crewArr.EnumerateArray())
                {
                    var job = member.TryGetProperty("job", out var jobEl) ? jobEl.GetString() : null;
                    if (job is "Director" or "Writer" or "Screenplay" &&
                        member.TryGetProperty("name", out var crewName) && crewName.GetString() is { } cn)
                        crew.Add(cn);
                }

            // Runtime: episode may have its own runtime
            int? runtime = null;
            if (root.TryGetProperty("runtime", out var rtEl) && rtEl.ValueKind == JsonValueKind.Number)
                runtime = rtEl.GetInt32();

            return new TvEpisodeDetail
            {
                SeasonNumber  = root.TryGetProperty("season_number", out var snEl)  ? snEl.GetInt32()   : seasonNumber,
                EpisodeNumber = root.TryGetProperty("episode_number", out var enEl) ? enEl.GetInt32()   : episodeNumber,
                Name          = root.TryGetProperty("name", out var nameEl)         ? nameEl.GetString() : null,
                Overview      = root.TryGetProperty("overview", out var ovEl)       ? ovEl.GetString()   : null,
                AirDate       = root.TryGetProperty("air_date", out var adEl)       ? adEl.GetString()   : null,
                StillPath     = root.TryGetProperty("still_path", out var spEl)     ? spEl.GetString()   : null,
                VoteAverage   = root.TryGetProperty("vote_average", out var vaEl)   ? vaEl.GetDouble()   : null,
                RuntimeMinutes = runtime,
                GuestStars    = guestStars,
                Crew          = crew,
                RawJson       = json
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "TMDB: failed fetching s{S}e{E} for series {Id}", seasonNumber, episodeNumber, seriesId);
            return null;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _http.Dispose();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void RequireApiKey()
    {
        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("TMDB API key is not configured.");
    }

    private static string NormalizeMediaType(string mediaType) =>
        mediaType.ToLowerInvariant() switch
        {
            "movie"  or "movies" => "movie",
            "tv"     or "tv shows" => "tv",
            _ => "movie"
        };

    private static (string type, int id) ParseExternalId(string externalId)
    {
        // Formats: "movie:550", "tv:1396"
        var parts = externalId.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var numId))
            return (parts[0].ToLowerInvariant(), numId);

        // Plain numeric fallback — assume movie
        if (int.TryParse(externalId, out var plainId))
            return ("movie", plainId);

        throw new ArgumentException($"Cannot parse TMDB external ID: '{externalId}'");
    }

    private static MediaMetadata ToMetadata(TmdbSearchResult r, string tmdbType)
    {
        var title  = tmdbType == "movie" ? r.Title : r.Name;
        var date   = tmdbType == "movie" ? r.ReleaseDate : r.FirstAirDate;
        int? year  = null;
        if (date?.Length >= 4 && int.TryParse(date[..4], out var y)) year = y;

        return new MediaMetadata
        {
            ExternalId = $"{tmdbType}:{r.Id}",
            Source     = "tmdb",
            Title      = title ?? string.Empty,
            Year       = year,
            Overview   = r.Overview,
            PosterUrl  = r.PosterPath is { } p ? $"{ImageBase}{p}" : null,
            Rating     = r.VoteAverage
        };
    }

    private static MediaMetadata ParseDetailResponse(JsonElement root, string tmdbType, string externalId)
    {
        var title  = tmdbType == "movie"
            ? root.TryGetProperty("title", out var t) ? t.GetString() : null
            : root.TryGetProperty("name", out var n) ? n.GetString() : null;

        var dateStr = tmdbType == "movie"
            ? (root.TryGetProperty("release_date", out var rd) ? rd.GetString() : null)
            : (root.TryGetProperty("first_air_date", out var fad) ? fad.GetString() : null);

        int? year = null;
        if (dateStr?.Length >= 4 && int.TryParse(dateStr[..4], out var y)) year = y;

        var posterPath   = root.TryGetProperty("poster_path", out var pp) ? pp.GetString()   : null;
        var backdropPath = root.TryGetProperty("backdrop_path", out var bp) ? bp.GetString() : null;
        var overview     = root.TryGetProperty("overview", out var ov) ? ov.GetString()      : null;
        var rating       = root.TryGetProperty("vote_average", out var va) ? va.GetDouble()  : (double?)null;

        int? runtime = null;
        if (root.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number)
            runtime = rt.GetInt32();
        else if (root.TryGetProperty("episode_run_time", out var ert) && ert.ValueKind == JsonValueKind.Array)
            foreach (var v in ert.EnumerateArray()) { runtime = v.GetInt32(); break; }

        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var genArr) && genArr.ValueKind == JsonValueKind.Array)
            foreach (var g in genArr.EnumerateArray())
                if (g.TryGetProperty("name", out var gn) && gn.GetString() is { } gname)
                    genres.Add(gname);

        var cast      = new List<string>();
        var directors = new List<string>();
        if (root.TryGetProperty("credits", out var credits))
        {
            if (credits.TryGetProperty("cast", out var castArr) && castArr.ValueKind == JsonValueKind.Array)
                foreach (var c in castArr.EnumerateArray().Take(20))
                    if (c.TryGetProperty("name", out var cn) && cn.GetString() is { } cname)
                        cast.Add(cname);

            if (credits.TryGetProperty("crew", out var crewArr) && crewArr.ValueKind == JsonValueKind.Array)
                foreach (var m in crewArr.EnumerateArray())
                    if (m.TryGetProperty("job", out var job) && job.GetString() == "Director" &&
                        m.TryGetProperty("name", out var dn) && dn.GetString() is { } dname)
                        directors.Add(dname);
        }

        return new MediaMetadata
        {
            ExternalId      = externalId,
            Source          = "tmdb",
            Title           = title ?? string.Empty,
            Year            = year,
            Overview        = overview,
            PosterUrl       = posterPath   is { } pp2 ? $"{ImageBase}{pp2}"   : null,
            BackdropUrl     = backdropPath is { } bp2 ? $"{ImageBase}{bp2}" : null,
            RuntimeMinutes  = runtime,
            Genres          = genres,
            Cast            = cast,
            Directors       = directors,
            Rating          = rating
        };
    }

    // ── Private DTOs (only used for JSON deserialisation within this file) ────

    private sealed class TmdbSearchResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbSearchResult> Results { get; set; } = [];

        [JsonPropertyName("total_results")]
        public int TotalResults { get; set; }
    }

    private sealed class TmdbSearchResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }
    }
}
