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
        /// Queries the first available metadata provider for <paramref name="query"/> and
        /// returns raw search results. Use this to power the "Add Media" search UI.
        /// </summary>
        Task<List<MetadataCandidate>> SearchMetadataAsync(string query, string mediaTypeHint, CancellationToken ct = default);

        /// <summary>
        /// Fetches full metadata for <paramref name="externalId"/>, creates (or updates) a
        /// MediaItem, adds it to the user's library, and returns the saved item.
        /// </summary>
        Task<Chronicle.Core.Models.MediaItem> AddFromSearchAsync(string externalId, int mediaTypeId, int userId, CancellationToken ct = default);
    }
}
