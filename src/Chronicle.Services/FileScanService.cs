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

            var names = scanners
                .SelectMany(s => s.GetSupportedMediaTypes())
                .Select(m => m.MediaTypeName)
                .Distinct()
                .ToArray();

            return await Task.FromResult((true, names));
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

            // Find a scanner that supports this media type
            var scanner = _registry.GetFileScannerPlugins()
                .FirstOrDefault(s => s.GetSupportedMediaTypes()
                    .Any(m => string.Equals(m.MediaTypeName, mediaType.Name, StringComparison.OrdinalIgnoreCase)));

            if (scanner is null)
                throw new InvalidOperationException($"No file scanner plugin supports media type '{mediaType.Name}'.");

            var threshold = request.ConfidenceThreshold == 80
                ? await GetConfidenceThresholdAsync(ct)
                : request.ConfidenceThreshold;

            _log.Information("Starting file scan of {Path} (recursive={Recursive}, threshold={Threshold}, mediaType={MediaType})",
                request.Path, request.Recursive, threshold, mediaType.Name);

            var scannedFiles = await scanner.ScanDirectoryAsync(request.Path, request.Recursive, ct);

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
            // LIKE '%fileScanner%' narrows the result set before in-memory comparison.
            var candidates = await _context.MediaItems
                .Where(m => m.MediaTypeId == mediaTypeId
                         && m.MetadataJson != null
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
            catch { /* malformed JSON — treat as no path */ }
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
                var byTitleYear = await _context.MediaItems.FirstOrDefaultAsync(
                    m => m.MediaTypeId == mediaTypeId
                      && m.Year == file.ParsedYear
                      && EF.Functions.Like(m.Name, file.ParsedTitle), ct);
                if (byTitleYear is not null)
                    return byTitleYear;
            }

            // 3. Title-only match (lower confidence — only when year is unknown)
            if (!file.ParsedYear.HasValue)
            {
                var byTitle = await _context.MediaItems.FirstOrDefaultAsync(
                    m => m.MediaTypeId == mediaTypeId
                      && EF.Functions.Like(m.Name, file.ParsedTitle), ct);
                if (byTitle is not null)
                    return byTitle;
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
            var stub = new MediaItem
            {
                MediaTypeId  = mediaTypeId,
                Name         = file.ParsedTitle,
                Year         = file.ParsedYear,
                PosterUrl    = file.NfoPosterUrl ?? file.LocalPosterPath,
                MetadataJson = SerializeMetadata(scannedFile: file),
                HierarchyLevel = 0,
                CreatedAt    = DateTime.UtcNow,
                UpdatedAt    = DateTime.UtcNow,
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
                    .Select(c => new { Result = c.Metadata, Score = ScoreByNameYear(c.Metadata.Title, c.Metadata.Year, item.Name, item.Year) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (best is null)
                {
                    _log.Information("RefreshMetadata: no TMDB match found for '{Name}'", item.Name);
                    return item;
                }
                extId = best.Result.ExternalId;
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

        public async Task<List<MetadataCandidate>> SearchMetadataAsync(
            string query, string mediaTypeHint, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No metadata provider is loaded. Install and configure a metadata plugin (e.g. TMDB).");

            var results = await provider.SearchAsync(new MediaSearchContext(query), ct);

            return results
                .Select(c => new MetadataCandidate(c.Metadata.ExternalId, c.Metadata.Title, c.Metadata.Year, c.Metadata.PosterUrl, c.Metadata.Overview, c.Metadata.Rating, 0))
                .ToList();
        }

        public async Task<Chronicle.Core.Models.MediaItem> AddFromSearchAsync(
            string externalId, int mediaTypeId, int userId, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault()
                ?? throw new InvalidOperationException("No metadata provider is loaded.");

            _ = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == mediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {mediaTypeId} not found.");

            var meta = await provider.GetByIdAsync(externalId, ct);
            var (source, extId) = ParseSuggestedExternalId(externalId);

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
                item.MetadataJson   = SerializeMetadata(tmdbMeta: meta, existingJson: item.MetadataJson);
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
                    MetadataJson   = SerializeMetadata(tmdbMeta: meta),
                    HierarchyLevel = 0,
                    CreatedAt      = DateTime.UtcNow,
                    UpdatedAt      = DateTime.UtcNow,
                };
                _context.MediaItems.Add(item);
                await _context.SaveChangesAsync(ct);
                await UpsertExternalIdAsync(item.Id, externalId, ct);
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

            // "movie:*" or "tv:*" — stored verbatim with source="tmdb"
            return ("tmdb", suggested);
        }

        /// <summary>Maps a Chronicle media type name to the hint expected by metadata providers.</summary>
        private static string ToMediaTypeHint(string mediaTypeName)
        {
            var n = mediaTypeName.ToLowerInvariant();
            if (n.Contains("tv") || n.Contains("show") || n.Contains("series")) return "tv";
            if (n.Contains("music") || n.Contains("album") || n.Contains("track")) return "music";
            return "movie";
        }

        // ── Hierarchy grouping ────────────────────────────────────────────────────

        internal record ShowGroup(string ShowTitle, Dictionary<int, SeasonGroup> Seasons);
        internal record SeasonGroup(int SeasonNumber, List<Chronicle.Plugins.Models.ScannedFile> Episodes);

        /// <summary>Exposed for unit testing only.</summary>
        internal static List<ShowGroup> GroupByShowForTest(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> files) => GroupByShow(files);

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
            string? FilePath, string? LocalPosterPath, string? NfoPosterUrl);

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
                fsData = new FileScannerMetaJson(
                    scannedFile.FilePath,
                    scannedFile.LocalPosterPath,
                    scannedFile.NfoPosterUrl);
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

        // ── Import groups ────────────────────────────────────────────────────────

        public async Task<ImportApprovedSummary> ImportGroupsAsync(
            ImportGroupsRequest request, IReadOnlyList<int> userIds, CancellationToken ct = default)
        {
            var mediaType = await _context.MediaTypes
                .FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            int imported = 0, failed = 0, duplicates = 0;
            var failures = new List<string>();
            int processed = 0;
            int total = request.Groups.Count;
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

            _importProgress.Start(total);

            foreach (var rootGroup in request.Groups)
            {
                ct.ThrowIfCancellationRequested();
                _importProgress.Update(processed, total, rootGroup.Name);
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
                    processed++;
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
            _importProgress.Complete(new ImportProgressResult
            {
                Imported   = summary.Imported,
                Failed     = summary.Failed,
                Failures   = summary.Failures,
                Duplicates = summary.Duplicates,
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

        private async Task<(MediaItem Item, bool IsNew)> UpsertGroupItemAsync(
            ScanGroupImport group, int mediaTypeId,
            int? parentId, int hierarchyLevel, CancellationToken ct)
        {
            MediaItem? existing = null;

            // Primary: match by folder path stored in MetadataJson — survives name changes
            // (e.g. first import stored "Enterprise", re-import resolves "Star Trek: Enterprise")
            if (!string.IsNullOrEmpty(group.FolderPath))
            {
                var candidates = await _context.MediaItems
                    .Where(m => m.MediaTypeId == mediaTypeId
                             && m.ParentId   == parentId
                             && m.HierarchyLevel == hierarchyLevel
                             && m.MetadataJson != null
                             && EF.Functions.Like(m.MetadataJson, "%folderPath%"))
                    .ToListAsync(ct);

                existing = candidates.FirstOrDefault(m =>
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(m.MetadataJson!);
                        if (doc.RootElement.TryGetProperty("fileScanner", out var fs) &&
                            fs.TryGetProperty("folderPath", out var fp))
                            return string.Equals(fp.GetString(), group.FolderPath,
                                                 StringComparison.OrdinalIgnoreCase);
                    }
                    catch { /* malformed JSON */ }
                    return false;
                });
            }

            // Fallback: match by name (covers items imported before folderPath was added).
            // Strip trailing "(YYYY)" from both sides so "Show (2016)" and "Show" deduplicate
            // correctly when year extraction now produces a clean name without the suffix.
            var groupNameClean = System.Text.RegularExpressions.Regex
                .Replace(group.Name ?? "", @"\s*\(\d{4}\)\s*$", "").Trim();
            var candidates2 = await _context.MediaItems
                .Where(m => m.MediaTypeId   == mediaTypeId
                         && m.ParentId      == parentId
                         && m.HierarchyLevel == hierarchyLevel)
                .ToListAsync(ct);
            existing ??= candidates2.FirstOrDefault(m =>
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
                // Refresh MetadataJson so folderPath is written even on pre-existing items
                existing.MetadataJson = JsonSerializer.Serialize(new
                {
                    fileScanner = new { importedAt = DateTime.UtcNow, filePaths = group.Files, folderPath = group.FolderPath }
                });
                return (existing, false);
            }

            var item = new MediaItem
            {
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                Name           = group.Name,
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
        /// Inserts pending <see cref="MediaItemEnrichmentStatus"/> rows for each of
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
            foreach (var (manifestPluginId, provider) in entries)
            {
                var supportedNames = provider.GetSupportedMediaTypes()
                    .Select(t => t.MediaTypeName)
                    .ToList();

                if (!supportedNames.Any(n => NormalizeMediaTypeName(n) == NormalizeMediaTypeName(mediaTypeName)))
                    continue;

                var existingSet = (await _context.EnrichmentStatuses
                    .Where(x => x.PluginId == manifestPluginId && itemIds.Contains(x.MediaItemId))
                    .Select(x => x.MediaItemId)
                    .ToListAsync(ct))
                    .ToHashSet();

                foreach (var itemId in itemIds)
                {
                    if (existingSet.Contains(itemId))
                        continue;

                    _context.EnrichmentStatuses.Add(new Chronicle.Core.Models.MediaItemEnrichmentStatus
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
                catch { /* ignore malformed JSON */ }
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
