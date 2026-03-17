using System.IO.Compression;
using System.Text.Json;
using Chronicle.API.DTOs;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/plugins")]
[Authorize]
public class PluginsController : ControllerBase
{
    private readonly IPluginService      _pluginService;
    private readonly IPluginRegistry     _registry;
    private readonly IHttpClientFactory  _httpClientFactory;
    private readonly IMemoryCache        _cache;
    private readonly IWebHostEnvironment _environment;

    // ── Icon proxy constants ───────────────────────────────────────────────────

    /// <summary>Maximum permitted favicon file size (100 KB).</summary>
    private const int MaxIconBytes = 100 * 1024;

    /// <summary>How long to cache a fetched favicon before re-fetching.</summary>
    private static readonly TimeSpan IconCacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Allowed image Content-Type prefixes.
    /// SVG is intentionally excluded — SVG can embed JavaScript.
    /// </summary>
    private static readonly string[] AllowedContentTypePrefixes =
    [
        "image/x-icon",
        "image/vnd.microsoft.icon",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    ];

    /// <summary>
    /// Magic byte signatures for permitted image formats.
    /// Used as a second validation layer after the Content-Type check.
    /// </summary>
    private static readonly (byte[] Magic, string Name)[] ImageMagicBytes =
    [
        ([ 0xFF, 0xD8, 0xFF ],              "JPEG"),
        ([ 0x89, 0x50, 0x4E, 0x47 ],       "PNG"),   // ‌PNG
        ([ 0x47, 0x49, 0x46, 0x38 ],       "GIF"),   // GIF8
        ([ 0x00, 0x00, 0x01, 0x00 ],       "ICO"),   // ICO
        ([ 0x52, 0x49, 0x46, 0x46 ],       "WEBP"),  // RIFF (WebP)
    ];

    public PluginsController(
        IPluginService pluginService,
        IPluginRegistry registry,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IWebHostEnvironment environment)
    {
        _pluginService     = pluginService;
        _registry          = registry;
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _environment       = environment;
    }

    // ── GET /api/v1/plugins ───────────────────────────────────────────────────

    /// <summary>Lists all installed plugins.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPlugins()
    {
        var plugins = await _pluginService.GetAllPluginsAsync();
        var dtos = plugins.Select(ToDto).ToList();
        return Ok(ApiResponse<List<PluginDto>>.Ok(dtos));
    }

    // ── GET /api/v1/plugins/{id} ──────────────────────────────────────────────

    /// <summary>Gets a single installed plugin by its database id.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetPlugin(int id)
    {
        var plugin = await _pluginService.GetPluginAsync(id);
        if (plugin is null)
            return NotFound(ApiResponse<PluginDto>.Fail("PLUGIN_NOT_FOUND", "Plugin not found."));
        return Ok(ApiResponse<PluginDto>.Ok(ToDto(plugin)));
    }

    // ── GET /api/v1/plugins/{id}/icon ─────────────────────────────────────────

    /// <summary>
    /// Secure favicon proxy: fetches, validates, caches, and serves the plugin's
    /// icon from the URL declared in its manifest.json.
    ///
    /// Security measures:
    ///   1. The icon URL comes ONLY from the plugin's manifest (not user input).
    ///   2. Content-Type is validated — only raster image types are accepted;
    ///      SVG is explicitly excluded because it can embed JavaScript.
    ///   3. Magic bytes are inspected to verify the binary matches its claimed type.
    ///   4. File size is capped at 100 KB.
    ///   5. Result is cached for 24 h — the browser never contacts the external site.
    ///   6. HTTPS-only URLs are required (enforced via Uri.Scheme check).
    ///   7. Only HTTPS schemes (https://) are permitted — plain http:// is rejected.
    /// </summary>
    // Favicons are not sensitive — allow unauthenticated access so <img> tags work.
    [HttpGet("{id:int}/icon")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPluginIcon(int id, CancellationToken ct)
    {
        // Resolve iconUrl from the in-memory registry (not user-supplied input)
        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == id);
        var iconUrl = loaded?.Manifest.IconUrl;

        if (string.IsNullOrWhiteSpace(iconUrl))
            return NotFound();

        // Require HTTPS to prevent loading resources from plain-HTTP sites
        if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var cacheKey = $"plugin_icon:{id}:{iconUrl}";

        // Return cached bytes if available
        if (_cache.TryGetValue(cacheKey, out (byte[] Data, string ContentType) cached))
            return File(cached.Data, cached.ContentType);

        // Fetch from external site using the dedicated named HttpClient
        byte[] bytes;
        string contentType;
        try
        {
            var http = _httpClientFactory.CreateClient("favicon");
            using var response = await http.GetAsync(iconUrl, ct);

            if (!response.IsSuccessStatusCode)
                return NotFound();

            // Validate Content-Type before reading body
            var rawContentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                                 ?? string.Empty;

            if (!AllowedContentTypePrefixes.Any(a => rawContentType.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
            {
                return StatusCode(415, ApiResponse<object>.Fail(
                    "INVALID_ICON_TYPE",
                    $"Remote server returned unsupported content type '{rawContentType}'. " +
                    "Only raster image formats are permitted."));
            }

            contentType = rawContentType;

            // Read body — limit to MaxIconBytes to prevent oversized payloads
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxIconBytes + 1];
            var totalRead = 0;

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct)) > 0)
            {
                totalRead += read;
                if (totalRead > MaxIconBytes)
                    return StatusCode(502, ApiResponse<object>.Fail(
                        "ICON_TOO_LARGE",
                        $"Plugin icon exceeds the {MaxIconBytes / 1024} KB limit."));
            }

            bytes = buffer[..totalRead];
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504);
        }
        catch (HttpRequestException)
        {
            return StatusCode(502);
        }

        // Validate magic bytes — rejects files that lie about their Content-Type
        if (!HasValidImageMagic(bytes))
        {
            return StatusCode(415, ApiResponse<object>.Fail(
                "INVALID_ICON_CONTENT",
                "Plugin icon binary does not match any permitted image format."));
        }

        // Cache and return
        _cache.Set(cacheKey, (bytes, contentType),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = IconCacheDuration });

        return File(bytes, contentType);
    }

    // ── POST /api/v1/plugins ──────────────────────────────────────────────────

    /// <summary>
    /// Installs a plugin from a DLL path on the server's filesystem.
    /// Admin only.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> InstallPlugin([FromBody] InstallPluginRequest request)
    {
        try
        {
            var plugin = await _pluginService.InstallPluginAsync(request.DllPath);
            return Ok(ApiResponse<PluginDto>.Ok(ToDto(plugin)));
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(ApiResponse<PluginDto>.Fail("DLL_NOT_FOUND", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiResponse<PluginDto>.Fail("ALREADY_INSTALLED", ex.Message));
        }
    }

    // ── PUT /api/v1/plugins/{id}/settings ─────────────────────────────────────

    /// <summary>Updates the settings for a plugin.</summary>
    [HttpPut("{id:int}/settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateSettings(int id, [FromBody] UpdatePluginSettingsRequest request)
    {
        try
        {
            await _pluginService.UpdateSettingsAsync(id, request.Settings);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    // ── POST /api/v1/plugins/{id}/enable ──────────────────────────────────────

    /// <summary>Enables an installed plugin.</summary>
    [HttpPost("{id:int}/enable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EnablePlugin(int id)
    {
        try
        {
            await _pluginService.EnablePluginAsync(id);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    // ── POST /api/v1/plugins/{id}/disable ─────────────────────────────────────

    /// <summary>Disables a plugin (unloads from memory, keeps database record).</summary>
    [HttpPost("{id:int}/disable")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DisablePlugin(int id)
    {
        try
        {
            await _pluginService.DisablePluginAsync(id);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    // ── DELETE /api/v1/plugins/{id} ───────────────────────────────────────────

    /// <summary>Uninstalls a plugin (removes database record, unloads from memory).</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UninstallPlugin(int id)
    {
        try
        {
            await _pluginService.UninstallPluginAsync(id);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", ex.Message));
        }
    }

    // ── GET /api/v1/plugins/{id}/health ───────────────────────────────────────

    /// <summary>
    /// Runs the plugin's health check and returns the result.
    /// Includes an optional FailureReason and IsCritical severity so the UI can render
    /// a yellow badge for config issues and a red badge for unexpected failures.
    /// </summary>
    [HttpGet("{id:int}/health")]
    public async Task<IActionResult> HealthCheck(int id)
    {
        var result = await _pluginService.HealthCheckAsync(id);
        if (result is null)
            return NotFound(ApiResponse<PluginHealthDto>.Fail("PLUGIN_NOT_LOADED", "Plugin not found or not loaded."));
        return Ok(ApiResponse<PluginHealthDto>.Ok(
            new PluginHealthDto(result.Healthy, result.FailureReason, result.IsCritical)));
    }

    // ── GET /api/v1/plugins/{id}/settings-schema ──────────────────────────────

    /// <summary>Returns the settings schema for the specified plugin.</summary>
    [HttpGet("{id:int}/settings-schema")]
    public async Task<IActionResult> GetSettingsSchema(int id)
    {
        var plugin = await _pluginService.GetPluginAsync(id);
        if (plugin is null)
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", "Plugin not found."));

        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == id);
        if (loaded is null)
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_LOADED", "Plugin is not currently loaded."));

        // Try metadata provider first, then file scanner plugins, then return empty schema
        if (loaded.MetadataProviders.Count > 0)
            return Ok(ApiResponse<object>.Ok(loaded.MetadataProviders[0].GetSettingsSchema()));

        if (loaded.FileScannerPlugins.Count > 0)
        {
            var schema = loaded.FileScannerPlugins[0].GetSettingsSchema();
            return Ok(ApiResponse<object>.Ok(schema));
        }

        return Ok(ApiResponse<object>.Ok(new { settings = Array.Empty<object>() }));
    }

    // ── GET /api/v1/plugins/{id}/search ───────────────────────────────────────

    /// <summary>
    /// Searches for media metadata via the specified plugin's IMetadataProvider.
    /// Returns a list of matching results from the upstream source (e.g. TMDB).
    /// </summary>
    [HttpGet("{id:int}/search")]
    public async Task<IActionResult> SearchMetadata(
        int id,
        [FromQuery] string query,
        [FromQuery] string mediaType = "movie",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(ApiResponse<object>.Fail("QUERY_REQUIRED", "A search query is required."));

        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == id);
        if (loaded is null)
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_LOADED", "Plugin not found or not loaded."));

        if (loaded.MetadataProviders.Count == 0)
            return BadRequest(ApiResponse<object>.Fail("NOT_METADATA_PROVIDER", "This plugin does not provide metadata search."));

        try
        {
            var result = await loaded.MetadataProviders[0].SearchAsync(query, mediaType, ct);
            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("PLUGIN_NOT_CONFIGURED", ex.Message));
        }
    }

    // ── GET /api/v1/plugins/{id}/metadata/{externalId} ────────────────────────

    /// <summary>
    /// Fetches full metadata for a specific item by its external ID.
    /// The ID format is plugin-specific (e.g. "movie:550" or "tv:1399" for TMDB).
    /// </summary>
    [HttpGet("{id:int}/metadata/{externalId}")]
    public async Task<IActionResult> GetMetadata(
        int id,
        string externalId,
        CancellationToken ct = default)
    {
        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == id);
        if (loaded is null)
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_LOADED", "Plugin not found or not loaded."));

        if (loaded.MetadataProviders.Count == 0)
            return BadRequest(ApiResponse<object>.Fail("NOT_METADATA_PROVIDER", "This plugin does not provide metadata lookup."));

        try
        {
            var result = await loaded.MetadataProviders[0].GetByIdAsync(externalId, ct);
            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("PLUGIN_NOT_CONFIGURED", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, ApiResponse<object>.Fail("UPSTREAM_ERROR", ex.Message));
        }
    }

    // ── Static plugin catalog ─────────────────────────────────────────────────

    private static readonly PluginCatalogEntry[] PluginCatalog =
    [
        new PluginCatalogEntry(
            PluginId:    "tmdb",
            Name:        "TMDB",
            Description: "Metadata from The Movie Database (TMDB) for movies and TV shows.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://www.themoviedb.org/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.TMDB",
            AssetName:   "Chronicle.Plugin.TMDB.zip",
            DllName:     "Chronicle.Plugin.TMDB.dll",
            Tags:        ["movies", "tv", "metadata"],
            Sha256:      "600936c8c5e8bef83d2de4e51c29c975977feac57f80ed6d0d8fd0723b478480"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.filescanner",
            Name:        "File Scanner",
            Description: "Scans local directories for media files. Parses NFO sidecars and filenames to extract title, year, and media type. Supports TV hierarchy (SxxExx), audio files (MP3/FLAC/OGG/etc.), and embedded tag reading via TagLib#.",
            Author:      "Chronicle",
            IconUrl:     null,
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.FileScanner",
            AssetName:   "Chronicle.Plugin.FileScanner.zip",
            DllName:     "Chronicle.Plugin.FileScanner.dll",
            Tags:        ["movies", "tv", "audio", "filescanner", "local"],
            Sha256:      "30f7996b2b3edd47f57084c1c774aa87d137fabdee50ffd3e0a185c2bef730e9"
        ),
    ];

    // ── GET /api/v1/plugins/catalog ───────────────────────────────────────────

    /// <summary>Lists all plugins available in the Chronicle plugin catalog.</summary>
    [HttpGet("catalog")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetCatalog()
    {
        var installed = await _pluginService.GetAllPluginsAsync();
        var installedIds = installed.Select(p => p.PluginId).ToHashSet();

        var entries = PluginCatalog
            .Select(e => e with { IsInstalled = installedIds.Contains(e.PluginId) })
            .ToList();

        return Ok(ApiResponse<List<PluginCatalogEntry>>.Ok(entries));
    }

    // ── POST /api/v1/plugins/catalog/{pluginId}/install ───────────────────────

    /// <summary>
    /// Downloads and installs a plugin from the Chronicle plugin catalog.
    /// Fetches the latest GitHub release, extracts the ZIP, and installs the DLL.
    /// </summary>
    [HttpPost("catalog/{pluginId}/install")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> InstallFromCatalog(string pluginId, CancellationToken ct)
    {
        var entry = Array.Find(PluginCatalog, e => e.PluginId == pluginId);
        if (entry is null)
            return NotFound(ApiResponse<object>.Fail("CATALOG_ENTRY_NOT_FOUND",
                $"No catalog entry found for plugin '{pluginId}'."));

        // Resolve download URL from the latest GitHub release
        string downloadUrl;
        try
        {
            var github = _httpClientFactory.CreateClient("github");
            var apiUrl = $"https://api.github.com/repos/{entry.GithubRepo}/releases/latest";
            using var releaseResponse = await github.GetAsync(apiUrl, ct);

            if (!releaseResponse.IsSuccessStatusCode)
                return StatusCode(502, ApiResponse<object>.Fail("GITHUB_ERROR",
                    $"GitHub returned HTTP {(int)releaseResponse.StatusCode} for the releases API."));

            await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(releaseStream, cancellationToken: ct);

            downloadUrl = string.Empty;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == entry.AssetName)
                {
                    // Use the API asset URL (not browser_download_url). When downloaded
                    // with Accept: application/octet-stream the GitHub API redirects to a
                    // presigned storage URL, which works even for repos that require auth.
                    downloadUrl = asset.GetProperty("url").GetString() ?? string.Empty;
                    break;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
                return StatusCode(502, ApiResponse<object>.Fail("ASSET_NOT_FOUND",
                    $"Asset '{entry.AssetName}' not found in the latest release."));
        }
        catch (OperationCanceledException) { return StatusCode(504); }
        catch (HttpRequestException)
        {
            return StatusCode(502, ApiResponse<object>.Fail("GITHUB_UNREACHABLE",
                "Could not contact GitHub. Check the server's internet connection."));
        }

        // Download the ZIP archive
        // Must request Accept: application/octet-stream so the GitHub API redirects to
        // the actual binary rather than returning the asset metadata JSON.
        byte[] zipBytes;
        try
        {
            var github = _httpClientFactory.CreateClient("github");
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await github.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return StatusCode(502, ApiResponse<object>.Fail("DOWNLOAD_FAILED",
                    $"Failed to download the plugin archive (HTTP {(int)resp.StatusCode})."));
            zipBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) { return StatusCode(504); }
        catch (HttpRequestException)
        {
            return StatusCode(502, ApiResponse<object>.Fail("DOWNLOAD_FAILED",
                "Failed to download the plugin archive from GitHub."));
        }

        // ── SHA-256 integrity check ───────────────────────────────────────────
        // The catalog entry carries the expected digest computed from the
        // locally built and inspected ZIP.  Reject the download if it doesn't
        // match — this catches a compromised GitHub release or a MITM attack.
        if (!string.IsNullOrEmpty(entry.Sha256))
        {
            var actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(zipBytes)
            ).ToLowerInvariant();

            if (actualHash != entry.Sha256.ToLowerInvariant())
            {
                return StatusCode(502, ApiResponse<object>.Fail("HASH_MISMATCH",
                    $"Downloaded archive failed integrity check. " +
                    $"Expected SHA-256 {entry.Sha256}, got {actualHash}. " +
                    "The file may have been tampered with. Installation aborted."));
            }
        }

        // Extract to {ContentRoot}/plugins/{pluginId}/
        var pluginDir = Path.Combine(_environment.ContentRootPath, "plugins", pluginId);
        Directory.CreateDirectory(pluginDir);

        using (var zipStream = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(pluginDir, overwriteFiles: true);
        }

        // Locate the DLL (search recursively in case the ZIP has a root folder)
        var dllMatches = Directory.GetFiles(pluginDir, entry.DllName, SearchOption.AllDirectories);
        if (dllMatches.Length == 0)
            return StatusCode(502, ApiResponse<object>.Fail("DLL_NOT_FOUND",
                $"'{entry.DllName}' was not found after extracting the archive."));

        var dllPath = dllMatches[0];

        // Install via the plugin service
        try
        {
            var plugin = await _pluginService.InstallPluginAsync(dllPath);
            return Ok(ApiResponse<PluginDto>.Ok(ToDto(plugin)));
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(ApiResponse<PluginDto>.Fail("DLL_NOT_FOUND", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiResponse<PluginDto>.Fail("ALREADY_INSTALLED", ex.Message));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PluginDto ToDto(Chronicle.Core.Models.Plugin p)
    {
        // Look up the loaded plugin so we can include the iconUrl from its manifest.
        // Disabled / unloaded plugins will have IconUrl = null.
        var loaded = _registry.GetLoadedPlugins()
            .FirstOrDefault(lp => lp.DbId == p.Id);

        // The iconUrl in the DTO now points to Chronicle's own proxy endpoint,
        // not the raw external URL — this keeps the browser off external sites.
        var iconUrl = loaded?.Manifest.IconUrl is not null
            ? $"/api/v1/plugins/{p.Id}/icon"
            : null;

        return new(p.Id, p.PluginId, p.Name, p.Version, p.Author, p.Description,
            p.IsEnabled, p.InstalledAt, p.UpdatedAt, iconUrl);
    }

    /// <summary>
    /// Returns true if the first bytes of <paramref name="data"/> match any
    /// known safe raster image format's magic number.
    /// </summary>
    private static bool HasValidImageMagic(byte[] data)
    {
        if (data.Length < 4) return false;

        foreach (var (magic, _) in ImageMagicBytes)
        {
            if (data.Length < magic.Length) continue;

            var match = true;
            for (var i = 0; i < magic.Length; i++)
            {
                if (data[i] != magic[i]) { match = false; break; }
            }
            if (match) return true;
        }

        return false;
    }
}
