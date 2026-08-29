namespace Chronicle.Core.Models
{
    public class InteractionEvent
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int MediaItemId { get; set; }
        public DateTime Timestamp { get; set; }

        /// <summary>0–100 progress percentage at the time of scrobble.</summary>
        public double? ProgressPercent { get; set; }

        /// <summary>Source device or app (e.g. "Kodi Living Room").</summary>
        public string? DeviceName { get; set; }

        /// <summary>Whether this event triggered a "watched" mark (progress >= 80%).</summary>
        public bool MarkedAsWatched { get; set; } = false;

        /// <summary>
        /// True when <see cref="Timestamp"/> is not a genuine per-item watch time but a
        /// fallback borrowed from somewhere coarser -- e.g. a SIMKL-imported episode the
        /// service never gave its own watched-at, stamped with the whole show's last-watched
        /// date instead. Lets the UI avoid presenting a fabricated time as if it were exact.
        /// </summary>
        public bool IsApproximateTimestamp { get; set; } = false;

        public DateTime CreatedAt { get; set; }

        // Navigation
        public User? User { get; set; }
        public MediaItem? MediaItem { get; set; }
    }
}
