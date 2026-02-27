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

        public DateTime CreatedAt { get; set; }

        // Navigation
        public User? User { get; set; }
        public MediaItem? MediaItem { get; set; }
    }
}
