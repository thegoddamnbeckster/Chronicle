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
        /// Imports scanned files directly into the library using only the data the file
        /// scanner already collected (title, year, file path). No metadata provider call is
        /// made — the background MetadataRefreshService will enrich each item with TMDB
        /// (or another provider) data automatically after import.
        /// </summary>
        Task<ImportApprovedSummary> ImportDirectAsync(DirectImportRequest request, CancellationToken ct = default);

        /// <summary>
        /// Scans a directory and returns files grouped into a candidate hierarchy
        /// (Artist→Album→Track, Show→Season→Episode) with confidence scores.
        /// No database changes are made.
        /// </summary>
        Task<Chronicle.Core.Models.Scan.ScanGroupResult> PreviewGroupedAsync(
            ScanPreviewRequest request, CancellationToken ct = default);

        /// <summary>
        /// Persists accepted ScanGroups as a MediaItem hierarchy.
        /// Root groups get UserLibrary entries; children do not.
        /// When <paramref name="manageProgress"/> is <c>false</c>, the caller owns the
        /// <see cref="ImportProgressService"/> lifecycle (Start / Complete); this method only
        /// calls <c>UpdateProcessed(offset + processed, name)</c> so the caller can accumulate
        /// progress across multiple folders into a single grand-total counter.
        /// </summary>
        Task<ImportApprovedSummary> ImportGroupsAsync(
            ImportGroupsRequest request,
            IReadOnlyList<int> userIds,
            CancellationToken ct = default,
            int progressOffset = 0,
            bool manageProgress = true);

        /// <summary>
        /// Queries the first available metadata provider for <paramref name="query"/> and
        /// returns raw search results. Use this to power the "Add Media" search UI.
        /// </summary>
        Task<List<MetadataCandidate>> SearchMetadataAsync(string query, string mediaTypeHint, CancellationToken ct = default);

        /// <summary>
        /// Fetches full metadata for <paramref name="externalId"/>, creates (or updates) a
        /// MediaItem, adds it to the user's library, and returns the saved item.
        /// </summary>
        Task<Chronicle.Core.Models.MediaItem> AddFromSearchAsync(string externalId, int mediaTypeId, int userId, CancellationToken ct = default, List<ContributingExternalId>? contributingExternalIds = null);

        /// <summary>
        /// Returns the confidence threshold for the given media type. Reads the per-type key
        /// <c>confidence_threshold_{mediaTypeName}</c> from the file scanner plugin's stored
        /// settings, then falls back to the legacy global <c>confidence_threshold</c> key,
        /// then to the plugin's built-in default (75).
        /// </summary>
        Task<int> GetConfidenceThresholdAsync(string mediaTypeName, CancellationToken ct = default);

        /// <summary>
        /// Returns the global fallback confidence threshold (no media type specified).
        /// Prefer the overload that accepts a media type name wherever one is available.
        /// </summary>
        Task<int> GetConfidenceThresholdAsync(CancellationToken ct = default);

        /// <summary>
        /// One-time data migration: finds all file-scanner MediaItems where
        /// <c>fileScanner.folderPath</c> was stored as JSON null and backfills it
        /// from the first entry in <c>filePaths</c>. Safe to call on every startup —
        /// items already having a non-null folderPath are skipped.
        /// </summary>
        Task BackfillFolderPathsAsync(CancellationToken ct = default);

        /// <summary>
        /// One-time data migration: populates MediaItemKnownFileNames (the indexed lookup
        /// ScraperController.SearchMovies's filename fast-path now uses) for every MediaItem
        /// that already has fileScanner.filePaths from before this table existed. Safe to call
        /// on every startup -- gated on a durable completion marker (an AppSettings row, not
        /// "does the table have any rows"), and resumable from where it left off if interrupted
        /// mid-run, since every ordinary write to fileScanner data already keeps the table
        /// current going forward regardless. Returns the number of items backfilled.
        /// </summary>
        Task<int> BackfillKnownFileNamesAsync(CancellationToken ct = default);

        /// <summary>
        /// Ensures MediaItemKnownFileNames has a row for this exact (item, fileName) pair --
        /// additive only, never removes other rows, since this and the fileScanner-derived set
        /// SyncKnownFileNamesAsync reconciles are independent sources that can both legitimately
        /// be known for the same item. For the "slow path" scraper fallback (an item resolved
        /// via title/year matching or source-browsing, with no real fileScanner record at all)
        /// -- without this, such an item would never become findable through SearchMovies's own
        /// filename fast-path, silently falling back to a full candidate-list load on every
        /// future scrape. Does not save -- the caller controls when to commit.
        /// </summary>
        Task EnsureKnownFileNameAsync(Chronicle.Core.Models.MediaItem item, string fileName, CancellationToken ct = default);
    }
}
