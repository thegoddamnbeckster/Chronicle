using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class EnrichmentStatsCacheTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly IReadOnlyList<EnrichmentCounts> Rows = [new("p", 1, 2, 0, 0, 0, 0, 0)];

    private static (EnrichmentStatsCache Cache, FakeTimeProvider Clock, Func<Task<IReadOnlyList<EnrichmentCounts>>> Load, Func<int> Loads) Make()
    {
        var clock = new FakeTimeProvider();
        var loads = 0;
        return (new EnrichmentStatsCache(clock), clock, () => { loads++; return Task.FromResult(Rows); }, () => loads);
    }

    [Fact]
    public async Task ServesTheSameCountsForAFewSeconds_ThenReloads()
    {
        var (cache, clock, load, loads) = Make();
        await cache.GetAsync(load);
        await cache.GetAsync(load);
        loads().Should().Be(1);

        clock.Advance(EnrichmentStatsCache.Lifetime + TimeSpan.FromMilliseconds(1));
        await cache.GetAsync(load);
        loads().Should().Be(2);
    }

    [Fact]
    public async Task Invalidate_ForcesTheNextCallToReload()
    {
        var (cache, _, load, loads) = Make();
        await cache.GetAsync(load);
        cache.Invalidate();
        await cache.GetAsync(load);
        loads().Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneLoad()
    {
        var cache = new EnrichmentStatsCache(new FakeTimeProvider());
        var gate = new TaskCompletionSource();
        var loads = 0;
        async Task<IReadOnlyList<EnrichmentCounts>> Slow() { Interlocked.Increment(ref loads); await gate.Task; return Rows; }

        var a = cache.GetAsync(Slow);
        var b = cache.GetAsync(Slow);
        gate.SetResult();
        await Task.WhenAll(a, b);
        loads.Should().Be(1);
    }

    [Fact]
    public async Task AnInvalidationDuringALoad_MeansItsResultIsNotKept()
    {
        var cache = new EnrichmentStatsCache(new FakeTimeProvider());
        var gate = new TaskCompletionSource();
        var loads = 0;
        async Task<IReadOnlyList<EnrichmentCounts>> Slow() { Interlocked.Increment(ref loads); await gate.Task; return Rows; }

        var first = cache.GetAsync(Slow);
        cache.Invalidate();
        gate.SetResult();
        await first;

        await cache.GetAsync(Slow);
        loads.Should().Be(2, "numbers read before the reset must not be served after it");
    }
}
