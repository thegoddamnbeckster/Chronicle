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
    private const string TaskName = "Chronicle Watchdog";
    // Older builds started the watchdog from the Run key; it is cleaned up when the scheduled task is set.
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WatchdogService> _log;
    private readonly int _apiPort;
    private readonly int _webPort;
    private readonly string _directory = AppContext.BaseDirectory;
    private readonly string _launchDirectory = Directory.GetCurrentDirectory();

    public WatchdogService(IServiceScopeFactory scopes, ILogger<WatchdogService> log, PortConfig ports)
    {
        _scopes = scopes;
        _log = log;
        _apiPort = ports.Api;
        _webPort = ports.Web;
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
        // On unless an administrator switched it off: keeping Chronicle running should not depend on anyone remembering to ask.
        return row is null || !bool.TryParse(row.Value, out var on) || on;
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

        // A source checkout also runs the web UI as a dev server (Vite); an installed Chronicle serves its own UI.
        var webDir = Path.GetFullPath(Path.Combine(_launchDirectory, "..", "Chronicle.Web"));
        object? web = File.Exists(Path.Combine(webDir, "package.json"))
            ? new
            {
                url = $"http://localhost:{_webPort}",
                launch = new { file = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", arguments = "/c npm run dev", workingDirectory = webDir, environment = new Dictionary<string, string>(), processName = "" },
            }
            : null;

        var config = new
        {
            enabled,
            apiUrl = $"http://localhost:{_apiPort}",
            intervalSeconds = 10,
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
            web,
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    /// <summary>
    /// Starts the watchdog through Task Scheduler, not as a child of this process: a watchdog that is a child of the API
    /// (or of whatever started the API) is killed along with it, which is exactly when it is needed.
    /// </summary>
    private void EnsureRunning()
    {
        if (IsWatchdogRunning()) return;
        if (!RunSchtasks($"/Run /TN \"{TaskName}\""))
            Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = _directory, UseShellExecute = false, CreateNoWindow = true });
    }

    /// <summary>
    /// A scheduled task for the current user (no administrator needed) that starts the watchdog at logon and then every
    /// minute; a copy that finds one already running exits at once, so the effect is "the watchdog is always running",
    /// even after it or everything else was killed. Task Scheduler is outside the process tree of whatever launched Chronicle.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void SetAutostart(bool enabled)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            key?.DeleteValue("Chronicle Watchdog", throwOnMissingValue: false);

        if (!enabled)
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            return;
        }

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Keeps Chronicle running: restarts it if it stops answering. Managed from Chronicle, Settings, Watchdog.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId></LogonTrigger>
                <TimeTrigger>
                  <StartBoundary>2026-01-01T00:00:00</StartBoundary>
                  <Enabled>true</Enabled>
                  <Repetition><Interval>PT1M</Interval></Repetition>
                </TimeTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Hidden>true</Hidden>
              </Settings>
              <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(ExePath)}</Command><WorkingDirectory>{System.Security.SecurityElement.Escape(_directory)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        // Kept in the install folder (not the system temp folder) like everything else Chronicle writes.
        var xmlPath = Path.Combine(_directory, "watchdog-task.xml");
        try
        {
            File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
            if (!RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F"))
                _log.LogWarning("WATCHDOG could not register its scheduled task; it will not restart after a reboot or if it is killed");
        }
        finally { TryDelete(xmlPath); }
    }

    private bool RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15_000)) { p.Kill(); return false; }
            if (p.ExitCode != 0) _log.LogDebug("WATCHDOG schtasks {Args} failed: {Error}", arguments, error.Trim());
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogDebug(ex, "WATCHDOG schtasks could not run");
            return false;
        }
    }

    public async Task<WatchdogStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var unsupported = UnsupportedReason;
        var enabled = await IsEnabledAsync(ct) && unsupported is null;
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
