namespace Chronicle.Core.Models
{
    public class MediaItem
    {
        public int Id { get; set; }
        public int MediaTypeId { get; set; }
        public int? ParentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? SortName { get; set; }
        public int? Year { get; set; }
        public string? Overview { get; set; }
        public string? PosterUrl { get; set; }
        public int? RuntimeMinutes { get; set; }

        /// <summary>Hierarchy depth (0 = root, 1 = child, 2 = grandchild).</summary>
        public int HierarchyLevel { get; set; } = 0;

        /// <summary>Episode/season number within parent.</summary>
        public int? Number { get; set; }

        /// <summary>Extra type-specific fields stored as JSON.</summary>
        public string? MetadataJson { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation
        public MediaType? MediaType { get; set; }
        public MediaItem? Parent { get; set; }
        public ICollection<MediaItem> Children { get; set; } = new List<MediaItem>();
        public ICollection<MediaExternalId> ExternalIds { get; set; } = new List<MediaExternalId>();
    }
}
