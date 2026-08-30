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

        // Navigation
        public User? User { get; set; }
        public MediaItem? MediaItem { get; set; }
    }
}
