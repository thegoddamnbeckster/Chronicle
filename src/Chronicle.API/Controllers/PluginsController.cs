using System.IO.Compression;
using System.Text.Json;
using Chronicle.API.DTOs;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Svg.Skia;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/plugins")]
[Authorize]
public class PluginsController : ControllerBase
{
    private readonly IPluginService                _pluginService;
    private readonly IPluginRegistry               _registry;
    private readonly IPluginSettingsProtector      _protector;
    private readonly IHttpClientFactory            _httpClientFactory;
    private readonly IMemoryCache                  _cache;
    private readonly IWebHostEnvironment           _environment;
    private readonly ILogger<PluginsController>    _logger;

    // ── Icon proxy constants ───────────────────────────────────────────────────

    /// <summary>Maximum permitted favicon file size (100 KB).</summary>
    private const int MaxIconBytes = 100 * 1024;

    /// <summary>Maximum edge length (px) when rasterising an SVG favicon.</summary>
    private const int SvgRenderSize = 64;

    /// <summary>How long to cache a fetched favicon before re-fetching.</summary>
    private static readonly TimeSpan IconCacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Raster Content-Type prefixes that can be served directly after a magic-byte check.
    /// </summary>
    private static readonly string[] RasterContentTypePrefixes =
    [
        "image/x-icon",
        "image/vnd.microsoft.icon",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    ];

    /// <summary>
    /// Magic byte signatures for permitted raster image formats.
    /// Used as a second validation layer after the Content-Type check.
    /// </summary>
    private static readonly (byte[] Magic, string Name)[] ImageMagicBytes =
    [
        ([ 0xFF, 0xD8, 0xFF ],              "JPEG"),
        ([ 0x89, 0x50, 0x4E, 0x47 ],       "PNG"),   // PNG
        ([ 0x47, 0x49, 0x46, 0x38 ],       "GIF"),   // GIF8
        ([ 0x00, 0x00, 0x01, 0x00 ],       "ICO"),   // ICO
        ([ 0x52, 0x49, 0x46, 0x46 ],       "WEBP"),  // RIFF (WebP)
    ];

    private readonly IServiceScopeFactory _scopeFactory;

    public PluginsController(
        IPluginService pluginService,
        IPluginRegistry registry,
        IPluginSettingsProtector protector,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IWebHostEnvironment environment,
        ILogger<PluginsController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _pluginService     = pluginService;
        _registry          = registry;
        _protector         = protector;
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _environment       = environment;
        _logger            = logger;
        _scopeFactory      = scopeFactory;
    }

    // ── GET /api/v1/plugins/auth-failures ────────────────────────────────────

    /// <summary>
    /// Returns distinct plugins that have at least one enrichment row in the
    /// <see cref="EnrichmentStatus.AuthFailed"/> state. Used by the frontend to
    /// surface a "plugin needs credentials" alert with a link to Settings → Plugins.
    /// </summary>
    [HttpGet("auth-failures")]
    public async Task<IActionResult> GetAuthFailures(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await using var db    = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        var failedPluginIds = await db.MediaEnrichments
            .Where(e => e.Status == EnrichmentStatus.AuthFailed)
            .Select(e => e.PluginId)
            .Distinct()
            .ToListAsync(ct);

        var allPlugins = await _pluginService.GetAllPluginsAsync();
        var results = failedPluginIds
            .Select(pluginId =>
            {
                var plugin = allPlugins.FirstOrDefault(p => p.PluginId == pluginId);
                return new
                {
                    pluginId,
                    pluginName = plugin?.Name ?? pluginId,
                    dbId       = plugin?.Id,
                };
            })
            .ToList();

        return Ok(new { success = true, data = results });
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
    /// Secure favicon proxy: fetches, converts if necessary, validates, caches,
    /// and serves the plugin's icon from the URL declared in its manifest.json.
    ///
    /// Security measures:
    ///   1. The icon URL comes ONLY from the plugin's manifest (not user input).
    ///   2. SVG is accepted but converted to PNG server-side before caching —
    ///      the browser never receives raw SVG (which can embed JavaScript).
    ///   3. Raster images are validated against magic bytes to confirm the binary
    ///      matches its declared Content-Type.
    ///   4. File size is capped at 100 KB (raw download).
    ///   5. Result is cached for 24 h — the browser never contacts the external site.
    ///   6. Only HTTPS URLs are accepted (enforced via Uri.Scheme check).
    ///      Exception: data: URIs are allowed — they contain no external dependency.
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

        // data: URIs are self-contained — decode and serve without any external fetch.
        if (iconUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ServeDataUri(id, iconUrl);

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
        byte[] rawBytes;
        string rawContentType;
        try
        {
            var http = _httpClientFactory.CreateClient("favicon");
            using var response = await http.GetAsync(iconUrl, ct);

            if (!response.IsSuccessStatusCode)
                return NotFound();

            rawContentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                             ?? string.Empty;

            // Accept SVG and all standard raster formats
            var isSvg    = rawContentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase);
            var isRaster = RasterContentTypePrefixes.Any(p =>
                rawContentType.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!isSvg && !isRaster)
            {
                return StatusCode(415, ApiResponse<object>.Fail(
                    "INVALID_ICON_TYPE",
                    $"Remote server returned unsupported content type '{rawContentType}'."));
            }

            // Read body — limit to MaxIconBytes to prevent oversized payloads
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer    = new byte[MaxIconBytes + 1];
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

            rawBytes = buffer[..totalRead];
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504);
        }
        catch (HttpRequestException)
        {
            return StatusCode(502);
        }

        // If SVG: rasterise to PNG so the browser never receives executable markup.
        // The converted PNG is what gets cached and returned.
        byte[] bytes;
        string contentType;

        if (rawContentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bytes       = RasteriseSvgToPng(rawBytes, SvgRenderSize);
                contentType = "image/png";
            }
            catch (Exception)
            {
                return StatusCode(502, ApiResponse<object>.Fail(
                    "ICON_CONVERT_FAILED",
                    "SVG icon could not be rasterised to PNG."));
            }
        }
        else
        {
            bytes       = rawBytes;
            contentType = rawContentType;

            // Validate magic bytes — rejects raster files that lie about their Content-Type
            if (!HasValidImageMagic(bytes))
            {
                return StatusCode(415, ApiResponse<object>.Fail(
                    "INVALID_ICON_CONTENT",
                    "Plugin icon binary does not match any permitted image format."));
            }
        }

        // Cache and return
        _cache.Set(cacheKey, (bytes, contentType),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = IconCacheDuration });

        return File(bytes, contentType);
    }

    /// <summary>
    /// Decodes a <c>data:</c> URI and serves the embedded bytes, rasterising SVG to PNG.
    /// Format: <c>data:[&lt;mediatype&gt;][;base64],&lt;data&gt;</c>
    /// </summary>
    private IActionResult ServeDataUri(int id, string dataUri)
    {
        var cacheKey = $"plugin_icon:{id}:data";
        if (_cache.TryGetValue(cacheKey, out (byte[] Data, string ContentType) cached))
            return File(cached.Data, cached.ContentType);

        var commaIdx = dataUri.IndexOf(',');
        if (commaIdx < 0) return NotFound();

        var header  = dataUri[5..commaIdx];  // everything between "data:" and ","
        var payload = dataUri[(commaIdx + 1)..];

        string mediaType;
        byte[] rawBytes;

        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = header[..^7];  // strip ";base64"
            try   { rawBytes = Convert.FromBase64String(payload); }
            catch { return NotFound(); }
        }
        else
        {
            mediaType = header;
            rawBytes  = System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }

        byte[] bytes;
        string contentType;

        if (mediaType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bytes       = RasteriseSvgToPng(rawBytes, SvgRenderSize);
                contentType = "image/png";
            }
            catch { return StatusCode(502); }
        }
        else
        {
            if (!HasValidImageMagic(rawBytes)) return StatusCode(415);
            bytes       = rawBytes;
            contentType = mediaType;
        }

        _cache.Set(cacheKey, (bytes, contentType),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = IconCacheDuration });

        return File(bytes, contentType);
    }

    /// <summary>
    /// Rasterises an SVG document to a PNG byte array.
    /// The output is scaled to fit within <paramref name="maxEdgePx"/> × <paramref name="maxEdgePx"/>
    /// while preserving the aspect ratio.  Falls back to that square size when the SVG
    /// declares zero or negative dimensions.
    /// </summary>
    private static byte[] RasteriseSvgToPng(byte[] svgBytes, int maxEdgePx)
    {
        using var stream = new MemoryStream(svgBytes);
        using var svg    = new SKSvg();

        var picture = svg.Load(stream)
            ?? throw new InvalidOperationException("SVG could not be parsed.");

        var src    = picture.CullRect;
        var srcW   = src.Width  > 0 ? src.Width  : maxEdgePx;
        var srcH   = src.Height > 0 ? src.Height : maxEdgePx;

        // Scale down if either dimension exceeds maxEdgePx; never scale up.
        var scale  = Math.Min(1f, maxEdgePx / Math.Max(srcW, srcH));
        var dstW   = Math.Max(1, (int)(srcW * scale));
        var dstH   = Math.Max(1, (int)(srcH * scale));

        using var surface = SKSurface.Create(new SKImageInfo(dstW, dstH));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (Math.Abs(scale - 1f) > 0.0001f)
            canvas.Scale(scale, scale);

        canvas.DrawPicture(picture);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error installing plugin from {DllPath}", request.DllPath);
            return StatusCode(500, ApiResponse<PluginDto>.Fail("INSTALL_FAILED", ex.Message));
        }
    }

    // ── GET /api/v1/plugins/{id}/settings ─────────────────────────────────────

    /// <summary>Returns the current saved settings for a plugin (Admin only).</summary>
    [HttpGet("{id:int}/settings")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetSettings(int id)
    {
        var plugin = await _pluginService.GetPluginAsync(id);
        if (plugin is null)
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_FOUND", "Plugin not found."));

        var plainJson = _protector.Unprotect(plugin.SettingsJson);
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson) ?? new();

        return Ok(ApiResponse<object>.Ok(settings));
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

    // ── POST /api/v1/plugins/{pluginId}/unload ────────────────────────────────

    /// <summary>
    /// Unloads the plugin assembly from memory, releasing the file lock on its DLL.
    /// Does NOT change the enabled/disabled state in the database.
    /// Call this before overwriting the DLL on disk during a hot deploy, then call /reload.
    /// </summary>
    [HttpPost("{pluginId}/unload")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UnloadPlugin(string pluginId)
    {
        var wasLoaded = await _pluginService.UnloadFromRegistryAsync(pluginId);
        var status = wasLoaded ? "unloaded" : "already_unloaded";
        return Ok(ApiResponse<object>.Ok(new { pluginId, status }));
    }

    // ── POST /api/v1/plugins/{pluginId}/reload ────────────────────────────────

    /// <summary>
    /// Reloads the plugin from its DLL path on disk without restarting the API.
    /// Safe to call after /unload once the new DLL has been copied into place.
    /// </summary>
    [HttpPost("{pluginId}/reload")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReloadPlugin(string pluginId, CancellationToken ct)
    {
        try
        {
            await _pluginService.ReloadPluginAsync(pluginId, ct);
            return Ok(ApiResponse<object>.Ok(new { pluginId, status = "reloaded" }));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("RELOAD_FAILED", ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("DLL_NOT_FOUND", ex.Message));
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

        // Merge settings schemas from all provider types in the plugin DLL.
        // A plugin like Trakt has both IMetadataProvider (client_id only) and
        // IImportProvider (client_id + client_secret), so we must union them.
        var merged = new Dictionary<string, SettingDefinition>(StringComparer.OrdinalIgnoreCase);
        void MergeSchema(PluginSettingsSchema s)
        {
            foreach (var def in s.Settings)
                merged.TryAdd(def.Key, def);
        }

        foreach (var p in loaded.MetadataProviders)  try { MergeSchema(p.GetSettingsSchema()); } catch (Exception ex) { _logger.LogWarning(ex, "GetSettingsSchema failed for metadata provider in plugin {PluginId}", id); }
        foreach (var p in loaded.FileScannerPlugins) try { MergeSchema(p.GetSettingsSchema()); } catch (Exception ex) { _logger.LogWarning(ex, "GetSettingsSchema failed for file scanner in plugin {PluginId}", id); }
        foreach (var p in loaded.ImportProviders)    try { MergeSchema(p.GetSettingsSchema()); } catch (Exception ex) { _logger.LogWarning(ex, "GetSettingsSchema failed for import provider in plugin {PluginId}", id); }

        if (merged.Count > 0)
            return Ok(ApiResponse<object>.Ok(new PluginSettingsSchema { Settings = [.. merged.Values] }));

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
            var result = await ProviderCallGuard.CallAsync(
                t => loaded.MetadataProviders[0].SearchAsync(new MediaSearchContext(query), t),
                loaded.Manifest.PluginId, "SearchAsync", (IReadOnlyList<ScoredCandidate>)[],
                msg => _logger.LogWarning("{Msg}", msg), msg => _logger.LogError("{Msg}", msg), ct);
            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("PLUGIN_NOT_CONFIGURED", ex.Message));
        }
    }

    // ── GET /api/v1/plugins/{id}/metadata?externalId=... ──────────────────────

    /// <summary>
    /// Fetches full metadata for a specific item by its external ID.
    /// The ID format is plugin-specific (e.g. "movie:550" or "tv:1399" for TMDB), and may
    /// also be a full provider URL (e.g. a TMDB movie/tv/collection page link).
    /// Taken as a query parameter rather than a route segment because values like a full
    /// URL contain '/' and '?' themselves -- ASP.NET Core route segments don't decode an
    /// encoded slash back to '/' by default, so a URL passed as {externalId} in the path
    /// arrives at the provider mangled (e.g. "https:%2F%2F...") and fails to parse.
    /// </summary>
    [HttpGet("{id:int}/metadata")]
    public async Task<IActionResult> GetMetadata(
        int id,
        [FromQuery] string externalId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return BadRequest(ApiResponse<object>.Fail("EXTERNAL_ID_REQUIRED", "externalId is required."));

        var loaded = _registry.GetLoadedPlugins().FirstOrDefault(lp => lp.DbId == id);
        if (loaded is null)
            return NotFound(ApiResponse<object>.Fail("PLUGIN_NOT_LOADED", "Plugin not found or not loaded."));

        if (loaded.MetadataProviders.Count == 0)
            return BadRequest(ApiResponse<object>.Fail("NOT_METADATA_PROVIDER", "This plugin does not provide metadata lookup."));

        try
        {
            var result = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                t => loaded.MetadataProviders[0].GetByIdAsync(externalId, t),
                loaded.Manifest.PluginId, "GetByIdAsync", null,
                msg => _logger.LogWarning("{Msg}", msg), msg => _logger.LogError("{Msg}", msg), ct);
            if (result is null)
                return StatusCode(504, ApiResponse<object>.Fail("UPSTREAM_TIMEOUT", "The plugin did not respond in time."));
            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("PLUGIN_NOT_CONFIGURED", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_ID", ex.Message));
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
            PluginId:    "chronicle.plugin.tmdb",
            Name:        "TMDB",
            Description: "Fetches movie and TV metadata from The Movie Database (TMDB). Requires a free TMDB API key.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://www.themoviedb.org/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.TMDB",
            AssetName:   "Chronicle.Plugin.TMDB.zip",
            DllName:     "Chronicle.Plugin.TMDB.dll",
            Tags:        ["movies", "tv", "metadata"],
            Sha256:      "",     // cleared — recalculate after each plugin release
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.musicbrainz",
            Name:        "MusicBrainz",
            Description: "Fetches comprehensive music metadata from MusicBrainz (artist, album, track) and cover art from the Cover Art Archive. No API key required.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://musicbrainz.org/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.MusicBrainz",
            AssetName:   "Chronicle.Plugin.MusicBrainz.zip",
            DllName:     "Chronicle.Plugin.MusicBrainz.dll",
            Tags:        ["music", "audio", "metadata"],
            Sha256:      "dc34647a59f0974154f1d3a50bc4872143475b5be6f9af609a1b575fb755ea3b",
            Version:     "1.0.2"
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
            Sha256:      "30f7996b2b3edd47f57084c1c774aa87d137fabdee50ffd3e0a185c2bef730e9",
            Version:     "1.2.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.wikipedia",
            Name:        "Wikipedia",
            Description: "Broad fallback summaries, full article sections, and images from Wikipedia for any media type — including People. No API key required.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://en.wikipedia.org/static/apple-touch/wikipedia.png",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Wikipedia",
            AssetName:   "Chronicle.Plugin.Wikipedia.zip",
            DllName:     "Chronicle.Plugin.Wikipedia.dll",
            Tags:        ["movies", "tv", "music", "books", "games", "people", "metadata"],
            Sha256:      "98b9943b24cad8bfd0c60bfd8cec2e2398d0cc99b23461b89fbc4255a7197fce",
            Version:     "1.0.0"
        ),
        // The five plugins below this comment (FanEdit, Simkl, FanartTV, Themes.Default --
        // and Kodi.NFO once it has a release, see docs/plans/2026-09-02-kodi-nfo-plugin-design.md)
        // were added 2026-09-02 after discovering this array was badly stale: CLAUDE.md lists
        // 12 plugins as of v0.7.0 and this catalog only had 4. Confirmed directly against each
        // repo's actual latest GitHub release (tag + attached asset name + SHA-256 digest) before
        // adding -- not guessed. Still missing from this catalog and deliberately NOT added:
        // TheTVDB and TVMaze have no release at all -- adding entries for those would put a
        // broken "Install" button in the UI. Create their releases first, then add entries the
        // same way.
        //
        // MoviesRemastered, Trakt, and Hardcover (below) had the same problem -- a GitHub release
        // with no zip asset attached -- as of 2026-09-02. Built, tested, and packaged locally from
        // each repo's current buildable code, then handed the zips to the repo owner to attach as
        // release assets (this session cannot push tags or create/attach GitHub releases -- see
        // the Kodi.NFO precedent). AssetName/Sha256/Version below match those handed-off zips
        // exactly, so installs will work as soon as the matching asset lands on the matching
        // release tag. Trakt's zip is v1.2.0 (built from HEAD) even though its last tagged release
        // is v1.1.0 -- that old tag's code no longer compiles against current
        // Chronicle.Plugins.Models (CastMember/CrewMember refactor), so v1.1.0 is not a buildable
        // target anymore. Hardcover has the same drift (release v1.1.3 vs HEAD v1.2.0); its
        // manifest.json's plugin_id is "hardcover", not "chronicle.plugin.hardcover" -- that's the
        // authoritative id per Chronicle's plugin-loading convention. If a new release tag
        // (v1.2.0/v1.1.0) is created instead of reusing the old one, update GithubRepo's implied
        // "latest release" lookup needs no change (it always resolves "latest"), but re-verify the
        // asset name and SHA-256 still match.
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.moviesremastered",
            Name:        "Movies Remastered (MRDb)",
            Description: "Fetches fan edit metadata from the Movies Remastered Database (moviesremastered.com / MRDb), a community fanedit archive. No account required. Please use responsibly — a minimum 1-second delay between requests is enforced.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAyNCAyNCc+PHJlY3Qgd2lkdGg9JzI0JyBoZWlnaHQ9JzI0JyByeD0nMycgZmlsbD0nI0ZGMDAwMCcvPjxwYXRoIGQ9J001IDhoMnYySDV6bTEyIDBoMnYyaC0yek01IDE0aDJ2Mkg1em0xMiAwaDJ2MmgtMnonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjYnLz48cGF0aCBkPSdNOCA2aDh2MTJIOHonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjknLz48cGF0aCBkPSdNMTAgOWg0TTEwIDEyaDRNMTAgMTVoMycgc3Ryb2tlPScjRkYwMDAwJyBzdHJva2Utd2lkdGg9JzEuMicgc3Ryb2tlLWxpbmVjYXA9J3JvdW5kJy8+PC9zdmc+",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.MoviesRemastered",
            AssetName:   "chronicle.plugin.moviesremastered-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.MoviesRemastered.dll",
            Tags:        ["movies", "fanedits", "metadata"],
            Sha256:      "8d647f5a2496ae322514ab102e1b417a3807eb2182142bbff4365eff2852c87f",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.trakt",
            Name:        "Trakt",
            Description: "Import watch history, ratings, watchlist, and in-progress playback position from Trakt.tv into Chronicle. Requires a Trakt API application (Settings → Your API Apps on trakt.tv) — as of 2026, creating one requires a paid Trakt VIP membership, so a free account cannot obtain a client_id at all.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://trakt.tv/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Trakt",
            AssetName:   "chronicle.plugin.trakt-v1.2.0.zip",
            DllName:     "Chronicle.Plugin.Trakt.dll",
            Tags:        ["movies", "tv", "scrobbling", "sync"],
            Sha256:      "9cb0b48d53be17d127402051ac8bad442f02055ecac23ec8cda27d5b0ed172a8",
            Version:     "1.2.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "hardcover",
            Name:        "Hardcover",
            Description: "Book and audiobook metadata from Hardcover.app, plus reading history import.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAyNCAyNCc+PHJlY3QgeD0nMycgeT0nMycgd2lkdGg9JzE4JyBoZWlnaHQ9JzE4JyByeD0nMicgZmlsbD0nIzdjM2FlZCcvPjxwYXRoIGZpbGw9J3doaXRlJyBkPSdNNyA3aDEwdjJIN3ptMCA0aDEwdjJIN3ptMCA0aDd2Mkg3eicvPjwvc3ZnPg==",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Hardcover",
            AssetName:   "chronicle.plugin.hardcover-v1.2.0.zip",
            DllName:     "Chronicle.Plugin.Hardcover.dll",
            Tags:        ["books", "audiobooks", "metadata", "sync"],
            Sha256:      "717ee1b81c78a896cda72c1ce8c7692ac2f667e0871290c098d7b0c12c635fa2",
            Version:     "1.2.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.fanedit",
            Name:        "FanEdit",
            Description: "Fetches fanedit metadata from the Internet Fan Edit Database (fanedit.org). Requires a registered fanedit.org account. Please use responsibly — a minimum 1-second delay between requests is enforced.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAyNCAyNCc+PHJlY3Qgd2lkdGg9JzI0JyBoZWlnaHQ9JzI0JyByeD0nMycgZmlsbD0nI2MyNDEwYycvPjxwYXRoIGQ9J001IDhoMnYySDV6bTEyIDBoMnYyaC0yek01IDE0aDJ2Mkg1em0xMiAwaDJ2MmgtMnonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjYnLz48cGF0aCBkPSdNOCA2aDh2MTJIOHonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjknLz48cGF0aCBkPSdNMTAgOWg0TTEwIDEyaDRNMTAgMTVoMycgc3Ryb2tlPScjYzI0MTBjJyBzdHJva2Utd2lkdGg9JzEuMicgc3Ryb2tlLWxpbmVjYXA9J3JvdW5kJy8+PC9zdmc+",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.FanEdit",
            AssetName:   "chronicle.plugin.fanedit-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.FanEdit.dll",
            Tags:        ["movies", "fanedits", "metadata"],
            Sha256:      "eb559c681d9f2fd5edddc8981fbea5a106bd4931f713d221aff703b039a44117",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.simkl",
            Name:        "SIMKL",
            Description: "Metadata for Movies, TV, and Anime from SIMKL. Requires a free SIMKL API Client ID.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://simkl.com/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Simkl",
            AssetName:   "chronicle.plugin.simkl-v1.1.0.zip",
            DllName:     "Chronicle.Plugin.Simkl.dll",
            Tags:        ["movies", "tv", "anime", "metadata"],
            Sha256:      "0be5da25f81a5a58cc42a0a0c6574a5070e5b1006448a8720046c3aab0535869",
            // The latest GitHub release (the version actually installable through this catalog)
            // is v1.1.0 -- the repo's own HEAD manifest.json has moved on to 1.4.0 since, but
            // that newer code has no attached release asset yet. Bump this once it does.
            Version:     "1.1.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.fanarttv",
            Name:        "Fanart.tv",
            Description: "Fetches high-quality artwork from Fanart.tv — posters, backgrounds, logos, disc art, clearart, and banners for movies, TV, and music. Requires a free Fanart.tv API key.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://fanart.tv/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.FanartTV",
            AssetName:   "chronicle.plugin.fanarttv-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.FanartTV.dll",
            Tags:        ["movies", "tv", "music", "artwork", "metadata"],
            Sha256:      "449ebf8b1905d349dcc51bcb8e2708267d6e53e006b7001ed63d6e15d3fce532",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.themes.default",
            Name:        "Default Themes",
            Description: "Provides the four built-in Chronicle themes: Light, Dark, Navy & Pink, and Dark Teal. Install additional theme plugins to expand the available theme list.",
            Author:      "Chronicle",
            IconUrl:     null,
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Themes.Default",
            AssetName:   "chronicle.plugin.themes.default-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.Themes.Default.dll",
            Tags:        ["themes", "ui"],
            Sha256:      "1bdf6ae1c4a109c946629baf7787aef8b3d9555127888d0182cb9b6b58cf7079",
            Version:     "1.0.0"
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error installing catalog plugin {PluginId}", pluginId);
            return StatusCode(500, ApiResponse<PluginDto>.Fail("INSTALL_FAILED", ex.Message));
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

        var fixMatchHint = loaded?.Manifest.FixMatchHint ?? p.FixMatchHint;

        var supportedMediaTypes = loaded?.MetadataProviders
            .SelectMany(mp => mp.GetSupportedMediaTypes())
            .Select(t => t.MediaTypeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return new(p.Id, p.PluginId, p.Name, p.Version, p.Author, p.Description,
            p.IsEnabled, p.InstalledAt, p.UpdatedAt, iconUrl, fixMatchHint, supportedMediaTypes);
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
