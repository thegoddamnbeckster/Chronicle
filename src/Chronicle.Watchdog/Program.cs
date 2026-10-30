using Chronicle.Watchdog;

// Runs without a window. Only one copy runs per install; a second one exits silently. (Per install, not per machine:
// two Chronicle folders on one PC each need their own watchdog.)
var directory = AppContext.BaseDirectory;
var installId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToLowerInvariant())))[..16];
using var single = new Mutex(true, $@"Global\Chronicle.Watchdog.{installId}", out var isFirst);
if (!isFirst) return;

var configPath = Path.Combine(directory, "watchdog.json");
var host = new WatchdogHost(directory);
var apiEngine = new WatchdogEngine(host, () => DateTime.UtcNow);
var webEngine = new WatchdogEngine(host, () => DateTime.UtcNow);

while (true)
{
    var config = WatchdogConfig.TryLoad(configPath);
    // Switched off (or its settings are gone): nothing left to do, so leave instead of idling in Task Manager.
    if (config is null || !config.Enabled) return;

    try
    {
        await apiEngine.TickAsync(config, config.ApiComponent);
        if (config.WebComponent is { } web) await webEngine.TickAsync(config, web);
    }
    catch (Exception ex) { host.RecordError($"Watchdog check failed: {ex.Message}"); }
    host.Heartbeat();

    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.IntervalSeconds, 5, 600)));
}
