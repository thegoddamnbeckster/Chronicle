namespace Chronicle.Core.Models
{
    public class MediaItem
    {
        public int Id { get; set; }
        public int MediaTypeId { get; set; }
        public int? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? SortName { get; set; }

        /// <summary>
        /// Lowercased, punctuation-stripped name used for duplicate detection.
        /// Populated at creation/update time by MediaItemNormalizer.NormalizeName().
        /// </summary>
        public string? NormalizedName { get; set; }
        public int? Year { get; set; }
        public string? Overview { get; set; }
        public string? PosterUrl { get; set; }
        public int? RuntimeMinutes { get; set; }

        /// <summary>
        /// Promoted canonical fields for MediaTypeName == "people" (birth_date/death_date in
        /// MetadataResolutionService.FieldMap), same promotion pattern as PosterUrl/RuntimeMinutes
        /// above -- needed so the People grid can render birth/death text and a deceased badge on
        /// every card without per-card resolution-blob parsing. Null for every other media type.
        /// </summary>
        public DateTime? BirthDate { get; set; }
        public DateTime? DeathDate { get; set; }

        /// <summary>Hierarchy depth (0 = root, 1 = child, 2 = grandchild).</summary>
        public int HierarchyLevel { get; set; } = 0;

        /// <summary>Episode/season number within parent.</summary>
        public int? Number { get; set; }

        /// <summary>Extra type-specific fields stored as JSON.</summary>
        public string? MetadataJson { get; set; }

        /// <summary>
        /// True when this item was auto-created as a collection stub (a movie belonging to a
        /// collection the user doesn't own yet). Stubs are hidden or shown based on user preference.
        /// </summary>
        public bool IsStub { get; set; } = false;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public MediaType? MediaType { get; set; }
        public MediaItem? Parent { get; set; }
        public ICollection<MediaItem> Children { get; set; } = new List<MediaItem>();
        public ICollection<MediaExternalId> ExternalIds { get; set; } = new List<MediaExternalId>();
        public ICollection<MediaItemAlias> Aliases { get; set; } = new List<MediaItemAlias>();
        public ICollection<MediaItemMerge> MergesAsWinner { get; set; } = new List<MediaItemMerge>();
    }
}
