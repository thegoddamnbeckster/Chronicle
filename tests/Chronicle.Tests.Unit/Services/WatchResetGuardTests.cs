using Chronicle.Services;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Timestamps reach Chronicle in two conventions: Kodi's reconciliation pass sends naive LOCAL
/// lastplayed, everything else sends UTC. The reset stamp is UTC, so the reconciliation claim is
/// compared against the stamp converted to server-local time. These tests are timezone-independent
/// (they derive the local instant from the same conversion) so they hold on any machine.
/// </summary>
public class WatchResetGuardTests
{
    private static readonly DateTime ResetUtc = new(2026, 9, 25, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoReset_NeverBlocks() =>
        Assert.False(WatchResetGuard.IsAtOrBeforeReset(DateTime.MinValue, null, null));

    [Fact]
    public void UtcClaim_IsComparedAgainstTheUtcStamp()
    {
        Assert.True(WatchResetGuard.IsAtOrBeforeReset(ResetUtc.AddMinutes(-1), "Living Room", ResetUtc));
        Assert.False(WatchResetGuard.IsAtOrBeforeReset(ResetUtc.AddMinutes(1), "Living Room", ResetUtc));
    }

    [Fact]
    public void ReconciliationClaim_IsComparedAgainstTheStampInLocalTime()
    {
        var localReset = ResetUtc.ToLocalTime();
        var device = ScrobbleService.ReconciliationDeviceName;

        Assert.True(WatchResetGuard.IsAtOrBeforeReset(
            DateTime.SpecifyKind(localReset.AddMinutes(-1), DateTimeKind.Unspecified), device, ResetUtc));
        Assert.False(WatchResetGuard.IsAtOrBeforeReset(
            DateTime.SpecifyKind(localReset.AddMinutes(1), DateTimeKind.Unspecified), device, ResetUtc));
    }
}
