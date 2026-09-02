using Chronicle.Services.Plugins;
using FluentAssertions;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.InMemory;

namespace Chronicle.Tests.Unit.Plugins;

public class PluginLoggerFactoryTests : IDisposable
{
    // Ensure a global Log.Logger is initialised before each test so that
    // PluginLoggerFactory.CreatePluginLogger (which forwards to Log.Logger)
    // doesn't throw a NullReferenceException.
    public PluginLoggerFactoryTests()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.InMemory()
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
    }

    [Fact]
    public void CreatePluginLogger_ValidPluginId_ReturnsLogger()
    {
        // Arrange
        var pluginId = "chronicle.plugin.test";

        // Act
        var logger = PluginLoggerFactory.CreatePluginLogger(pluginId, retainedLogDays: 7);

        // Assert
        logger.Should().NotBeNull();
    }

    [Theory]
    [InlineData("chronicle.plugin.tmdb")]
    [InlineData("chronicle.plugin.musicbrainz")]
    [InlineData("my-custom-plugin")]
    public void CreatePluginLogger_VariousPluginIds_DoesNotThrow(string pluginId)
    {
        // Act
        var act = () => PluginLoggerFactory.CreatePluginLogger(pluginId);

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreatePluginLogger_NullOrWhitespacePluginId_ThrowsArgumentException(string pluginId)
    {
        // Act
        var act = () => PluginLoggerFactory.CreatePluginLogger(pluginId);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreatePluginLogger_WritesEvents_WithPluginIdProperty()
    {
        // Arrange — create an in-memory sink on the global logger so we can
        // inspect forwarded events.
        var sink = new InMemorySink();
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var pluginId = "chronicle.plugin.test";
        var logger = PluginLoggerFactory.CreatePluginLogger(pluginId, retainedLogDays: 7);

        // Act
        logger.Information("Test event from {PluginId}", pluginId);

        // Assert — the event should have been forwarded to the global logger. Serilog's
        // Log.Logger is process-wide static state, and other test classes running
        // concurrently (xUnit parallelizes across classes by default) can log to whatever
        // logger happens to be assigned at that moment — so the sink isn't necessarily
        // exclusive to this test. Filter to events carrying THIS test's PluginId property
        // rather than asserting on the sink's total count.
        //
        // sink.LogEvents itself is a plain in-memory list with no synchronization (it's
        // Serilog.Sinks.InMemory, not our code), so a concurrent test's incidental log call
        // landing on the same global Log.Logger mid-enumeration throws
        // InvalidOperationException ("Collection was modified") -- confirmed happening in
        // practice (2026-09-02), unrelated to anything this test itself does wrong. Retry the
        // snapshot rather than failing the test on a race that has nothing to do with what's
        // actually being verified here.
        List<LogEvent> ownEvents = [];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                ownEvents = sink.LogEvents
                    .Where(e => e.Properties.TryGetValue("PluginId", out var v)
                             && v.ToString().Trim('"') == pluginId)
                    .ToList();
                break;
            }
            catch (InvalidOperationException) when (attempt < 4)
            {
                // Concurrent mutation of the shared sink -- snapshot again.
            }
        }
        ownEvents.Should().ContainSingle();
        ownEvents.Single().Level.Should().Be(LogEventLevel.Information);
    }
}
