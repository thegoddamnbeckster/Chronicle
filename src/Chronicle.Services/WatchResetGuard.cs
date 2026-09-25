namespace Chronicle.Services;

/// <summary>
/// Decides whether a watch claim predates the user's explicit "rewatch" reset of an item
/// (UserLibrary.WatchResetAt, stored in UTC). Timestamps reach Chronicle in two conventions: the
/// Kodi reconciliation pass sends Kodi's naive LOCAL lastplayed, while live scrobbles and the
/// Trakt/SIMKL importers send UTC. Comparing either against the UTC stamp as-is misorders them by
/// the UTC offset, so a reconciliation claim is compared against the stamp converted to the
/// server's local clock (the household's timezone, same as the Kodi devices).
/// </summary>
public static class WatchResetGuard
{
    public static bool IsAtOrBeforeReset(DateTime claimedAt, string? deviceName, DateTime? resetAtUtc)
    {
        if (resetAtUtc is not { } reset) return false;

        var resetUtc = DateTime.SpecifyKind(reset, DateTimeKind.Utc);
        var comparable = deviceName is not null && deviceName.Contains(ScrobbleService.ReconciliationDeviceName)
            ? resetUtc.ToLocalTime()
            : resetUtc;
        return claimedAt <= DateTime.SpecifyKind(comparable, DateTimeKind.Unspecified) ||
               claimedAt <= comparable;
    }
}
