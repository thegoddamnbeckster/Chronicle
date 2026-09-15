using System.Net;
using Chronicle.Services;
using FluentAssertions;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Root-caused live (2026-09-14/15): a brief DNS resolution failure reaching
/// api.themoviedb.org ("No such host is known") -- not a real TMDB rate limit -- aborted
/// MetadataEnrichmentService's entire batch pass immediately and left a 270,000+ item queue
/// waiting for the next scheduled/manual trigger, repeatedly, across multiple days, without
/// ever draining. Every plugin call routes through ProviderCallGuard.CallAsync (see its own
/// doc), so these tests pin its retry behavior directly rather than through the much larger
/// enrichment pipeline.
/// </summary>
public class ProviderCallGuardTests
{
    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(1);

    private static List<string> Warnings() => [];
    private static List<string> Errors() => [];

    [Fact]
    public async Task CallAsync_TransientNetworkFailureThenSuccess_RetriesAndReturnsResult()
    {
        var attempts = 0;
        var warnings = Warnings();

        var result = await ProviderCallGuard.CallAsync(
            call: _ =>
            {
                attempts++;
                if (attempts < 2)
                    throw new HttpRequestException("No such host is known.", null, statusCode: null);
                return Task.FromResult<string?>("matched");
            },
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)null,
            logWarning: warnings.Add, logError: _ => { }, ct: CancellationToken.None,
            retryDelayOverride: FastRetryDelay);

        result.Should().Be("matched");
        attempts.Should().Be(2, "the first transient failure should be retried, not given up on immediately");
        warnings.Should().ContainSingle(w => w.Contains("transient network failure"));
    }

    [Fact]
    public async Task CallAsync_TransientFailureNeverRecovers_RetriesBoundedTimesThenThrows()
    {
        var attempts = 0;

        Func<Task> act = () => ProviderCallGuard.CallAsync(
            call: _ =>
            {
                attempts++;
                throw new HttpRequestException("No such host is known.", null, statusCode: null);
            },
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)null,
            logWarning: _ => { }, logError: _ => { }, ct: CancellationToken.None,
            retryDelayOverride: FastRetryDelay);

        await act.Should().ThrowAsync<HttpRequestException>();
        // 1 initial attempt + 2 retries = 3 total -- a real, sustained outage must still
        // surface as a failure (and let MetadataEnrichmentService stop the pass), not retry
        // forever.
        attempts.Should().Be(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task CallAsync_ClassicTransientStatusCodes_AreRetried(HttpStatusCode statusCode)
    {
        var attempts = 0;

        var result = await ProviderCallGuard.CallAsync(
            call: _ =>
            {
                attempts++;
                if (attempts < 2) throw new HttpRequestException("transient", null, statusCode);
                return Task.FromResult<string?>("matched");
            },
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)null,
            logWarning: _ => { }, logError: _ => { }, ct: CancellationToken.None,
            retryDelayOverride: FastRetryDelay);

        result.Should().Be("matched");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task CallAsync_NotFoundResponse_IsNeverRetried()
    {
        // A real 404 is a meaningful answer, not a transient failure -- retrying it can't
        // change the outcome, and MetadataEnrichmentService's own classification (404 →
        // NotFound) depends on seeing it exactly once.
        var attempts = 0;

        Func<Task> act = () => ProviderCallGuard.CallAsync(
            call: _ =>
            {
                attempts++;
                throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            },
            pluginId: "chronicle.plugin.tmdb", operation: "GetByIdAsync", fallbackValue: (string?)null,
            logWarning: _ => { }, logError: _ => { }, ct: CancellationToken.None,
            retryDelayOverride: FastRetryDelay);

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1, "a definitive 404 must never be retried");
    }

    [Fact]
    public async Task CallAsync_NonHttpException_IsNeverRetried()
    {
        var attempts = 0;

        Func<Task> act = () => ProviderCallGuard.CallAsync(
            call: _ =>
            {
                attempts++;
                throw new InvalidOperationException("plugin not configured");
            },
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)null,
            logWarning: _ => { }, logError: _ => { }, ct: CancellationToken.None,
            retryDelayOverride: FastRetryDelay);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1, "a non-network exception (e.g. a plugin config error) must never be retried");
    }

    [Fact]
    public async Task CallAsync_HardTimeout_StillReturnsFallbackWithoutRetrying()
    {
        // Existing behavior (predates the retry feature) must survive unchanged: a call that
        // genuinely never completes returns the fallback value once, it is not retried --
        // retrying a call that hangs for the full timeout every time would just multiply the
        // wait, not help.
        var attempts = 0;
        var errors = Errors();

        var result = await ProviderCallGuard.CallAsync(
            call: async t =>
            {
                attempts++;
                await Task.Delay(Timeout.Infinite, t);
                return "unreachable";
            },
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)"fallback",
            logWarning: _ => { }, logError: errors.Add, ct: CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(20));

        result.Should().Be("fallback");
        attempts.Should().Be(1);
        errors.Should().ContainSingle(e => e.Contains("did not complete within"));
    }

    [Fact]
    public async Task CallAsync_NoDelayOverride_UsesTheRealMultiSecondDefaultDelay()
    {
        // Every other retry test above passes retryDelayOverride so it doesn't have to sit
        // through real multi-second sleeps -- which means none of them actually exercise
        // `retryDelayOverride ?? TransientRetryBaseDelay` resolving to the real production
        // value. This one omits the override and asserts a real wait happened, so a future typo
        // in that default wiring (wrong field, wrong units, defaulting to zero) can't slip past
        // every test still passing.
        var attempts = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await ProviderCallGuard.CallAsync(
            call: _ =>
            {
                attempts++;
                if (attempts < 2)
                    throw new HttpRequestException("No such host is known.", null, statusCode: null);
                return Task.FromResult<string?>("matched");
            },
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)null,
            logWarning: _ => { }, logError: _ => { }, ct: CancellationToken.None);

        sw.Stop();
        result.Should().Be("matched");
        attempts.Should().Be(2);
        // The real base delay is 2s; a generous lower bound avoids test flakiness from
        // scheduling jitter while still failing hard if the default collapsed to ~0.
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task CallAsync_SucceedsFirstTry_ReturnsResultWithNoRetryLogging()
    {
        var warnings = Warnings();

        var result = await ProviderCallGuard.CallAsync(
            call: _ => Task.FromResult<string?>("matched"),
            pluginId: "chronicle.plugin.tmdb", operation: "SearchAsync", fallbackValue: (string?)null,
            logWarning: warnings.Add, logError: _ => { }, ct: CancellationToken.None,
            retryDelayOverride: FastRetryDelay);

        result.Should().Be("matched");
        warnings.Should().BeEmpty();
    }
}
