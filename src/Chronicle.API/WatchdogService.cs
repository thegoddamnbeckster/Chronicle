using System.Diagnostics;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace Chronicle.API;

public sealed record WatchdogStatusDto(
    bool Enabled, bool Supported, string? UnsupportedReason, bool Running, string State, string Message,
    DateTime? LastCheckUtc, DateTime? LastRestartUtc, IReadOnlyList<WatchdogEventDto> Events);

public sealed record WatchdogEventDto(DateTime TimeUtc, string State, string Message);

/// <summary>
/// The Chronicle side of the watchdog (Settings -> Watchdog). The watchdog itself is a separate, windowless
/// program (Chronicle.Watchdog.exe) because a watchdog living inside the process it watches cannot restart it.
/// This service switches it on and off, tells it exactly how this instance was started (so a restart is the same
/// launch, not a guess), keeps it starting with Windows, and reads back what it has been doing. It also leaves a
/// marker when Chronicle is shut down on purpose, so the watchdog does not "fix" a deliberate stop.
/// </summary>
public sealed class WatchdogService : IHostedService
{
    public const string SettingKey = "watchdog.enabled";
    private const string ExeName = "Chronicle.Watchdog.exe";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Chronicle Watchdog";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WatchdogService> _log;
    private readonly int _apiPort;
    private readonly string _directory = AppContext.BaseDirectory;
    private readonly string _launchDirectory = Directory.GetCurrentDirectory();

    public WatchdogService(IServiceScopeFactory scopes, ILogger<WatchdogService> log, PortConfig ports)
    {
        _scopes = scopes;
        _log = log;
        _apiPort = ports.Api;
    }

    private string ConfigPath => Path.Combine(_directory, "watchdog.json");
    private string StatusPath => Path.Combine(_directory, "watchdog-status.json");
    private string StopMarkerPath => Path.Combine(_directory, "chronicle-stopped.marker");
    private string ExePath => Path.Combine(_directory, ExeName);

    public string? UnsupportedReason =>
        !OperatingSystem.IsWindows() ? "The watchdog is for Windows installs. On Linux or Docker, use the platform's own restart policy (for example restart: unless-stopped)."
        : WindowsServiceHelpers.IsWindowsService() ? "Chronicle is running as a Windows service, which already restarts itself if it stops."
        : !File.Exists(ExePath) ? $"{ExeName} was not found next to Chronicle."
        : null;

    public async Task StartAsync(CancellationToken ct)
    {
        TryDelete(StopMarkerPath);   // Chronicle is running again: a deliberate stop is over
        try { if (await IsEnabledAsync(ct)) await ApplyAsync(true, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "WATCHDOG could not be (re)started at startup"); }
    }

    public Task StopAsync(CancellationToken ct)
    {
        // A graceful shutdown is somebody stopping Chronicle (or Windows shutting down); a crash or a kill never gets here.
        try { File.WriteAllText(StopMarkerPath, DateTime.UtcNow.ToString("O")); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        return row is not null && bool.TryParse(row.Value, out var on) && on;
    }

    /// <summary>Saves the choice and makes it so. Throws InvalidOperationException when this install cannot run a watchdog.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (enabled && UnsupportedReason is { } reason) throw new InvalidOperationException(reason);

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var row = await db.AppSettings.FindAsync([SettingKey], ct);
            if (row is null) db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = enabled.ToString() });
            else row.Value = enabled.ToString();
            await db.SaveChangesAsync(ct);
        }
        await ApplyAsync(enabled, ct);
        _log.LogInformation("WATCHDOG {State} by an administrator", enabled ? "switched on" : "switched off");
    }

    private Task ApplyAsync(bool enabled, CancellationToken ct)
    {
        if (UnsupportedReason is not null) return Task.CompletedTask;

        WriteConfig(enabled);
        if (OperatingSystem.IsWindows()) SetAutostart(enabled);
        if (enabled) EnsureRunning();
        // Disabled: the watchdog reads the file on its next cycle and exits on its own.
        return Task.CompletedTask;
    }

    private void WriteConfig(bool enabled)
    {
        var env = new Dictionary<string, string>();
        foreach (var name in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) env[name] = value;

        var config = new
        {
            enabled,
            apiUrl = $"http://localhost:{_apiPort}",
            intervalSeconds = 30,
            failuresBeforeRestart = 2,
            maxRestartsPerHour = 4,
            startupGraceSeconds = 300,
            launch = new
            {
                file = Environment.ProcessPath ?? "",
                arguments = "",
                workingDirectory = _launchDirectory,
                environment = env,
                processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Chronicle.API"),
            },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private void EnsureRunning()
    {
        if (IsWatchdogRunning()) return;
        Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;
        if (enabled) key.SetValue(RunValueName, $"\"{ExePath}\"");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    public async Task<WatchdogStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var enabled = await IsEnabledAsync(ct);
        var unsupported = UnsupportedReason;
        var running = IsWatchdogRunning();

        var state = enabled ? (running ? "starting" : "not-running") : "off";
        var message = enabled ? (running ? "" : "The watchdog is switched on but not running.") : "The watchdog is switched off.";
        DateTime? lastCheck = null, lastRestart = null;
        var events = new List<WatchdogEventDto>();

        if (enabled && running && File.Exists(StatusPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(StatusPath));
                var root = doc.RootElement;
                state = root.GetProperty("state").GetString() ?? state;
                message = root.GetProperty("message").GetString() ?? message;
                lastCheck = root.TryGetProperty("lastCheckUtc", out var c) && c.ValueKind == JsonValueKind.String ? c.GetDateTime() : null;
                lastRestart = root.TryGetProperty("lastRestartUtc", out var r) && r.ValueKind == JsonValueKind.String ? r.GetDateTime() : null;
                if (root.TryGetProperty("events", out var list))
                    foreach (var e in list.EnumerateArray())
                        events.Add(new WatchdogEventDto(e.GetProperty("timeUtc").GetDateTime(), e.GetProperty("state").GetString() ?? "", e.GetProperty("message").GetString() ?? ""));
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _log.LogDebug(ex, "WATCHDOG status file could not be read");
            }
        }
        events.Reverse();
        return new WatchdogStatusDto(enabled, unsupported is null, unsupported, running, state, message, lastCheck, lastRestart, events);
    }

    /// <summary>True when THIS install's watchdog is running (another Chronicle folder's does not count).</summary>
    private bool IsWatchdogRunning()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            using (p)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* cannot inspect it; not ours to claim */ }
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
