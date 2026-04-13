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

    private static readonly Dictionary<string, string[]> AssignableFields = new()
    {
        ["movies"]     = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "directors", "tags"],
        ["tv"]         = ["title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "directors", "tags"],
        ["music"]      = ["title", "overview", "poster_url", "rating", "genres", "tags"],
        ["albums"]     = ["title", "overview", "year", "poster_url", "rating", "genres", "tags"],
        ["tracks"]     = ["title", "year", "runtime_minutes", "tags"],
        ["books"]      = ["title", "overview", "year", "poster_url", "rating", "genres", "tags"],
        ["audiobooks"] = ["title", "overview", "year", "poster_url", "runtime_minutes", "rating", "genres", "tags"],
    };

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

        // Build per-media-type plugin lists, filtering by each plugin's declared supported types.
        // "movies" normalises to "movie" to match how TMDB (and similar) plugins declare support.
        static string NormalizeForAssignment(string name) =>
            name.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : name.ToLowerInvariant();

        var availablePlugins = AssignableFields.Keys.ToDictionary(
            mediaType => mediaType,
            mediaType =>
            {
                var normalised = NormalizeForAssignment(mediaType);
                return allEntries
                    .Where(e => e.Provider.GetSupportedMediaTypes()
                        .Any(t => string.Equals(
                            NormalizeForAssignment(t.MediaTypeName), normalised,
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

        return Ok(new
        {
            success = true,
            data = new
            {
                assignments,
                assignableFields = AssignableFields,
                availablePlugins,
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
