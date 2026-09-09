namespace Chronicle.Core.Models
{
    public enum LibraryStatus
    {
        Unwatched,
        PlanToWatch,
        Watching,
        Completed,
        Dropped,
        OnHold,
        Rewatching
    }

    public class UserLibrary
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int MediaItemId { get; set; }
        public LibraryStatus Status { get; set; } = LibraryStatus.PlanToWatch;
        public int? UserRating { get; set; }
        /// <summary>
        /// When the current UserRating value was actually set -- distinct from the generic
        /// UpdatedAt (which is bumped by status changes, resume updates, notes, etc. too).
        /// Drives "most recent wins" conflict resolution when Trakt/Simkl/Chronicle's own web
        /// UI/a Kodi-pushed manual rating disagree about the same item -- see
        /// SyncOrchestrationService.UpsertRatingAsync. Null means the rating predates this
        /// column (treated as "unknown, assume old" so a real incoming timestamp always wins).
        /// </summary>
        public DateTime? UserRatingUpdatedAt { get; set; }
        public string? Notes { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Percent-of-duration playback position from the most recent scrobble that
        /// didn't cross the watched threshold -- the cross-device "resume where I left
        /// off" value. Percent, not raw seconds: a different device's copy of the same
        /// content (a re-encode, a different cut) can have a slightly different exact
        /// duration, so seconds computed on one device don't transfer cleanly to
        /// another, but percent-of-that-device's-own-duration does. Null means either
        /// never scrobbled or the item is fully watched (cleared on MarkedAsWatched --
        /// see ScrobbleService.ScrobbleAsync).
        /// </summary>
        public double? ResumePositionPercent { get; set; }
        public DateTime? ResumeUpdatedAt { get; set; }

        /// <summary>
        /// Percent-of-duration playback position from the MOST RECENT scrobble of any kind,
        /// including one that crossed the watched threshold -- unlike ResumePositionPercent,
        /// this is NEVER cleared on completion. Purely informational (a "how far did you
        /// actually get" display value, e.g. a poster's progress bar for a Completed item), not
        /// a resume-functional one -- per-user correction (2026-09-09): showing 100% for every
        /// completed item was wrong; stopping at 96% and crossing the watched threshold should
        /// still display as 96%, not be inflated. Null only when the item has never been
        /// scrobbled at all (e.g. marked Completed by hand, or imported from a watch-history
        /// sync that reports no percentage) -- callers fall back to 100% in that case, since
        /// that's the best available assumption with no better information.
        /// </summary>
        public double? LastKnownProgressPercent { get; set; }
        public DateTime? LastKnownProgressAt { get; set; }

        // Navigation
        public User? User { get; set; }
        public MediaItem? MediaItem { get; set; }
    }
}
