namespace Chronicle.Services;

/// <summary>
/// Shared by ScrobbleService.UpsertLibraryStateAsync and SyncOrchestrationService.
/// UpsertPlaybackProgressAsync -- both need to know whether an incoming progress timestamp is
/// newer than the LATER of two independently-tracked timestamps (ResumeUpdatedAt and
/// LastKnownProgressAt) before accepting it into LastKnownProgressPercent. Extracted after code
/// review (2026-09-09) found the same date-arithmetic duplicated near-verbatim in both files --
/// pure timestamp comparison with no domain coupling to Kodi vs. Trakt, unlike the surrounding
/// business logic in each of those methods, which stays deliberately separate (see this
/// project's own precedent of not sharing logic between the two write paths).
/// </summary>
internal static class ProgressTimestampHelper
{
    /// <summary>The later of two nullable timestamps, or whichever one is non-null, or null if
    /// both are.</summary>
    public static DateTime? Latest(DateTime? a, DateTime? b) =>
        a is DateTime av && (b is not DateTime bv || av > bv) ? a : b;
}
