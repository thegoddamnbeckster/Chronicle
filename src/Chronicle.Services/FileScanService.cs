using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicle.Core.Helpers;
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

        /// <summary>
        /// Locates and losslessly reads a sidecar for <paramref name="filePath"/>, for import
        /// paths (ImportApprovedAsync, ImportDirectAsync/ImportSingleFileAsync) that only ever
        /// receive a bare file path with no upstream ScannedFile/ScanGroup already carrying a
        /// resolved NfoPath (unlike the grouped-scan and Identify flows, which compute it once
        /// up front and thread it through). Asks every loaded <see cref="ISidecarFormatPlugin"/>
        /// in turn (Kodi's .nfo today) -- same rule ScanGroupingService/ApplyNfoSignals use, so
        /// an item found this way and one found via a grouped scan agree on which sidecar
        /// belongs to it.
        /// </summary>
        private (string? path, string? raw, JsonElement? parsed) LookupNfo(string filePath)
        {
            foreach (var plugin in _registry.GetSidecarFormatPlugins())
            {
                var nfoPath = plugin.FindSidecar(filePath);
                if (nfoPath is null) continue;
                var capture = plugin.CaptureLossless(nfoPath);
                return (nfoPath, capture?.RawText, capture?.Parsed);
            }
            return (null, null, null);
        }

        /// <summary>
        /// Captures a sidecar already located at <paramref name="sidecarPath"/> losslessly, by
        /// asking every loaded <see cref="ISidecarFormatPlugin"/> in turn until one recognizes
        /// it. Used where the path is already known (e.g. ScannedFile.NfoPath, ScanGroup.NfoPath)
        /// and only the raw+parsed capture is still needed.
        /// </summary>
        private SidecarCapture? CaptureSidecar(string? sidecarPath)
        {
            if (sidecarPath is null) return null;
            foreach (var plugin in _registry.GetSidecarFormatPlugins())
            {
                var capture = plugin.CaptureLossless(sidecarPath);
                if (capture is not null) return capture;
            }
            return null;
        }

        /// <summary>
        /// Overlays sidecar-format-plugin (e.g. Kodi .nfo) signal onto raw scanner results.
        /// BuiltInFileScannerPlugin itself cannot do this: plugins are instantiated via bare
        /// Activator.CreateInstance (see PluginRegistry.DiscoverAndInstantiate) with no DI, so
        /// it can never receive IPluginRegistry to look up installed ISidecarFormatPlugins. This
        /// is the compensating step, run once against every ScanDirectoryAsync result, that
        /// reproduces exactly what BuiltInFileScannerPlugin's own ParseFile used to do directly
        /// against the old NfoSignalExtractor -- same field-priority rules (sidecar wins over
        /// tag/filename-derived values when present), now driven through the plugin registry.
        /// </summary>
        private void ApplyNfoSignals(IEnumerable<Chronicle.Plugins.Models.ScannedFile> files)
        {
            var sidecarPlugins = _registry.GetSidecarFormatPlugins();
            if (sidecarPlugins.Count == 0) return;

            foreach (var file in files)
            {
                string? nfoPath = null;
                SidecarSignal? nfo = null;
                foreach (var plugin in sidecarPlugins)
                {
                    var candidate = plugin.FindSidecar(file.FilePath);
                    if (candidate is null) continue;
                    nfoPath = candidate;
                    nfo = plugin.ExtractSignal(candidate);
                    break;
                }
                if (nfoPath is null) continue;

                file.NfoPath = nfoPath;
                if (nfo is null) continue;

                if (nfo.Title is not null)     file.ParsedTitle         = nfo.Title;
                if (nfo.Year.HasValue)         file.ParsedYear          = nfo.Year;
                file.SuggestedExternalId       = nfo.ExternalId;
                file.NfoPosterUrl              = nfo.PosterUrl;
                file.ShowTitle                 = nfo.ShowTitle;
                if (nfo.Season.HasValue)       file.SeasonNumber        = nfo.Season;
                if (nfo.Episode.HasValue)      file.EpisodeNumber       = nfo.Episode;

                if (nfo.ExternalId is not null)
                    file.ConfidenceScore = 100;
                else if (nfo.Title is not null && nfo.Year.HasValue)
                    file.ConfidenceScore = 85;
                else if (nfo.Title is not null)
                    file.ConfidenceScore = 78;
            }
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
            ApplyNfoSignals(scannedFiles);

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

            // Other 3-level hierarchy types (TV, anime, ...): the flat per-file loop below
            // treats every file as an independent top-level item using FileNameParser's
            // single-character-separator regex, which mis-parses the extremely common
            // "Show - S01E01 - Episode Title" naming convention (three-char " - " separators)
            // into orphaned, wrongly-named items with no Show/Season nesting at all -- confirmed
            // 2026-08-24 scanning a real library where this silently produced hundreds of
            // garbage top-level "episodes". ScheduledScanService's nightly scan already avoids
            // this by routing through the folder-hierarchy-aware PreviewGroupedAsync/
            // ImportGroupsAsync pair instead of the flat scanner output; a manual "Scan Now"
            // must use the exact same path so it doesn't silently misbehave for any type the
            // flat loop was never designed for.
            if (mediaType.HierarchyLevels >= 3 &&
                !string.Equals(mediaType.Name, "audiobooks", StringComparison.OrdinalIgnoreCase))
                return await ScanHierarchicalAsync(request, mediaType, userId, threshold, ct);

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

        /// <summary>
        /// "Scan Now" for a 3-level hierarchy type other than audiobooks (TV, anime, ...):
        /// groups files by folder structure via PreviewGroupedAsync, then persists the
        /// groups meeting the confidence threshold via ImportGroupsAsync -- the same pair
        /// ScheduledScanService's nightly scan uses, so a manual scan produces the same
        /// correctly-nested Show/Season/Episode hierarchy instead of flat, ungrouped items.
        /// </summary>
        private async Task<FileScanSummary> ScanHierarchicalAsync(
            FileScanRequest request, MediaType mediaType, int userId, int threshold, CancellationToken ct)
        {
            var groupResult = await PreviewGroupedAsync(
                new ScanPreviewRequest(request.Path, request.Recursive, request.MediaTypeId), ct);

            double thresholdFraction = threshold / 100.0;
            var passing = groupResult.Groups.Where(g => g.ConfidenceScore >= thresholdFraction).ToList();
            var below   = groupResult.Groups.Where(g => g.ConfidenceScore < thresholdFraction).ToList();

            var skippedFiles = below
                .Select(g => (Group: g, Score: (int)Math.Round(g.ConfidenceScore * 100)))
                .SelectMany(g => CollectLeafFiles(g.Group).Select(f =>
                    new SkippedFile(f, Path.GetFileNameWithoutExtension(f), g.Score)))
                .ToList();

            if (passing.Count == 0)
            {
                _log.Information("Hierarchical scan complete: 0 added, {Skipped} below threshold", skippedFiles.Count);
                return new FileScanSummary(0, skippedFiles.Count, 0, skippedFiles);
            }

            var importGroups  = passing.Select(ToScanGroupImport).ToList();
            var importRequest = new ImportGroupsRequest(importGroups, request.MediaTypeId);

            // manageProgress:false hands responsibility for Start/Complete to THIS caller
            // (see ImportGroupsAsync's own manageProgress branches) -- ScheduledScanService
            // brackets its own manageProgress:false calls the same way. Omitting this left
            // the shared ImportProgressService singleton permanently reporting IsRunning
            // after every manual "Scan Now" on a TV/anime folder.
            var totalFiles = importGroups.Sum(g => g.TotalFileCount);
            _importProgress.Start(totalFiles);
            ImportApprovedSummary summary;
            try
            {
                summary = await ImportGroupsAsync(importRequest, [userId], ct, manageProgress: false);
            }
            catch
            {
                _importProgress.Fail("Hierarchical scan failed");
                throw;
            }
            _importProgress.Complete(new ImportProgressResult
            {
                Imported   = summary.Imported,
                Failed     = summary.Failed,
                Failures   = summary.Failures,
                Duplicates = summary.Duplicates,
                TotalFiles = totalFiles,
            });

            _log.Information(
                "Hierarchical scan complete: {Added} added, {AlreadyInLibrary} already in library, {Skipped} below threshold",
                summary.Imported, summary.Duplicates, skippedFiles.Count);

            return new FileScanSummary(summary.Imported, skippedFiles.Count, summary.Duplicates, skippedFiles);
        }

        private static List<string> CollectLeafFiles(ScanGroup group) =>
            group.Files.Concat(group.Children.SelectMany(CollectLeafFiles)).ToList();

        /// <summary>Converts a grouped-scan result into the shape ImportGroupsAsync persists.
        /// Internal (not private) so ScheduledScanService's nightly scan uses this exact same
        /// conversion instead of maintaining its own copy -- the two used to be
        /// near-verbatim duplicates that could silently drift apart.</summary>
        internal static ScanGroupImport ToScanGroupImport(ScanGroup group) => new(
            group.Name,
            group.Year,
            group.PosterPath,
            group.Children.Select(ToScanGroupImport).ToList(),
            group.Files,
            group.FolderPath,
            group.Number);

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
                    ApplyNfoSignals(fallback);
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
            ApplyNfoSignals(allFiles);

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
            // Was previously _registry.GetMetadataProviders().FirstOrDefault() -- whichever
            // provider happened to be first in registration order, with NO regard for whether
            // it declares support for request.MediaTypeId at all. That meant a music/book/etc.
            // identify call could silently be handed to (say) a movie-only provider, which would
            // then either return nothing useful or waste a full timeout per file. Scoped to
            // type-supporting providers only, same as SearchMetadataAsync already does.
            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");
            var provider = ProvidersForType(mediaType.Name).FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"No metadata provider supports media type \"{mediaType.Name}\". " +
                    "Install and configure a matching metadata plugin (e.g. TMDB for movies/TV).");

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
                        var meta = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                            async t => (MediaMetadata?)await provider.GetByIdAsync(file.SuggestedExternalId, t), provider.PluginId, "GetByIdAsync",
                            null, msg => _log.Warning(msg), msg => _log.Error(msg), ct);
                        if (meta is not null)
                        {
                            candidates.Add(new MetadataCandidate(
                                meta.ExternalId,
                                meta.Title,
                                meta.Year,
                                meta.PosterUrl,
                                meta.Overview,
                                meta.Rating,
                                95));
                        }
                    }
                    else
                    {
                        // Title-only search — do NOT append the year to the query string.
                        // TMDB treats the query as plain text; the year is not in the
                        // stored title so appending it returns zero results.  ScoreCandidate
                        // already handles year matching on the returned candidates.
                        var query = file.ParsedTitle;

                        var searchResults = await ProviderCallGuard.CallAsync(
                            t => provider.SearchAsync(new MediaSearchContext(query, file.ParsedYear), t),
                            provider.PluginId, "SearchAsync", (IReadOnlyList<ScoredCandidate>)[],
                            msg => _log.Warning(msg), msg => _log.Error(msg), ct);

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
                    var meta = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                        async t => (MediaMetadata?)await provider.GetByIdAsync(approval.ExternalId, t), provider.PluginId, "GetByIdAsync",
                        null, msg => _log.Warning(msg), msg => _log.Error(msg), ct)
                        ?? throw new InvalidOperationException(
                            $"Provider {provider.PluginId} did not return metadata for {approval.ExternalId}");
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
                        // Confirmed directly (2026-08-21): FindByTitleAsync treats a null year as
                        // "match any year at all", not "match nothing" -- fine for its other,
                        // explicitly-intentional no-year-filter callers, but here it silently
                        // merged "The Running Man (1987)" onto the unrelated 2025 remake because
                        // the provider's own metadata for that specific title had no parseable
                        // year. An unknown candidate year must never justify reusing a same-titled
                        // item that has a real, different year -- skip the reuse fallback entirely
                        // and create a new item instead; a possible duplicate is far cheaper to
                        // fix than two real movies silently merged into one.
                        var existingByTitle = meta.Year.HasValue
                            ? await FindByTitleAsync(meta.Title, request.MediaTypeId, meta.Year, ct)
                            : null;
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
                            // Record the approved file's own path (and any .nfo sidecar next
                            // to it) the same way every other import path does -- this branch
                            // previously called SerializeMetadata(tmdbMeta: meta) with no
                            // scanner data at all, so a brand-new item created here had no
                            // fileScanner section whatsoever: no file path, no NFO, nothing,
                            // even though approval.FilePath was right here. Confirmed gap
                            // (2026-09-02) while closing out NFO lossless-ingestion coverage --
                            // not previously about NFO specifically, this item's own file path
                            // was never tracked either.
                            var (nfoPath, nfoRaw, nfoParsed) = LookupNfo(approval.FilePath);
                            mediaItem = new MediaItem
                            {
                                MediaTypeId    = request.MediaTypeId,
                                Name           = meta.Title,
                                Year           = meta.Year,
                                Overview       = meta.Overview,
                                PosterUrl      = meta.PosterUrl,
                                RuntimeMinutes = meta.RuntimeMinutes,
                                MetadataJson   = SerializeMetadata(tmdbMeta: meta,
                                    scannerFilePath: approval.FilePath,
                                    scannerNfoPath: nfoPath, scannerNfoRaw: nfoRaw, scannerNfoParsed: nfoParsed),
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
                    var existingItem = await FindItemByFilePathAsync(file.FilePath, ct);
                    if (existingItem is not null)
                    {
                        _log.Information("Duplicate file '{Path}' already imported as '{Title}' (id={Id}) — skipping",
                            file.FilePath, existingItem.Name, existingItem.Id);
                        duplicates++;
                        pairs.Add((file, existingItem));
                        continue;
                    }

                    // DirectImportFileDto never carried an NfoPath from the client (the scanner
                    // resolved one at scan-preview time, but ImportDirectAsync only ever receives
                    // title/year/filePath back) -- look it up fresh the same way scan time did, so
                    // this flow's items get the same lossless NFO capture as every other import path.
                    var (nfoPath, nfoRaw, nfoParsed) = LookupNfo(file.FilePath);
                    var item = new MediaItem
                    {
                        Name           = file.ParsedTitle,
                        MediaTypeId    = mediaType.Id,
                        ParentId       = null,
                        HierarchyLevel = 0,
                        Year           = file.ParsedYear,
                        Number         = file.EpisodeNumber ?? file.AudioTrackNumber,
                        MetadataJson   = SerializeMetadata(scannerFilePath: file.FilePath,
                            scannerNfoPath: nfoPath, scannerNfoRaw: nfoRaw, scannerNfoParsed: nfoParsed),
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
                    authorGroup.Name, mediaType.Id, parentId: null, hierarchyLevel: 0, ct,
                    folderPath: authorGroup.FolderPath);

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
            var existing = await FindItemByFilePathAsync(folderPath, ct);
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
            var existing = await FindItemByFilePathAsync(file.FilePath, ct);
            if (existing is not null)
            {
                _log.Information("Duplicate file '{Path}' already imported as '{Title}' (id={Id}) — skipping",
                    file.FilePath, existing.Name, existing.Id);
                if (addLibraryEntry)
                    await UpsertLibraryEntryAsync(userIds, existing.Id, ct);
                return false;
            }

            // Same NFO lookup as ImportDirectAsync's flat branch and for the same reason --
            // this covers TV episodes (this method is also the leaf-item creator for the
            // hierarchical/Direct-import path) and audiobook chapters alike.
            var (nfoPath, nfoRaw, nfoParsed) = LookupNfo(file.FilePath);
            var item = new MediaItem
            {
                Name           = file.ParsedTitle,
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                Year           = file.ParsedYear,
                Number         = file.EpisodeNumber ?? file.AudioTrackNumber,
                MetadataJson   = SerializeMetadata(scannerFilePath: file.FilePath,
                    scannerNfoPath: nfoPath, scannerNfoRaw: nfoRaw, scannerNfoParsed: nfoParsed),
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
        /// Returns the <see cref="MediaItem"/> whose <c>fileScanner.filePaths</c> array
        /// (see <see cref="FileIdentityJson"/>) contains <paramref name="filePath"/>
        /// (case-insensitive exact match). Used to guarantee only one MediaItem is ever
        /// created for a given physical file.
        ///
        /// Searches across ALL media types — a physical file's identity does not depend on
        /// how it's currently classified. This lets a user's manual "Change Type" survive a
        /// rescan without creating a duplicate. Cross-type identity confusion is NOT prevented
        /// here (a real file only ever has one path, so an exact match is always the same
        /// physical file) — the guard against accidental cross-type MERGING of two genuinely
        /// different items lives in DuplicateCleanupService, which never crosses types even
        /// when it observes a filePaths collision.
        /// </summary>
        private async Task<MediaItem?> FindItemByFilePathAsync(string filePath, CancellationToken ct)
        {
            // LIKE '%fileScanner%' narrows the result set before in-memory JSON comparison.
            var candidates = await _context.MediaItems
                .Where(m => m.MetadataJson != null
                         && EF.Functions.Like(m.MetadataJson, "%fileScanner%"))
                .ToListAsync(ct);

            return candidates.FirstOrDefault(m =>
                Chronicle.Services.Scan.FileIdentityJson.ContainsFilePath(m.MetadataJson, filePath));
        }

        private async Task<MediaItem?> FindExistingItemAsync(
            Chronicle.Plugins.Models.ScannedFile file, int mediaTypeId, CancellationToken ct)
        {
            // 1. Match by exact physical file path (highest confidence — this IS the file).
            //    Global across types, same as FindItemByFilePathAsync everywhere else.
            var byPath = await FindItemByFilePathAsync(file.FilePath, ct);
            if (byPath is not null) return byPath;

            // 2. Match by external ID
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

            // 3. Match by title + year
            if (file.ParsedYear.HasValue)
            {
                var hit = await FindByTitleAsync(file.ParsedTitle, mediaTypeId, file.ParsedYear, ct);
                if (hit is not null) return hit;
            }

            // 4. Title-only match (lower confidence — only when year is unknown)
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

        // Matches any trailing parenthetical, e.g. " (film)", " (TV series)" -- see
        // FindByTitleAsync's deparenthesized variant.
        private static readonly System.Text.RegularExpressions.Regex _trailingParenthetical =
            new(@"\s*\([^)]+\)$",
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
                      && m.HierarchyLevel == 0
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
                          && m.HierarchyLevel == 0
                          && (year == null || m.Year == year)
                          && m.Name.ToLower() == variantLower, ct);
                    if (hit is not null) return hit;
                }
            }

            // Strip a trailing parenthetical disambiguator (e.g. "Dogma (film)", "Chosen (TV
            // series)") and retry all three variants. Root-caused a real duplicate (2026-08-30):
            // Wikipedia's own article title for a movie is often its disambiguated form ("Dogma"
            // is a disambiguation page there; the film's article is "Dogma (film)"), which never
            // matched the already-catalogued "Dogma" and created a second MediaItem instead of
            // reusing it. Deliberately generic (any trailing "(...)", not a hardcoded list of
            // known disambiguator words) so it isn't a Wikipedia-specific patch -- same technique
            // as the trailing-year strip above, just one token class wider. Still scoped to the
            // same media type + year as every other variant here, which keeps the false-positive
            // risk in line with the colon/dash variants already tried.
            var deparenthesized = _trailingParenthetical.Replace(title, string.Empty).Trim();
            if (deparenthesized != title && deparenthesized.Length > 0)
            {
                var deparenColon = deparenthesized.Replace(" - ", ": ");
                var deparenDash  = deparenthesized.Replace(": ", " - ");

                foreach (var variant in new[] { deparenthesized, deparenColon, deparenDash }.Distinct(StringComparer.Ordinal))
                {
                    var variantLower = variant.ToLowerInvariant();
                    var hit = await _context.MediaItems.FirstOrDefaultAsync(
                        m => m.MediaTypeId == mediaTypeId
                          && m.HierarchyLevel == 0
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
                      && m.HierarchyLevel == 0
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
                        var meta = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                            async t => (MediaMetadata?)await provider.GetByIdAsync(file.SuggestedExternalId, t), provider.PluginId, "GetByIdAsync",
                            null, msg => _log.Warning(msg), msg => _log.Error(msg), ct)
                            ?? throw new InvalidOperationException(
                                $"Provider {provider.PluginId} did not return metadata for {file.SuggestedExternalId}");
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
                // Root-caused a real duplicate (2026-08-30, "Dogma" / "Dogma (film)"): two
                // MediaItems ended up carrying the identical (source, externalId) pair because
                // nothing ever checked whether another item already owned it before writing.
                // If one does, this item is (almost certainly) a duplicate of that one -- don't
                // silently attach the same identity to both; that's what let the pair sit
                // unflagged. Skip the write and log loudly enough to find in server logs rather
                // than deciding FOR the user whether to merge or delete.
                var ownedByOther = await _context.MediaExternalIds
                    .Include(e => e.MediaItem)
                    .Where(e => e.Source == source && e.ExternalId == extId && e.MediaItemId != mediaItemId)
                    .FirstOrDefaultAsync(ct);
                if (ownedByOther is not null)
                {
                    _log.Warning(
                        "Skipped attaching external id {Source}:{ExternalId} to media item {MediaItemId} -- " +
                        "already owned by media item {OtherMediaItemId} ({OtherName}). Likely duplicate.",
                        source, extId, mediaItemId, ownedByOther.MediaItemId, ownedByOther.MediaItem?.Name);
                    return;
                }

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

        // ── Metadata search + direct add (for Add Media UI) ──────────────────────
        //
        // Single-item metadata refresh lives only in MetadataEnrichmentService.EnrichItemAsync
        // (routed through /media/{id}/refresh) -- collection-aware, since it calls
        // IMovieCollectionService.EnsureCollectionParentAsync on every successful match. A
        // second, separate "refresh one item" implementation used to live here
        // (RefreshMetadataAsync/RefreshChildFromRootAsync); it updated MetadataJson (including
        // any fresh belongsToCollection data) but never re-synced collection membership to
        // match, so collection state could silently drift out of sync with metadata if it were
        // ever called. It had zero call sites anywhere in the app -- removed rather than fixed,
        // so there is exactly one "refresh this item's metadata" implementation to keep correct.

        /// <summary>
        /// Returns providers that explicitly declare support for the given media type.
        /// No parent-type broadening — if the user selected "Anime", only providers that
        /// declare "anime" are searched (not every "tv" provider).
        /// Parent-type hints are used for enrichment seeding only, not for search.
        /// </summary>
        private IReadOnlyList<IMetadataProvider> ProvidersForType(string mediaTypeHint)
        {
            var all = _registry.GetMetadataProviders();
            if (all.Count == 0) return all;

            var normalizedType = NormalizeMediaTypeName(mediaTypeHint);

            return all.Where(p => p.GetSupportedMediaTypes().Any(t =>
                    string.Equals(NormalizeMediaTypeName(t.MediaTypeName), normalizedType,
                        StringComparison.OrdinalIgnoreCase)))
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

            // Run all providers in parallel with a per-provider timeout so a slow or
            // unresponsive provider doesn't block results from fast ones. Shorter than
            // ProviderCallGuard's own 25s default -- this backs an interactive user-facing
            // search, so a provider that's merely slow (not fully stuck) should still drop
            // out quickly rather than making the whole search feel unresponsive. A provider
            // throwing for a reason unrelated to timeout (e.g. SIMKL/Trakt not supporting
            // text search) is expected here and must not fail the whole multi-provider
            // search -- ProviderCallGuard logs and re-throws non-timeout exceptions by
            // design (right default for most call sites), so this one still needs its own
            // catch to preserve "skip the provider that failed, keep the others' results".
            var context     = new MediaSearchContext(query, MediaTypeName: mediaTypeHint);
            var tasks       = providers.Select(async p =>
            {
                try
                {
                    return await ProviderCallGuard.CallAsync(
                        t => p.SearchAsync(context, t), p.PluginId, "SearchAsync", (IReadOnlyList<ScoredCandidate>)[],
                        msg => _log.Warning(msg), msg => _log.Error(msg), ct, TimeSpan.FromSeconds(4));
                }
                catch
                {
                    return (IReadOnlyList<ScoredCandidate>)[];
                }
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
                var contribExternalIds   = new List<ContributingExternalId>();
                if (!string.IsNullOrEmpty(m.Source)) sources.Add(m.Source);
                foreach (var (_, other) in allCandidates)
                {
                    if (other.Metadata.ExternalId == m.ExternalId) continue;
                    var otherTk = TitleYearKey(other.Metadata.Title, other.Metadata.Year);
                    if (otherTk is null || otherTk != tk) continue;
                    if (!string.IsNullOrEmpty(other.Metadata.Source) && !sources.Contains(other.Metadata.Source))
                        sources.Add(other.Metadata.Source);
                    // Source is carried alongside the id itself -- not re-derived later from the
                    // id string's prefix convention -- so downstream consumers (LibraryItemResolver,
                    // AddFromSearchAsync's enrichment pre-seeding) can require it to match instead
                    // of trusting a bare id string that another provider could coincidentally share.
                    if (!string.IsNullOrEmpty(other.Metadata.ExternalId) && !string.IsNullOrEmpty(other.Metadata.Source))
                        contribExternalIds.Add(new ContributingExternalId(other.Metadata.Source, other.Metadata.ExternalId));
                }

                merged.Add(new MetadataCandidate(
                    m.ExternalId!, m.Title, m.Year, poster,
                    m.Overview, m.Rating, 0,
                    m.Source,
                    m.Genres.Count > 0           ? m.Genres           : null,
                    m.Cast.Count > 0             ? m.Cast.Select(c => c.Name).ToList() : null,
                    sources.Count > 1            ? sources            : null,
                    contribExternalIds.Count > 0 ? contribExternalIds : null));
            }

            return merged;
        }

        public async Task<Chronicle.Core.Models.MediaItem> AddFromSearchAsync(
            string externalId, int mediaTypeId, int userId, CancellationToken ct = default,
            List<ContributingExternalId>? contributingExternalIds = null)
        {
            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == mediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {mediaTypeId} not found.");

            // Derive which plugin should handle this externalId.
            var (idSource, _) = ParseSuggestedExternalId(externalId);
            var pluginId = SourceToPluginId(idSource);
            var provider = (pluginId is not null ? _registry.GetMetadataProvider(pluginId) : null)
                ?? ProvidersForType(mediaType.Name).FirstOrDefault()
                ?? throw new InvalidOperationException("No metadata provider is loaded.");

            var meta = await ProviderCallGuard.CallAsync<MediaMetadata?>(
                async t => (MediaMetadata?)await provider.GetByIdAsync(externalId, t), provider.PluginId, "GetByIdAsync",
                null, msg => _log.Warning(msg), msg => _log.Error(msg), ct)
                ?? throw new InvalidOperationException(
                    $"Provider {provider.PluginId} did not return metadata for {externalId}");

            // DELIBERATELY PERMANENT — added 2026-08-03 after several "Add Media" items (all
            // sourced from a currently-flaky provider) ended up with Name set but MetadataJson
            // and every media_external_ids row completely empty, with no exception/error logged
            // anywhere and the request still returning 200. Static code review couldn't pin the
            // exact mechanism -- every branch below unconditionally sets MetadataJson and calls
            // UpsertExternalIdAsync, so this should be structurally impossible. Do not remove or
            // downgrade this without confirming the failure mode first; the whole point is to
            // stop guessing next time it happens.
            _log.Information(
                "AddFromSearchAsync: provider={Provider} externalId={ExternalId} -> meta.Title={Title} " +
                "meta.ExternalId={MetaExternalId} meta.Year={Year}",
                provider.PluginId, externalId, meta.Title, meta.ExternalId, meta.Year);

            var (source, extId) = ParseSuggestedExternalId(externalId);

            // Extract cross-reference IDs from the provider's ExtendedData (e.g. Trakt → TMDB/IMDB IDs).
            // These are used to pre-seed enrichment rows so other plugins don't text-search and mis-match.
            var crossRefs = ExtractCrossRefIds(meta, source, mediaType.Name);

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
                // Fall back to a same-media-type title+year match so a pre-existing item
                // (e.g. a file-scanner stub, or a previous partial add) is reused instead of
                // duplicated — mirrors ImportApprovedAsync's equivalent fallback. This never
                // matches across media types: a Fan Edit and its source Movie are intentionally
                // distinct catalog entries even when they share a title/year, so mediaTypeId is
                // passed through unchanged to FindByTitleAsync's type-scoped lookup.
                // An unknown meta.Year must never fall back to a title-only match here — see the
                // identical guard (and its "Running Man" root-cause story) in ImportApprovedAsync.
                var existingByTitle = meta.Year.HasValue
                    ? await FindByTitleAsync(meta.Title, mediaTypeId, meta.Year, ct)
                    : null;
                if (existingByTitle is not null)
                {
                    item                = existingByTitle;
                    item.Name           = meta.Title;
                    item.Year           = meta.Year;
                    item.Overview       = meta.Overview;
                    item.PosterUrl      = meta.PosterUrl;
                    item.RuntimeMinutes = meta.RuntimeMinutes;
                    item.MetadataJson   = MergeProviderBlob(item.MetadataJson, providerBlobKey, meta);
                    item.UpdatedAt      = DateTime.UtcNow;
                    await _context.SaveChangesAsync(ct);
                    await UpsertExternalIdAsync(item.Id, externalId, ct);
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

                    // Track which plugin IDs have already had an enrichment row added in this call.
                    // AnyAsync only queries the DB, not in-memory tracked entities, so without this
                    // guard multiple cross-ref paths targeting the same plugin produce a duplicate-key error.
                    var seededPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Always seed an enrichment row for the source plugin itself with the known ID.
                    // This guarantees the source plugin appears on the detail page even if its
                    // declared media types don't exactly match the item's type (e.g. Trakt added
                    // under an "anime" tab that Trakt doesn't explicitly declare support for).
                    if (pluginId is not null)
                    {
                        seededPluginIds.Add(pluginId);
                        _context.MediaEnrichments.Add(new Chronicle.Core.Models.MediaItemEnrichment
                        {
                            MediaItemId = item.Id,
                            PluginId    = pluginId,
                            ExternalId  = externalId,
                            Status      = Chronicle.Core.Models.EnrichmentStatus.Pending,
                        });

                        // This seed is unconditional by design (see comment above) -- but that
                        // means a genuinely mismatched (item, plugin) pair reaching here is
                        // silent otherwise. Log it so "why is plugin X enriching a type it
                        // doesn't declare" is traceable instead of requiring speculation.
                        //
                        // DELIBERATELY PERMANENT — added 2026-08-02 alongside the matching log in
                        // MetadataEnrichmentService.EnrichPendingAsync, same investigation, same
                        // reasoning. Do not remove or quiet this down without the user's explicit
                        // go-ahead; the whole point is to never have to guess about provider
                        // dispatch again.
                        var sourceProvider = _registry.GetMetadataProvider(pluginId);
                        var sourceDeclaresType = sourceProvider?.GetSupportedMediaTypes().Any(t =>
                            NormalizeMediaTypeName(t.MediaTypeName) == NormalizeMediaTypeName(mediaType.Name)) ?? false;
                        if (!sourceDeclaresType)
                            _log.Warning(
                                "AddFromSearchAsync: seeded unconditional enrichment row for item {ItemId} " +
                                "\"{Name}\" (type={Type}) -> source plugin {Plugin}, which does NOT declare " +
                                "support for this type (declares: {SupportedTypes}) -- intentional per design, " +
                                "not a bug, but logged so it's visible rather than assumed",
                                item.Id, item.Name, mediaType.Name, pluginId,
                                sourceProvider is null ? "(plugin not found)" : string.Join(", ",
                                    sourceProvider.GetSupportedMediaTypes().Select(t => t.MediaTypeName)));
                    }

                    // Pre-seed enrichment rows for providers that contributed a matching result
                    // during search (e.g. TVMaze matched the same show by title+year). Their IDs
                    // aren't in the primary provider's cross-ref data so must be passed explicitly.
                    foreach (var contrib in contributingExternalIds ?? [])
                    {
                        var contribId = contrib.ExternalId;
                        // The contributing provider's own Source travels with the id (set at
                        // search-merge time in SearchMetadataAsync) rather than being re-derived
                        // here from the id string's prefix convention -- see ContributingExternalId's
                        // and LibraryItemResolver's docs for the cross-provider id collision that
                        // made re-deriving it unsafe.
                        var cPluginId = SourceToPluginId(contrib.Source);
                        if (cPluginId is null || cPluginId == pluginId) continue;

                        // Only seed if this contributing plugin actually supports the item's media
                        // type — a multi-provider search can return a same-titled false-positive
                        // match from a plugin that has nothing to do with this item's real type
                        // (e.g. SIMKL text-matching a music track's title to an unrelated movie).
                        var cProvider = _registry.GetMetadataProvider(cPluginId);
                        if (cProvider is null) continue;
                        var cTypeSupported = cProvider.GetSupportedMediaTypes().Any(t =>
                        {
                            var n = NormalizeMediaTypeName(t.MediaTypeName);
                            return n == NormalizeMediaTypeName(mediaType.Name)
                                || n == ToMediaTypeHint(mediaType.Name);
                        });
                        if (!cTypeSupported) continue;

                        if (!seededPluginIds.Add(cPluginId)) continue; // already queued
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

                            // Only seed plugins that support this media type (or its parent hint).
                            var typeSupported = candidateProvider.GetSupportedMediaTypes()
                                .Any(t =>
                                {
                                    var n = NormalizeMediaTypeName(t.MediaTypeName);
                                    return n == NormalizeMediaTypeName(mediaType.Name)
                                        || n == ToMediaTypeHint(mediaType.Name);
                                });
                            if (!typeSupported) continue;

                            // Accept if this plugin owns the source OR declares it accepts the prefix.
                            var isOwner    = candidatePluginId == ownPluginId;
                            var acceptsCrossRef = !isOwner && candidateProvider
                                .GetAcceptedCrossRefPrefixes()
                                .Any(prefix => xId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                            if (!isOwner && !acceptsCrossRef) continue;

                            if (!seededPluginIds.Add(candidatePluginId)) continue; // already queued this call
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

            // Part of the same permanent diagnostic above — confirms whether MetadataJson and
            // the external-id row actually made it to the database for this item, right before
            // the request returns 200. If this ever logs HasMetadata=False/ExternalIdCount=0
            // again, that's the exact moment/item to chase down.
            var persistedExtIdCount = await _context.MediaExternalIds
                .CountAsync(e => e.MediaItemId == item.Id, ct);
            _log.Information(
                "AddFromSearchAsync: persisted item {ItemId} \"{Name}\" (type={MediaTypeId}) -- " +
                "HasMetadata={HasMetadata} ExternalIdCount={ExternalIdCount}",
                item.Id, item.Name, mediaTypeId, !string.IsNullOrEmpty(item.MetadataJson), persistedExtIdCount);

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
            Chronicle.Plugins.Models.MediaMetadata meta, string fromSource, string? mediaTypeName = null) =>
            CrossRefHelper.ExtractCrossRefIds(meta, fromSource, mediaTypeName)
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
                ["crew"]           = meta.Crew,
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
                // The full path (not just the name) of that same author folder — carried onto
                // the representative entry so the Author container item can later record where
                // it physically lives. Kept independent of whether an AudioAlbumArtist/AudioArtist
                // tag was present: the folder-name fallback above only fires when tags are absent,
                // but the physical folder path is valid signal either way.
                rep.AuthorFolderPath = (!parentIsRoot && parentDir is not null) ? parentDir : null;

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
                if (!m.Success || !DigitParsingHelper.TryParseDigits(m.Groups[1].Value, out var parsedYear)) continue;
                year   = parsedYear;
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

        /// <summary>
        /// Broadens a media type name to a related type a provider might declare instead
        /// (anime→tv, fanedits→movie) so that, e.g., a provider declaring only "tv" support
        /// still gets seeded for an "anime" item. Returns null — no broadening — for anything
        /// not in this explicit allowlist. This must stay an allowlist, not a catch-all default:
        /// it previously defaulted to "movie" for any unrecognized name, which silently matched
        /// every movie-supporting provider (TMDB, FanartTV, SIMKL) against "audiobooks" items
        /// (and would do the same for "books" or any future custom type), seeding hundreds of
        /// enrichment rows those plugins can never resolve.
        /// </summary>
        private static string? ToMediaTypeHint(string mediaTypeName)
        {
            var n = mediaTypeName.ToLowerInvariant();
            // Must be checked before the generic "anime" → tv fallback below — "anime_movies"
            // contains "anime" as a substring but is flat (like movies), not TV-hierarchical.
            if (n.Contains("anime") && n.Contains("movie")) return "movie";
            if (n.Contains("tv") || n.Contains("show") || n.Contains("series")
                || n.Contains("anime")) return "tv";
            if (n.Contains("music") || n.Contains("album") || n.Contains("track")) return "music";
            if (n.Contains("fanedit")) return "movie";
            return null;
        }

        // ── Hierarchy grouping ────────────────────────────────────────────────────

        internal record ShowGroup(string ShowTitle, Dictionary<int, SeasonGroup> Seasons);
        internal record SeasonGroup(int SeasonNumber, List<Chronicle.Plugins.Models.ScannedFile> Episodes);

        /// <summary>Exposed for unit testing only.</summary>
        internal static List<ShowGroup> GroupByShowForTest(
            IEnumerable<Chronicle.Plugins.Models.ScannedFile> files) => GroupByShow(files);

        /// <summary>Exposed for unit testing only.</summary>
        /// <summary>Exposed for unit testing only -- lets a test exercise the real
        /// FindOrCreateParentAsync (including its merge-alias resolution) without needing
        /// a full plugin-registry-backed ScanAsync call, since none of this method's
        /// actual work touches _registry/_protector/_progress/_importProgress/_groupingService.</summary>
        internal Task<FileScanSummary> ScanAudiobooksHierarchicallyForTest(
            List<Chronicle.Plugins.Models.ScannedFile> collapsed, MediaType mediaType,
            int userId, int threshold, CancellationToken ct = default) =>
            ScanAudiobooksHierarchicallyAsync(collapsed, mediaType, userId, threshold, ct);

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
                // First known folder wins — later files for the same tag-derived author name
                // filling in what an earlier one (e.g. a root-level standalone file) couldn't.
                authorGroup.FolderPath ??= file.AuthorFolderPath;

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
            CancellationToken ct,
            string? folderPath = null)
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
            {
                // Backfill: this container was created before folderPath was ever recorded for
                // L0/L1 parent nodes (only leaf book items got one), so every author scanned
                // before this fix shows "No file on disk" in the UI despite the scanner knowing
                // exactly which directory it lives in. Patch it in on the next rescan.
                if (folderPath is not null)
                    SetContainerFolderPathIfMissing(existing, folderPath);
                return existing;
            }

            // No exact-name match -- before creating a brand new item, check whether this
            // literal name string was already merged away into a survivor under a different
            // name. This method only ever matches on an exact Name string; MergeService deletes
            // the loser row and records the loser's name only as a display-only MediaItemAlias
            // (Source="merge") -- nothing here ever consulted it before. Without this check,
            // every rescan re-derives the same tag-sourced name from the same files (e.g. an
            // ID3 AudioAlbumArtist tag literally containing "James Hunter, eden Hudson") and
            // recreates the exact duplicate a user had just merged away. Confirmed live
            // (2026-08-27): several audiobook authors merged one day reappeared as fresh stubs
            // after the very next night's scheduled scan.
            var aliasedItem = await _context.MediaItemAliases
                .Where(a => a.Alias.ToLower() == nameLower
                         && a.MediaItem!.MediaTypeId == mediaTypeId
                         && a.MediaItem.HierarchyLevel == hierarchyLevel
                         && (parentId == null || a.MediaItem.ParentId == parentId))
                .Select(a => a.MediaItem!)
                .FirstOrDefaultAsync(ct);

            if (aliasedItem is not null)
            {
                _log.Information(
                    "FindOrCreateParentAsync: '{Name}' matches a merge alias -- resolving to " +
                    "existing item {Id} ('{WinnerName}') instead of creating a duplicate",
                    name, aliasedItem.Id, aliasedItem.Name);
                if (folderPath is not null)
                    SetContainerFolderPathIfMissing(aliasedItem, folderPath);
                return aliasedItem;
            }

            // Still no match -- try a loose, whitespace-insensitive comparison before giving up
            // and creating a new item. Root-caused a real duplicate (2026-08-31): "James S. A.
            // Corey" vs "James S.A. Corey" are the same author, spaced differently around their
            // initials, and the exact-lowercase check above treats them as different names.
            var looseTarget = MediaItemNormalizer.NormalizeNameLoose(name);
            if (!string.IsNullOrEmpty(looseTarget))
            {
                var siblingCandidates = await _context.MediaItems
                    .Where(m => m.MediaTypeId == mediaTypeId && m.ParentId == parentId && m.HierarchyLevel == hierarchyLevel)
                    .ToListAsync(ct);
                var looseMatch = siblingCandidates.FirstOrDefault(
                    m => MediaItemNormalizer.NormalizeNameLoose(m.Name) == looseTarget);
                if (looseMatch is not null)
                {
                    _log.Information(
                        "FindOrCreateParentAsync: '{Name}' loosely matches existing item {Id} ('{ExistingName}') " +
                        "-- resolving instead of creating a duplicate",
                        name, looseMatch.Id, looseMatch.Name);
                    if (folderPath is not null)
                        SetContainerFolderPathIfMissing(looseMatch, folderPath);
                    return looseMatch;
                }
            }

            var item = new MediaItem
            {
                Name           = name,
                MediaTypeId    = mediaTypeId,
                ParentId       = parentId,
                HierarchyLevel = hierarchyLevel,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
            };
            if (folderPath is not null)
                SetContainerFolderPathIfMissing(item, folderPath);

            _context.MediaItems.Add(item);
            await _context.SaveChangesAsync(ct);
            return item;
        }

        /// <summary>
        /// Sets "fileScanner.folderPath" on a container item (Author/Series) if not already
        /// present — a surgical single-key merge, deliberately NOT routed through
        /// SerializeMetadata(). SerializeMetadata rebuilds MetadataJson from a typed
        /// {Tmdb, FileScanner} root, which silently drops every other partition (a provider's
        /// own full-plugin-id key, "_overrides", "_resolved") on round-trip — fine for a fresh
        /// item with no MetadataJson yet, but every one of these container items has typically
        /// already been enriched by a metadata provider (e.g. Hardcover) by the time a rescan
        /// runs this backfill, and that data must survive untouched.
        /// </summary>
        private static void SetContainerFolderPathIfMissing(MediaItem item, string folderPath)
        {
            JsonObject root;
            try
            {
                root = (!string.IsNullOrWhiteSpace(item.MetadataJson)
                    ? JsonNode.Parse(item.MetadataJson) as JsonObject
                    : null) ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }

            if (root["fileScanner"] is not JsonObject fs)
            {
                fs = new JsonObject();
                root["fileScanner"] = fs;
            }

            if (fs["folderPath"] is not null)
                return;   // already recorded — never overwrite with a possibly-stale rescan value

            fs["folderPath"] = folderPath;
            item.MetadataJson = root.ToJsonString();
            item.UpdatedAt    = DateTime.UtcNow;
        }

        // ── MetadataJson helpers ──────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _metaJsonOpts =
            new(JsonSerializerDefaults.Web);

        // Internal model types matching the namespaced MetadataJson structure
        private sealed record TmdbMetaJson(
            double? Rating, List<string> Genres, List<Chronicle.Plugins.Models.CastMember> Cast,
            List<Chronicle.Plugins.Models.CrewMember> Crew, string? PosterUrl, string? BackdropUrl);

        // FilePaths is always an array — even for single-file items — so every writer
        // (flat scan, direct import, hierarchical group scan) agrees on one schema and
        // FileIdentityJson's matching helpers work regardless of which path created the item.
        private sealed record FileScannerMetaJson(
            List<string>? FilePaths, string? LocalPosterPath, string? NfoPosterUrl,
            string? Author = null, string? Series = null, string? FolderPath = null,
            string? NfoPath = null,
            /// <summary>Raw .nfo sidecar text, captured verbatim at import time -- the
            /// lossless-ingestion guarantee itself. NfoParsed (a generic structured view
            /// of the same content, via XmlToJsonConverter) is a convenience for display/
            /// querying; NfoRaw is what makes this immune to that converter ever missing a
            /// tag, and to the source .nfo file being edited, moved, or deleted later.</summary>
            string? NfoRaw = null,
            JsonElement? NfoParsed = null);

        private sealed record MediaMetaJsonRoot(TmdbMetaJson? Tmdb, FileScannerMetaJson? FileScanner);

        /// <summary>
        /// Builds the MetadataJson blob for a MediaItem.
        /// Pass <paramref name="existingJson"/> to preserve the other provider's data when only one changes.
        /// Pass <paramref name="scannerFilePath"/> to record a plain file path without a full ScannedFile;
        /// pair it with <paramref name="scannerNfoPath"/>/Raw/Parsed (see LookupNfo) when the caller
        /// resolved a sidecar for that path itself, since a bare path alone carries no NFO signal.
        /// </summary>
        private string SerializeMetadata(
            Chronicle.Plugins.Models.MediaMetadata? tmdbMeta = null,
            string? existingJson = null,
            Chronicle.Plugins.Models.ScannedFile? scannedFile = null,
            string? scannerFilePath = null,
            string? scannerNfoPath = null,
            string? scannerNfoRaw = null,
            JsonElement? scannerNfoParsed = null)
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
                var nfoCapture = CaptureSidecar(scannedFile.NfoPath);
                fsData = new FileScannerMetaJson(
                    [scannedFile.FilePath],
                    scannedFile.LocalPosterPath,
                    scannedFile.NfoPosterUrl,
                    Author: string.IsNullOrWhiteSpace(author) ? null : author,
                    Series: scannedFile.AudioGrouping,
                    FolderPath: Path.GetDirectoryName(scannedFile.FilePath),
                    NfoPath: scannedFile.NfoPath,
                    NfoRaw: nfoCapture?.RawText,
                    NfoParsed: nfoCapture?.Parsed);
            }

            // Override with a plain file path (direct import without full ScannedFile).
            // Callers may pass either a file path or (audiobooks) a folder path as the
            // identifying path — don't derive FolderPath here, it would be wrong for the
            // folder case.
            if (scannerFilePath is not null)
                fsData = new FileScannerMetaJson([scannerFilePath], null, null,
                    NfoPath: scannerNfoPath, NfoRaw: scannerNfoRaw, NfoParsed: scannerNfoParsed);

            var tmdbData = tmdbMeta is null ? null : new TmdbMetaJson(
                tmdbMeta.Rating,
                tmdbMeta.Genres,
                tmdbMeta.Cast,
                tmdbMeta.Crew,
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
            ApplyNfoSignals(scannedFiles);
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

            // Primary: match by exact physical file path — the strongest possible signal,
            // since two items can only legitimately share an exact file path if they ARE the
            // same physical file. Global across types (don't restrict by mediaTypeId): this is
            // what lets a user's manual "Change Type" survive a rescan without creating a
            // duplicate. Also don't restrict by parentId/hierarchyLevel — enrichment can
            // reparent items into collections (e.g. a movie moves from Level 0 to Level 1
            // under a collection parent).
            //
            // Deliberately NOT matched by folderPath alone: a folder path is weaker evidence
            // than an exact file (reorganizations, trailing-slash/case differences, or two
            // distinct releases landing under similarly-computed paths can collide) and using
            // it as the primary signal is what let unrelated items (e.g. a movie and an
            // unrelated fan edit) get silently attached to the wrong DB row in the past.
            if (group.Files.Count > 0)
            {
                var fpsCandidates = await _context.MediaItems
                    .Where(m => m.MetadataJson != null
                             && EF.Functions.Like(m.MetadataJson, "%filePaths%"))
                    .ToListAsync(ct);

                existing = fpsCandidates.FirstOrDefault(m =>
                    Chronicle.Services.Scan.FileIdentityJson.ContainsAnyFilePath(m.MetadataJson, group.Files));
            }

            // Secondary: exact folder path match — only reached for groups with no files of
            // their own (pure hierarchy containers: a Show or Season/Author folder). There is
            // no file-level signal available for those, so the folder path is the best we have.
            // Scoped by mediaTypeId (unlike Primary above): a container-level match has no
            // per-file evidence to fall back on, so a bare folder-path string collision is the
            // ONLY signal here — and an unscoped one can match a container of a completely
            // different type. Confirmed live (2026-09-02): a music library folder literally
            // named "Dogma" (E:\Music\Dogma\) matched this tier against an existing MOVIE named
            // "Dogma" (no music item shared that folder path yet), silently attaching two
            // Crown the Empire / other albums as children of the film instead of the real
            // "Dogma" band artist item that already existed. Primary intentionally stays
            // type-unscoped (see its own comment: an exact file path is strong enough evidence
            // to survive a manual "Change Type"), but a folder path alone isn't that strong.
            if (existing is null && group.Files.Count == 0 && !string.IsNullOrEmpty(group.FolderPath))
            {
                var fpCandidates = await _context.MediaItems
                    .Where(m => m.MediaTypeId == mediaTypeId
                             && m.MetadataJson != null
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

            // Tertiary: match by name (covers items where neither filePaths nor folderPath matched).
            // Strip trailing "(YYYY)" from both sides so "Show (2016)" and "Show" deduplicate.
            //
            // Root items (hierarchyLevel == 0) stay unscoped by parentId — same reasoning as the
            // folderPath check above: a movie can legitimately be reparented into/out of a
            // collection and must still match itself afterward.
            //
            // Every non-root level (season, episode, album, track, ...) MUST additionally require
            // ParentId/HierarchyLevel to match. Confirmed root cause (2026-08-05): a container node
            // synthesized purely from a parsed filename (e.g. a Season built from
            // "Show.S04E02.mkv" with no real season subfolder — see ScanGroupingService's
            // filename-only branch, which never sets FolderPath) skips both the file-path and
            // folder-path tiers above and falls straight through to this one. Without a ParentId
            // filter, "Season 04" from a brand-new show scan matched "Season 04" already sitting
            // under a COMPLETELY UNRELATED show, silently re-parenting an entire season's worth of
            // new episodes under the wrong show — which then inherited that wrong show's own
            // external IDs during enrichment (MetadataEnrichmentService derives an episode's ID
            // from its grandparent's stored ExternalId). A whole Star Trek: TNG season was
            // misfiled under Rick and Morty's Season 04 this way, in one real run.
            var groupNameClean = System.Text.RegularExpressions.Regex
                .Replace(group.Name ?? "", @"\s*\(\d{4}\)\s*$", "").Trim();
            var nameCandidates = await _context.MediaItems
                .Where(m => m.MediaTypeId == mediaTypeId &&
                            (hierarchyLevel == 0 || (m.ParentId == parentId && m.HierarchyLevel == hierarchyLevel)))
                .ToListAsync(ct);
            existing ??= nameCandidates.FirstOrDefault(m =>
            {
                var dbNameClean = System.Text.RegularExpressions.Regex
                    .Replace(m.Name ?? "", @"\s*\(\d{4}\)\s*$", "").Trim();
                return string.Equals(dbNameClean, groupNameClean, StringComparison.OrdinalIgnoreCase);
            });

            // Quaternary: resolve a merged-away name via MediaItemAlias. UpsertGroupItemAsync
            // is a SEPARATE implementation from FindOrCreateParentAsync (used by
            // ScanAudiobooksHierarchicallyAsync/ImportHierarchicalAsync) -- despite the
            // similar purpose, patching one does not patch the other, and this is the one
            // ScheduledScanService's nightly scan (via ImportGroupsAsync) actually calls for
            // audiobooks in production. FindOrCreateParentAsync's 2026-08-27 alias-resolution
            // fix (see its own docstring) therefore never covered the real scheduled-scan path
            // at all. Confirmed live (2026-08-28): "Domagoj Kurmaić" and "Shirtaloon, Travis
            // Deverell" were each merged the day before via the UI -- which does correctly
            // record a MediaItemAlias row (Source="merge") -- yet the very next scheduled scan
            // recreated both as fresh duplicate stubs, because this method never consulted
            // MediaItemAliases at all before today. Same nameLower/parentId/hierarchyLevel
            // scoping as the name tier just above, and as FindOrCreateParentAsync's own check.
            if (existing is null)
            {
                // ToLowerInvariant, not ToLower -- matches FindOrCreateParentAsync's own alias
                // check and avoids a culture-dependent casing bug (e.g. Turkish "İ"/"i") that a
                // thread's current culture could introduce into this comparison.
                var groupNameCleanLower = groupNameClean.ToLowerInvariant();
                existing = await _context.MediaItemAliases
                    .Where(a => a.Alias.ToLower() == groupNameCleanLower
                             && a.MediaItem!.MediaTypeId == mediaTypeId
                             && (hierarchyLevel == 0 ||
                                 (a.MediaItem.ParentId == parentId && a.MediaItem.HierarchyLevel == hierarchyLevel)))
                    .Select(a => a.MediaItem!)
                    .FirstOrDefaultAsync(ct);

                if (existing is not null)
                    _log.Information(
                        "UpsertGroupItemAsync: '{Name}' matches a merge alias -- resolving to " +
                        "existing item {Id} ('{WinnerName}') instead of creating a duplicate",
                        group.Name, existing.Id, existing.Name);
            }

            // Read once, used by whichever branch below actually needs it -- lossless
            // ingestion of the sidecar itself (see FileScannerMetaJson's own doc), not just
            // its path. Every hierarchy level that can carry an NfoPath goes through this one
            // method (shows, seasons, episodes, and flat/movie-shaped groups alike), so this
            // single read covers all of them rather than needing a per-level special case.
            var nfoCapture = CaptureSidecar(group.NfoPath);
            var nfoRaw     = nfoCapture?.RawText;
            var nfoParsed  = nfoCapture?.Parsed;

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
                    new {
                        importedAt = DateTime.UtcNow, filePaths = group.Files, folderPath = group.FolderPath,
                        nfoPath = group.NfoPath, nfoRaw, nfoParsed,
                    }));
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
                    fileScanner = new {
                        importedAt = DateTime.UtcNow, filePaths = group.Files, folderPath = group.FolderPath,
                        nfoPath = group.NfoPath, nfoRaw, nfoParsed,
                    }
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
