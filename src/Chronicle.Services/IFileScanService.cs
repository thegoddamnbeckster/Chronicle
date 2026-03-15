namespace Chronicle.Services
{
    public interface IFileScanService
    {
        Task<FileScanSummary> ScanAsync(FileScanRequest request, int userId, CancellationToken ct = default);
        Task<(bool Available, string[] SupportedMediaTypeNames)> GetStatusAsync();

        /// <summary>
        /// Scans a directory and returns what was found without touching the database.
        /// The caller reviews the results, then calls IdentifyAsync.
        /// </summary>
        Task<ScanPreview> PreviewAsync(ScanPreviewRequest request, CancellationToken ct = default);

        /// <summary>
        /// For each scanned file, queries the first available metadata provider and
        /// returns a ranked list of candidates for the user to approve or dismiss.
        /// </summary>
        Task<IdentifyResult> IdentifyAsync(IdentifyRequest request, CancellationToken ct = default);

        /// <summary>
        /// Fetches full metadata for each approved (filePath, externalId) pair,
        /// creates MediaItems, stores external IDs, and adds them to the user's library.
        /// </summary>
        Task<ImportApprovedSummary> ImportApprovedAsync(ImportApprovedRequest request, CancellationToken ct = default);

        /// <summary>
        /// Re-fetches full metadata from the first available metadata provider for an
        /// existing MediaItem using its stored external IDs. Updates Name, Year, Overview,
        /// PosterUrl, and RuntimeMinutes in-place.
        /// Returns null if the item has no external IDs or no metadata provider is loaded.
        /// </summary>
        Task<Chronicle.Core.Models.MediaItem?> RefreshMetadataAsync(int mediaItemId, CancellationToken ct = default);

        /// <summary>
        /// Imports scanned files directly into the library using only the data the file
        /// scanner already collected (title, year, file path). No metadata provider call is
        /// made — the background MetadataRefreshService will enrich each item with TMDB
        /// (or another provider) data automatically after import.
        /// </summary>
        Task<ImportApprovedSummary> ImportDirectAsync(DirectImportRequest request, CancellationToken ct = default);

        /// <summary>
        /// Queries the first available metadata provider for <paramref name="query"/> and
        /// returns raw search results. Use this to power the "Add Media" search UI.
        /// </summary>
        Task<List<MetadataCandidate>> SearchMetadataAsync(string query, string mediaTypeHint, CancellationToken ct = default);

        /// <summary>
        /// Fetches full metadata for <paramref name="externalId"/>, creates (or updates) a
        /// MediaItem, adds it to the user's library, and returns the saved item.
        /// </summary>
        Task<Chronicle.Core.Models.MediaItem> AddFromSearchAsync(string externalId, int mediaTypeId, int userId, CancellationToken ct = default);

        /// <summary>
        /// Re-identifies an existing MediaItem using a user-supplied TMDB reference.
        /// <paramref name="input"/> may be a bare numeric ID (assumed movie), a typed ID
        /// ("movie:1159831", "tv:1396"), or a full TMDB URL
        /// ("https://www.themoviedb.org/movie/1159831-the-bride").
        /// Replaces the item's name, year, overview, poster, and TMDB metadata in-place.
        /// </summary>
        Task<Chronicle.Core.Models.MediaItem> ReidentifyAsync(int mediaItemId, string input, CancellationToken ct = default);
    }
}
