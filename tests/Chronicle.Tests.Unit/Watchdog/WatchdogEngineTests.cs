using Chronicle.Watchdog;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Watchdog;

public class WatchdogEngineTests
{
    private sealed class FakeHost : IWatchdogHost
    {
        public bool Healthy = true;
        public bool StoppedOnPurpose;
        public int Restarts;
        public List<string> States = new();

        public Task<ProbeResult> ProbeAsync(ComponentSpec component) =>
            Task.FromResult(new ProbeResult(Healthy, Healthy ? "ok" : "no answer"));
        public void Restart(ComponentSpec component) => Restarts++;
        public bool StoppedOnPurposeSince(DateTime since) => StoppedOnPurpose;
        public void Record(ComponentSpec component, string state, string message) => States.Add(state);
    }

    private static readonly ComponentSpec Api = new("Chronicle", "http://localhost:7979/api/health/deep", new LaunchSpec(), RespectsStopMarker: true);
    private static readonly ComponentSpec Web = new("The Chronicle web UI", "http://localhost:8888", new LaunchSpec(), RespectsStopMarker: false);

    private static readonly WatchdogConfig Config = new() { Enabled = true, FailuresBeforeRestart = 2, MaxRestartsPerHour = 2, StartupGraceSeconds = 300 };

    private static (WatchdogEngine engine, FakeHost host, Action<TimeSpan> advance) Create()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var host = new FakeHost();
        return (new WatchdogEngine(host, () => now), host, by => now += by);
    }

    [Fact]
    public async Task A_healthy_chronicle_is_left_alone_and_reported_once()
    {
        var (engine, host, _) = Create();

        await engine.TickAsync(Config, Api);
        await engine.TickAsync(Config, Api);

        host.Restarts.Should().Be(0);
        host.States.Should().Equal("healthy");
    }

    [Fact]
    public async Task One_failed_check_is_not_an_outage()
    {
        var (engine, host, _) = Create();
        host.Healthy = false;

        await engine.TickAsync(Config, Api);
        host.Healthy = true;
        await engine.TickAsync(Config, Api);

        host.Restarts.Should().Be(0);
    }

    [Fact]
    public async Task Consecutive_failures_restart_chronicle_then_wait_out_the_startup_grace()
    {
        var (engine, host, advance) = Create();
        host.Healthy = false;

        await engine.TickAsync(Config, Api);
        await engine.TickAsync(Config, Api);
        host.Restarts.Should().Be(1);

        advance(TimeSpan.FromSeconds(120));      // still inside the grace period: a big database is still opening
        await engine.TickAsync(Config, Api);
        await engine.TickAsync(Config, Api);
        host.Restarts.Should().Be(1);
        host.States.Should().Contain("starting");

        advance(TimeSpan.FromSeconds(200));      // grace over and still down
        await engine.TickAsync(Config, Api);
        await engine.TickAsync(Config, Api);
        host.Restarts.Should().Be(2);
    }

    [Fact]
    public async Task A_crash_loop_stops_after_the_hourly_limit_and_says_so()
    {
        var (engine, host, advance) = Create();
        host.Healthy = false;

        for (var i = 0; i < 6; i++)
        {
            await engine.TickAsync(Config, Api);
            await engine.TickAsync(Config, Api);
            advance(TimeSpan.FromSeconds(400));
        }

        host.Restarts.Should().Be(2);
        host.States.Should().Contain("gave-up");
    }

    [Fact]
    public async Task A_deliberate_shutdown_is_not_undone()
    {
        var (engine, host, _) = Create();
        host.Healthy = false;
        host.StoppedOnPurpose = true;

        await engine.TickAsync(Config, Api);
        await engine.TickAsync(Config, Api);

        host.Restarts.Should().Be(0);
        host.States.Should().Contain("stopped");
    }

    [Fact]
    public async Task The_web_ui_is_restarted_even_after_a_deliberate_chronicle_stop()
    {
        var (engine, host, _) = Create();
        host.Healthy = false;
        host.StoppedOnPurpose = true;

        await engine.TickAsync(Config, Web);
        await engine.TickAsync(Config, Web);

        host.Restarts.Should().Be(1);
    }

    [Fact]
    public void The_web_component_exists_only_when_a_launch_command_is_given()
    {
        new WatchdogConfig().WebComponent.Should().BeNull();
        new WatchdogConfig { Web = new WebSpec { Launch = new LaunchSpec { File = "cmd.exe" } } }.WebComponent.Should().NotBeNull();
    }

    [Fact]
    public async Task Recovery_clears_the_failure_count()
    {
        var (engine, host, _) = Create();
        host.Healthy = false;
        await engine.TickAsync(Config, Api);
        host.Healthy = true;
        await engine.TickAsync(Config, Api);
        host.Healthy = false;
        await engine.TickAsync(Config, Api);

        host.Restarts.Should().Be(0);
    }

    [Fact]
    public void Config_round_trips_through_the_file_the_api_writes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, new WatchdogConfig
            {
                Enabled = true, ApiUrl = "http://localhost:7979",
                Launch = new LaunchSpec { File = "C:\\Chronicle\\Chronicle.API.exe", WorkingDirectory = "C:\\Chronicle", Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Development" } },
            }.ToJson());

            var loaded = WatchdogConfig.TryLoad(path);

            loaded.Should().NotBeNull();
            loaded!.Enabled.Should().BeTrue();
            loaded.Launch.WorkingDirectory.Should().Be("C:\\Chronicle");
            loaded.Launch.Environment["ASPNETCORE_ENVIRONMENT"].Should().Be("Development");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_or_broken_config_file_loads_as_null()
    {
        WatchdogConfig.TryLoad(Path.Combine(Path.GetTempPath(), "does-not-exist.json")).Should().BeNull();
    }
}
