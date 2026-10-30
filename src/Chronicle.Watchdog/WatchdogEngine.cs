namespace Chronicle.Watchdog;

public sealed record ProbeResult(bool Healthy, string Detail);

/// <summary>Everything the engine does to the outside world, so the decisions can be tested without processes or HTTP.</summary>
public interface IWatchdogHost
{
    Task<ProbeResult> ProbeAsync(WatchdogConfig config);
    /// <summary>Stops any running instance, then starts a new one.</summary>
    void RestartApi(WatchdogConfig config);
    /// <summary>True when Chronicle was shut down on purpose (it leaves a marker) after <paramref name="since"/>.</summary>
    bool StoppedOnPurposeSince(DateTime since);
    void Record(string state, string message);
}

/// <summary>
/// The watchdog's decisions. A failed check is only acted on after several in a row (a slow response is not an
/// outage); after a restart the API gets a long grace period (a big database takes minutes to open); and
/// restarts are capped per hour so a crash loop stops thrashing and says so. A deliberate shutdown is never
/// "fixed" by starting Chronicle again.
/// </summary>
public sealed class WatchdogEngine
{
    private readonly IWatchdogHost _host;
    private readonly Func<DateTime> _now;
    private readonly DateTime _startedAt;
    private readonly List<DateTime> _restarts = new();
    private int _failures;
    private DateTime? _graceUntil;
    private string _lastState = "";

    public WatchdogEngine(IWatchdogHost host, Func<DateTime> now)
    {
        _host = host;
        _now = now;
        _startedAt = now();
    }

    public async Task TickAsync(WatchdogConfig config)
    {
        var probe = await _host.ProbeAsync(config);
        if (probe.Healthy)
        {
            _failures = 0;
            _graceUntil = null;
            Set("healthy", "Chronicle is running normally.");
            return;
        }

        if (_graceUntil is { } grace && _now() < grace)
        {
            Set("starting", "Chronicle is starting up.");
            return;
        }

        _failures++;
        if (_failures < Math.Max(1, config.FailuresBeforeRestart))
            return;

        if (_host.StoppedOnPurposeSince(_startedAt))
        {
            Set("stopped", "Chronicle was stopped on purpose; not restarting it.");
            return;
        }

        var now = _now();
        _restarts.RemoveAll(t => t < now.AddHours(-1));
        if (_restarts.Count >= Math.Max(1, config.MaxRestartsPerHour))
        {
            Set("gave-up", $"Chronicle keeps failing ({probe.Detail}); it was restarted {_restarts.Count} times in the last hour, so the watchdog is not trying again for now.");
            return;
        }

        _restarts.Add(now);
        _failures = 0;
        _graceUntil = now.AddSeconds(Math.Max(30, config.StartupGraceSeconds));
        _lastState = "restarting";
        _host.Record("restarting", $"Chronicle was not working ({probe.Detail}); restarting it.");
        _host.RestartApi(config);
    }

    /// <summary>Records a state change once, not on every cycle.</summary>
    private void Set(string state, string message)
    {
        if (state == _lastState) return;
        _lastState = state;
        _host.Record(state, message);
    }
}
