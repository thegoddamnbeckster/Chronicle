using Chronicle.API;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Integration;

/// <summary>
/// PortManager.LoadConfig's precedence: CHRONICLE_API_PORT/CHRONICLE_WEB_PORT env vars, then
/// ports.json, then Chronicle's own documented defaults (7979/8888) -- added alongside the
/// official-deployment work (2026-09-29) so a Windows Service or Docker install can override
/// the listening port without needing a ports.json at all. Not run in parallel with anything
/// else that touches these env vars: each test saves and restores them itself.
/// </summary>
public class PortManagerTests
{
    private static void WithEnvVars(string? apiPort, string? webPort, Action action)
    {
        var savedApi = Environment.GetEnvironmentVariable("CHRONICLE_API_PORT");
        var savedWeb = Environment.GetEnvironmentVariable("CHRONICLE_WEB_PORT");
        try
        {
            Environment.SetEnvironmentVariable("CHRONICLE_API_PORT", apiPort);
            Environment.SetEnvironmentVariable("CHRONICLE_WEB_PORT", webPort);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHRONICLE_API_PORT", savedApi);
            Environment.SetEnvironmentVariable("CHRONICLE_WEB_PORT", savedWeb);
        }
    }

    private static string EmptyTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chronicle-portmanager-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void LoadConfig_NoPortsJsonNoEnvVars_UsesChronicleDefaults()
    {
        WithEnvVars(null, null, () =>
        {
            var dir = EmptyTempDir();
            try
            {
                var config = PortManager.LoadConfig(dir);
                config.Api.Should().Be(7979);
                config.Web.Should().Be(8888);
            }
            finally { Directory.Delete(dir, recursive: true); }
        });
    }

    [Fact]
    public void LoadConfig_PortsJsonPresent_UsesItOverDefaults()
    {
        WithEnvVars(null, null, () =>
        {
            var dir = EmptyTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ports.json"), """{"api": 12345, "web": 54321}""");
                var config = PortManager.LoadConfig(dir);
                config.Api.Should().Be(12345);
                config.Web.Should().Be(54321);
            }
            finally { Directory.Delete(dir, recursive: true); }
        });
    }

    [Fact]
    public void LoadConfig_EnvVarsPresent_OverridesBothDefaultsAndPortsJson()
    {
        WithEnvVars("9001", "9002", () =>
        {
            var dir = EmptyTempDir();
            try
            {
                // Even with a ports.json on disk saying something else, env vars must win --
                // the whole point of supporting them is a Docker/Windows Service install
                // overriding the port without touching any file.
                File.WriteAllText(Path.Combine(dir, "ports.json"), """{"api": 12345, "web": 54321}""");
                var config = PortManager.LoadConfig(dir);
                config.Api.Should().Be(9001);
                config.Web.Should().Be(9002);
            }
            finally { Directory.Delete(dir, recursive: true); }
        });
    }

    [Fact]
    public void LoadConfig_InvalidEnvVarValue_IgnoredFallsBackToDefault()
    {
        WithEnvVars("not-a-port", null, () =>
        {
            var dir = EmptyTempDir();
            try
            {
                var config = PortManager.LoadConfig(dir);
                config.Api.Should().Be(7979, "an unparseable CHRONICLE_API_PORT must not crash startup or silently bind port 0");
            }
            finally { Directory.Delete(dir, recursive: true); }
        });
    }

    [Fact]
    public void LoadConfig_OnlyApiPortInPortsJson_WebFallsBackToDefault()
    {
        WithEnvVars(null, null, () =>
        {
            var dir = EmptyTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "ports.json"), """{"api": 12345}""");
                var config = PortManager.LoadConfig(dir);
                config.Api.Should().Be(12345);
                config.Web.Should().Be(8888, "a ports.json that only specifies one port must not zero out the other");
            }
            finally { Directory.Delete(dir, recursive: true); }
        });
    }
}
