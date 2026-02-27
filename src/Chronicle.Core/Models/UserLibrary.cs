namespace Chronicle.Core.Models
{
    public enum LibraryStatus
    {
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
        public string? Notes { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Navigation
        public User? User { get; set; }
        public MediaItem? MediaItem { get; set; }
    }
}
