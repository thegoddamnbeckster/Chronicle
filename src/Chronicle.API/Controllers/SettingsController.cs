using Chronicle.Core.Models;
using Chronicle.Data;
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

    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry    _pluginRegistry;

    // Keys follow the pattern "{dbTypeName}" for flat types and "{dbTypeName}.{levelIndex}" for
    // hierarchical types.  Level indices match MediaItem.HierarchyLevel (0 = root, 1 = child, …).
    // The compound-key BaseType() helper below extracts the DB type name for plugin matching.
    private static readonly Dictionary<string, string[]> AssignableFields = new()
    {
        // ── Flat types (HierarchyLevels = 1) ─────────────────────────────────────
        ["movies"]        = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "directors", "tags"],
        ["fanedits"]      = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "directors", "tags"],
        ["books"]         = ["title", "overview", "year", "poster_url", "rating", "genres", "tags"],
        ["audiobooks"]    = ["title", "overview", "year", "poster_url", "runtime_minutes", "rating", "genres", "tags"],

        // ── TV  (HierarchyLevels = 3): Show → Season → Episode ───────────────────
        ["tv"]            = ["title", "overview", "year", "poster_url", "backdrop_url", "rating", "genres", "cast", "directors", "tags"],
        ["tv.1"]          = ["title", "overview", "year", "poster_url", "backdrop_url", "tags"],
        ["tv.2"]          = ["title", "overview", "year", "runtime_minutes", "tags"],

        // ── Music (HierarchyLevels = 3): Artist → Album → Track ──────────────────
        ["music"]         = ["title", "overview", "poster_url", "rating", "genres", "tags"],
        ["music.1"]       = ["title", "overview", "year", "poster_url", "rating", "genres", "tags"],
        ["music.2"]       = ["title", "year", "runtime_minutes", "tags"],
    };

    /// <summary>
    /// Extracts the base DB type name from a compound assignment key.
    /// "tv.1" → "tv", "music.2" → "music", "movies" → "movies".
    /// </summary>
    private static string BaseType(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot];
    }

    public SettingsController(ChronicleDbContext db, IPluginRegistry pluginRegistry)
    {
        _db             = db;
        _pluginRegistry = pluginRegistry;
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

        // Load DB plugin records to map PluginId → DB id for the proxy URL
        var dbPlugins = await _db.Plugins.ToListAsync();

        var allEntries = _pluginRegistry.GetMetadataProviderEntries().ToList();

        // Build per-media-type plugin lists.
        // Compound keys like "tv.1" or "music.2" match plugins that declare support for the base
        // type ("tv", "music").  "movies"/"fanedits" normalise to "movie"/"fanedit" for TMDB-style
        // plugin declarations that use the singular form.
        static string NormalizeForAssignment(string name) => name.ToLowerInvariant() switch
        {
            "movies"   => "movie",
            "fanedits" => "fanedits",   // TMDB declares "fanedits" exactly
            var n      => n,
        };

        var availablePlugins = AssignableFields.Keys.ToDictionary(
            assignmentKey => assignmentKey,
            assignmentKey =>
            {
                // For compound keys ("tv.1") match plugins supporting the base type ("tv").
                var baseTypeName = NormalizeForAssignment(BaseType(assignmentKey));
                return allEntries
                    .Where(e => e.Provider.GetSupportedMediaTypes()
                        .Any(t => string.Equals(
                            NormalizeForAssignment(t.MediaTypeName), baseTypeName,
                            StringComparison.OrdinalIgnoreCase)))
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

        // Build display name map.
        // Flat keys: use the DB MediaType.DisplayName ("fanedits" → "Fan Edits").
        // Compound keys: use the DB type's HierarchyLabels to get the level name, then
        //   format as "<TypeDisplay> <LevelLabel>s" (e.g. "tv.1" → "TV Seasons").
        var dbMediaTypes = await _db.Set<Chronicle.Core.Models.MediaType>().ToListAsync();
        var mediaTypeDisplayNames = AssignableFields.Keys.ToDictionary(
            k => k,
            k =>
            {
                var dot = k.IndexOf('.');
                if (dot < 0)
                {
                    // Flat key — look up DB display name.
                    return dbMediaTypes
                        .FirstOrDefault(t => string.Equals(t.Name, k, StringComparison.OrdinalIgnoreCase))
                        ?.DisplayName
                        ?? (k.Length > 0 ? char.ToUpper(k[0]) + k[1..] : k);
                }

                // Compound key — resolve base type and level index.
                var baseName  = k[..dot];
                var levelIdx  = int.TryParse(k[(dot + 1)..], out var li) ? li : 0;
                var dbType    = dbMediaTypes.FirstOrDefault(t =>
                    string.Equals(t.Name, baseName, StringComparison.OrdinalIgnoreCase));
                var labels    = dbType?.HierarchyLabels?.Split(',') ?? [];
                var baseDisplay = dbType?.DisplayName ?? char.ToUpper(baseName[0]) + baseName[1..];
                var levelLabel = levelIdx < labels.Length ? labels[levelIdx].Trim() : $"Level {levelIdx}";
                return $"{baseDisplay} {levelLabel}s"; // e.g. "TV Seasons", "Music Albums"
            });

        return Ok(new
        {
            success = true,
            data = new
            {
                assignments,
                assignableFields = AssignableFields,
                availablePlugins,
                mediaTypeDisplayNames,
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

        foreach (var (mediaType, fields) in request.Assignments)
        {
            if (!AssignableFields.TryGetValue(mediaType, out var allowedFields))
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
        return Ok(new { success = true });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            var elapsed = DateTime.Now - processes[0].StartTime;
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

public record ServiceStatusDto(
    bool IsInstalled,
    string Status,
    string StartType,
    string Account,
    string? Uptime
);
