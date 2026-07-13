using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using System.ComponentModel.DataAnnotations;
using System.ServiceProcess;
using System.Text.Json;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private const string ServiceName = "Chronicle";

    private readonly ChronicleDbContext          _db;
    private readonly IPluginRegistry             _pluginRegistry;
    private readonly AssignmentConfigCache       _assignmentCache;
    private readonly FieldAliasCache             _fieldAliasCache;
    private readonly IMetadataResolutionService  _resolutionService;
    private readonly ILogger<SettingsController> _logger;

    // NormalizeMediaTypeName: maps DB type names to the canonical form used by plugin declarations.
    // "movies" (DB plural) ↔ "movie" (TMDB/enrichment canonical).
    private static string NormalizeMediaTypeName(string name) =>
        name.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : name.ToLowerInvariant();

    // Parent-type relationships: providers that support the parent are also offered for the sub-type.
    // e.g. Trakt supports "tv" → it should appear for "anime" assignments too.
    private static readonly Dictionary<string, string> TypeParentMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anime"]    = "tv",
            ["fanedits"] = "movie",
        };

    public SettingsController(
        ChronicleDbContext db,
        IPluginRegistry pluginRegistry,
        AssignmentConfigCache assignmentCache,
        FieldAliasCache fieldAliasCache,
        IMetadataResolutionService resolutionService,
        ILogger<SettingsController> logger)
    {
        _db                = db;
        _pluginRegistry    = pluginRegistry;
        _assignmentCache   = assignmentCache;
        _fieldAliasCache   = fieldAliasCache;
        _resolutionService = resolutionService;
        _logger            = logger;
    }

    // ── App settings (key/value store) ───────────────────────────────────────

    /// <summary>Returns all app settings as a key/value dictionary.</summary>
    [HttpGet("app")]
    public async Task<IActionResult> GetAppSettings()
    {
        var settings = await _db.AppSettings.ToListAsync();
        var dict = settings.ToDictionary(s => s.Key, s => s.Value);
        return Ok(dict);
    }

    /// <summary>Creates or updates a single app setting. Admin only.</summary>
    [HttpPut("app/{key}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PutAppSetting(string key, [FromBody] AppSettingUpdateRequest body)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting is null)
            _db.AppSettings.Add(new AppSetting { Key = key, Value = body.Value });
        else
            setting.Value = body.Value;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Returns the current Windows service status for Chronicle.
    /// When running in development (service not installed) returns isInstalled: false.
    /// </summary>
    [HttpGet("service")]
    public IActionResult GetServiceStatus()
    {
        if (!OperatingSystem.IsWindows())
            return Ok(new ServiceStatusDto(false, "NotAvailable", "N/A", "N/A", null));

        try
        {
            using var sc = new ServiceController(ServiceName);

            // Reading Status will throw InvalidOperationException if not installed.
            var status = sc.Status.ToString();
            var startType = sc.StartType.ToString();
            var account = GetServiceAccount(ServiceName);
            var uptime = sc.Status == ServiceControllerStatus.Running
                ? GetServiceUptime(ServiceName)
                : null;

            return Ok(new ServiceStatusDto(true, status, startType, account, uptime));
        }
        catch (InvalidOperationException)
        {
            return Ok(new ServiceStatusDto(false, "NotInstalled", "N/A", "N/A", null));
        }
    }

    /// <summary>
    /// Generates the PowerShell command to change the service account.
    /// Chronicle cannot change its own account at runtime (Windows restriction),
    /// so the UI shows the command for the user to run as admin.
    /// </summary>
    [HttpGet("service/change-account-command")]
    public IActionResult GetChangeAccountCommand([FromQuery] string accountType, [FromQuery] string? username)
    {
        var cmd = accountType switch
        {
            "LocalService"    => $"sc.exe config {ServiceName} obj= \"NT AUTHORITY\\LocalService\" password= \"\"",
            "NetworkService"  => $"sc.exe config {ServiceName} obj= \"NT AUTHORITY\\NetworkService\" password= \"\"",
            "LocalSystem"     => $"sc.exe config {ServiceName} obj= LocalSystem password= \"\"",
            "Custom"          => username is null
                                    ? null
                                    : $"sc.exe config {ServiceName} obj= \"{username}\" password= \"YOUR_PASSWORD_HERE\"",
            _ => null
        };

        if (cmd is null)
            return BadRequest(new { error = "Invalid account type or missing username." });

        return Ok(new { command = cmd });
    }

    // ── Metadata assignment ───────────────────────────────────────────────────

    /// <summary>Returns current metadata assignment config, available plugins, and assignable fields per media type.</summary>
    [HttpGet("metadata-assignment")]
    public async Task<IActionResult> GetMetadataAssignment()
    {
        var setting = await _db.AppSettings.FindAsync("metadata_assignment.config");
        Dictionary<string, Dictionary<string, string[]>> assignments;

        if (setting?.Value is not null)
            assignments = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(setting.Value)
                          ?? new();
        else
            assignments = new();

        var assignableFields = await BuildAssignableFieldsAsync();

        // Load DB plugin records to map PluginId → DB id for the proxy URL.
        var dbPlugins  = await _db.Plugins.ToListAsync();
        var allEntries = _pluginRegistry.GetMetadataProviderEntries().ToList();

        // Build per-field plugin lists first — computed before availablePlugins so we can
        // use it to filter which plugins appear in the Display Order row.
        //
        // For each (mediaTypeKey, field), which plugins actually declare support for that field
        // via their SupportedFields / LevelFields?  A plugin with no SupportedFields declaration
        // for a type is treated as supporting every field (generic / legacy provider).  A plugin
        // that explicitly lists SupportedFields is held to that list.
        // Shape: mediaTypeKey → fieldName → pluginId[]
        var fieldPlugins = assignableFields.Keys.ToDictionary(
            assignmentKey => assignmentKey,
            assignmentKey =>
            {
                var dot          = assignmentKey.IndexOf('.');
                var baseTypeName = NormalizeMediaTypeName(dot < 0 ? assignmentKey : assignmentKey[..dot]);
                var level        = dot < 0 ? 0 : (int.TryParse(assignmentKey[(dot + 1)..], out var li) ? li : 0);

                TypeParentMap.TryGetValue(baseTypeName, out var parentTypeName);

                return assignableFields[assignmentKey].ToDictionary(
                    field => field,
                    field => allEntries
                        .Where(e =>
                        {
                            // Find the matching MediaTypeSupport for this plugin + media type.
                            var support = e.Provider.GetSupportedMediaTypes()
                                .FirstOrDefault(s =>
                                {
                                    var tn = NormalizeMediaTypeName(s.MediaTypeName);
                                    return string.Equals(tn, baseTypeName, StringComparison.OrdinalIgnoreCase)
                                        || (parentTypeName != null && string.Equals(tn, parentTypeName, StringComparison.OrdinalIgnoreCase));
                                });

                            if (support == null) return false;

                            // If the plugin declares no SupportedFields at all, treat it as
                            // supporting every field (generic / legacy provider).
                            var rootFields = support.SupportedFields;
                            if (rootFields == null || rootFields.Count == 0) return true;

                            // For sub-levels: if the plugin opted into per-level declarations
                            // (LevelFields is non-null), a missing entry means "no fields at this
                            // level" — do NOT fall back to root fields, or artwork-only plugins
                            // (e.g. Fanart.tv with LevelFields only for seasons) would incorrectly
                            // appear for episode/track levels.  Only fall back to root when the
                            // plugin has no LevelFields dict at all (legacy/generic provider).
                            IEnumerable<string> effectiveFields;
                            if (level > 0)
                            {
                                effectiveFields = support.LevelFields != null
                                    ? (support.LevelFields.GetValueOrDefault(level) ?? (IEnumerable<string>)[])
                                    : rootFields;
                            }
                            else
                            {
                                effectiveFields = rootFields;
                            }

                            return effectiveFields.Contains(field, StringComparer.OrdinalIgnoreCase);
                        })
                        .Select(e => e.PluginId)
                        .ToList());
            });

        // Build per-type plugin lists for the Display Order row.
        // A plugin only appears here if it contributes to at least one assignable field at this
        // specific level — this prevents artwork-only plugins (e.g. Fanart.tv) from cluttering
        // the display order for levels where every field is text-only (e.g. TV/anime episodes).
        var availablePlugins = assignableFields.Keys.ToDictionary(
            assignmentKey => assignmentKey,
            assignmentKey =>
            {
                // Collect the set of plugin IDs that appear in at least one field at this level.
                var pluginsWithAnyField = fieldPlugins[assignmentKey]
                    .Values
                    .SelectMany(ids => ids)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return allEntries
                    .Where(e => pluginsWithAnyField.Contains(e.PluginId))
                    .Select(e =>
                    {
                        var dbPlugin = dbPlugins.FirstOrDefault(p =>
                            string.Equals(p.PluginId, e.PluginId, StringComparison.OrdinalIgnoreCase));
                        var iconUrl = dbPlugin != null && e.IconUrl != null
                            ? $"/api/v1/plugins/{dbPlugin.Id}/icon"
                            : (string?)null;
                        return new { pluginId = e.PluginId, name = e.Provider.Name, iconUrl };
                    })
                    .ToList<object>();
            });

        // Build display name map from DB MediaType rows.
        // Flat keys: use the DB type's DisplayName.
        // Compound keys: "<TypeDisplay> <LevelLabel>s", e.g. "TV Seasons", "Music Albums".
        var dbMediaTypes = await _db.Set<Chronicle.Core.Models.MediaType>().ToListAsync();
        var mediaTypeDisplayNames = assignableFields.Keys.ToDictionary(
            k => k,
            k =>
            {
                var dot = k.IndexOf('.');
                if (dot < 0)
                {
                    return dbMediaTypes
                        .FirstOrDefault(t => string.Equals(t.Name, k, StringComparison.OrdinalIgnoreCase))
                        ?.DisplayName
                        ?? (k.Length > 0 ? char.ToUpper(k[0]) + k[1..] : k);
                }

                var baseName    = k[..dot];
                var levelIdx    = int.TryParse(k[(dot + 1)..], out var li) ? li : 0;
                var dbType      = dbMediaTypes.FirstOrDefault(t =>
                    string.Equals(t.Name, baseName, StringComparison.OrdinalIgnoreCase));
                var labels      = dbType?.HierarchyLabels?.Split(',') ?? [];
                var baseDisplay = dbType?.DisplayName ?? char.ToUpper(baseName[0]) + baseName[1..];
                var levelLabel  = levelIdx < labels.Length ? labels[levelIdx].Trim() : $"Level {levelIdx}";
                // Avoid double-s on words that are already plural (e.g. "Series" → "Series", not "Seriess")
                var plural = levelLabel.EndsWith('s') ? levelLabel : levelLabel + "s";
                return $"{baseDisplay} {plural}";
            });

        // Load plugin display order (stored separately so it can be changed without
        // touching field assignments).
        var displayOrderSetting = await _db.AppSettings.FindAsync("plugin_display_order.config");
        var displayOrder = displayOrderSetting?.Value is not null
            ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(displayOrderSetting.Value)
              ?? new Dictionary<string, string[]>()
            : new Dictionary<string, string[]>();

        return Ok(new
        {
            success = true,
            data = new
            {
                assignments,
                assignableFields,
                availablePlugins,
                mediaTypeDisplayNames,
                displayOrder,
                fieldPlugins,
            },
        });
    }

    /// <summary>Saves metadata assignment config to the app_settings table. Admin only.</summary>
    [HttpPut("metadata-assignment")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PutMetadataAssignment([FromBody] MetadataAssignmentRequest request)
    {
        if (request.Assignments is null)
            return BadRequest(new { success = false, error = new { message = "assignments required" } });

        var assignableFields = await BuildAssignableFieldsAsync();

        foreach (var (mediaType, fields) in request.Assignments)
        {
            if (!assignableFields.TryGetValue(mediaType, out var allowedFields))
                return BadRequest(new { success = false, error = new { message = $"Unknown media type: {mediaType}" } });

            foreach (var field in fields.Keys)
            {
                if (!allowedFields.Contains(field))
                    return BadRequest(new { success = false, error = new { message = $"Field '{field}' is not assignable for media type '{mediaType}'" } });
            }
        }

        var json = JsonSerializer.Serialize(request.Assignments);
        var existing = await _db.AppSettings.FindAsync("metadata_assignment.config");

        if (existing is null)
            _db.AppSettings.Add(new AppSetting { Key = "metadata_assignment.config", Value = json });
        else
            existing.Value = json;

        await _db.SaveChangesAsync();

        // Invalidate the in-memory cache so the next enrichment picks up the new config immediately.
        _assignmentCache.Invalidate();

        // Determine which base media types are affected and trigger a background bulk recompute.
        // This re-walks all stored plugin data — no network calls, just a JSON re-pass.
        var changedTypes = request.Assignments.Keys
            .Select(k => k.Contains('.') ? k[..k.LastIndexOf('.')] : k)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _ = Task.Run(async () =>
        {
            foreach (var mediaType in changedTypes)
            {
                try   { await _resolutionService.ResolveAllForMediaTypeAsync(mediaType); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Background _resolved recompute failed for media type '{Type}'", mediaType);
                }
            }
        }, CancellationToken.None);

        return Ok(new { success = true });
    }

    // ── Field-name aliasing ─────────────────────────────────────────────────
    // Extra alias JSON key names per canonical resolution field (e.g. "label" also
    // matching "recordLabel"/"publisher" in some plugin's blob) — admin-configurable so
    // plugin-naming differences can be corrected without a code change. The canonical field
    // SET itself (MetadataResolutionService.FieldMap.Keys) stays code-defined; this only
    // supplies additional alias names layered on top at resolve time.

    /// <summary>Returns the current extra-alias config plus the full set of canonical fields it can apply to.</summary>
    [HttpGet("field-aliases")]
    public async Task<IActionResult> GetFieldAliases(CancellationToken ct)
    {
        var aliases = await _fieldAliasCache.GetAllAsync(ct);
        var canonicalFields = _resolutionService.GetCanonicalFields().ToList();

        return Ok(new
        {
            success = true,
            data = new { aliases, canonicalFields },
        });
    }

    /// <summary>Saves the extra-alias config. Admin only.</summary>
    [HttpPut("field-aliases")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PutFieldAliases([FromBody] FieldAliasesRequest request)
    {
        if (request.Aliases is null)
            return BadRequest(new { success = false, error = new { message = "aliases required" } });

        var canonicalFields = _resolutionService.GetCanonicalFields();
        var cleaned = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (field, values) in request.Aliases)
        {
            if (!canonicalFields.Contains(field))
                return BadRequest(new { success = false, error = new { message = $"Unknown canonical field: {field}" } });

            var deduped = (values ?? [])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (deduped.Count > 0)
                cleaned[field] = deduped;
        }

        var json = JsonSerializer.Serialize(cleaned);
        var existing = await _db.AppSettings.FindAsync("metadata_field_aliases.config");

        if (existing is null)
            _db.AppSettings.Add(new AppSetting { Key = "metadata_field_aliases.config", Value = json });
        else
            existing.Value = json;

        await _db.SaveChangesAsync();

        // Invalidate so the next resolve picks up the new config immediately.
        _fieldAliasCache.Invalidate();

        // Fetch the active type list now, while the request's DbContext is still guaranteed
        // alive — the background loop below only calls ResolveAllForMediaTypeAsync, which
        // re-scopes its own DbContext internally, so it stays safe to run after this request
        // has already responded (see PutMetadataAssignment above for the same pattern). The
        // controller's own _db must not be touched inside that Task.Run, though — its scope
        // is disposed as soon as the response is sent.
        var activeTypeNames = await _db.MediaTypes
            .Where(t => t.IsActive)
            .Select(t => t.Name)
            .ToListAsync();

        // Aliases are global (not per media type) — an alias change can affect any type, so
        // recompute _resolved across every active type in the background.
        _ = Task.Run(async () =>
        {
            foreach (var mediaType in activeTypeNames)
            {
                try   { await _resolutionService.ResolveAllForMediaTypeAsync(mediaType); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Background _resolved recompute failed for media type '{Type}' after field-alias change", mediaType);
                }
            }
        }, CancellationToken.None);

        return Ok(new { success = true });
    }

    // ── Plugin display order ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the saved plugin display order per media type.
    /// Keys are DB media type names ("movies", "audiobooks", …); values are ordered plugin-ID arrays.
    /// </summary>
    [HttpGet("plugin-display-order")]
    public async Task<IActionResult> GetPluginDisplayOrder()
    {
        var setting = await _db.AppSettings.FindAsync("plugin_display_order.config");
        if (setting?.Value is null)
            return Ok(new Dictionary<string, string[]>());

        var order = JsonSerializer.Deserialize<Dictionary<string, string[]>>(setting.Value)
                    ?? new Dictionary<string, string[]>();
        return Ok(order);
    }

    /// <summary>Saves plugin display order per media type. Admin only.</summary>
    [HttpPut("plugin-display-order")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PutPluginDisplayOrder(
        [FromBody] Dictionary<string, string[]> order)
    {
        var json     = JsonSerializer.Serialize(order);
        var existing = await _db.AppSettings.FindAsync("plugin_display_order.config");

        if (existing is null)
            _db.AppSettings.Add(new AppSetting { Key = "plugin_display_order.config", Value = json });
        else
            existing.Value = json;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the assignable-fields dictionary dynamically from the DB's active media types
    /// and the fields declared by all loaded metadata providers.
    ///
    /// Keys follow "{dbTypeName}" for root levels and "{dbTypeName}.{levelIndex}" for
    /// sub-levels of hierarchical types (TV seasons = "tv.1", music albums = "music.1", …).
    /// Values are the union of <see cref="MediaTypeSupport.SupportedFields"/> /
    /// <see cref="MediaTypeSupport.LevelFields"/> across all plugins, plus "tags" which is
    /// always assignable.  If no plugin declares fields for a level, sensible defaults are used.
    /// </summary>
    private async Task<Dictionary<string, string[]>> BuildAssignableFieldsAsync()
    {
        var dbTypes = await _db.Set<Chronicle.Core.Models.MediaType>()
            .Where(t => t.IsActive)
            .OrderBy(t => t.DisplayName)
            .ToListAsync();

        var allEntries = _pluginRegistry.GetMetadataProviderEntries().ToList();

        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var dbType in dbTypes)
        {
            var dbNorm = NormalizeMediaTypeName(dbType.Name);

            // Collect all MediaTypeSupport entries from plugins that match this DB type.
            var matchingSupport = allEntries
                .SelectMany(e => e.Provider.GetSupportedMediaTypes()
                    .Where(s => string.Equals(
                        NormalizeMediaTypeName(s.MediaTypeName), dbNorm,
                        StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Root level (level 0) fields.
            var rootFields = matchingSupport
                .SelectMany(s => s.SupportedFields)
                .Append("tags")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f)
                .ToArray();

            result[dbType.Name] = rootFields.Length > 1
                ? rootFields
                : DefaultFieldsForLevel(0);

            // Sub-level keys for hierarchical types (tv.1, tv.2, music.1, …).
            for (var level = 1; level < dbType.HierarchyLevels; level++)
            {
                var levelKey = $"{dbType.Name}.{level}";
                var capturedLevel = level;

                var levelFields = matchingSupport
                    .SelectMany(s => s.LevelFields?.GetValueOrDefault(capturedLevel) ?? [])
                    .Append("tags")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f)
                    .ToArray();

                result[levelKey] = levelFields.Length > 1
                    ? levelFields
                    : DefaultFieldsForLevel(level);
            }
        }

        return result;
    }

    private static string[] DefaultFieldsForLevel(int level) => level switch
    {
        0 => ["backdrop_url", "cast", "directors", "genres", "overview", "poster_url", "rating", "runtime_minutes", "tags", "title", "year"],
        1 => ["backdrop_url", "overview", "poster_url", "tags", "title", "year"],
        _ => ["overview", "runtime_minutes", "tags", "title", "year"],
    };

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string GetServiceAccount(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("ObjectName") as string ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? GetServiceUptime(string serviceName)
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Chronicle.API");
            if (processes.Length == 0) return null;
            var elapsed = DateTime.UtcNow - processes[0].StartTime.ToUniversalTime();
            return FormatUptime(elapsed);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }
}

public record AppSettingUpdateRequest([Required] string Value);

public record MetadataAssignmentRequest(
    [Required] Dictionary<string, Dictionary<string, string[]>>? Assignments
);

public record FieldAliasesRequest(
    [Required] Dictionary<string, List<string>>? Aliases
);

public record ServiceStatusDto(
    bool IsInstalled,
    string Status,
    string StartType,
    string Account,
    string? Uptime
);
