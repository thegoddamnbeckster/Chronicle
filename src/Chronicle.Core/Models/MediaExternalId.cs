namespace Chronicle.Core.Models
{
    public class MediaExternalId
    {
        public int Id { get; set; }
        public int MediaItemId { get; set; }

        /// <summary>Source name (e.g. "tmdb", "imdb", "tvdb", "musicbrainz").</summary>
        public string Source { get; set; } = string.Empty;

        public string ExternalId { get; set; } = string.Empty;

        // Navigation
        public MediaItem? MediaItem { get; set; }
    }
}
