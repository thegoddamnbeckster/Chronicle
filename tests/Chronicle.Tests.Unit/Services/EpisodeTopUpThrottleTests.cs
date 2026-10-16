using Chronicle.Services;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class EpisodeTopUpThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void ANewShowIsNeverFresh_ACheckedOneIsFreshUntilTheIntervalPasses()
    {
        var clock = new Clock();
        var throttle = new EpisodeTopUpThrottle(clock);
        throttle.IsFresh(7).Should().BeFalse();

        throttle.MarkChecked(7);
        throttle.IsFresh(7).Should().BeTrue();
        throttle.IsFresh(8).Should().BeFalse("freshness is per show");

        clock.Advance(EpisodeTopUpThrottle.Interval - TimeSpan.FromSeconds(1));
        throttle.IsFresh(7).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(2));
        throttle.IsFresh(7).Should().BeFalse();
    }
}
