using System.Text.Json;

namespace Chronicle.Watchdog;

/// <summary>How to start Chronicle again. Written by the API itself from how it was started, so a restart is
/// always launched the same way (same folder, same environment) as the instance that is being replaced.</summary>
public sealed class LaunchSpec
{
    public string File { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new();
    /// <summary>Process name (no .exe) to stop before starting again, so a hung instance is replaced rather than duplicated. Empty: stop nothing.</summary>
    public string ProcessName { get; set; } = "Chronicle.API";
}

/// <summary>The development web UI (Vite). Only present when Chronicle runs from a source checkout; an installed Chronicle serves its own UI.</summary>
public sealed class WebSpec
{
    public string Url { get; set; } = "http://localhost:8888";
    public LaunchSpec Launch { get; set; } = new();
}

/// <summary>What the watchdog looks after and how to bring it back.</summary>
public sealed record ComponentSpec(string Name, string ProbeUrl, LaunchSpec Launch, bool RespectsStopMarker);

/// <summary>Settings file the API writes next to itself (watchdog.json). Re-read on every cycle, so changes in
/// Settings apply without restarting the watchdog.</summary>
public sealed class WatchdogConfig
{
    public bool Enabled { get; set; }
    public string ApiUrl { get; set; } = "http://localhost:7979";
    public int IntervalSeconds { get; set; } = 30;
    public int FailuresBeforeRestart { get; set; } = 2;
    public int MaxRestartsPerHour { get; set; } = 4;
    public int StartupGraceSeconds { get; set; } = 300;
    public LaunchSpec Launch { get; set; } = new();
    public WebSpec? Web { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ComponentSpec ApiComponent => new("Chronicle", $"{ApiUrl.TrimEnd('/')}/api/health/deep", Launch, RespectsStopMarker: true);
    [System.Text.Json.Serialization.JsonIgnore]
    public ComponentSpec? WebComponent => Web is { Launch.File.Length: > 0 } w ? new("The Chronicle web UI", w.Url, w.Launch, RespectsStopMarker: false) : null;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static WatchdogConfig? TryLoad(string path)
    {
        try { return JsonSerializer.Deserialize<WatchdogConfig>(System.IO.File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
