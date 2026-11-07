using System.Text.Json;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// A plugin that must make many paced requests to describe one item completely (MusicBrainz: one request a second, a thousand release
/// groups behind one artist) declares a longer deadline for a call in its manifest. Without it the host's 25 seconds cut the fetch off and
/// treated it as "no result", so a complete answer could never be stored.
/// </summary>
public class PluginCallDeadlineTests
{
    private const string Id = "test.plugin.deadline";

    [Fact]
    public void A_plugin_gets_the_default_unless_it_declared_a_longer_deadline()
    {
        ProviderCallGuard.SetPluginTimeout(Id, null);
        ProviderCallGuard.TimeoutFor(Id).Should().Be(TimeSpan.FromSeconds(25));

        ProviderCallGuard.SetPluginTimeout(Id, 14400);
        try { ProviderCallGuard.TimeoutFor(Id).Should().Be(TimeSpan.FromHours(4)); }
        finally { ProviderCallGuard.SetPluginTimeout(Id, null); }
        ProviderCallGuard.TimeoutFor(Id).Should().Be(TimeSpan.FromSeconds(25));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(-5)]
    public void A_declared_deadline_no_longer_than_the_default_changes_nothing(int seconds)
    {
        ProviderCallGuard.SetPluginTimeout(Id, seconds);
        ProviderCallGuard.TimeoutFor(Id).Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void A_deadline_is_capped_at_a_day_and_the_plugin_id_is_not_case_sensitive()
    {
        ProviderCallGuard.SetPluginTimeout(Id, int.MaxValue);
        try
        {
            ProviderCallGuard.TimeoutFor(Id.ToUpperInvariant()).Should().Be(TimeSpan.FromHours(24));
        }
        finally { ProviderCallGuard.SetPluginTimeout(Id, null); }
    }

    [Fact]
    public async Task A_callers_own_shorter_deadline_still_wins_over_the_plugins()
    {
        // Interactive search passes 2.5 s; a plugin's long deadline must not slow that down.
        ProviderCallGuard.SetPluginTimeout(Id, 3600);
        try
        {
            var errors = new List<string>();
            var started = DateTime.UtcNow;
            var result = await ProviderCallGuard.CallAsync(
                async t => { await Task.Delay(TimeSpan.FromSeconds(30), t); return "late"; },
                Id, "SearchAsync", "fallback", _ => { }, errors.Add, default, timeout: TimeSpan.FromMilliseconds(50));

            result.Should().Be("fallback");
            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
            errors.Should().ContainSingle();
        }
        finally { ProviderCallGuard.SetPluginTimeout(Id, null); }
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_guards_own_deadline_is_a_failure_not_no_result()
    {
        // The plugin's own HTTP client timing out, or the app shutting down mid-call, throws a cancellation the guard did not cause.
        // As "no result" it was recorded as "not found": two artists Completed since September became NotFound (2026-11-07).
        var errors = new List<string>();
        var act = () => ProviderCallGuard.CallAsync<string?>(
            _ => throw new TaskCanceledException("the plugin's own client gave up"),
            Id, "GetByIdAsync", null, _ => { }, errors.Add, default);

        await act.Should().ThrowAsync<OperationCanceledException>();
        errors.Should().BeEmpty("nothing was described as a timeout, because it was not one");
    }

    [Fact]
    public async Task The_guards_own_deadline_is_still_no_result()
    {
        var errors = new List<string>();
        var result = await ProviderCallGuard.CallAsync<string?>(
            async t => { await Task.Delay(TimeSpan.FromSeconds(30), t); return "late"; },
            Id, "GetByIdAsync", null, _ => { }, errors.Add, default, timeout: TimeSpan.FromMilliseconds(50));

        result.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("did not complete within");
    }

    [Fact]
    public void The_manifest_reads_call_timeout_seconds_and_an_old_manifest_without_it_is_unchanged()
    {
        var withIt = JsonSerializer.Deserialize<PluginManifest>("{\"plugin_id\":\"p\",\"call_timeout_seconds\":14400}")!;
        withIt.CallTimeoutSeconds.Should().Be(14400);

        var without = JsonSerializer.Deserialize<PluginManifest>("{\"plugin_id\":\"p\"}")!;
        without.CallTimeoutSeconds.Should().BeNull();
    }
}
