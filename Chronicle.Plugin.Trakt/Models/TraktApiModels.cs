using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Trakt.Models;

// ── Auth flow ─────────────────────────────────────────────────────────────────

internal record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")]    string DeviceCode,
    [property: JsonPropertyName("user_code")]      string UserCode,
    [property: JsonPropertyName("verification_url")] string VerificationUrl,
    [property: JsonPropertyName("expires_in")]     int    ExpiresIn,
    [property: JsonPropertyName("interval")]       int    Interval);

internal record TokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("token_type")]    string TokenType,
    [property: JsonPropertyName("expires_in")]    long   ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("scope")]         string Scope,
    [property: JsonPropertyName("created_at")]    long   CreatedAt);

// ── Shared sub-types ──────────────────────────────────────────────────────────

internal record TraktIds(
    [property: JsonPropertyName("trakt")] long?   Trakt,
    [property: JsonPropertyName("slug")]  string? Slug,
    [property: JsonPropertyName("imdb")]  string? Imdb,
    [property: JsonPropertyName("tmdb")]  long?   Tmdb,
    [property: JsonPropertyName("tvdb")]  long?   Tvdb);

internal record TraktMovie(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids);

internal record TraktShow(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids);

internal record TraktEpisode(
    [property: JsonPropertyName("season")] int      Season,
    [property: JsonPropertyName("number")] int      Number,
    [property: JsonPropertyName("title")]  string?  Title,
    [property: JsonPropertyName("ids")]    TraktIds Ids);

// ── History ───────────────────────────────────────────────────────────────────

internal record TraktHistoryItem(
    [property: JsonPropertyName("id")]         long            Id,
    [property: JsonPropertyName("watched_at")] DateTimeOffset  WatchedAt,
    [property: JsonPropertyName("action")]     string          Action,
    [property: JsonPropertyName("type")]       string          Type,
    [property: JsonPropertyName("movie")]      TraktMovie?     Movie,
    [property: JsonPropertyName("show")]       TraktShow?      Show,
    [property: JsonPropertyName("episode")]    TraktEpisode?   Episode);

// ── Ratings ───────────────────────────────────────────────────────────────────

internal record TraktRatingItem(
    [property: JsonPropertyName("rated_at")] DateTimeOffset RatedAt,
    [property: JsonPropertyName("rating")]   int            Rating,
    [property: JsonPropertyName("type")]     string         Type,
    [property: JsonPropertyName("movie")]    TraktMovie?    Movie,
    [property: JsonPropertyName("show")]     TraktShow?     Show,
    [property: JsonPropertyName("episode")]  TraktEpisode?  Episode);

// ── Watchlist ─────────────────────────────────────────────────────────────────

internal record TraktWatchlistItem(
    [property: JsonPropertyName("listed_at")] DateTimeOffset ListedAt,
    [property: JsonPropertyName("id")]        long           Id,
    [property: JsonPropertyName("rank")]      int?           Rank,
    [property: JsonPropertyName("notes")]     string?        Notes,
    [property: JsonPropertyName("type")]      string         Type,
    [property: JsonPropertyName("movie")]     TraktMovie?    Movie,
    [property: JsonPropertyName("show")]      TraktShow?     Show);
