using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using System.ComponentModel.DataAnnotations;
using System.ServiceProcess;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private const string ServiceName = "Chronicle";

    private readonly ChronicleDbContext _db;

    public SettingsController(ChronicleDbContext db)
    {
        _db = db;
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

public record ServiceStatusDto(
    bool IsInstalled,
    string Status,
    string StartType,
    string Account,
    string? Uptime
);
