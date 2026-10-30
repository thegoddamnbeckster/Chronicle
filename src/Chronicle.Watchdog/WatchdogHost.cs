using System.Diagnostics;
using System.Text.Json;

namespace Chronicle.Watchdog;

/// <summary>The real world: HTTP probes, processes and the status file the Settings page reads.</summary>
public sealed class WatchdogHost : IWatchdogHost
{
    private const int MaxEvents = 50;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _statusPath;
    private readonly string _stopMarkerPath;
    private readonly List<object> _events = new();
    private string _state = "starting";
    private string _message = "";
    private DateTime? _lastRestartUtc;

    public WatchdogHost(string directory)
    {
        _statusPath = Path.Combine(directory, "watchdog-status.json");
        _stopMarkerPath = Path.Combine(directory, "chronicle-stopped.marker");
    }

    public async Task<ProbeResult> ProbeAsync(WatchdogConfig config)
    {
        try
        {
            using var response = await Http.GetAsync($"{config.ApiUrl.TrimEnd('/')}/api/health/deep");
            if (response.IsSuccessStatusCode) return new ProbeResult(true, "ok");
            var body = await response.Content.ReadAsStringAsync();
            return new ProbeResult(false, $"HTTP {(int)response.StatusCode}: {Trim(body)}");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, ex is TaskCanceledException ? "no answer within 10 seconds" : ex.Message);
        }
    }

    public void RestartApi(WatchdogConfig config)
    {
        var launch = config.Launch;
        foreach (var p in Process.GetProcessesByName(launch.ProcessName))
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(15_000); } catch { /* already gone */ }
        }
        if (string.IsNullOrWhiteSpace(launch.File) || !File.Exists(launch.File))
        {
            Record("error", $"Cannot restart Chronicle: '{launch.File}' does not exist.");
            return;
        }

        var info = new ProcessStartInfo(launch.File, launch.Arguments)
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in launch.Environment) info.Environment[key] = value;
        try
        {
            Process.Start(info);
            _lastRestartUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Record("error", $"Cannot restart Chronicle: {ex.Message}");
        }
    }

    public bool StoppedOnPurposeSince(DateTime since) =>
        File.Exists(_stopMarkerPath) && File.GetLastWriteTimeUtc(_stopMarkerPath) > since;

    public void Record(string state, string message)
    {
        _state = state;
        _message = message;
        _events.Add(new { timeUtc = DateTime.UtcNow, state, message });
        if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        WriteStatus();
    }

    /// <summary>Refreshes the "last checked" time even when nothing changed.</summary>
    public void Heartbeat() => WriteStatus();

    private void WriteStatus()
    {
        try
        {
            var status = new
            {
                pid = Environment.ProcessId,
                lastCheckUtc = DateTime.UtcNow,
                state = _state,
                message = _message,
                lastRestartUtc = _lastRestartUtc,
                events = _events,
            };
            var temp = _statusPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
            File.Move(temp, _statusPath, overwrite: true);
        }
        catch { /* a status file we cannot write must never stop the watchdog */ }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}
