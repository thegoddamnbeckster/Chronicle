using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Plugins
{
    /// <summary>The list of plugin repositories that make up the catalog, and where it came from.</summary>
    public sealed record CatalogListing(
        IReadOnlyList<PluginCatalogSeed> Seeds,
        /// <summary>The address the list was read from, or "built-in" when none could be reached.</summary>
        string Source,
        /// <summary>True when the hosted file could not be read and an older copy (or the built-in list) is being used.</summary>
        bool UsingFallback,
        DateTime FetchedAtUtc,
        string? Error);

    public interface IPluginCatalogSource
    {
        Task<CatalogListing> GetAsync(CancellationToken ct = default);

        /// <summary>Forgets the cached list so the next <see cref="GetAsync"/> fetches again.</summary>
        void Refresh();
    }

    /// <summary>
    /// Reads the catalog from a hosted file (<c>plugins.json</c> in Chronicle's repository by default), so a plugin can be
    /// added to the catalog without a Chronicle release. The address is the app setting <c>plugins.catalog_url</c> (https
    /// only). The last list that was read successfully is kept in <c>plugins.catalog_cache</c>; if the file cannot be
    /// reached or is not valid, that copy is used, and failing that the list compiled into Chronicle.
    /// </summary>
    public sealed class PluginCatalogSource : IPluginCatalogSource
    {
        public const string UrlKey = "plugins.catalog_url";
        public const string CacheKey = "plugins.catalog_cache";
        public const string DefaultUrl = "https://raw.githubusercontent.com/thegoddamnbeckster/Chronicle/main/plugins.json";
        public const int MaxEntries = 500;
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

        private static readonly Regex IdRule = new(@"^[a-z0-9][a-z0-9._-]{1,99}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RepoRule = new(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        private readonly IServiceScopeFactory _scopes;
        private readonly IHttpClientFactory _http;
        private readonly TimeProvider _clock;
        private readonly ILogger _log = Log.ForContext<PluginCatalogSource>();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CatalogListing? _cached;

        public PluginCatalogSource(IServiceScopeFactory scopes, IHttpClientFactory http, TimeProvider? clock = null)
        {
            _scopes = scopes;
            _http = http;
            _clock = clock ?? TimeProvider.System;
        }

        public void Refresh() => _cached = null;

        public async Task<CatalogListing> GetAsync(CancellationToken ct = default)
        {
            var cached = _cached;
            if (cached is not null && _clock.GetUtcNow().UtcDateTime - cached.FetchedAtUtc < CacheFor) return cached;

            await _gate.WaitAsync(ct);
            try
            {
                cached = _cached;
                if (cached is not null && _clock.GetUtcNow().UtcDateTime - cached.FetchedAtUtc < CacheFor) return cached;
                return _cached = await LoadAsync(ct);
            }
            finally { _gate.Release(); }
        }

        private async Task<CatalogListing> LoadAsync(CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var now = _clock.GetUtcNow().UtcDateTime;

            var settings = await db.AppSettings.AsNoTracking()
                .Where(s => s.Key == UrlKey || s.Key == CacheKey || s.Key == PluginIntegrity.AllowUnlistedKey)
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            var url = settings.TryGetValue(UrlKey, out var configured) && !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : DefaultUrl;

            // The catalog is the allowlist for what can be installed, so pointing it somewhere else is the same decision as
            // allowing unlisted plugins and needs the same explicit setting. Otherwise a stolen administrator session could
            // repoint the catalog at someone else's list.
            var customAllowed = settings.TryGetValue(PluginIntegrity.AllowUnlistedKey, out var allow)
                                && string.Equals(allow, "true", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(url, DefaultUrl, StringComparison.Ordinal) && !customAllowed)
            {
                _log.Warning("Plugin catalog: ignoring the custom address {Url}; it is only used when {Key} is true", url, PluginIntegrity.AllowUnlistedKey);
                url = DefaultUrl;
            }

            string? error = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                error = $"The catalog address must be an https address ({url}).";
            }
            else
            {
                try
                {
                    var client = _http.CreateClient("github");
                    using var response = await client.GetAsync(uri, ct);
                    response.EnsureSuccessStatusCode();
                    var text = await response.Content.ReadAsStringAsync(ct);
                    var seeds = Parse(text, out var problem);
                    if (seeds is not null)
                    {
                        // Remember it, so the catalog still works when the host is unreachable later.
                        var existing = await db.AppSettings.FindAsync([CacheKey], ct);
                        if (existing is null) db.AppSettings.Add(new AppSetting { Key = CacheKey, Value = text });
                        else existing.Value = text;
                        await db.SaveChangesAsync(ct);
                        return new CatalogListing(seeds, url, false, now, null);
                    }
                    error = problem;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    if (ct.IsCancellationRequested) throw;
                    error = ex.Message;
                }
            }

            _log.Warning("Plugin catalog: could not use {Url}: {Error}. Falling back to the last list that worked.", url, error);
            if (settings.TryGetValue(CacheKey, out var last) && Parse(last, out _) is { } lastSeeds)
                return new CatalogListing(lastSeeds, "last successful copy of " + url, true, now, error);
            return new CatalogListing(PluginCatalogSeeds.Entries, "built-in", true, now, error);
        }

        /// <summary>Reads the file. Null (with a reason) when it is not a usable catalog; bad individual entries are skipped.</summary>
        public static List<PluginCatalogSeed>? Parse(string json, out string? problem)
        {
            problem = null;
            File? file;
            try { file = JsonSerializer.Deserialize<File>(json, Json); }
            catch (JsonException ex) { problem = "The catalog file is not valid JSON: " + ex.Message; return null; }
            if (file?.Plugins is null) { problem = "The catalog file has no \"plugins\" list."; return null; }

            var seeds = new List<PluginCatalogSeed>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in file.Plugins.Take(MaxEntries))
            {
                if (p.PluginId is null || p.GithubRepo is null || !IdRule.IsMatch(p.PluginId) || !RepoRule.IsMatch(p.GithubRepo)) continue;
                if (!seen.Add(p.PluginId)) continue;
                seeds.Add(new PluginCatalogSeed(p.PluginId, p.GithubRepo, (p.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray()));
            }
            if (seeds.Count == 0) { problem = "The catalog file lists no usable plugins."; return null; }
            return seeds;
        }

        private sealed class File { public List<Entry>? Plugins { get; set; } }

        private sealed class Entry
        {
            [System.Text.Json.Serialization.JsonPropertyName("plugin_id")] public string? PluginId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("github_repo")] public string? GithubRepo { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        }
    }
}
