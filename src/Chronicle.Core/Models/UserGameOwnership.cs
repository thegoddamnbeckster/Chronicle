namespace Chronicle.Core.Models
{
    /// <summary>
    /// One user's relationship to one title on one platform: owned or wishlisted, hours played, last played,
    /// acquired. Keyed on (user, source, source id) so a game held on two platforms is one <see cref="MediaItem"/>
    /// with two rows, and two different games that happen to share a title never collide.
    /// <para>
    /// <see cref="MediaItemId"/> is nullable with SET NULL on delete. A row whose item is gone is a tombstone: the user
    /// removed that title from Chronicle, so the importer must not recreate it.
    /// </para>
    /// </summary>
    public class UserGameOwnership
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int? MediaItemId { get; set; }

        /// <summary>Platform key, e.g. "steam".</summary>
        public string Source { get; set; } = "";
        /// <summary>The platform's own id for the title, e.g. the Steam appid.</summary>
        public string SourceId { get; set; } = "";
        /// <summary>"PC", "PS5", "Android" ...</summary>
        public string Platform { get; set; } = "";

        /// <summary>Null = the platform does not say; 0 = owned and never started.</summary>
        public int? PlaytimeMinutes { get; set; }
        public DateTime? LastPlayedAt { get; set; }
        public DateTime? AcquiredAt { get; set; }
        public bool OwnedViaSubscription { get; set; }
        public bool IsWishlist { get; set; }

        /// <summary>JSON array of tags ("dlc", "demo", "unclassified" ...). Null = none.</summary>
        public string? TagsJson { get; set; }
        /// <summary>The platform's complete record for this title, as given.</summary>
        public string? RawJson { get; set; }

        /// <summary>The library status this importer last wrote for the item. While the user's status still equals it,
        /// the importer may advance it (Unwatched to Watching); once the user changed it, the importer never touches it.</summary>
        public LibraryStatus? ImportStatusApplied { get; set; }

        public DateTime FirstSeenAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public User? User { get; set; }
        public MediaItem? MediaItem { get; set; }
    }
}
