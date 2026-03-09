using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Chronicle.Services
{
    public class FileScanService : IFileScanService
    {
        private readonly ChronicleDbContext _context;
        private readonly IPluginRegistry _registry;
        private readonly ILogger _log = Log.ForContext<FileScanService>();

        public FileScanService(ChronicleDbContext context, IPluginRegistry registry)
        {
            _context = context;
            _registry = registry;
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
            // Verify media type exists
            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            // Find a scanner that supports this media type
            var scanner = _registry.GetFileScannerPlugins()
                .FirstOrDefault(s => s.GetSupportedMediaTypes()
                    .Any(m => string.Equals(m.MediaTypeName, mediaType.Name, StringComparison.OrdinalIgnoreCase)));

            if (scanner is null)
                throw new InvalidOperationException($"No file scanner plugin supports media type '{mediaType.Name}'.");

            _log.Information("Starting file scan of {Path} (recursive={Recursive}, threshold={Threshold}, mediaType={MediaType})",
                request.Path, request.Recursive, request.ConfidenceThreshold, mediaType.Name);

            var scannedFiles = await scanner.ScanDirectoryAsync(request.Path, request.Recursive, ct);

            var added = 0;
            var alreadyInLibrary = 0;
            var skippedFiles = new List<SkippedFile>();

            foreach (var file in scannedFiles)
            {
                ct.ThrowIfCancellationRequested();

                // Below threshold — report but don't add
                if (file.ConfidenceScore < request.ConfidenceThreshold)
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
                        Status = LibraryStatus.Completed,
                        AddedAt = DateTime.UtcNow,
                        CompletedAt = DateTime.UtcNow,
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
            var mediaType = await _context.MediaTypes.FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
                ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

            var scanner = _registry.GetFileScannerPlugins()
                .FirstOrDefault(s => s.GetSupportedMediaTypes()
                    .Any(m => string.Equals(m.MediaTypeName, mediaType.Name, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException($"No file scanner plugin supports media type '{mediaType.Name}'.");

            _log.Information("Preview scan of {Path} (recursive={Recursive}, mediaType={MediaType})",
                request.Path, request.Recursive, mediaType.Name);

            var scannedFiles = await scanner.ScanDirectoryAsync(request.Path, request.Recursive, ct);

            var results = scannedFiles
                .Select(f => new ScannedFileResult(
                    f.FilePath,
                    f.ParsedTitle,
                    f.ParsedYear,
                    f.ConfidenceScore,
                    f.SuggestedExternalId,
                    string.IsNullOrEmpty(f.MediaTypeHint) ? "movie" : f.MediaTypeHint))
                .ToList();

            _log.Information("Preview complete: {Count} files found", results.Count);
            return new ScanPreview(results);
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
                        // Title (+ year) search
                        var query = file.ParsedYear.HasValue
                            ? $"{file.ParsedTitle} {file.ParsedYear}"
                            : file.ParsedTitle;

                        var searchResult = await provider.SearchAsync(query, file.MediaTypeHint, ct);

                        foreach (var r in searchResult.Results.Take(5))
                        {
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
                            Status = LibraryStatus.Completed,
                            AddedAt = DateTime.UtcNow,
                            CompletedAt = DateTime.UtcNow,
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

            var imported = 0;
            var failed   = 0;
            var failures = new List<string>();

            foreach (var file in request.Files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    MediaItem mediaItem;

                    // If the scanner extracted an external ID (e.g. from an NFO file),
                    // check whether we already have this item so we can update rather than duplicate.
                    if (!string.IsNullOrEmpty(file.SuggestedExternalId))
                    {
                        var (source, extId) = ParseSuggestedExternalId(file.SuggestedExternalId);
                        var existingExt = await _context.MediaExternalIds
                            .Include(e => e.MediaItem)
                            .FirstOrDefaultAsync(
                                e => e.Source == source && e.ExternalId == extId
                                  && e.MediaItem!.MediaTypeId == request.MediaTypeId, ct);

                        if (existingExt?.MediaItem is not null)
                        {
                            // Update file-scanner metadata on the existing item and add to library.
                            mediaItem = existingExt.MediaItem;
                            mediaItem.MetadataJson = SerializeMetadata(
                                scannerFilePath: file.FilePath, existingJson: mediaItem.MetadataJson);
                            mediaItem.UpdatedAt = DateTime.UtcNow;
                            await _context.SaveChangesAsync(ct);
                            await UpsertLibraryEntryAsync(request.UserId, mediaItem.Id, ct);
                            imported++;
                            _log.Information("Updated (direct) existing '{Title}' from {FilePath}",
                                mediaItem.Name, file.FilePath);
                            continue;
                        }
                    }

                    // Create a new MediaItem from scanner data alone.
                    // Name + Year are enough for MetadataRefreshService to find and attach TMDB data.
                    mediaItem = new MediaItem
                    {
                        MediaTypeId    = request.MediaTypeId,
                        Name           = file.ParsedTitle,
                        Year           = file.ParsedYear,
                        MetadataJson   = SerializeMetadata(scannerFilePath: file.FilePath),
                        HierarchyLevel = 0,
                        CreatedAt      = DateTime.UtcNow,
                        UpdatedAt      = DateTime.UtcNow,
                    };
                    _context.MediaItems.Add(mediaItem);
                    await _context.SaveChangesAsync(ct);

                    // Store any NFO-derived external ID so the refresh service can use it.
                    if (!string.IsNullOrEmpty(file.SuggestedExternalId))
                        await UpsertExternalIdAsync(mediaItem.Id, file.SuggestedExternalId, ct);

                    await UpsertLibraryEntryAsync(request.UserId, mediaItem.Id, ct);

                    imported++;
                    _log.Information("Imported (direct) '{Title}' ({Year}) from {FilePath}",
                        file.ParsedTitle, file.ParsedYear, file.FilePath);
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{file.FilePath}: {ex.Message}");
                    _log.Warning(ex, "Failed to direct-import {FilePath}", file.FilePath);
                }
            }

            _log.Information("Direct import complete: {Imported} imported, {Failed} failed", imported, failed);
            return new ImportApprovedSummary(imported, failed, failures);
        }

        /// <summary>Adds a UserLibrary entry for <paramref name="mediaItemId"/> if one doesn't already exist.</summary>
        private async Task UpsertLibraryEntryAsync(int userId, int mediaItemId, CancellationToken ct)
        {
            var exists = await _context.UserLibraries
                .AnyAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId, ct);
            if (!exists)
            {
                _context.UserLibraries.Add(new UserLibrary
                {
                    UserId      = userId,
                    MediaItemId = mediaItemId,
                    Status      = LibraryStatus.Completed,
                    AddedAt     = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                    UpdatedAt   = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync(ct);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

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

        public async Task<MediaItem?> RefreshMetadataAsync(int mediaItemId, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault();
            if (provider is null)
            {
                _log.Warning("RefreshMetadata: no metadata provider loaded");
                return null;
            }

            var item = await _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.ExternalIds)
                .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);

            if (item is null)
                return null;

            // Prefer TMDB external ID (stored as "movie:NNN" or "tv:NNN")
            var extId = item.ExternalIds
                .FirstOrDefault(e => e.Source == "tmdb")
                ?.ExternalId;

            if (extId is null)
            {
                // No external ID yet — try searching by name to find one
                _log.Information("RefreshMetadata: item {Id} has no TMDB external ID, searching by name '{Name}'", mediaItemId, item.Name);
                var hint = ToMediaTypeHint(item.MediaType?.Name ?? string.Empty);
                var searchResult = await provider.SearchAsync(item.Name, hint, ct);
                var top = searchResult.Results.FirstOrDefault();
                if (top is null)
                {
                    _log.Information("RefreshMetadata: no TMDB match found for '{Name}'", item.Name);
                    return item;
                }
                extId = top.ExternalId;
                await UpsertExternalIdAsync(item.Id, extId, ct);
                _log.Information("RefreshMetadata: matched '{Name}' → {ExtId}", item.Name, extId);
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

        // ── Metadata search + direct add (for Add Media UI) ──────────────────────

        public async Task<List<MetadataCandidate>> SearchMetadataAsync(
            string query, string mediaTypeHint, CancellationToken ct = default)
        {
            var provider = _registry.GetMetadataProviders().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No metadata provider is loaded. Install and configure a metadata plugin (e.g. TMDB).");

            var result = await provider.SearchAsync(query, mediaTypeHint, ct);

            return result.Results
                .Select(r => new MetadataCandidate(r.ExternalId, r.Title, r.Year, r.PosterUrl, r.Overview, r.Rating, 0))
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
                var seasonNum = file.SeasonNumber ?? 0; // 0 = "Specials"

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
    }
}
