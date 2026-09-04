using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Plugins.Models;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Resolves Chronicle's plugin catalog live from GitHub instead of a static, hand-maintained
/// list baked into Chronicle's own compiled code -- see PluginCatalogSeed's own doc for why.
/// For each seeded repo: the actual latest release (tag + attached .zip asset) comes from
/// GitHub's Releases API, and Name/Author/Description/IconUrl/the DLL to load come from that
/// same tag's manifest.json (fetched from raw.githubusercontent.com, so it reflects exactly
/// what that release shipped, not whatever HEAD has moved on to since).
///
/// A repo with no release yet, or a release with no attached zip, resolves to null rather
/// than throwing -- one broken/not-yet-released repo must not take down the rest of the
/// catalog listing. Results are cached briefly per plugin to keep the catalog page and
/// repeated install/update calls fast without hammering GitHub's API on every request.
/// </summary>
public class PluginCatalogService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger _log = Log.ForContext<PluginCatalogService>();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    public PluginCatalogService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
    }

    /// <summary>Every catalog entry that could actually be resolved right now (skips repos with no usable release).</summary>
    public async Task<List<PluginCatalogEntry>> GetCatalogAsync(CancellationToken ct = default)
    {
        var results = await Task.WhenAll(PluginCatalogSeeds.Entries.Select(seed => ResolveAsync(seed, ct)));
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    /// <summary>Resolves a single plugin by id -- used by install/update, which need one entry, not the whole catalog.</summary>
    public Task<PluginCatalogEntry?> ResolveAsync(string pluginId, CancellationToken ct = default)
    {
        var seed = Array.Find(PluginCatalogSeeds.Entries, e => e.PluginId == pluginId);
        return seed is null ? Task.FromResult<PluginCatalogEntry?>(null) : ResolveAsync(seed, ct);
    }

    private async Task<PluginCatalogEntry?> ResolveAsync(PluginCatalogSeed seed, CancellationToken ct)
    {
        var cacheKey = $"plugin-catalog-entry:{seed.PluginId}";
        if (_cache.TryGetValue(cacheKey, out PluginCatalogEntry? cached))
            return cached;

        var entry = await FetchLiveAsync(seed, ct);
        // Deliberately not caching a null (repo temporarily unreachable, or genuinely has no
        // release yet) -- a real GitHub outage or a brand-new repo publishing its first
        // release should be reflected on the very next request, not stuck absent for the
        // full TTL.
        if (entry is not null)
            _cache.Set(cacheKey, entry, CacheTtl);
        return entry;
    }

    private async Task<PluginCatalogEntry?> FetchLiveAsync(PluginCatalogSeed seed, CancellationToken ct)
    {
        var github = _httpClientFactory.CreateClient("github");

        string tag;
        string? assetName = null;
        try
        {
            using var resp = await github.GetAsync(
                $"https://api.github.com/repos/{seed.GithubRepo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Debug("Catalog: {Repo} has no latest release ({Status})", seed.GithubRepo, (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(tag)) return null;

            // Take the first .zip asset -- every repo in this catalog ships exactly one.
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var candidateAssetName = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (candidateAssetName is not null && candidateAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = candidateAssetName;
                        break;
                    }
                }
            }

            if (assetName is null)
            {
                _log.Debug("Catalog: {Repo}'s latest release {Tag} has no .zip asset attached", seed.GithubRepo, tag);
                return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Catalog: failed to check latest release for {Repo}", seed.GithubRepo);
            return null;
        }

        // manifest.json at the exact release tag -- reflects what that release actually
        // shipped, not whatever the default branch has moved on to since. A fetch failure
        // here degrades to a bare-bones entry (repo name as the display name, no
        // description/icon) rather than dropping the whole entry -- the plugin is still
        // genuinely installable even if its manifest couldn't be read for display purposes.
        var version = tag.TrimStart('v', 'V');
        var name = seed.PluginId;
        string? author = null, description = null, iconUrl = null;
        var dllName = $"{GuessAssemblyNameFromRepo(seed.GithubRepo)}.dll";

        try
        {
            var manifestUrl = $"https://raw.githubusercontent.com/{seed.GithubRepo}/{tag}/manifest.json";
            using var resp = await github.GetAsync(manifestUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(json);
                if (manifest is not null)
                {
                    if (!string.IsNullOrWhiteSpace(manifest.Name)) name = manifest.Name;
                    author      = manifest.Author;
                    description = manifest.Description;
                    iconUrl     = manifest.IconUrl;

                    // The DLL's assembly name is the entry_type's namespace with its final
                    // (class name) segment dropped -- confirmed against every manifest in
                    // this catalog (e.g. "Chronicle.Plugin.Kodi.NFO.KodiNfoPlugin" ->
                    // "Chronicle.Plugin.Kodi.NFO.dll"), since every one of these projects
                    // uses its root namespace as its assembly name.
                    if (!string.IsNullOrWhiteSpace(manifest.EntryType))
                    {
                        var lastDot = manifest.EntryType.LastIndexOf('.');
                        if (lastDot > 0)
                            dllName = manifest.EntryType[..lastDot] + ".dll";
                    }
                }
            }
            else
            {
                _log.Debug("Catalog: {Repo} has no manifest.json at tag {Tag} ({Status})",
                    seed.GithubRepo, tag, (int)resp.StatusCode);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Catalog: failed to read manifest.json for {Repo} at {Tag}", seed.GithubRepo, tag);
        }

        return new PluginCatalogEntry(
            PluginId:    seed.PluginId,
            Name:        name,
            Description: description ?? $"See {seed.GithubRepo} on GitHub.",
            Author:      author ?? "Unknown",
            IconUrl:     iconUrl,
            GithubRepo:  seed.GithubRepo,
            AssetName:   assetName,
            DllName:     dllName,
            Tags:        seed.Tags,
            Sha256:      null, // no longer pinned -- see this class's own doc
            Version:     version
        );
    }

    /// <summary>Fallback DLL-name guess (repo's own short name + ".dll") used only if manifest.json couldn't be read at all -- entry_type-derived is preferred whenever the manifest fetch succeeds.</summary>
    private static string GuessAssemblyNameFromRepo(string githubRepo)
    {
        var slash = githubRepo.IndexOf('/');
        return slash >= 0 ? githubRepo[(slash + 1)..] : githubRepo;
    }
}
