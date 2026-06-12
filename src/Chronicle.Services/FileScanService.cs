using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Core.Models.Scan;
using Chronicle.Data;
using Chronicle.Core.Exceptions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using Chronicle.Services.Scan;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services
{
    public class FileScanService : IFileScanService
    {
        private readonly ChronicleDbContext _context;
        private readonly IPluginRegistry _registry;
        private readonly IPluginSettingsProtector _protector;
        private readonly ScanProgressService _progress;
        private readonly ImportProgressService _importProgress;
        private readonly IScanGroupingService _groupingService;
        private readonly ILogger _log = Log.ForContext<FileScanService>();

        public FileScanService(ChronicleDbContext context, IPluginRegistry registry,
            IPluginSettingsProtector protector,
            ScanProgressService progress, ImportProgressService importProgress,
            IScanGroupingService groupingService)
        {
            _context = context;
            _registry = registry;
            _protector = protector;
            _progress = progress;
            _importProgress = importProgress;
            _groupingService = groupingService;
        }

        public async Task<(bool Available, string[] SupportedMediaTypeNames)> GetStatusAsync()
        {
            var scanners = _registry.GetFileScannerPlugins();
            if (!scanners.Any())
                return (false, []);

            // Return all media types defined in the DB so the dropdown is never
            // out of sync with what the user has configured.
            var names = await _context.MediaTypes
                .OrderBy(t => t.Name)
                .Select(t => t.Name)
                .ToArrayAsync();

            return (true, names);
        }

        public async Task<FileScanSummary> ScanAsync(FileScanRequest request, int userId, CancellationToken ct = default)
        {
            if (!Directory.Exists(request.Path))
            {
                var hint = BuildMappedDriveHint(request.Path);
                throw new DirectoryNotFoundException(
                    $"Scan path does not exist or is not accessible: {request.Path}.{hint}");
            }

            // Verify media type exists
            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            // Prefer a scanner that explicitly declares this media type; fall back to the first
            // available scanner so user-defined types (e.g. audiobooks) still work.
            var allScanners = _registry.GetFileScannerPlugins();
            var scanner = allScanners
                .FirstOrDefault(s => s.GetSupportedMediaTypes()
                    .Any(m => string.Equals(m.MediaTypeName, mediaType.Name, StringComparison.OrdinalIgnoreCase)))
                ?? allScanners.FirstOrDefault();

            if (scanner is null)
                throw new InvalidOperationException("No file scanner plugin is loaded.");

            var threshold = request.ConfidenceThreshold == 80
                ? await GetConfidenceThresholdAsync(ct)
                : request.ConfidenceThreshold;

            _log.Information("Starting file scan of {Path} (recursive={Recursive}, threshold={Threshold}, mediaType={MediaType})",
                request.Path, request.Recursive, threshold, mediaType.Name);

            var scannedFiles = await scanner.ScanDirectoryAsync(request.Path, request.Recursive, ct);

            // Audiobooks: each book folder is one library entry regardless of how many
            // audio files (parts) or support files (covers, extras) it contains.
            if (string.Equals(mediaType.Name, "audiobooks", StringComparison.OrdinalIgnoreCase))
                scannedFiles = CollapseAudiobooksToFolders(scannedFiles, request.Path);

            // Audiobooks with a 3-level hierarchy (Author → Series? → Book):
            // group into author/series tree so the library shows Authors as root items,
            // not individual book titles.
            if (mediaType.HierarchyLevels >= 3 &&
                string.Equals(mediaType.Name, "audiobooks", StringComparison.OrdinalIgnoreCase))
                return await ScanAudiobooksHierarchicallyAsync(
                    scannedFiles, mediaType, userId, threshold, ct);

            var added = 0;
            var alreadyInLibrary = 0;
            var skippedFiles = new List<SkippedFile>();

            foreach (var file in scannedFiles)
            {
                ct.ThrowIfCancellationRequested();

                // Below threshold — report but don't add
                if (file.ConfidenceScore < threshold)
                {
                    skippedFiles.Add(new SkippedFile(file.FilePath, file.ParsedTitle, file.ConfidenceScore));
                    continue;
                }

                // Try to find an existing media item
                var mediaItem = await FindExistingItemAsync(file, request.MediaTypeId, ct);

                bool isNew = mediaItem is null;
                if (isNew)
                {
                    mediaItem = await CreateStubItemAsync(file, request.MediaTypeId, ct);
                    _log.Debug("Created stub media item {Title} ({Year})", file.ParsedTitle, file.ParsedYear);
                }

                // Store external ID if we have one and the item is new (or it's missing)
                if (file.SuggestedExternalId is not null)
                    await UpsertExternalIdAsync(mediaItem!.Id, file.SuggestedExternalId, ct);

                // Upsert library entry
                var existing = await _context.UserLibraries
                    .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItem!.Id, ct);

                if (existing is null)
                {
                    _context.UserLibraries.Add(new UserLibrary
                    {
                        UserId = userId,
                        MediaItemId = mediaItem!.Id,
                        Status = LibraryStatus.Unwatched,
                        AddedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                    added++;
                }
                else
                {
                    alreadyInLibrary++;
                }
            }

            await _context.SaveChangesAsync(ct);

            _log.Information("Scan complete: {Added} added, {AlreadyInLibrary} already in library, {Skipped} below threshold",
                added, alreadyInLibrary, skippedFiles.Count);

            return new FileScanSummary(added, skippedFiles.Count, alreadyInLibrary, skippedFiles);
        }

        // ── Preview ───────────────────────────────────────────────────────────────

        public async Task<ScanPreview> PreviewAsync(ScanPreviewRequest request, CancellationToken ct = default)
        {
            // Validate path accessibility before handing off to the scanner plugin.
            // On Windows, mapped network drives (e.g. H:\) may not be visible to child
            // processes that were started in a different session.  Providing a clear error
            // here — along with the UNC equivalent — is much more helpful than the raw
            // "Scan path does not exist" message that the plugin would otherwise surface.
            if (!Directory.Exists(request.Path))
            {
                var hint = BuildMappedDriveHint(request.Path);
                throw new DirectoryNotFoundException(
                    $"Scan path does not exist or is not accessible: {request.Path}.{hint}");
            }

            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            var scanner = _registry.GetFileScannerPlugins()
                .FirstOrDefault(s => s.GetSupportedMediaTypes()
                    .Any(m => string.Equals(m.MediaTypeName, mediaType.Name, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException($"No file scanner plugin supports media type '{mediaType.Name}'.");

            _log.Information("Preview scan of {Path} (recursive={Recursive}, mediaType={MediaType})",
                request.Path, request.Recursive, mediaType.Name);

            // Build the ordered list of directories to scan.
            // Scanning each directory non-recursively lets us report per-folder progress.
            var dirsToScan = new List<string> { request.Path };
            if (request.Recursive)
            {
                try
                {
                    dirsToScan.AddRange(
                        Directory.GetDirectories(request.Path, "*", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to enumerate subdirectories of {Path} — " +
                        "falling back to single recursive scan", request.Path);
                    // Fall back: single call with recursive=true (no per-folder progress)
                    _progress.Start(1);
                    _progress.UpdateFolder(request.Path, 1, 0);
                    List<Chronicle.Plugins.Models.ScannedFile> fallback;
                    try
                    {
                        fallback = await scanner.ScanDirectoryAsync(request.Path, recursive: true, ct);
                    }
                    catch (DirectoryNotFoundException dnfe)
                    {
                        var hint = BuildMappedDriveHint(request.Path);
                        throw new DirectoryNotFoundException(
                            $"Scan path is not accessible: {request.Path}.{hint}", dnfe);
                    }
                    _progress.Complete();
                    return BuildPreview(fallback);
                }
            }

            _progress.Start(dirsToScan.Count);
            var allFiles = new List<Chronicle.Plugins.Models.ScannedFile>(capacity: 256);

            for (int i = 0; i < dirsToScan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var dir = dirsToScan[i];
                _progress.UpdateFolder(dir, i + 1, allFiles.Count);

                try
                {
                    var files = await scanner.ScanDirectoryAsync(dir, recursive: false, ct);
                    allFiles.AddRange(files);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Skipping directory {Dir} during preview", dir);
                }
            }

            _progress.Complete();

            _log.Information("Preview complete: {Count} files found across {Dirs} directories",
                allFiles.Count, dirsToScan.Count);

            return BuildPreview(allFiles);
        }

        private static ScanPreview BuildPreview(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> scannedFiles)
        {
            var results = scannedFiles
                .Select(f => new ScannedFileResult(
                    f.FilePath,
                    f.ParsedTitle,
                    f.ParsedYear,
                    f.ConfidenceScore,
                    f.SuggestedExternalId,
                    string.IsNullOrEmpty(f.MediaTypeHint) ? "movie" : f.MediaTypeHint))
                .ToList();
            return new ScanPreview(results);
        }

        /// <summary>
        /// Returns an optional hint message when a path looks like a mapped drive letter.
        /// Mapped drives are per-user-session; a spawned process may not inherit them.
        /// The hint recommends switching to the equivalent UNC path.
        /// </summary>
        private static string BuildMappedDriveHint(string path)
        {
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                return $" If '{char.ToUpper(path[0])}:' is a mapped network drive, " +
                       "try entering the UNC path instead (e.g. \\\\server\\share\\...). " +
                       "Mapped drives may not be visible to background processes.";
            }
            return string.Empty;
        }

        // ── Identify ──────────────────────────────────────────────────────────────

        public async Task<IdentifyResult> IdentifyAsync(IdentifyRequest request, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No metadata provider is loaded. Install and configure a metadata plugin (e.g. TMDB).");

            var identifications = new List<FileIdentification>();

            foreach (var file in request.Files)
            {
                ct.ThrowIfCancellationRequested();

                var candidates = new List<MetadataCandidate>();
                try
                {
                    // High-confidence NFO match — fetch by external ID directly
                    if (file.SuggestedExternalId is not null && file.ConfidenceScore >= 85)
                    {
                        var meta = await provider.GetByIdAsync(file.SuggestedExternalId, ct);
                        candidates.Add(new MetadataCandidate(
                            meta.ExternalId,
                            meta.Title,
                            meta.Year,
                            meta.PosterUrl,
                            meta.Overview,
                            meta.Rating,
                            95));
                    }
                    else
                    {
                        // Title-only search — do NOT append the year to the query string.
                        // TMDB treats the query as plain text; the year is not in the
                        // stored title so appending it returns zero results.  ScoreCandidate
                        // already handles year matching on the returned candidates.
                        var query = file.ParsedTitle;

                        var searchResults = await provider.SearchAsync(
                            new MediaSearchContext(query, file.ParsedYear), ct);

                        foreach (var c in searchResults.Take(5))
                        {
                            var r = c.Metadata;
                            candidates.Add(new MetadataCandidate(
                                r.ExternalId,
                                r.Title,
                                r.Year,
                                r.PosterUrl,
                                r.Overview,
                                r.Rating,
                                ScoreCandidate(file, r)));
                        }

                        candidates = [.. candidates.OrderByDescending(c => c.MatchScore)];
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to identify '{Title}'", file.ParsedTitle);
                }

                identifications.Add(new FileIdentification(file, candidates));
            }

            return new IdentifyResult(identifications);
        }

        // ── Import approved ───────────────────────────────────────────────────────

        public async Task<ImportApprovedSummary> ImportApprovedAsync(
            ImportApprovedRequest request, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault()
                ?? throw new InvalidOperationException("No metadata provider is loaded.");

            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            var imported = 0;
            var failed = 0;
            var failures = new List<string>();

            foreach (var approval in request.Approvals)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var meta = await provider.GetByIdAsync(approval.ExternalId, ct);
                    var (source, extId) = ParseSuggestedExternalId(approval.ExternalId);

                    // Check if an item with this external ID already exists
                    var existingExt = await _context.MediaExternalIds
                        .Include(e => e.MediaItem)
                        .FirstOrDefaultAsync(
                            e => e.Source == source && e.ExternalId == extId
                              && e.MediaItem!.MediaTypeId == request.MediaTypeId, ct);

                    MediaItem mediaItem;
                    if (existingExt?.MediaItem is not null)
                    {
                        // Refresh metadata on existing item
                        mediaItem = existingExt.MediaItem;
                        mediaItem.Name           = meta.Title;
                        mediaItem.Year           = meta.Year;
                        mediaItem.Overview       = meta.Overview;
                        mediaItem.PosterUrl      = meta.PosterUrl;
                        mediaItem.RuntimeMinutes = meta.RuntimeMinutes;
                        mediaItem.MetadataJson   = SerializeMetadata(tmdbMeta: meta, existingJson: mediaItem.MetadataJson);
                        mediaItem.UpdatedAt      = DateTime.UtcNow;
                    }
                    else
                    {
                        // Fall back to title+year match so a file-scanner stub with the same
                        // title (in any normalised variant) is reused instead of duplicated.
                        var existingByTitle = await FindByTitleAsync(meta.Title, request.MediaTypeId, meta.Year, ct);
                        if (existingByTitle is not null)
                        {
                            mediaItem                  = existingByTitle;
                            mediaItem.Name             = meta.Title;
                            mediaItem.Year             = meta.Year;
                            mediaItem.Overview         = meta.Overview;
                            mediaItem.PosterUrl        = meta.PosterUrl;
                            mediaItem.RuntimeMinutes   = meta.RuntimeMinutes;
                            mediaItem.MetadataJson     = SerializeMetadata(tmdbMeta: meta, existingJson: mediaItem.MetadataJson);
                            mediaItem.UpdatedAt        = DateTime.UtcNow;
                            await UpsertExternalIdAsync(mediaItem.Id, approval.ExternalId, ct);
                        }
                        else
                        {
                            mediaItem = new MediaItem
                            {
                                MediaTypeId    = request.MediaTypeId,
                                Name           = meta.Title,
                                Year           = meta.Year,
                                Overview       = meta.Overview,
                                PosterUrl      = meta.PosterUrl,
                                RuntimeMinutes = meta.RuntimeMinutes,
                                MetadataJson   = SerializeMetadata(tmdbMeta: meta),
                                HierarchyLevel = 0,
                                CreatedAt      = DateTime.UtcNow,
                                UpdatedAt      = DateTime.UtcNow,
                            };
                            _context.MediaItems.Add(mediaItem);
                            await _context.SaveChangesAsync(ct);
                            await UpsertExternalIdAsync(mediaItem.Id, approval.ExternalId, ct);
                        }
                    }

                    // Upsert user library entry
                    var libEntry = await _context.UserLibraries
                        .FirstOrDefaultAsync(l => l.UserId == request.UserId && l.MediaItemId == mediaItem.Id, ct);

                    if (libEntry is null)
                    {
                        _context.UserLibraries.Add(new UserLibrary
                        {
                            UserId = request.UserId,
                            MediaItemId = mediaItem.Id,
                            Status = LibraryStatus.Unwatched,
                            AddedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                        });
                    }

                    await _context.SaveChangesAsync(ct);
                    imported++;
                    _log.Information("Imported '{Title}' ({Year}) [{ExternalId}]",
                        meta.Title, meta.Year, approval.ExternalId);
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{approval.ExternalId}: {ex.Message}");
                    _log.Warning(ex, "Failed to import {ExternalId}", approval.ExternalId);
                }
            }

            _log.Information("Import complete: {Imported} imported, {Failed} failed", imported, failed);
            return new ImportApprovedSummary(imported, failed, failures);
        }

        // ── Direct import (no metadata provider) ─────────────────────────────────

        public async Task<ImportApprovedSummary> ImportDirectAsync(
            DirectImportRequest request, CancellationToken ct = default)
        {
            var mediaType = await _context.MediaTypes
                .FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            int imported = 0, failed = 0, duplicates = 0;
            var failures = new List<string>();

            if (mediaType.HierarchyLevels >= 3)
            {
                // Three-tier import: root (show/artist) → mid (season/album) → leaf (episode/track)
                (imported, failed, duplicates, failures) = await ImportHierarchicalAsync(
                    request.Files, mediaType, [request.UserId], ct);
            }
            else
            {
                // Flat import.
                // Pass 1: check for duplicates and create only new items.
                // Pass 2: upsert external IDs.
                // Pass 3: upsert a library entry for the requesting user (other users get
                //         entries auto-created by GetForUserAsync on their next library view).
                var pairs = new List<(DirectImportFile file, MediaItem item)>(request.Files.Count);

                foreach (var file in request.Files)
                {
                    ct.ThrowIfCancellationRequested();

                    // Skip files whose path is already registered in the database.
                    var existingItem = await FindItemByFilePathAsync(file.FilePath, mediaType.Id, ct);
                    if (existingItem is not null)
                    {
                        _log.Information("Duplicate file '{Path}' already imported as '{Title}' (id={Id}) — skipping",
                            file.FilePath, existingItem.Name, existingItem.Id);
                        duplicates++;
                        pairs.Add((file, existingItem));
                        continue;
                    }

                    var item = new MediaItem
                    {
                        Name           = file.ParsedTitle,
                        MediaTypeId    = mediaType.Id,
                        ParentId       = null,
                        HierarchyLevel = 0,
                        Year           = file.ParsedYear,
                        Number         = file.EpisodeNumber ?? file.AudioTrackNumber,
                        MetadataJson   = SerializeMetadata(scannerFilePath: file.FilePath),
                        CreatedAt      = DateTime.UtcNow,
                        UpdatedAt      = DateTime.UtcNow,
                    };
                    _context.MediaItems.Add(item);
                    pairs.Add((file, item));
                }

                // Save new items (EF Core populates auto-generated IDs).
                var newCount = pairs.Count - duplicates;
                if (newCount > 0)
                    await _context.SaveChangesAsync(ct);
                imported = newCount;

                // Upsert ExternalIds for any file that carries an NFO/external ID hint.
                foreach (var (file, item) in pairs)
                {
                    if (!string.IsNullOrEmpty(file.SuggestedExternalId))
                        await UpsertExternalIdAsync(item.Id, file.SuggestedExternalId!, ct);
                }

                // Upsert a library entry for the requesting user.
                // Other users get entries auto-created by GetForUserAsync on their first library view.
                var allItemIds = pairs.Select(p => p.item.Id).ToList();
                var existingLibSet = new HashSet<int>(
                    await _context.UserLibraries
                        .Where(l => l.UserId == request.UserId && allItemIds.Contains(l.MediaItemId))
                        .Select(l => l.MediaItemId)
                        .ToListAsync(ct));

                foreach (var (_, item) in pairs)
                {
                    if (!existingLibSet.Contains(item.Id))
                    {
                        _context.UserLibraries.Add(new UserLibrary
                        {
                            UserId      = request.UserId,
                            MediaItemId = item.Id,
                            Status      = LibraryStatus.Unwatched,
                            AddedAt     = DateTime.UtcNow,
                            UpdatedAt   = DateTime.UtcNow,
                        });
                    }
                }

                // Final save: ExternalIds + UserLibrary entries in one round-trip.
                await _context.SaveChangesAsync(ct);
            }

            _log.Information("Direct import complete: {Imported} imported, {Duplicates} skipped (duplicate), {Failed} failed",
                imported, duplicates, failed);
            return new ImportApprovedSummary(imported, failed, failures, duplicates);
        }

        // ── Audiobook hierarchical scan ───────────────────────────────────────────

        /// <summary>
        /// Variant of ScanAsync for audiobooks with HierarchyLevels ≥ 3.
        /// Groups collapsed files into the Author → Series? → Book tree and creates
        /// the correct hierarchy in the DB.  Library entries are attached at the
        /// Author (L0) level, matching how Music and TV attach at Artist/Show level.
        /// </summary>
        private async Task<FileScanSummary> ScanAudiobooksHierarchicallyAsync(
            List<Chronicle.Plugins.Models.ScannedFile> collapsed,
            MediaType mediaType,
            int userId,
            int threshold,
            CancellationToken ct)
        {
            var added          = 0;
            var alreadyInLib   = 0;
            var skippedFiles   = new List<SkippedFile>();

            var authorGroups = GroupAudiobooksByAuthorAndSeries(collapsed);

            foreach (var authorGroup in authorGroups)
            {
                ct.ThrowIfCancellationRequested();

                // Find or create Author (L0)
                var author = await FindOrCreateParentAsync(
                    authorGroup.Name, mediaType.Id, parentId: null, hierarchyLevel: 0, ct);

                // Library entry lives at the Author level (one entry per author, not per book)
                var entryExists = await _context.UserLibraries
                    .AnyAsync(l => l.UserId == userId && l.MediaItemId == author.Id, ct);
                if (!entryExists)
                {
                    _context.UserLibraries.Add(new UserLibrary
                    {
                        UserId      = userId,
                        MediaItemId = author.Id,
                        Status      = LibraryStatus.Unwatched,
                        AddedAt     = DateTime.UtcNow,
                        UpdatedAt   = DateTime.UtcNow,
                    });
                    added++;
                }
                else { alreadyInLib++; }

                // Process children: may be Series (with Book grandchildren) or standalone Books
                foreach (var child in authorGroup.Children)
                {
                    ct.ThrowIfCancellationRequested();

                    if (child.Children.Count > 0)
                    {
                        // child is a Series (L1); grandchildren are Books (L2)
                        var series = await FindOrCreateParentAsync(
                            child.Name, mediaType.Id, author.Id, hierarchyLevel: 1, ct);

                        foreach (var book in child.Children)
                            await ImportAudiobookBookAsync(
                                book, mediaType.Id, series.Id, hierarchyLevel: 2,
                                threshold, skippedFiles, ct);
                    }
                    else
                    {
                        // Standalone book (L1 directly under author)
                        await ImportAudiobookBookAsync(
                            child, mediaType.Id, author.Id, hierarchyLevel: 1,
                            threshold, skippedFiles, ct);
                    }
                }
            }

            await _context.SaveChangesAsync(ct);

            _log.Information(
                "Audiobook scan complete: {Added} new authors, {Existing} existing, {Skipped} below threshold",
                added, alreadyInLib, skippedFiles.Count);

            return new FileScanSummary(added, skippedFiles.Count, alreadyInLib, skippedFiles);
        }

        /// <summary>
        /// Find-or-create a single audiobook Book item under a parent (Author or Series).
        /// Skips items below the confidence threshold and handles re-scan de-duplication
        /// by matching on the stored folder path in MetadataJson.
        /// </summary>
        private async Task ImportAudiobookBookAsync(
            Chronicle.Core.Models.Scan.ScanGroup book,
            int mediaTypeId,
            int parentId,
            int hierarchyLevel,
            int threshold,
            List<SkippedFile> skipped,
            CancellationToken ct)
        {
            var folderPath = book.FolderPath ?? book.Files.FirstOrDefault() ?? string.Empty;
            var scoreInt   = (int)Math.Round(book.ConfidenceScore * 100);

            if (scoreInt < threshold)
            {
                skipped.Add(new SkippedFile(folderPath, book.Name, scoreInt));
                return;
            }

            // De-duplicate on re-scan: if a media item with this folder path already exists,
            // update its parent/level in case the hierarchy was rebuilt (e.g. author renamed),
            // but don't create a duplicate.
            var existing = await FindItemByFilePathAsync(folderPath, mediaTypeId, ct);
            if (existing is not null)
            {
                if (existing.ParentId != parentId || existing.HierarchyLevel != hierarchyLevel)
                {
                    existing.ParentId      = parentId;
                    existing.HierarchyLevel = hierarchyLevel;
                }
                return;
            }

            var item = new MediaItem
            {
                Name           = book.Name,
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                Year           = book.Year,
                MetadataJson   = SerializeMetadata(scannerFilePath: folderPath),
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync(ct);
        }

        private async Task<(int imported, int failed, int duplicates, List<string> failures)> ImportHierarchicalAsync(
            List<DirectImportFile> files,
            MediaType mediaType,
            IReadOnlyList<int> userIds,
            CancellationToken ct)
        {
            int imported = 0, failed = 0, duplicates = 0;
            var failures = new List<string>();

            var showGroups = GroupByShow(files.Select(f => new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath      = f.FilePath,
                ParsedTitle   = f.ParsedTitle,
                ParsedYear    = f.ParsedYear,
                ShowTitle     = f.ShowTitle,
                SeasonNumber  = f.SeasonNumber,
                EpisodeNumber = f.EpisodeNumber,
                EpisodeTitle  = f.EpisodeTitle,
            }));

            // Pre-index files by path for O(1) episode lookup (case-insensitive for Windows paths)
            var fileByPath = files.ToDictionary(f => f.FilePath, StringComparer.OrdinalIgnoreCase);

            foreach (var show in showGroups)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Level 0 — root item (show / artist)
                    var rootItem = await FindOrCreateParentAsync(
                        show.ShowTitle, mediaType.Id, parentId: null, hierarchyLevel: 0, ct);

                    // Upsert library entry for root only — for all users
                    await UpsertLibraryEntryAsync(userIds, rootItem.Id, ct);

                    foreach (var (seasonNum, season) in show.Seasons)
                    {
                        // Level 1 — mid item (season / album)
                        var seasonName = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";
                        var midItem = await FindOrCreateParentAsync(
                            seasonName, mediaType.Id, rootItem.Id, hierarchyLevel: 1, ct);

                        foreach (var ep in season.Episodes)
                        {
                            try
                            {
                                // Find the original DirectImportFile to get all its fields
                                if (!fileByPath.TryGetValue(ep.FilePath, out var file))
                                {
                                    _log.Warning("Could not find original file for path {Path} — skipping", ep.FilePath);
                                    failed++;
                                    continue;
                                }
                                var epName = ep.EpisodeTitle ?? ep.ParsedTitle;

                                var wasNew = await ImportSingleFileAsync(file with { ParsedTitle = epName },
                                    mediaType.Id, midItem.Id, hierarchyLevel: 2,
                                    userIds, addLibraryEntry: false, ct);
                                if (wasNew) imported++;
                                else duplicates++;
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "Skipping episode {Path}", ep.FilePath);
                                failures.Add($"{ep.FilePath}: {ex.Message}");
                                failed++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Skipping show group {Show}", show.ShowTitle);
                    failures.Add($"Show '{show.ShowTitle}': {ex.Message}");
                    failed += show.Seasons.Values.Sum(s => s.Episodes.Count);
                }
            }

            return (imported, failed, duplicates, failures);
        }

        private async Task<bool> ImportSingleFileAsync(
            DirectImportFile file,
            int mediaTypeId,
            int? parentId,
            int hierarchyLevel,
            IReadOnlyList<int> userIds,
            bool addLibraryEntry,
            CancellationToken ct)
        {
            // Skip if a media item with the same file path already exists.
            var existing = await FindItemByFilePathAsync(file.FilePath, mediaTypeId, ct);
            if (existing is not null)
            {
                _log.Information("Duplicate file '{Path}' already imported as '{Title}' (id={Id}) — skipping",
                    file.FilePath, existing.Name, existing.Id);
                if (addLibraryEntry)
                    await UpsertLibraryEntryAsync(userIds, existing.Id, ct);
                return false;
            }

            var item = new MediaItem
            {
                Name           = file.ParsedTitle,
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                Year           = file.ParsedYear,
                Number         = file.EpisodeNumber ?? file.AudioTrackNumber,
                MetadataJson   = SerializeMetadata(scannerFilePath: file.FilePath),
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync(ct);

            if (!string.IsNullOrEmpty(file.SuggestedExternalId))
                await UpsertExternalIdAsync(item.Id, file.SuggestedExternalId!, ct);

            if (addLibraryEntry)
                await UpsertLibraryEntryAsync(userIds, item.Id, ct);

            return true;
        }

        /// <summary>
        /// Adds a UserLibrary entry for <paramref name="mediaItemId"/> for each user in
        /// <paramref name="userIds"/> if one doesn't already exist. File-scanned content is
        /// visible to all users regardless of who triggered the import.
        /// </summary>
        private async Task UpsertLibraryEntryAsync(IReadOnlyList<int> userIds, int mediaItemId, CancellationToken ct)
        {
            foreach (var userId in userIds)
            {
                var exists = await _context.UserLibraries
                    .AnyAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);
                if (!exists)
                {
                    _context.UserLibraries.Add(new UserLibrary
                    {
                        UserId      = userId,
                        MediaItemId = mediaItemId,
                        Status      = LibraryStatus.Unwatched,
                        AddedAt     = DateTime.UtcNow,
                        UpdatedAt   = DateTime.UtcNow,
                    });
                }
            }
            await _context.SaveChangesAsync(ct);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the first <see cref="MediaItem"/> whose <c>fileScanner.filePath</c>
        /// in <c>MetadataJson</c> matches <paramref name="filePath"/> (case-insensitive).
        /// Used to prevent duplicate imports of the same physical file.
        /// </summary>
        private async Task<MediaItem?> FindItemByFilePathAsync(
            string filePath, int mediaTypeId, CancellationToken ct)
        {
            // Search across ALL media types — a physical file is globally unique regardless of
            // which type the item was assigned to.  This prevents the scanner from creating a
            // duplicate "movies" item when the file is already tracked as "fanedits" (or any
            // other type the user may have changed it to via the Change Type control).
            // LIKE '%fileScanner%' narrows the result set before in-memory JSON comparison.
            var candidates = await _context.MediaItems
                .Where(m => m.MetadataJson != null
                         && EF.Functions.Like(m.MetadataJson, "%fileScanner%"))
                .ToListAsync(ct);

            return candidates.FirstOrDefault(m =>
                string.Equals(ExtractFilePath(m.MetadataJson), filePath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Extracts <c>fileScanner.filePath</c> from a <c>MetadataJson</c> blob.</summary>
        private static string? ExtractFilePath(string? metadataJson)
        {
            if (metadataJson is null) return null;
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("fileScanner", out var scanner)
                    && scanner.TryGetProperty("filePath", out var fp))
                    return fp.GetString();
            }
            catch (JsonException)
            {
                // Malformed MetadataJson — treat as no stored path. Logged at caller if needed.
            }
            return null;
        }

        private async Task<MediaItem?> FindExistingItemAsync(
            Chronicle.Plugins.Models.ScannedFile file, int mediaTypeId, CancellationToken ct)
        {
            // 1. Match by external ID (highest confidence)
            if (file.SuggestedExternalId is not null)
            {
                var (source, extId) = ParseSuggestedExternalId(file.SuggestedExternalId);
                var byExtId = await _context.MediaExternalIds
                    .Include(e => e.MediaItem)
                    .FirstOrDefaultAsync(e => e.Source == source && e.ExternalId == extId
                                           && e.MediaItem!.MediaTypeId == mediaTypeId, ct);
                if (byExtId?.MediaItem is not null)
                    return byExtId.MediaItem;
            }

            // 2. Match by title + year
            if (file.ParsedYear.HasValue)
            {
                var hit = await FindByTitleAsync(file.ParsedTitle, mediaTypeId, file.ParsedYear, ct);
                if (hit is not null) return hit;
            }

            // 3. Title-only match (lower confidence — only when year is unknown)
            if (!file.ParsedYear.HasValue)
            {
                var hit = await FindByTitleAsync(file.ParsedTitle, mediaTypeId, year: null, ct);
                if (hit is not null) return hit;
            }

            return null;
        }

        // Matches a trailing " (YYYY)" or " [YYYY]" that may be embedded in a parsed filename title.
        private static readonly System.Text.RegularExpressions.Regex _trailingYearInTitle =
            new(@"\s*[\(\[]\d{4}[\)\]]$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Compiled regex used by ParseAudiobookFolderName to locate a "(YYYY)" segment.
        private static readonly System.Text.RegularExpressions.Regex _yearSegmentRegex =
            new(@"^\((\d{4})\)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Looks up a media item by title (and optionally year), trying several normalised
        /// variants so that file-scanner titles (e.g. <c>Movie - Subtitle (2025)</c>) and
        /// canonical metadata titles (e.g. <c>Movie: Subtitle</c>) resolve to the same row.
        ///
        /// Variants tried in order:
        ///   1. Literal title
        ///   2. Colon-variant  (" - " → ": ")
        ///   3. Dash-variant   (": " → " - ")
        ///   4–6. The same three variants with any trailing " (YYYY)" / " [YYYY]" stripped
        /// </summary>
        private async Task<MediaItem?> FindByTitleAsync(
            string title, int mediaTypeId, int? year, CancellationToken ct)
        {
            var colonTitle = title.Replace(" - ", ": ");
            var dashTitle  = title.Replace(": ", " - ");

            // Use lower() == lower() for case-insensitive exact matching.
            // EF.Functions.Like was previously used here but treats '%' and '_' in titles
            // as SQL wildcards, producing incorrect matches for titles such as "100% Hotter".
            foreach (var variant in new[] { title, colonTitle, dashTitle }.Distinct(StringComparer.Ordinal))
            {
                var variantLower = variant.ToLowerInvariant();
                var hit = await _context.MediaItems.FirstOrDefaultAsync(
                    m => m.MediaTypeId == mediaTypeId
                      && (year == null || m.Year == year)
                      && m.Name.ToLower() == variantLower, ct);
                if (hit is not null) return hit;
            }

            // Strip embedded trailing year (e.g. "Title (2026)") and retry all three variants.
            var stripped = _trailingYearInTitle.Replace(title, string.Empty).Trim();
            if (stripped != title)
            {
                var strippedColon = stripped.Replace(" - ", ": ");
                var strippedDash  = stripped.Replace(": ", " - ");

                foreach (var variant in new[] { stripped, strippedColon, strippedDash }.Distinct(StringComparer.Ordinal))
                {
                    var variantLower = variant.ToLowerInvariant();
                    var hit = await _context.MediaItems.FirstOrDefaultAsync(
                        m => m.MediaTypeId == mediaTypeId
                          && (year == null || m.Year == year)
                          && m.Name.ToLower() == variantLower, ct);
                    if (hit is not null) return hit;
                }
            }

            // Final fallback: normalize both sides (strips all punctuation/separators).
            // Catches cases where the filename has no separator where the stored title has ": "
            // e.g. "Alien Resurrection Resurrected" matches "Alien: Resurrection Resurrected".
            // Uses the indexed normalized_name column for efficiency.
            var normalizedTitle = MediaItemNormalizer.NormalizeName(title);
            if (!string.IsNullOrEmpty(normalizedTitle))
            {
                var hit = await _context.MediaItems.FirstOrDefaultAsync(
                    m => m.MediaTypeId == mediaTypeId
                      && (year == null || m.Year == year)
                      && m.NormalizedName == normalizedTitle, ct);
                if (hit is not null) return hit;
            }

            return null;
        }

        private async Task<MediaItem> CreateStubItemAsync(
            Chronicle.Plugins.Models.ScannedFile file, int mediaTypeId, CancellationToken ct)
        {
            // Enrich with full TMDB metadata when we have an external ID and a provider is loaded
            if (file.SuggestedExternalId is not null)
            {
                var provider = _registry.GetMetadataProviders().FirstOrDefault();
                if (provider is not null)
                {
                    try
                    {
                        var meta = await provider.GetByIdAsync(file.SuggestedExternalId, ct);
                        var enriched = new MediaItem
                        {
                            MediaTypeId    = mediaTypeId,
                            Name           = meta.Title,
                            NormalizedName = MediaItemNormalizer.NormalizeName(meta.Title),
                            Year           = meta.Year,
                            Overview       = meta.Overview,
                            PosterUrl      = meta.PosterUrl,
                            RuntimeMinutes = meta.RuntimeMinutes,
                            MetadataJson   = SerializeMetadata(tmdbMeta: meta, scannedFile: file),
                            HierarchyLevel = 0,
                            CreatedAt      = DateTime.UtcNow,
                            UpdatedAt      = DateTime.UtcNow,
                        };
                        _context.MediaItems.Add(enriched);
                        await _context.SaveChangesAsync(ct);
                        return enriched;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "TMDB enrichment failed for '{Title}', falling back to stub", file.ParsedTitle);
                    }
                }
            }

            // Fallback: filename-only stub
            var runtimeMinutes = file.TotalDurationSeconds.HasValue
                ? (int)Math.Round(file.TotalDurationSeconds.Value / 60.0)
                : file.DurationSeconds.HasValue
                    ? (int)Math.Round(file.DurationSeconds.Value / 60.0)
                    : (int?)null;
            var stub = new MediaItem
            {
                MediaTypeId    = mediaTypeId,
                Name           = file.ParsedTitle,
                NormalizedName = MediaItemNormalizer.NormalizeName(file.ParsedTitle),
                Year           = file.ParsedYear,
                PosterUrl      = file.NfoPosterUrl ?? file.LocalPosterPath,
                RuntimeMinutes = runtimeMinutes,
                MetadataJson   = SerializeMetadata(scannedFile: file),
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            _context.MediaItems.Add(stub);
            await _context.SaveChangesAsync(ct);
            return stub;
        }

        private async Task UpsertExternalIdAsync(int mediaItemId, string suggestedExternalId, CancellationToken ct)
        {
            var (source, extId) = ParseSuggestedExternalId(suggestedExternalId);

            var exists = await _context.MediaExternalIds.AnyAsync(
                e => e.MediaItemId == mediaItemId && e.Source == source && e.ExternalId == extId, ct);

            if (!exists)
            {
                _context.MediaExternalIds.Add(new MediaExternalId
                {
                    MediaItemId = mediaItemId,
                    Source = source,
                    ExternalId = extId,
                });
            }
        }

        /// <summary>
        /// Scores a metadata search result against a scanned file for ranking.
        /// Title match contributes 60 pts, year match contributes up to 40 pts.
        /// </summary>
        private static int ScoreCandidate(ScannedFileResult file, Chronicle.Plugins.Models.MediaMetadata candidate)
        {
            var exactTitle = string.Equals(candidate.Title, file.ParsedTitle, StringComparison.OrdinalIgnoreCase);
            var partialTitle = candidate.Title.Contains(file.ParsedTitle, StringComparison.OrdinalIgnoreCase)
                            || file.ParsedTitle.Contains(candidate.Title, StringComparison.OrdinalIgnoreCase);

            var score = exactTitle ? 60 : partialTitle ? 35 : 10;

            if (file.ParsedYear.HasValue && candidate.Year.HasValue)
            {
                if (candidate.Year == file.ParsedYear) score += 40;
                else if (Math.Abs(candidate.Year.Value - file.ParsedYear.Value) <= 1) score += 20;
            }
            else if (!file.ParsedYear.HasValue)
            {
                score += 15; // No year to penalise against
            }

            return Math.Min(score, 100);
        }

        // ── Refresh metadata ──────────────────────────────────────────────────────

        /// <summary>
        /// Scores a TMDB search result against a known item name + year.
        /// Mirrors <see cref="ScoreCandidate"/> but works with plain strings instead of <see cref="ScannedFileResult"/>.
        /// </summary>
        private static int ScoreByNameYear(string? candidateTitle, int? candidateYear, string itemName, int? itemYear)
        {
            var exactTitle = string.Equals(candidateTitle, itemName, StringComparison.OrdinalIgnoreCase);
            var partialTitle = candidateTitle?.Contains(itemName, StringComparison.OrdinalIgnoreCase) == true
                            || itemName.Contains(candidateTitle ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var score = exactTitle ? 60 : partialTitle ? 35 : 10;

            if (itemYear.HasValue && candidateYear.HasValue)
            {
                if (candidateYear == itemYear) score += 40;
                else if (Math.Abs(candidateYear.Value - itemYear.Value) <= 1) score += 20;
            }
            else if (!itemYear.HasValue)
            {
                score += 15; // No year to penalise against
            }

            return Math.Min(score, 100);
        }

        public async Task<MediaItem?> RefreshMetadataAsync(int mediaItemId, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault();
            if (provider is null)
            {
                _log.Warning("RefreshMetadata: no metadata provider loaded for item {Id}", mediaItemId);
                throw new NoProviderConfiguredException(
                    "No metadata provider configured. Add an API key in Settings → Plugins.");
            }

            var item = await _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);

            if (item is null)
                return null;

            // ── Child items (Season, Episode, Track, etc.) ──────────────────────
            // Do NOT search TMDB by child name — "Season 01" would match random shows.
            // Walk up to the root item and inherit its TMDB context instead.
            if (item.HierarchyLevel > 0)
                return await RefreshChildFromRootAsync(item, provider, ct);

            // Prefer TMDB external ID (stored as "movie:NNN" or "tv:NNN")
            var extId = item.ExternalIds
                .FirstOrDefault(e => e.Source == "tmdb")
                ?.ExternalId;

            if (extId is null)
            {
                // No external ID yet — search by name only.
                // Strip a trailing "(YYYY)" suffix that some folder names include
                // (e.g. "Rick and Morty (2013)") because TMDB's search doesn't match
                // those parenthesised years and returns zero results.
                // Also strip the plain year appended after a space (e.g. "Alien Romulus 2024")
                // as TMDB stores the canonical title only.
                var query = System.Text.RegularExpressions.Regex
                    .Replace(item.Name, @"\s*\(\d{4}\)\s*$", string.Empty)
                    .Trim();
                _log.Information("RefreshMetadata: item {Id} has no TMDB external ID, searching by '{Query}'", mediaItemId, query);
                var hint = ToMediaTypeHint(item.MediaType?.Name ?? string.Empty);
                var searchResults = await provider.SearchAsync(
                    new MediaSearchContext(query, item.Year), ct);

                // Score each candidate by title + year accuracy, prefer exact year match
                var best = searchResults
                    .Select(c => new { Metadata = c.Metadata, Score = ScoreByNameYear(c.Metadata.Title, c.Metadata.Year, item.Name, item.Year) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (best is null)
                {
                    _log.Information("RefreshMetadata: no TMDB match found for '{Name}'", item.Name);
                    return item;
                }
                extId = best.Metadata.ExternalId;
                await UpsertExternalIdAsync(item.Id, extId, ct);
                _log.Information("RefreshMetadata: matched '{Name}' → {ExtId} (score={Score})", item.Name, extId, best.Score);
            }

            try
            {
                var meta = await provider.GetByIdAsync(extId, ct);
                item.Name           = meta.Title;
                item.Year           = meta.Year;
                item.Overview       = meta.Overview;
                item.PosterUrl      = meta.PosterUrl;
                item.RuntimeMinutes = meta.RuntimeMinutes;
                item.MetadataJson   = SerializeMetadata(tmdbMeta: meta, existingJson: item.MetadataJson);
                item.UpdatedAt      = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                _log.Information("Refreshed metadata for '{Title}' ({Id})", meta.Title, mediaItemId);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "RefreshMetadata failed for item {Id} / extId={ExtId}", mediaItemId, extId);
                throw;
            }

            return item;
        }

        /// <summary>
        /// Refreshes a child MediaItem (Season, Episode, Track, etc.) by walking up
        /// the parent chain to find the root item's TMDB external ID, then using the
        /// root show's metadata to populate the child's poster and genre context.
        /// Prevents child items from independently matching TMDB by their generic names
        /// (e.g. "Season 01" matching "Goosebumps" instead of the correct parent show).
        /// </summary>
        private async Task<MediaItem?> RefreshChildFromRootAsync(
            MediaItem item, IMetadataProvider provider, CancellationToken ct)
        {
            // Clear any stale TMDB external IDs that old code may have written onto this child.
            // Child items should never carry their own TMDB ID — metadata comes from the root.
            var childTmdbIds = item.ExternalIds
                .Where(e => e.Source == "tmdb" && e.ExternalId != "__suppress__")
                .ToList();
            if (childTmdbIds.Count > 0)
            {
                _context.MediaExternalIds.RemoveRange(childTmdbIds);
                foreach (var stale in childTmdbIds)
                    item.ExternalIds.Remove(stale);
                _log.Information("RefreshChild: cleared {Count} stale TMDB ID(s) from child {Id}", childTmdbIds.Count, item.Id);
            }

            // Walk parent chain to find the root item
            var currentId = item.ParentId;
            MediaItem? root = null;
            while (currentId != null)
            {
                var candidate = await _context.MediaItems
                    .Include(m => m.ExternalIds)
                    .FirstOrDefaultAsync(m => m.Id == currentId, ct);
                if (candidate is null) break;
                if (candidate.ParentId is null) { root = candidate; break; }
                currentId = candidate.ParentId;
            }

            if (root is null) return item;

            var rootExtId = root.ExternalIds
                .FirstOrDefault(e => e.Source == "tmdb" && e.ExternalId != "__suppress__")
                ?.ExternalId;

            if (rootExtId is null)
            {
                _log.Information(
                    "RefreshChild: root item {RootId} has no TMDB ID — skipping child {Id}",
                    root.Id, item.Id);
                return item;
            }

            try
            {
                var meta = await provider.GetByIdAsync(rootExtId, ct);

                // Inherit the parent show's poster if this child has none
                if (string.IsNullOrEmpty(item.PosterUrl) && !string.IsNullOrEmpty(meta.PosterUrl))
                    item.PosterUrl = meta.PosterUrl;

                // Store the parent show's TMDB data as context (genres, cast, rating etc.)
                // without overwriting the child's Name or Year.
                item.MetadataJson = SerializeMetadata(tmdbMeta: meta, existingJson: item.MetadataJson);
                item.UpdatedAt    = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                _log.Information(
                    "RefreshChild: updated child {Id} ({Name}) using root {RootId} ({ExtId})",
                    item.Id, item.Name, root.Id, rootExtId);
            }
            catch (Exception ex)
            {
                _log.Warning(ex,
                    "RefreshChild: failed for item {Id} using root extId={ExtId}", item.Id, rootExtId);
                throw;
            }

            return item;
        }

        // ── Metadata search + direct add (for Add Media UI) ──────────────────────

        /// <summary>
        /// Returns providers that declare support for the given media type, normalised via
        /// <see cref="ToMediaTypeHint"/>.  Falls back to all providers when none match.
        /// </summary>
        private IReadOnlyList<IMetadataProvider> ProvidersForType(string mediaTypeHint)
        {
            var all = _registry.GetMetadataProviders();
            if (all.Count == 0) return all;

            // Match on exact type name OR parent-type hint (e.g. "anime" → "tv").
            // No fallback to all providers — the user explicitly chose a type.
            var normalizedType = NormalizeMediaTypeName(mediaTypeHint);
            var hintType       = ToMediaTypeHint(mediaTypeHint);

            return all.Where(p => p.GetSupportedMediaTypes().Any(t =>
                    string.Equals(NormalizeMediaTypeName(t.MediaTypeName), normalizedType, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeMediaTypeName(t.MediaTypeName), hintType,       StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// Queries all type-compatible providers in parallel, then merges results so that
        /// a provider that knows the identity (e.g. Trakt) and a provider that has images
        /// (e.g. TMDB) each contribute their best data to the same result card.
        /// Providers that throw (e.g. SIMKL/Trakt text-search not supported) are skipped.
        /// </summary>
        public async Task<List<MetadataCandidate>> SearchMetadataAsync(
            string query, string mediaTypeHint, CancellationToken ct = default)
        {
            var providers = ProvidersForType(mediaTypeHint);
            if (providers.Count == 0)
                throw new InvalidOperationException(
                    "No metadata provider is loaded. Install and configure a metadata plugin (e.g. TMDB).");

            // Run all providers in parallel; ignore failures from individual providers.
            var context = new MediaSearchContext(query, MediaTypeName: mediaTypeHint);
            var tasks   = providers.Select(async p =>
            {
                try   { return await p.SearchAsync(context, ct); }
                catch { return (IReadOnlyList<ScoredCandidate>)[]; }
            });
            var allProviderResults = await Task.WhenAll(tasks);

            // Flatten into candidates grouped by provider.
            var allCandidates = providers
                .Zip(allProviderResults, (p, results) => (provider: p, results))
                .SelectMany(x => x.results.Select(r => (x.provider, r)))
                .ToList();

            if (allCandidates.Count == 0) return [];

            // Build two poster lookups:
            //   1. By external ID / cross-ref ID (e.g. Trakt result → TMDB poster via tv:87533)
            //   2. By "title|year" key (e.g. Trakt "BONDiNG 2019" → TVMaze "Bonding 2019" poster)
            //      because TVMaze IDs don't appear in Trakt's cross-ref ids object.
            var idToPoster        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var titleYearToPoster = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, r) in allCandidates)
            {
                if (string.IsNullOrEmpty(r.Metadata.PosterUrl)) continue;
                idToPoster.TryAdd(r.Metadata.ExternalId, r.Metadata.PosterUrl);
                var tk = TitleYearKey(r.Metadata.Title, r.Metadata.Year);
                if (tk is not null) titleYearToPoster.TryAdd(tk, r.Metadata.PosterUrl);
            }

            // Deduplicate: same item from multiple providers → one result with best data.
            // Match on (1) shared external/cross-ref ID, or (2) identical title+year.
            var seenIds       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitleYear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged        = new List<MetadataCandidate>();

            foreach (var (_, r) in allCandidates)
            {
                var m = r.Metadata;

                // Collect all known external IDs for this result (own ID + cross-refs in ExtendedData).
                var allIds = new List<string> { m.ExternalId };
                if (m.ExtendedData is { } ext && ext.ValueKind == System.Text.Json.JsonValueKind.Object
                    && ext.TryGetProperty("ids", out var ids))
                {
                    bool isMovie = m.ExternalId?.Contains(":movie:") == true
                                || m.ExternalId?.StartsWith("movie:") == true;
                    foreach (var prop in ids.EnumerateObject())
                    {
                        var formatted = CrossRefHelper.FormatCrossRefId(prop.Name.ToLowerInvariant(), prop.Value, isMovie);
                        if (formatted is not null) allIds.Add(formatted);
                    }
                }

                var tk = TitleYearKey(m.Title, m.Year);

                // Skip if already emitted by cross-ref ID match or title+year match.
                if (allIds.Any(id => seenIds.Contains(id))) continue;
                if (tk is not null && seenTitleYear.Contains(tk)) continue;

                foreach (var id in allIds) seenIds.Add(id);
                if (tk is not null) seenTitleYear.Add(tk);

                // Supplement missing poster: try cross-ref IDs first, then title+year.
                var poster = m.PosterUrl;
                if (string.IsNullOrEmpty(poster))
                {
                    foreach (var id in allIds)
                    {
                        if (idToPoster.TryGetValue(id, out var p) && !string.IsNullOrEmpty(p))
                        { poster = p; break; }
                    }
                }
                if (string.IsNullOrEmpty(poster) && tk is not null)
                    titleYearToPoster.TryGetValue(tk, out poster);

                // Collect sources and external IDs from all providers that matched this result.
                var sources              = new List<string>();
                var contribExternalIds   = new List<string>();
                if (!string.IsNullOrEmpty(m.Source)) sources.Add(m.Source);
                foreach (var (_, other) in allCandidates)
                {
                    if (other.Metadata.ExternalId == m.ExternalId) continue;
                    var otherTk = TitleYearKey(other.Metadata.Title, other.Metadata.Year);
                    if (otherTk is null || otherTk != tk) continue;
                    if (!string.IsNullOrEmpty(other.Metadata.Source) && !sources.Contains(other.Metadata.Source))
                        sources.Add(other.Metadata.Source);
                    if (!string.IsNullOrEmpty(other.Metadata.ExternalId))
                        contribExternalIds.Add(other.Metadata.ExternalId);
                }

                merged.Add(new MetadataCandidate(
                    m.ExternalId, m.Title, m.Year, poster,
                    m.Overview, m.Rating, 0,
                    m.Source,
                    m.Genres.Count > 0           ? m.Genres           : null,
                    m.Cast.Count > 0             ? m.Cast             : null,
                    sources.Count > 1            ? sources            : null,
                    contribExternalIds.Count > 0 ? contribExternalIds : null));
            }

            return merged;
        }

        public async Task<Chronicle.Core.Models.MediaItem> AddFromSearchAsync(
            string externalId, int mediaTypeId, int userId, CancellationToken ct = default,
            List<string>? contributingExternalIds = null)
        {
            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == mediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {mediaTypeId} not found.");

            // Derive which plugin should handle this externalId.
            var (idSource, _) = ParseSuggestedExternalId(externalId);
            var pluginId = SourceToPluginId(idSource);
            var provider = (pluginId is not null ? _registry.GetMetadataProvider(pluginId) : null)
                ?? ProvidersForType(mediaType.Name).FirstOrDefault()
                ?? throw new InvalidOperationException("No metadata provider is loaded.");

            var meta = await provider.GetByIdAsync(externalId, ct);
            var (source, extId) = ParseSuggestedExternalId(externalId);

            // Extract cross-reference IDs from the provider's ExtendedData (e.g. Trakt → TMDB/IMDB IDs).
            // These are used to pre-seed enrichment rows so other plugins don't text-search and mis-match.
            var crossRefs = ExtractCrossRefIds(meta, source);

            // Build initial metadata JSON under the correct plugin blob key.
            var providerBlobKey = SourceToPluginId(source) ?? source;
            var initialMetaJson = BuildProviderMetaJson(providerBlobKey, meta);

            // Re-use existing item if already imported
            var existing = await _context.MediaExternalIds
                .Include(e => e.MediaItem)
                .FirstOrDefaultAsync(
                    e => e.Source == source && e.ExternalId == extId
                      && e.MediaItem!.MediaTypeId == mediaTypeId, ct);

            Chronicle.Core.Models.MediaItem item;
            if (existing?.MediaItem is not null)
            {
                item = existing.MediaItem;
                item.Name           = meta.Title;
                item.Year           = meta.Year;
                item.Overview       = meta.Overview;
                item.PosterUrl      = meta.PosterUrl;
                item.RuntimeMinutes = meta.RuntimeMinutes;
                item.MetadataJson   = MergeProviderBlob(item.MetadataJson, providerBlobKey, meta);
                item.UpdatedAt      = DateTime.UtcNow;
            }
            else
            {
                item = new Chronicle.Core.Models.MediaItem
                {
                    MediaTypeId    = mediaTypeId,
                    Name           = meta.Title,
                    Year           = meta.Year,
                    Overview       = meta.Overview,
                    PosterUrl      = meta.PosterUrl,
                    RuntimeMinutes = meta.RuntimeMinutes,
                    MetadataJson   = initialMetaJson,
                    HierarchyLevel = 0,
                    CreatedAt      = DateTime.UtcNow,
                    UpdatedAt      = DateTime.UtcNow,
                };
                _context.MediaItems.Add(item);
                await _context.SaveChangesAsync(ct);
                await UpsertExternalIdAsync(item.Id, externalId, ct);

                // Always seed an enrichment row for the source plugin itself with the known ID.
                // This guarantees the source plugin appears on the detail page even if its
                // declared media types don't exactly match the item's type (e.g. Trakt added
                // under an "anime" tab that Trakt doesn't explicitly declare support for).
                if (pluginId is not null)
                {
                    _context.MediaEnrichments.Add(new Chronicle.Core.Models.MediaItemEnrichment
                    {
                        MediaItemId = item.Id,
                        PluginId    = pluginId,
                        ExternalId  = externalId,
                        Status      = Chronicle.Core.Models.EnrichmentStatus.Pending,
                    });
                }

                // Pre-seed enrichment rows for providers that contributed a matching result
                // during search (e.g. TVMaze matched the same show by title+year). Their IDs
                // aren't in the primary provider's cross-ref data so must be passed explicitly.
                foreach (var contribId in contributingExternalIds ?? [])
                {
                    var (cSource, _) = ParseSuggestedExternalId(contribId);
                    var cPluginId = SourceToPluginId(cSource);
                    if (cPluginId is null || cPluginId == pluginId) continue;
                    var cExists = await _context.MediaEnrichments
                        .AnyAsync(r => r.MediaItemId == item.Id && r.PluginId == cPluginId, ct);
                    if (!cExists)
                    {
                        _context.MediaEnrichments.Add(new Chronicle.Core.Models.MediaItemEnrichment
                        {
                            MediaItemId = item.Id,
                            PluginId    = cPluginId,
                            ExternalId  = contribId,
                            Status      = Chronicle.Core.Models.EnrichmentStatus.Pending,
                        });
                        await UpsertExternalIdAsync(item.Id, contribId, ct);
                    }
                }

                // Pre-seed enrichment rows for cross-referenced plugins so they use the
                // known ID directly instead of running a text search that can mis-match.
                // Two cases:
                //   (a) Plugin that "owns" the source (e.g. tmdb → chronicle.plugin.tmdb)
                //   (b) Any other plugin that declares it accepts this ID prefix
                //       (e.g. SIMKL accepts "tv:N" so it can look up by TMDB ID)
                var allEntries = _registry.GetMetadataProviderEntries();
                foreach (var (xSource, xId) in crossRefs)
                {
                    var ownPluginId = SourceToPluginId(xSource);

                    foreach (var (candidatePluginId, candidateProvider, _) in allEntries)
                    {
                        if (candidatePluginId == pluginId) continue; // skip source plugin

                        // Accept if this plugin owns the source OR declares it accepts the prefix.
                        var isOwner    = candidatePluginId == ownPluginId;
                        var acceptsCrossRef = !isOwner && candidateProvider
                            .GetAcceptedCrossRefPrefixes()
                            .Any(prefix => xId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                        if (!isOwner && !acceptsCrossRef) continue;

                        var xExists = await _context.MediaEnrichments
                            .AnyAsync(r => r.MediaItemId == item.Id && r.PluginId == candidatePluginId, ct);
                        if (xExists) continue;

                        _context.MediaEnrichments.Add(new Chronicle.Core.Models.MediaItemEnrichment
                        {
                            MediaItemId = item.Id,
                            PluginId    = candidatePluginId,
                            ExternalId  = xId,
                            Status      = Chronicle.Core.Models.EnrichmentStatus.Pending,
                        });
                        await UpsertExternalIdAsync(item.Id, xId, ct);
                    }
                }
            }

            // Ensure library entry exists (default to PlanToWatch)
            var libEntry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == item.Id, ct);

            if (libEntry is null)
            {
                _context.UserLibraries.Add(new Chronicle.Core.Models.UserLibrary
                {
                    UserId      = userId,
                    MediaItemId = item.Id,
                    Status      = Chronicle.Core.Models.LibraryStatus.PlanToWatch,
                    AddedAt     = DateTime.UtcNow,
                    UpdatedAt   = DateTime.UtcNow,
                });
            }

            await _context.SaveChangesAsync(ct);

            // Seed enrichment rows for every registered provider that supports this media type.
            // Cross-ref rows pre-seeded above are skipped (existingSet check inside the method).
            await SeedEnrichmentRowsForNewItemsAsync([item.Id], mediaType.Name, ct);

            // Return with navigation properties populated for DTO conversion
            return await _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .FirstAsync(m => m.Id == item.Id, ct);
        }

        /// <summary>
        /// Parses Chronicle external-ID format into (source, externalId) for DB lookup.
        /// "movie:550"      → ("tmdb", "movie:550")
        /// "tv:1396"        → ("tmdb", "tv:1396")
        /// "imdb:tt0137523" → ("imdb", "tt0137523")
        /// </summary>
        private static (string source, string externalId) ParseSuggestedExternalId(string suggested)
        {
            if (suggested.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
                return ("imdb", suggested[5..]);

            // Trakt: "trakt:movie:NNN", "trakt:show:NNN", "trakt:episode:NNN"
            if (suggested.StartsWith("trakt:", StringComparison.OrdinalIgnoreCase))
                return ("trakt", suggested);

            // SIMKL: "simkl:NNN"
            if (suggested.StartsWith("simkl:", StringComparison.OrdinalIgnoreCase))
                return ("simkl", suggested);

            // Hardcover: "hardcover:NNN"
            if (suggested.StartsWith("hardcover:", StringComparison.OrdinalIgnoreCase))
                return ("hardcover", suggested);

            // "movie:*" or "tv:*" — stored verbatim with source="tmdb"
            return ("tmdb", suggested);
        }

        // Produces a normalised "title|year" deduplication key, or null if title is missing.
        // Strips punctuation and lowercases so "BONDiNG" and "Bonding" match.
        private static string? TitleYearKey(string? title, int? year)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var normalized = System.Text.RegularExpressions.Regex
                .Replace(title.ToLowerInvariant(), @"[^\w\s]", "")
                .Trim();
            return $"{normalized}|{year?.ToString() ?? ""}";
        }

        // Maps a short source name to the full canonical plugin ID used in the registry.
        private static string? SourceToPluginId(string source) => source switch
        {
            "tmdb"       => "chronicle.plugin.tmdb",
            "trakt"      => "chronicle.plugin.trakt",
            "simkl"      => "chronicle.plugin.simkl",
            "hardcover"  => "chronicle.plugin.hardcover",
            "musicbrainz"=> "chronicle.plugin.musicbrainz",
            _            => null,
        };

        private static List<(string source, string id)> ExtractCrossRefIds(
            Chronicle.Plugins.Models.MediaMetadata meta, string fromSource) =>
            CrossRefHelper.ExtractCrossRefIds(meta, fromSource)
                .Select(t => (t.Source, t.Id))
                .ToList();

        // Builds a new metadata_json string containing the provider blob under its plugin ID key.
        // FileScanner blob (if any) in existingJson is preserved.
        private static string BuildProviderMetaJson(
            string pluginBlobKey, Chronicle.Plugins.Models.MediaMetadata meta)
        {
            return MergeProviderBlob(null, pluginBlobKey, meta);
        }

        // Merges a provider blob into existing metadata_json.
        // Existing blobs from other plugins are preserved.
        // The blob stored under pluginBlobKey contains poster, backdrop, genres, cast, rating, etc.
        private static string MergeProviderBlob(
            string? existingJson,
            string pluginBlobKey,
            Chronicle.Plugins.Models.MediaMetadata meta)
        {
            Dictionary<string, System.Text.Json.JsonElement> blobs = [];
            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    blobs = System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(existingJson)
                        ?? [];
                }
                catch { /* corrupt json — start fresh */ }
            }

            // Remove stale resolved cache — it will be recomputed by MetadataResolutionService.
            blobs.Remove("_resolved");

            var providerObj = new Dictionary<string, object?>
            {
                ["title"]          = meta.Title,
                ["year"]           = meta.Year,
                ["overview"]       = meta.Overview,
                ["posterUrl"]      = meta.PosterUrl,
                ["backdropUrl"]    = meta.BackdropUrl,
                ["runtimeMinutes"] = meta.RuntimeMinutes,
                ["rating"]         = meta.Rating,
                ["genres"]         = meta.Genres,
                ["cast"]           = meta.Cast,
                ["directors"]      = meta.Directors,
            };

            // Preserve extendedData (cross-ref IDs etc.) if the provider supplied it.
            if (meta.ExtendedData.HasValue)
                providerObj["extendedData"] = meta.ExtendedData.Value;

            blobs[pluginBlobKey] = System.Text.Json.JsonSerializer
                .SerializeToElement(providerObj);

            return System.Text.Json.JsonSerializer.Serialize(blobs);
        }

        /// <summary>Maps a Chronicle media type name to the hint expected by metadata providers.</summary>
        // ── Audiobook folder grouping ─────────────────────────────────────────────

        /// <summary>
        /// Collapses a flat list of audio files (as returned by the scanner) into one
        /// representative <see cref="ScannedFile"/> per book.
        ///
        /// Rules:
        ///   - Audio files sitting directly in <paramref name="scanRoot"/> each become their own entry.
        ///   - Audio files in a sub-folder are all merged into one entry for that folder,
        ///     identified by the shallowest ancestor of <paramref name="scanRoot"/> that contains
        ///     audio files directly (deeper sub-folders, e.g. Extras/, are treated as supplemental
        ///     and dropped).
        ///
        /// The representative entry for each folder uses:
        ///   - <c>ParsedTitle</c> from AudioAlbum tag → NFO title → folder name
        ///   - <c>ParsedYear</c>  from AudioYear tag → NFO year → year in folder name
        ///   - <c>AudioArtist/AudioAlbumArtist</c> and <c>AudioGrouping</c> from the best-tagged file
        ///   - <c>FilePath</c> set to the book folder path (for stable rescan dedup)
        /// </summary>
        /// <summary>Exposed for unit testing only.</summary>
        internal static List<ScannedFile> CollapseAudiobooksToFoldersForTest(
            List<ScannedFile> files, string scanRoot) => CollapseAudiobooksToFolders(files, scanRoot);

        private static List<ScannedFile> CollapseAudiobooksToFolders(
            List<ScannedFile> files, string scanRoot)
        {
            if (files.Count == 0) return files;

            var rootNorm = scanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Group files by the directory that directly contains them.
            var byDir = files
                .GroupBy(f => Path.GetDirectoryName(f.FilePath) ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Identify "book folders": the shallowest directories with audio files.
            // A directory whose ancestor is already a book folder is supplemental — skip it.
            var bookFolders = new List<string>();
            foreach (var dir in byDir.Keys.OrderBy(d => d.Length))
            {
                var covered = bookFolders.Any(bf =>
                    dir.StartsWith(bf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    dir.StartsWith(bf + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!covered)
                    bookFolders.Add(dir);
            }

            var result = new List<ScannedFile>();

            foreach (var bookFolder in bookFolders)
            {
                var group  = byDir[bookFolder];
                var isRoot = string.Equals(
                    bookFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    rootNorm, StringComparison.OrdinalIgnoreCase);

                if (isRoot)
                {
                    // Root-level audio files: each is its own standalone book.
                    foreach (var f in group)
                        f.TotalDurationSeconds ??= f.DurationSeconds;
                    result.AddRange(group);
                    continue;
                }

                // Subfolder: merge all parts into one representative entry.
                // Pick the file with the richest tags (highest confidence score).
                var rep = group.OrderByDescending(f => f.ConfidenceScore).First();

                // Resolve author from tags (album artist preferred; falls back to performer).
                var author = group
                    .Select(f => f.AudioAlbumArtist ?? f.AudioArtist)
                    .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

                // Resolve series from grouping tag.
                var series = group
                    .Select(f => f.AudioGrouping)
                    .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));

                // Propagate best author/series onto the representative.
                if (author is not null) { rep.AudioAlbumArtist = author; rep.AudioArtist = author; }
                if (series is not null)   rep.AudioGrouping = series;

                // Resolve title: AudioAlbum tag > NFO-provided ParsedTitle (score ≥ 78)
                //                > folder name parsed as "<Series> - <Num> - (<Year>) - <Title>".
                //
                // Author resolution priority: embedded tags > parent folder name (user's layout
                // is <Author>\<Series - Num - (Year) - Title>\files, so the parent of the book
                // folder is the author folder unless it IS the scan root).
                var folderName = Path.GetFileName(bookFolder);
                var parentDir  = Path.GetDirectoryName(bookFolder);
                var parentIsRoot = string.Equals(
                    parentDir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    rootNorm, StringComparison.OrdinalIgnoreCase);
                var parentFolderName = (!parentIsRoot && parentDir is not null)
                    ? Path.GetFileName(parentDir)
                    : null;

                // Always parse the folder name for series/year — used as fallback below.
                var (folderTitle, folderYear, folderSeries) =
                    ParseAudiobookFolderName(folderName);

                if (!string.IsNullOrWhiteSpace(rep.AudioAlbum))
                {
                    rep.ParsedTitle = rep.AudioAlbum;
                    rep.ParsedYear ??= rep.AudioYear ?? group.Select(f => f.AudioYear).FirstOrDefault(y => y.HasValue);
                    // Author: tags > parent folder name
                    if (author is null && !string.IsNullOrWhiteSpace(parentFolderName))
                    {
                        rep.AudioAlbumArtist = parentFolderName;
                        rep.AudioArtist      = parentFolderName;
                    }
                    // Series: AudioGrouping tag > folder name (AudioAlbum doesn't carry series info)
                    if (series is null && !string.IsNullOrWhiteSpace(folderSeries))
                        rep.AudioGrouping = folderSeries;
                }
                else if (rep.ConfidenceScore < 78) // no NFO — derive from folder names
                {
                    rep.ParsedTitle = folderTitle;
                    if (folderYear.HasValue) rep.ParsedYear ??= folderYear;
                    // Author: tags > parent folder name
                    if (author is null && !string.IsNullOrWhiteSpace(parentFolderName))
                    {
                        rep.AudioAlbumArtist = parentFolderName;
                        rep.AudioArtist      = parentFolderName;
                    }
                    if (series is null && !string.IsNullOrWhiteSpace(folderSeries))
                        rep.AudioGrouping = folderSeries;
                }

                // Sum durations across all parts of this multi-file audiobook.
                var totalDuration = group
                    .Select(f => f.DurationSeconds)
                    .Where(d => d.HasValue)
                    .Sum(d => d!.Value);
                rep.TotalDurationSeconds = totalDuration > 0 ? totalDuration : rep.DurationSeconds;

                // Use the folder path as the stable file-path key so that a rescan of the
                // same folder matches the existing stub rather than creating a duplicate.
                rep.FilePath = bookFolder;

                result.Add(rep);
            }

            return result;
        }

        /// <summary>
        /// Parses an audiobook book-folder name into title, year, and series.
        ///
        /// Primary format (user's layout):
        ///   <c>Series - SeriesNum - (Year) - Title</c>
        ///   e.g. "Stormlight Archive - 1 - (2010) - The Way of Kings"
        ///        "- - (2015) - Armada"          ← dashes are placeholders for no series/num
        ///
        /// Author is NOT extracted here — callers derive it from the parent directory
        /// (the user's layout is <c>Author\Book\files</c>).
        ///
        /// Fallback for non-matching names: extract year from any <c>(YYYY)</c>, then use the
        /// last non-placeholder segment as the title and any preceding ones as series.
        /// </summary>
        private static (string Title, int? Year, string? Series)
            ParseAudiobookFolderName(string folderName)
        {
            // Split on " - " to get raw segments.
            var raw = folderName.Split(new[] { " - " }, StringSplitOptions.None)
                                .Select(p => p.Trim())
                                .ToArray();

            // Locate a segment that is purely "(YYYY)".
            int yearIdx = -1;
            int? year   = null;
            for (int i = 0; i < raw.Length; i++)
            {
                var m = _yearSegmentRegex.Match(raw[i]);
                if (!m.Success) continue;
                year   = int.Parse(m.Groups[1].Value);
                yearIdx = i;
                break;
            }

            if (yearIdx >= 0)
            {
                // Title = segments after the year segment (usually just one).
                var titleParts = raw[(yearIdx + 1)..]
                    .Where(p => !IsPlaceholder(p))
                    .ToArray();
                var title = titleParts.Length > 0 ? string.Join(" - ", titleParts) : folderName.Trim();

                // Series = non-placeholder, non-numeric segments before the year.
                // Filters integers AND decimals (e.g. 1.5, 0.5) which are series numbers.
                var preParts = raw[..yearIdx]
                    .Where(p => !IsPlaceholder(p) && !IsSeriesNumber(p))
                    .ToArray();
                var series = preParts.Length > 0 ? string.Join(" - ", preParts) : null;

                return (title, year, series);
            }

            // ── Fallback: no standalone (YYYY) segment found ─────────────────────
            // Extract year from anywhere in the string, then apply generic parsing.
            year = null;
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                folderName, @"\s*\((\d{4})\)\s*",
                m =>
                {
                    if (year is null && int.TryParse(m.Groups[1].Value, out var y)) year = y;
                    return " ";
                }).Trim(' ', '-', '_').Trim();

            var parts = cleaned
                .Split(new[] { " - " }, StringSplitOptions.None)
                .Select(p => p.Trim())
                .Where(p => !IsPlaceholder(p))
                .ToArray();

            if (parts.Length == 0) return (folderName.Trim(), year, null);
            if (parts.Length == 1) return (parts[0], year, null);

            // Last segment = title; everything else = series.
            return (parts[^1], year, string.Join(" - ", parts[..^1]));
        }

        private static bool IsPlaceholder(string s) =>
            string.IsNullOrWhiteSpace(s) || s.Trim('-', '_', ' ').Length == 0;

        private static bool IsSeriesNumber(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _);

        private static string ToMediaTypeHint(string mediaTypeName)
        {
            var n = mediaTypeName.ToLowerInvariant();
            if (n.Contains("tv") || n.Contains("show") || n.Contains("series")
                || n.Contains("anime")) return "tv";
            if (n.Contains("music") || n.Contains("album") || n.Contains("track")) return "music";
            return "movie";
        }

        // ── Hierarchy grouping ────────────────────────────────────────────────────

        internal record ShowGroup(string ShowTitle, Dictionary<int, SeasonGroup> Seasons);
        internal record SeasonGroup(int SeasonNumber, List<Chronicle.Plugins.Models.ScannedFile> Episodes);

        /// <summary>Exposed for unit testing only.</summary>
        internal static List<ShowGroup> GroupByShowForTest(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> files) => GroupByShow(files);

        /// <summary>Exposed for unit testing only.</summary>
        internal static List<ScanGroup> GroupAudiobooksByAuthorAndSeriesForTest(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> collapsed) =>
            GroupAudiobooksByAuthorAndSeries(collapsed);

        /// <summary>
        /// Groups a flat list of collapsed audiobook entries (one per book folder) into a
        /// three-level Author → Series? → Book tree for use by the audiobook import pipeline.
        /// Author is derived from AudioAlbumArtist/AudioArtist tags; series from AudioGrouping.
        /// Books without a series are placed directly under their author at HierarchyLevel 1.
        /// Books without a recognisable author are placed under an "Unknown" author stub.
        /// </summary>
        private static List<ScanGroup> GroupAudiobooksByAuthorAndSeries(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> collapsed)
        {
            var authorGroups = new Dictionary<string, ScanGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in collapsed)
            {
                var authorName = !string.IsNullOrWhiteSpace(file.AudioAlbumArtist) ? file.AudioAlbumArtist.Trim()
                               : !string.IsNullOrWhiteSpace(file.AudioArtist)      ? file.AudioArtist.Trim()
                               : "Unknown";

                var seriesName = !string.IsNullOrWhiteSpace(file.AudioGrouping)
                    ? file.AudioGrouping.Trim()
                    : null;

                // Find or create Author node (level 0)
                if (!authorGroups.TryGetValue(authorName, out var authorGroup))
                {
                    authorGroup = new ScanGroup
                    {
                        GroupKey        = authorName.Trim().ToLowerInvariant(),
                        Name            = authorName,
                        HierarchyLevel  = 0,
                        ConfidenceScore = 0.75,
                        SignalSources   = ["tags"],
                    };
                    authorGroups[authorName] = authorGroup;
                }

                // Build the leaf ScanGroup for the book itself
                var bookName = !string.IsNullOrWhiteSpace(file.ParsedTitle)
                    ? file.ParsedTitle.Trim()
                    : Path.GetFileName(file.FilePath);

                var book = new ScanGroup
                {
                    GroupKey        = NormalizeGroupKey(authorName + "/" + (seriesName ?? "") + "/" + bookName),
                    Name            = bookName,
                    Year            = file.ParsedYear,
                    HierarchyLevel  = seriesName is not null ? 2 : 1,
                    ConfidenceScore = file.ConfidenceScore / 100.0,
                    SignalSources   = ["tags"],
                    Files           = [file.FilePath],
                    FolderPath      = file.FilePath,
                    Author          = authorName,
                    Series          = seriesName,
                };

                if (seriesName is not null)
                {
                    // Find or create Series node (level 1) under this author
                    var seriesKey = NormalizeGroupKey(authorName + "/" + seriesName);
                    var seriesGroup = authorGroup.Children
                        .FirstOrDefault(c => c.GroupKey == seriesKey);

                    if (seriesGroup is null)
                    {
                        seriesGroup = new ScanGroup
                        {
                            GroupKey        = seriesKey,
                            Name            = seriesName,
                            HierarchyLevel  = 1,
                            ConfidenceScore = 0.75,
                            SignalSources   = ["tags"],
                            Author          = authorName,
                        };
                        authorGroup.Children.Add(seriesGroup);
                    }
                    seriesGroup.Children.Add(book);
                }
                else
                {
                    // Standalone book — attach directly under author at level 1
                    authorGroup.Children.Add(book);
                }
            }

            return [.. authorGroups.Values];
        }

        private static string NormalizeGroupKey(string s) =>
            s.Trim().ToLowerInvariant().Replace("  ", " ");

        private static List<ShowGroup> GroupByShow(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> files)
        {
            var shows = new Dictionary<string, ShowGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var showTitle = file.ShowTitle ?? file.ParsedTitle;
                if (string.IsNullOrWhiteSpace(showTitle))
                    continue; // skip files with no parseable title
                var seasonNum = file.SeasonNumber ?? 1; // default to Season 1 when not detectable

                if (!shows.TryGetValue(showTitle, out var show))
                {
                    show = new ShowGroup(showTitle, new Dictionary<int, SeasonGroup>());
                    shows[showTitle] = show;
                }

                if (!show.Seasons.TryGetValue(seasonNum, out var season))
                {
                    season = new SeasonGroup(seasonNum, new List<Chronicle.Plugins.Models.ScannedFile>());
                    show.Seasons[seasonNum] = season;
                }

                season.Episodes.Add(file);
            }

            return shows.Values.ToList();
        }

        private async Task<MediaItem> FindOrCreateParentAsync(
            string name,
            int mediaTypeId,
            int? parentId,
            int hierarchyLevel,
            CancellationToken ct)
        {
            var nameLower = name.ToLowerInvariant();

            // NOTE: find-or-create has a TOCTOU race if scans run concurrently; acceptable until
            // a unique index on (MediaTypeId, ParentId, HierarchyLevel, Name) is added.
            var existing = await _context.MediaItems
                .Where(m => m.MediaTypeId == mediaTypeId
                         && m.ParentId == parentId
                         && m.HierarchyLevel == hierarchyLevel
                         && m.Name.ToLower() == nameLower)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
                return existing;

            var item = new MediaItem
            {
                Name           = name,
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };

            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync(ct);
            return item;
        }

        // ── MetadataJson helpers ──────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _metaJsonOpts =
            new(JsonSerializerDefaults.Web);

        // Internal model types matching the namespaced MetadataJson structure
        private sealed record TmdbMetaJson(
            double? Rating, List<string> Genres, List<string> Cast,
            List<string> Directors, string? PosterUrl, string? BackdropUrl);

        private sealed record FileScannerMetaJson(
            string? FilePath, string? LocalPosterPath, string? NfoPosterUrl,
            string? Author = null, string? Series = null);

        private sealed record MediaMetaJsonRoot(TmdbMetaJson? Tmdb, FileScannerMetaJson? FileScanner);

        /// <summary>
        /// Builds the MetadataJson blob for a MediaItem.
        /// Pass <paramref name="existingJson"/> to preserve the other provider's data when only one changes.
        /// Pass <paramref name="scannerFilePath"/> to record a plain file path without a full ScannedFile.
        /// </summary>
        private static string SerializeMetadata(
            Chronicle.Plugins.Models.MediaMetadata? tmdbMeta = null,
            string? existingJson = null,
            Chronicle.Plugins.Models.ScannedFile? scannedFile = null,
            string? scannerFilePath = null)
        {
            // Preserve existing filescanner section when refreshing TMDB only
            FileScannerMetaJson? fsData = null;
            if (existingJson is not null)
            {
                try
                {
                    fsData = JsonSerializer
                        .Deserialize<MediaMetaJsonRoot>(existingJson, _metaJsonOpts)?.FileScanner;
                }
                catch { /* old flat format — drop it */ }
            }

            // Override with rich scan data if provided
            if (scannedFile is not null)
            {
                var author = scannedFile.AudioAlbumArtist ?? scannedFile.AudioArtist;
                fsData = new FileScannerMetaJson(
                    scannedFile.FilePath,
                    scannedFile.LocalPosterPath,
                    scannedFile.NfoPosterUrl,
                    Author: string.IsNullOrWhiteSpace(author) ? null : author,
                    Series: scannedFile.AudioGrouping);
            }

            // Override with a plain file path (direct import without full ScannedFile)
            if (scannerFilePath is not null)
                fsData = new FileScannerMetaJson(scannerFilePath, null, null);

            var tmdbData = tmdbMeta is null ? null : new TmdbMetaJson(
                tmdbMeta.Rating,
                tmdbMeta.Genres,
                tmdbMeta.Cast,
                tmdbMeta.Directors,
                tmdbMeta.PosterUrl,
                tmdbMeta.BackdropUrl);

            return JsonSerializer.Serialize(new MediaMetaJsonRoot(tmdbData, fsData), _metaJsonOpts);
        }

        // ── Grouped preview ──────────────────────────────────────────────────────

        public async Task<ScanGroupResult> PreviewGroupedAsync(
            ScanPreviewRequest request, CancellationToken ct = default)
        {
            if (!Directory.Exists(request.Path))
            {
                var hint = BuildMappedDriveHint(request.Path);
                throw new DirectoryNotFoundException(
                    $"Scan path does not exist or is not accessible: {request.Path}.{hint}");
            }

            var mediaType = await _context.MediaTypes
                .FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            _log.Information("Grouped preview scan of {Path} (recursive={Recursive}, mediaType={MediaType}, hierarchyLevels={Levels})",
                request.Path, request.Recursive, mediaType.Name, mediaType.HierarchyLevels);

            // Audiobooks get a scanner-backed preview: reads tags, filters to audio files only,
            // and groups by book folder so the preview matches what the actual import will produce.
            if (string.Equals(mediaType.Name, "audiobooks", StringComparison.OrdinalIgnoreCase))
                return await PreviewAudiobooksAsync(request, mediaType, ct);

            // Collect all file paths
            var allPaths = new List<string>();
            var dirsToScan = new List<string> { request.Path };
            if (request.Recursive)
            {
                try { dirsToScan.AddRange(Directory.GetDirectories(request.Path, "*", SearchOption.AllDirectories)); }
                catch { /* fall through with root only */ }
            }

            _progress.Start(dirsToScan.Count);
            for (int i = 0; i < dirsToScan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _progress.UpdateFolder(dirsToScan[i], i + 1, allPaths.Count);
                try
                {
                    allPaths.AddRange(Directory.EnumerateFiles(dirsToScan[i])
                        .Where(f => !Path.GetFileName(f).StartsWith('.')));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Skipping inaccessible directory {Dir}", dirsToScan[i]);
                }
            }
            _progress.Complete();

            _log.Information("Grouped preview: {Count} files found, grouping with {Levels} hierarchy levels",
                allPaths.Count, mediaType.HierarchyLevels);

            return _groupingService.Group(allPaths, request.Path, mediaType.HierarchyLevels);
        }

        private async Task<ScanGroupResult> PreviewAudiobooksAsync(
            ScanPreviewRequest request, MediaType mediaType, CancellationToken ct)
        {
            var allScanners = _registry.GetFileScannerPlugins();
            var scanner = allScanners
                .FirstOrDefault(s => s.GetSupportedMediaTypes()
                    .Any(m => string.Equals(m.MediaTypeName, "audiobooks", StringComparison.OrdinalIgnoreCase)))
                ?? allScanners.FirstOrDefault()
                ?? throw new InvalidOperationException("No file scanner plugin is loaded.");

            _progress.Start(1);
            _progress.UpdateFolder(request.Path, 1, 0);

            var scannedFiles = await scanner.ScanDirectoryAsync(request.Path, request.Recursive, ct);
            var collapsed    = CollapseAudiobooksToFolders(scannedFiles, request.Path);

            _progress.Complete();

            _log.Information("Audiobooks preview: {Raw} audio files collapsed to {Books} book groups",
                scannedFiles.Count, collapsed.Count);

            // When the media type uses 3-level hierarchy (Author → Series? → Book),
            // return a hierarchical ScanGroup tree so that ImportGroupsAsync (used by
            // both the scheduled scan and the manual scan-review import) creates the
            // correct Author L0 → Series L1 → Book L2 structure in the DB.
            if (mediaType.HierarchyLevels >= 3)
            {
                var authorGroups = GroupAudiobooksByAuthorAndSeries(collapsed);
                _log.Information("Audiobooks preview (hierarchical): {Books} books grouped into {Authors} author(s)",
                    collapsed.Count, authorGroups.Count);
                return new Chronicle.Core.Models.Scan.ScanGroupResult
                {
                    Groups     = authorGroups,
                    TotalFiles = scannedFiles.Count,
                };
            }

            // Flat fallback (HierarchyLevels < 3): one ScanGroup per book, author/series
            // stored as metadata on the group rather than as parent items.
            var groups = collapsed.Select(f =>
            {
                var author = f.AudioAlbumArtist ?? f.AudioArtist;
                var score  = f.ConfidenceScore / 100.0;
                var signals = new List<string>();
                if (!string.IsNullOrWhiteSpace(f.AudioAlbum))   signals.Add("tags");
                if (!string.IsNullOrWhiteSpace(f.NfoPosterUrl)) signals.Add("nfo");
                if (signals.Count == 0)                          signals.Add("folder");

                return new Chronicle.Core.Models.Scan.ScanGroup
                {
                    GroupKey        = f.FilePath.ToLowerInvariant(),
                    Name            = f.ParsedTitle,
                    Year            = f.ParsedYear ?? f.AudioYear,
                    ConfidenceScore = score,
                    FolderPath      = f.FilePath,
                    Files           = [f.FilePath],
                    SignalSources   = signals,
                    Author          = string.IsNullOrWhiteSpace(author) ? null : author,
                    Series          = f.AudioGrouping,
                };
            }).ToList();

            return new Chronicle.Core.Models.Scan.ScanGroupResult
            {
                Groups     = groups,
                TotalFiles = scannedFiles.Count,
            };
        }

        // ── Import groups ────────────────────────────────────────────────────────

        public async Task<ImportApprovedSummary> ImportGroupsAsync(
            ImportGroupsRequest request,
            IReadOnlyList<int> userIds,
            CancellationToken ct = default,
            int progressOffset = 0,
            bool manageProgress = true)
        {
            var mediaType = await _context.MediaTypes
                .FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            int imported = 0, failed = 0, duplicates = 0;
            var failures = new List<string>();
            int processed = 0;
            int total = request.Groups.Sum(g => g.TotalFileCount);
            var createdItemIds = new List<int>();

            // Read batch size from app settings (default 50 if not configured or invalid)
            var batchSetting = await _context.AppSettings.FindAsync(["import_batch_size"], ct);
            int batchSize = 50;
            if (batchSetting is not null && int.TryParse(batchSetting.Value, out var bs) && bs >= 1)
                batchSize = bs;

            int pendingInBatch = 0;
            // Tracks how many items have already been seeded so we only seed the new slice
            // after each batch commit rather than re-scanning the whole list every time.
            int lastSeededIndex = 0;

            if (manageProgress)
                _importProgress.Start(total);

            foreach (var rootGroup in request.Groups)
            {
                ct.ThrowIfCancellationRequested();
                if (manageProgress)
                    _importProgress.Update(processed, total, rootGroup.Name);
                else
                    _importProgress.UpdateProcessed(progressOffset + processed, rootGroup.Name);
                try
                {
                    var (rootItem, rootIsNew) = await UpsertGroupItemAsync(
                        rootGroup, request.MediaTypeId, parentId: null,
                        hierarchyLevel: 0, ct);

                    if (rootIsNew)
                        createdItemIds.Add(rootItem.Id);

                    // Library entry only at root level — create for every user so all
                    // accounts can see file-scanned content regardless of who triggered the import.
                    bool anyNew = false;
                    foreach (var uid in userIds)
                    {
                        var libEntry = await _context.UserLibraries
                            .FirstOrDefaultAsync(l => l.UserId == uid && l.MediaItemId == rootItem.Id, ct);

                        // Also check the EF change tracker for entries that are pending
                        // but not yet flushed to the DB — prevents a unique constraint
                        // violation when two scan groups resolve to the same MediaItem
                        // (e.g. two folders with the same parsed title).
                        libEntry ??= _context.UserLibraries.Local
                            .FirstOrDefault(l => l.UserId == uid && l.MediaItemId == rootItem.Id);

                        if (libEntry is null)
                        {
                            _context.UserLibraries.Add(new UserLibrary
                            {
                                UserId      = uid,
                                MediaItemId = rootItem.Id,
                                Status      = LibraryStatus.Unwatched,
                                AddedAt     = DateTime.UtcNow,
                                UpdatedAt   = DateTime.UtcNow,
                            });
                            anyNew = true;
                        }
                    }
                    if (anyNew) imported++; else duplicates++;

                    // Persist children recursively — no library entries
                    await PersistChildGroupsAsync(rootGroup.Children, request.MediaTypeId,
                        rootItem.Id, hierarchyLevel: 1, createdItemIds, ct);

                    pendingInBatch++;

                    // Flush to DB every batchSize groups to reduce transaction overhead,
                    // then immediately seed enrichment rows for the newly committed items
                    // so the enrichment stats update in real-time during a long import.
                    if (pendingInBatch >= batchSize)
                    {
                        await _context.SaveChangesAsync(ct);
                        _log.Information("ImportGroups: committed batch of {BatchSize} groups ({Processed}/{Total} total)",
                            pendingInBatch, processed + 1, total);

                        if (createdItemIds.Count > lastSeededIndex)
                        {
                            await SeedEnrichmentRowsForNewItemsAsync(
                                createdItemIds[lastSeededIndex..], mediaType.Name, ct);
                            lastSeededIndex = createdItemIds.Count;
                        }

                        pendingInBatch = 0;
                    }

                    _log.Information("Imported group '{Name}' with {ChildCount} child groups",
                        rootGroup.Name, rootGroup.Children.Count);
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{rootGroup.Name}: {ex.Message}");
                    _log.Warning(ex, "Failed to import group '{Name}'", rootGroup.Name);
                }
                finally
                {
                    processed += rootGroup.TotalFileCount;
                }
            }

            // Flush any remaining groups that didn't fill a complete batch,
            // then seed enrichment rows for those final items too.
            if (pendingInBatch > 0)
            {
                await _context.SaveChangesAsync(ct);
                _log.Information("ImportGroups: committed final batch of {BatchSize} groups", pendingInBatch);
            }

            if (createdItemIds.Count > lastSeededIndex)
                await SeedEnrichmentRowsForNewItemsAsync(
                    createdItemIds[lastSeededIndex..], mediaType.Name, ct);

            var summary = new ImportApprovedSummary(imported, failed, failures, duplicates);
            if (manageProgress)
                _importProgress.Complete(new ImportProgressResult
                {
                    Imported   = summary.Imported,
                    Failed     = summary.Failed,
                    Failures   = summary.Failures,
                    Duplicates = summary.Duplicates,
                    TotalFiles = total,
                });
            return summary;
        }

        private async Task PersistChildGroupsAsync(
            List<ScanGroupImport> children, int mediaTypeId,
            int parentId, int hierarchyLevel, List<int> createdItemIds, CancellationToken ct)
        {
            foreach (var child in children)
            {
                var (item, isNew) = await UpsertGroupItemAsync(child, mediaTypeId, parentId, hierarchyLevel, ct);
                if (isNew)
                    createdItemIds.Add(item.Id);
                if (child.Children.Count > 0)
                    await PersistChildGroupsAsync(child.Children, mediaTypeId,
                        item.Id, hierarchyLevel + 1, createdItemIds, ct);
            }
        }

        public async Task BackfillFolderPathsAsync(CancellationToken ct = default)
        {
            // Find items where fileScanner.folderPath is explicitly JSON null.
            // The literal text `"folderPath":null` will always be present because
            // JsonSerializer serializes nullable reference types as null, not omitting them.
            var candidates = await _context.MediaItems
                .Where(m => m.MetadataJson != null
                         && EF.Functions.Like(m.MetadataJson, "%\"folderPath\":null%"))
                .ToListAsync(ct);

            int updated = 0;
            foreach (var item in candidates)
            {
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(item.MetadataJson!);
                    if (node is not System.Text.Json.Nodes.JsonObject root) continue;
                    if (root["fileScanner"] is not System.Text.Json.Nodes.JsonObject fs) continue;

                    // Only backfill if folderPath is truly null (not missing)
                    if (!fs.ContainsKey("folderPath") || fs["folderPath"] is not null) continue;

                    var filePaths = fs["filePaths"]?.AsArray();
                    if (filePaths is null || filePaths.Count == 0) continue;

                    var firstFile = filePaths[0]?.GetValue<string>();
                    if (string.IsNullOrEmpty(firstFile)) continue;

                    var folderPath = Path.GetDirectoryName(firstFile);
                    if (string.IsNullOrEmpty(folderPath)) continue;

                    fs["folderPath"] = folderPath;
                    item.MetadataJson = root.ToJsonString();
                    updated++;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "BackfillFolderPaths: skipping item {Id} — malformed MetadataJson", item.Id);
                }
            }

            if (updated > 0)
                await _context.SaveChangesAsync(ct);

            _log.Information("BackfillFolderPaths: updated {Count} of {Total} candidate items",
                updated, candidates.Count);
        }

        private async Task<(MediaItem Item, bool IsNew)> UpsertGroupItemAsync(
            ScanGroupImport group, int mediaTypeId,
            int? parentId, int hierarchyLevel, CancellationToken ct)
        {
            MediaItem? existing = null;

            // Primary: match by folder path stored in MetadataJson.
            // A physical folder path is globally unique — don't restrict by parentId or
            // hierarchyLevel, because enrichment can reparent items into collections
            // (e.g. a movie moves from Level 0 / no parent to Level 1 / collection parent).
            // Dropping mediaTypeId mirrors FindItemByFilePathAsync: a user may have changed
            // the item's type (e.g. movie → fanedit) and we must not duplicate it.
            if (!string.IsNullOrEmpty(group.FolderPath))
            {
                var fpCandidates = await _context.MediaItems
                    .Where(m => m.MetadataJson != null
                             && EF.Functions.Like(m.MetadataJson, "%folderPath%"))
                    .ToListAsync(ct);

                existing = fpCandidates.FirstOrDefault(m =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(m.MetadataJson!);
                        if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                            fs.TryGetProperty("folderPath", out var fp))
                            return string.Equals(fp.GetString(), group.FolderPath,
                                                 StringComparison.OrdinalIgnoreCase);
                    }
                    catch (JsonException) { }
                    return false;
                });
            }

            // Secondary: match by any file path in the fileScanner.filePaths array.
            // Bridges the gap for items imported before folderPath was populated — those items
            // have "folderPath":null but do carry a filePaths array with the original file paths.
            if (existing is null && group.Files.Count > 0)
            {
                var groupFileSet = group.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fpsCandidates = await _context.MediaItems
                    .Where(m => m.MetadataJson != null
                             && EF.Functions.Like(m.MetadataJson, "%filePaths%"))
                    .ToListAsync(ct);

                existing = fpsCandidates.FirstOrDefault(m =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(m.MetadataJson!);
                        if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                            fs.TryGetProperty("filePaths", out var arr) &&
                            arr.ValueKind == JsonValueKind.Array)
                        {
                            return arr.EnumerateArray()
                                      .Any(el => groupFileSet.Contains(el.GetString() ?? string.Empty));
                        }
                    }
                    catch (JsonException) { }
                    return false;
                });
            }

            // Tertiary: match by name (covers items where neither folderPath nor filePaths matched).
            // Strip trailing "(YYYY)" from both sides so "Show (2016)" and "Show" deduplicate.
            // No parentId/hierarchyLevel filter — same reasoning as the folderPath check above.
            var groupNameClean = System.Text.RegularExpressions.Regex
                .Replace(group.Name ?? "", @"\s*\(\d{4}\)\s*$", "").Trim();
            var nameCandidates = await _context.MediaItems
                .Where(m => m.MediaTypeId == mediaTypeId)
                .ToListAsync(ct);
            existing ??= nameCandidates.FirstOrDefault(m =>
            {
                var dbNameClean = System.Text.RegularExpressions.Regex
                    .Replace(m.Name ?? "", @"\s*\(\d{4}\)\s*$", "").Trim();
                return string.Equals(dbNameClean, groupNameClean, StringComparison.OrdinalIgnoreCase);
            });

            if (existing is not null)
            {
                existing.UpdatedAt   = DateTime.UtcNow;
                if (group.Year.HasValue)   existing.Year   = group.Year;
                if (group.Number.HasValue) existing.Number = group.Number;
                // Merge fileScanner data into MetadataJson — preserve any plugin keys
                // (TMDB, MusicBrainz, etc.) already stored by enrichment.
                var existingNode = System.Text.Json.Nodes.JsonNode.Parse(existing.MetadataJson ?? "{}")
                    as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                existingNode["fileScanner"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
                    new { importedAt = DateTime.UtcNow, filePaths = group.Files, folderPath = group.FolderPath }));
                existing.MetadataJson = existingNode.ToJsonString();
                return (existing, false);
            }

            var item = new MediaItem
            {
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                Name           = group.Name ?? string.Empty,
                Year           = group.Year,
                Number         = group.Number,
                PosterUrl      = group.PosterPath,
                MetadataJson   = JsonSerializer.Serialize(new
                {
                    fileScanner = new { importedAt = DateTime.UtcNow, filePaths = group.Files, folderPath = group.FolderPath }
                }),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync(ct); // need the ID for children
            return (item, true);
        }

        // ── Enrichment seeding ───────────────────────────────────────────────────

        /// <summary>
        /// Inserts pending <see cref="MediaItemEnrichment"/> rows for each of
        /// <paramref name="itemIds"/> against every installed <see cref="IMetadataProvider"/>
        /// that supports <paramref name="mediaTypeName"/>. Existing rows are skipped.
        /// </summary>
        private async Task SeedEnrichmentRowsForNewItemsAsync(
            List<int> itemIds, string mediaTypeName, CancellationToken ct = default)
        {
            // Use GetMetadataProviderEntries() so the enrichment row PluginId comes from the
            // manifest (the authoritative canonical ID), not the DLL's PluginId property which
            // may differ for pre-built plugins (e.g. legacy TMDB DLL returning "tmdb" while
            // the manifest declares "chronicle.plugin.tmdb").
            var entries = _registry.GetMetadataProviderEntries();
            if (entries.Count == 0)
                return;

            int seeded = 0;
            foreach (var (manifestPluginId, provider, _) in entries)
            {
                var supportedNames = provider.GetSupportedMediaTypes()
                    .Select(t => t.MediaTypeName)
                    .ToList();

                // Match on normalized name OR on the parent type hint (anime→tv, etc.) so that
                // providers declaring "tv" are seeded for items with media type "anime".
                var normalizedItem = NormalizeMediaTypeName(mediaTypeName);
                var hintItem       = ToMediaTypeHint(mediaTypeName);
                if (!supportedNames.Any(n =>
                        NormalizeMediaTypeName(n) == normalizedItem ||
                        NormalizeMediaTypeName(n) == hintItem))
                    continue;

                // Avoid SQLite IN-clause limit: query all rows for this plugin, filter in-memory.
                var itemIdSet = itemIds.ToHashSet();
                var existingSet = (await _context.MediaEnrichments
                    .Where(x => x.PluginId == manifestPluginId)
                    .Select(x => x.MediaItemId)
                    .ToListAsync(ct))
                    .Where(id => itemIdSet.Contains(id))
                    .ToHashSet();

                foreach (var itemId in itemIds)
                {
                    if (existingSet.Contains(itemId))
                        continue;

                    _context.MediaEnrichments.Add(new Chronicle.Core.Models.MediaItemEnrichment
                    {
                        MediaItemId = itemId,
                        PluginId    = manifestPluginId,
                        Status      = Chronicle.Core.Models.EnrichmentStatus.Pending,
                        MaxRetries  = 3,
                    });
                    seeded++;
                }
            }

            if (seeded > 0)
            {
                await _context.SaveChangesAsync(ct);
                _log.Information(
                    "Seeded {Count} pending enrichment rows for {ItemCount} newly imported items",
                    seeded, itemIds.Count);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps DB media-type names to the canonical form used by plugin
        /// <c>GetSupportedMediaTypes()</c> declarations so "movies" (DB) matches
        /// "movie" (TMDB plugin) and vice versa.
        /// </summary>
        private static string NormalizeMediaTypeName(string name) =>
            name.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : name.ToLowerInvariant();

        // ── Confidence threshold ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the threshold for <paramref name="mediaTypeName"/> by reading (in order):
        ///   1. <c>confidence_threshold_{mediaTypeName}</c> from the scanner plugin's saved settings
        ///   2. Legacy global <c>confidence_threshold</c> key (backward compatibility)
        ///   3. The loaded plugin's per-type default via <see cref="IFileScannerPlugin.GetConfidenceThreshold"/>
        ///   4. Hard-coded fallback of 75
        /// </summary>
        public async Task<int> GetConfidenceThresholdAsync(string mediaTypeName, CancellationToken ct = default)
        {
            var plugin = await _context.Plugins
                .FirstOrDefaultAsync(p => p.PluginId == "chronicle.plugin.filescanner" && p.IsEnabled, ct);
            if (plugin?.SettingsJson is { } json)
            {
                try
                {
                    var plainJson = _protector.Unprotect(json);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
                    if (settings is not null)
                    {
                        // 1. Per-type key
                        var perTypeKey = $"confidence_threshold_{mediaTypeName}";
                        if (settings.TryGetValue(perTypeKey, out var perTypeRaw)
                            && int.TryParse(perTypeRaw, out var perTypeParsed)
                            && perTypeParsed >= 0 && perTypeParsed <= 100)
                            return perTypeParsed;

                        // 2. Legacy global key
                        if (settings.TryGetValue("confidence_threshold", out var legacyRaw)
                            && int.TryParse(legacyRaw, out var legacyParsed)
                            && legacyParsed >= 0 && legacyParsed <= 100)
                            return legacyParsed;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "GetConfidenceThreshold: failed to read scanner settings — using plugin/default value");
                }
            }
            // 3. Plugin instance's per-type method / fallback default
            var scanner = _registry.GetFileScannerPlugins().FirstOrDefault();
            return scanner?.GetConfidenceThreshold(mediaTypeName) ?? 75;
        }

        /// <inheritdoc cref="IFileScanService.GetConfidenceThresholdAsync(CancellationToken)"/>
        public Task<int> GetConfidenceThresholdAsync(CancellationToken ct = default) =>
            GetConfidenceThresholdAsync(string.Empty, ct);
    }
}
