using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Plugin.Trakt.Models;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// Thin wrapper over the Trakt v2 REST API.
/// One instance per plugin lifetime (recreated when Configure() is called).
/// </summary>
internal sealed class TraktClient : IDisposable
{
    private const string BaseUrl  = "https://api.trakt.tv";
    private const int    PageSize = 500;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string     _clientId;
    private readonly string     _clientSecret;

    private string? _accessToken;
    private string? _refreshToken;
    private long    _tokenExpiresAt;   // Unix seconds

    // ── Construction ─────────────────────────────────────────────────────────

    public TraktClient(string clientId, string clientSecret, HttpClient? httpClient = null)
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _http         = httpClient ?? new HttpClient { BaseAddress = new Uri(BaseUrl) };

        _http.DefaultRequestHeaders.Add("trakt-api-version", "2");
        _http.DefaultRequestHeaders.Add("trakt-api-key", clientId);
        _http.DefaultRequestHeaders.Accept
             .Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Token management ─────────────────────────────────────────────────────

    public void SetTokens(string accessToken, string refreshToken, long expiresAt)
    {
        _accessToken    = accessToken;
        _refreshToken   = refreshToken;
        _tokenExpiresAt = expiresAt;
    }

    public bool   IsAuthenticated => _accessToken is not null
                                  && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < _tokenExpiresAt;
    public string? AccessToken    => _accessToken;
    public string? RefreshToken   => _refreshToken;
    public long    TokenExpiresAt => _tokenExpiresAt;

    // ── Device auth flow ──────────────────────────────────────────────────────

    public async Task<DeviceCodeResponse> InitiateDeviceAuthAsync(CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(
            "/oauth/device/code",
            new { client_id = _clientId },
            ct);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Trakt returned null device-code response.");
    }

    /// <summary>
    /// Polls for token after the user has completed device authorization.
    /// Returns (token, status) where status is one of:
    ///   "authorized" | "pending" | "expired" | "denied" | "slow_down"
    /// </summary>
    public async Task<(TokenResponse? Token, string Status)> PollForTokenAsync(
        string deviceCode, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(
            "/oauth/device/token",
            new
            {
                code          = deviceCode,
                client_id     = _clientId,
                client_secret = _clientSecret
            },
            ct);

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.OK =>
                (await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct), "authorized"),
            System.Net.HttpStatusCode.BadRequest    => (null, "pending"),   // 400 — still waiting
            System.Net.HttpStatusCode.Gone          => (null, "expired"),   // 410
            System.Net.HttpStatusCode.TooManyRequests => (null, "slow_down"), // 429
            _ when (int)response.StatusCode == 418  => (null, "denied"),    // 418 I'm a teapot
            _                                       => (null, "pending")
        };
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
            return false;

        try
        {
            var response = await _http.PostAsJsonAsync(
                "/oauth/token",
                new
                {
                    refresh_token = _refreshToken,
                    client_id     = _clientId,
                    client_secret = _clientSecret,
                    redirect_uri  = "urn:ietf:wg:oauth:2.0:oob",
                    grant_type    = "refresh_token"
                },
                ct);

            if (!response.IsSuccessStatusCode)
                return false;

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct);
            if (token is null)
                return false;

            _accessToken    = token.AccessToken;
            _refreshToken   = token.RefreshToken;
            _tokenExpiresAt = token.CreatedAt + token.ExpiresIn;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures a valid access token is present, refreshing it if within 24 h of expiry.
    /// Throws if no valid token can be obtained.
    /// </summary>
    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken is null)
            throw new InvalidOperationException(
                "Trakt plugin is not authenticated. Complete the device authorization flow first.");

        // Proactively refresh within 24 h of expiry.
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > _tokenExpiresAt - 86_400)
            await TryRefreshAsync(ct);

        if (!IsAuthenticated)
            throw new InvalidOperationException(
                "Trakt access token has expired and could not be refreshed. Re-authenticate.");
    }

    // ── Authenticated request helper ──────────────────────────────────────────

    private HttpRequestMessage AuthGet(string relativeUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return req;
    }

    // ── Data endpoints ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full watch history, paginating automatically.
    /// <paramref name="since"/> restricts results to events after that timestamp.
    /// </summary>
    public async Task<List<TraktHistoryItem>> GetWatchHistoryAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var all  = new List<TraktHistoryItem>();
        var page = 1;

        while (true)
        {
            var url = since.HasValue
                ? $"/sync/history?limit={PageSize}&page={page}&start_at={Uri.EscapeDataString(since.Value.ToString("O"))}"
                : $"/sync/history?limit={PageSize}&page={page}";

            using var req      = AuthGet(url);
            using var response = await _http.SendAsync(req, ct);

            if (!response.IsSuccessStatusCode)
                break;

            var items = await response.Content
                .ReadFromJsonAsync<List<TraktHistoryItem>>(JsonOpts, ct);

            if (items is null || items.Count == 0)
                break;

            all.AddRange(items);

            if (items.Count < PageSize)
                break;   // Reached the last page.

            page++;
            await Task.Delay(100, ct);   // Be respectful of Trakt's rate limits.
        }

        return all;
    }

    public async Task<List<TraktRatingItem>> GetRatingsAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        using var req      = AuthGet("/sync/ratings");
        using var response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<List<TraktRatingItem>>(JsonOpts, ct) ?? [];
    }

    public async Task<List<TraktWatchlistItem>> GetWatchlistAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        using var req      = AuthGet("/sync/watchlist");
        using var response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<List<TraktWatchlistItem>>(JsonOpts, ct) ?? [];
    }

    /// <summary>Verifies the access token is valid by calling /users/me.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        if (_accessToken is null)
            return false;

        try
        {
            await EnsureTokenAsync(ct);
            using var req      = AuthGet("/users/me");
            using var response = await _http.SendAsync(req, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
